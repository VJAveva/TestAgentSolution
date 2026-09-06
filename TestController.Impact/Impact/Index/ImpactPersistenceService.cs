using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Impact.Index;

/// <summary>
/// Mirrors the impact databases to a snapshot-proof network share.
/// </summary>
/// <remarks>
/// Uses <c>VACUUM INTO</c> rather than a file copy. These databases run in WAL mode, so the main file alone
/// is not a consistent snapshot — committed pages can still be sitting in the -wal sidecar. VACUUM INTO asks
/// SQLite for a transactionally consistent, compacted single file while the database stays in use.
/// Every operation is best-effort: a missing or slow share must never fail startup, shutdown, or a run.
/// </remarks>
public sealed class ImpactPersistenceService : IImpactPersistenceService
{
    private readonly IImpactIndexPathProvider _paths;
    private readonly ImpactPersistenceOptions _options;
    private readonly IAppLogger _logger;

    private const string LogCategory = "ImpactPersistence";

    public ImpactPersistenceService(
        IImpactIndexPathProvider paths,
        IOptions<ImpactPersistenceOptions> options,
        IAppLogger logger)
    {
        _paths = paths;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Per-machine folder so hosts sharing the location never overwrite each other.</summary>
    private string RemoteRoot => Path.Combine(
        _options.NetworkPath,
        string.IsNullOrWhiteSpace(_options.HostId) ? Environment.MachineName : _options.HostId.Trim());

    public async Task<PersistenceResult> BackupAsync(CancellationToken ct = default)
    {
        if (!IsConfigured(out PersistenceResult? skip)) return skip!;

        try
        {
            Directory.CreateDirectory(RemoteRoot);

            long total = 0;
            var parts = new List<string>();

            PersistenceResult learning = await SnapshotAsync(
                _paths.OutcomeFilePath, Path.Combine(RemoteRoot, "impact-outcomes.db"), ct).ConfigureAwait(false);
            if (!learning.Succeeded) return learning;
            if (!learning.Skipped) { total += learning.SizeBytes; parts.Add($"learning {learning.SizeBytes / 1024d:N0} KB"); }

            if (_options.IncludeIndex)
            {
                PersistenceResult index = await SnapshotAsync(
                    _paths.IndexFilePath, Path.Combine(RemoteRoot, "impact-index.db"), ct).ConfigureAwait(false);
                if (index.Succeeded && !index.Skipped) { total += index.SizeBytes; parts.Add($"index {index.SizeBytes / 1024d / 1024d:N1} MB"); }
            }

            if (parts.Count == 0) return PersistenceResult.Skip("Nothing to back up yet.");

            var message = $"Backed up to '{RemoteRoot}': {string.Join(", ", parts)}.";
            _logger.Info(LogCategory, message);
            return PersistenceResult.Ok(message, RemoteRoot, total);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var message = $"Backup to '{RemoteRoot}' failed: {ex.Message}";
            _logger.Warn(LogCategory, message);
            return PersistenceResult.Fail(message);
        }
    }

    public async Task<PersistenceResult> RestoreIfMissingAsync(CancellationToken ct = default)
    {
        if (!IsConfigured(out PersistenceResult? skip)) return skip!;

        try
        {
            // A live local store is always assumed newer than any snapshot; never overwrite it.
            if (HasContent(_paths.OutcomeFilePath))
                return PersistenceResult.Skip("Local learning store present; no restore needed.");

            var remote = Path.Combine(RemoteRoot, "impact-outcomes.db");
            if (!File.Exists(remote))
                return PersistenceResult.Skip($"No learning snapshot at '{remote}'; starting fresh.");

            Directory.CreateDirectory(_paths.LearningRoot);
            var staging = _paths.OutcomeFilePath + ".restoring";

            File.Copy(remote, staging, overwrite: true);
            if (!await IsUsableAsync(staging, "MappingRuns", ct).ConfigureAwait(false))
            {
                TryDelete(staging);
                var bad = $"Learning snapshot at '{remote}' failed verification; ignored.";
                _logger.Warn(LogCategory, bad);
                return PersistenceResult.Fail(bad);
            }

            File.Move(staging, _paths.OutcomeFilePath, overwrite: true);
            var size = new FileInfo(_paths.OutcomeFilePath).Length;
            var message = $"Restored learning store from '{remote}' ({size / 1024d:N0} KB).";
            _logger.Info(LogCategory, message);
            return PersistenceResult.Ok(message, _paths.OutcomeFilePath, size);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var message = $"Restore from '{RemoteRoot}' failed: {ex.Message}";
            _logger.Warn(LogCategory, message);
            return PersistenceResult.Fail(message);
        }
    }

    private bool IsConfigured(out PersistenceResult? skip)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.NetworkPath))
        {
            skip = PersistenceResult.Skip("Impact persistence is disabled.");
            return false;
        }
        skip = null;
        return true;
    }

    // ── Cross-host merge ──────────────────────────────────────────────────────
    //
    // Outcome rows are append-only facts, so folding two stores together is a set union. The union key
    // differs per table though, and getting it wrong loses data silently:
    //
    //   MappingRuns / EscapeRecords   PK is a Guid -> globally unique -> INSERT OR IGNORE on the PK is safe.
    //   MappingSelections /           PK is an autoincrement int -> host A's Id=1 and host B's Id=1 are
    //   ExecutionOutcomes             DIFFERENT rows. INSERT OR IGNORE would discard one of them. These are
    //                                 inserted WITHOUT their Id (the target assigns a fresh rowid) and
    //                                 deduplicated on the natural key (RunId, TestCaseId) — RunId is a Guid,
    //                                 so that key is globally unique.

    private sealed record MergeTable(string Name, string Columns, string? NaturalKey);

    private static readonly MergeTable[] MergeTables =
    [
        new("MappingRuns",
            "[Id],[AreaId],[PullRequestId],[Tier],[CreatedUtc],[ModelVersion],[ScoringMode],[SelectedCount],[BudgetUsedSeconds],[EarlyExit]",
            null),
        new("EscapeRecords",
            "[Id],[AreaId],[TestCaseId],[DetectedUtc],[Source],[Notes]",
            null),
        new("MappingSelections",
            "[RunId],[TestCaseId],[FeatureId],[FinalScore],[Grade],[AnchorSource],[Rank],[WasExecuted]",
            "[RunId],[TestCaseId]"),
        new("ExecutionOutcomes",
            "[RunId],[TestCaseId],[Result],[DurationSeconds],[FailureSignature]",
            "[RunId],[TestCaseId]"),
    ];

    public async Task<MergeResult> MergeAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.NetworkPath))
            return MergeResult.Skip("Impact persistence is disabled.");
        if (!_options.MergeAcrossHosts)
            return MergeResult.Skip("Cross-host merge is disabled.");

        var canonicalDir = Path.Combine(_options.NetworkPath, _options.CanonicalFolderName);
        var canonical = Path.Combine(canonicalDir, "impact-outcomes.db");

        try
        {
            string[] sources = Directory.Exists(_options.NetworkPath)
                ? Directory.GetDirectories(_options.NetworkPath)
                    .Where(d => !string.Equals(Path.GetFileName(d), _options.CanonicalFolderName, StringComparison.OrdinalIgnoreCase))
                    .Select(d => Path.Combine(d, "impact-outcomes.db"))
                    .Where(HasContent)
                    .ToArray()
                : [];

            if (sources.Length == 0 && !HasContent(canonical))
                return MergeResult.Skip("No host snapshots to merge yet.");

            Directory.CreateDirectory(canonicalDir);

            // Build into a staging copy and swap, so a crashed merge never leaves a torn canonical file.
            var staging = canonical + ".merging";
            TryDelete(staging);
            if (HasContent(canonical)) File.Copy(canonical, staging, overwrite: true);
            else await CreateEmptyOutcomeStoreAsync(staging, ct).ConfigureAwait(false);

            var intoCanonical = 0;
            foreach (var src in sources)
                intoCanonical += await FoldAsync(src, staging, ct).ConfigureAwait(false);

            if (!await IsUsableAsync(staging, "MappingRuns", ct).ConfigureAwait(false))
            {
                TryDelete(staging);
                return MergeResult.Fail("Merged store failed verification; canonical copy left unchanged.");
            }
            File.Move(staging, canonical, overwrite: true);

            // Pull the union back down so this host benefits from runs executed elsewhere.
            var intoLocal = 0;
            if (HasContent(_paths.OutcomeFilePath))
                intoLocal = await FoldAsync(canonical, _paths.OutcomeFilePath, ct).ConfigureAwait(false);

            var message = $"Merged {sources.Length} host store(s): +{intoCanonical} row(s) into canonical, " +
                          $"+{intoLocal} row(s) into local.";
            if (intoCanonical > 0 || intoLocal > 0) _logger.Info(LogCategory, message);
            return new MergeResult
            {
                Succeeded = true,
                Message = message,
                SourcesMerged = sources.Length,
                RowsIntoCanonical = intoCanonical,
                RowsIntoLocal = intoLocal,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var message = $"Merge failed: {ex.Message}";
            _logger.Warn(LogCategory, message);
            return MergeResult.Fail(message);
        }
    }

    /// <summary>Set-unions <paramref name="source"/> into <paramref name="target"/>. Returns rows added.</summary>
    private static async Task<int> FoldAsync(string source, string target, CancellationToken ct)
    {
        if (!HasContent(source) || string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return 0;

        var csb = new SqliteConnectionStringBuilder { DataSource = target, Pooling = false };
        await using var cn = new SqliteConnection(csb.ToString());
        await cn.OpenAsync(ct).ConfigureAwait(false);

        await using (var attach = cn.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $src AS src;";
            attach.Parameters.AddWithValue("$src", source);
            await attach.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var added = 0;
        try
        {
            await using var tx = await cn.BeginTransactionAsync(ct).ConfigureAwait(false);

            foreach (MergeTable t in MergeTables)
            {
                await using var cmd = cn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = t.NaturalKey is null
                    // Guid PK: a straight union on the primary key.
                    ? $"INSERT OR IGNORE INTO main.[{t.Name}] ({t.Columns}) SELECT {t.Columns} FROM src.[{t.Name}];"
                    // Autoincrement PK: omit Id so the target assigns one, dedupe on the natural key.
                    : $"""
                       INSERT INTO main.[{t.Name}] ({t.Columns})
                       SELECT {t.Columns} FROM src.[{t.Name}] s
                       WHERE NOT EXISTS (
                           SELECT 1 FROM main.[{t.Name}] m
                           WHERE {string.Join(" AND ", t.NaturalKey.Split(',').Select(k => $"m.{k} = s.{k}"))});
                       """;
                added += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await using var detach = cn.CreateCommand();
            detach.CommandText = "DETACH DATABASE src;";
            await detach.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return added;
    }

    /// <summary>Creates an empty canonical store with the outcome schema, for the first merge on a share.</summary>
    private static async Task CreateEmptyOutcomeStoreAsync(string path, CancellationToken ct)
    {
        var csb = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        await using var cn = new SqliteConnection(csb.ToString());
        await cn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS MappingRuns (
                Id TEXT PRIMARY KEY, AreaId TEXT NOT NULL, PullRequestId INTEGER NULL, Tier INTEGER NOT NULL,
                CreatedUtc TEXT NOT NULL, ModelVersion TEXT NOT NULL, ScoringMode TEXT NOT NULL,
                SelectedCount INTEGER NOT NULL, BudgetUsedSeconds REAL NOT NULL, EarlyExit INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS MappingSelections (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, RunId TEXT NOT NULL, TestCaseId INTEGER NOT NULL,
                FeatureId INTEGER NOT NULL, FinalScore REAL NOT NULL, Grade INTEGER NOT NULL,
                AnchorSource INTEGER NULL, Rank INTEGER NOT NULL, WasExecuted INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS ExecutionOutcomes (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, RunId TEXT NOT NULL, TestCaseId INTEGER NOT NULL,
                Result TEXT NOT NULL, DurationSeconds REAL NOT NULL, FailureSignature TEXT NULL);
            CREATE TABLE IF NOT EXISTS EscapeRecords (
                Id TEXT PRIMARY KEY, AreaId TEXT NOT NULL, TestCaseId INTEGER NOT NULL,
                DetectedUtc TEXT NOT NULL, Source TEXT NOT NULL, Notes TEXT NULL);
            CREATE INDEX IF NOT EXISTS IX_MappingSelections_RunId_TestCaseId ON MappingSelections (RunId, TestCaseId);
            CREATE INDEX IF NOT EXISTS IX_ExecutionOutcomes_RunId_TestCaseId ON ExecutionOutcomes (RunId, TestCaseId);
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>VACUUM INTO a temp file, then swap — so a failed or partial run never replaces a good snapshot.</summary>
    private async Task<PersistenceResult> SnapshotAsync(string source, string destination, CancellationToken ct)
    {
        if (!HasContent(source))
            return PersistenceResult.Skip($"'{source}' is absent or empty.");

        var staging = destination + ".tmp";
        TryDelete(staging);

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = source,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };

        await using (var cn = new SqliteConnection(csb.ToString()))
        {
            await cn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = cn.CreateCommand();
            // Parameterised: the path is data, and VACUUM INTO accepts a bound value.
            cmd.CommandText = "VACUUM INTO $target;";
            cmd.Parameters.AddWithValue("$target", staging);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        RotateVersions(destination);
        File.Move(staging, destination, overwrite: true);
        return PersistenceResult.Ok("snapshot written", destination, new FileInfo(destination).Length);
    }

    /// <summary>Keeps the previous snapshots as timestamped siblings so a bad mirror can be rolled back.</summary>
    private void RotateVersions(string destination)
    {
        if (_options.KeepVersions <= 0 || !File.Exists(destination)) return;

        try
        {
            var dir = Path.GetDirectoryName(destination)!;
            var name = Path.GetFileNameWithoutExtension(destination);
            var ext = Path.GetExtension(destination);
            File.Copy(destination, Path.Combine(dir, $"{name}.{DateTime.UtcNow:yyyyMMdd-HHmmss}{ext}"), overwrite: true);

            var old = Directory.GetFiles(dir, $"{name}.*{ext}")
                .Where(f => !string.Equals(f, destination, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f)
                .Skip(_options.KeepVersions);
            foreach (var f in old) TryDelete(f);
        }
        catch (Exception ex)
        {
            _logger.Warn(LogCategory, $"Version rotation failed (snapshot still written): {ex.Message}");
        }
    }

    private static bool HasContent(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    /// <summary>A snapshot is only trusted once it opens, passes quick_check and has the expected table.</summary>
    private static async Task<bool> IsUsableAsync(string path, string requiredTable, CancellationToken ct)
    {
        try
        {
            var csb = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            await using var cn = new SqliteConnection(csb.ToString());
            await cn.OpenAsync(ct).ConfigureAwait(false);

            await using var check = cn.CreateCommand();
            check.CommandText = "PRAGMA quick_check(1);";
            if (!string.Equals((await check.ExecuteScalarAsync(ct).ConfigureAwait(false))?.ToString(), "ok",
                    StringComparison.OrdinalIgnoreCase))
                return false;

            await using var table = cn.CreateCommand();
            table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$t;";
            table.Parameters.AddWithValue("$t", requiredTable);
            return Convert.ToInt32(await table.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}

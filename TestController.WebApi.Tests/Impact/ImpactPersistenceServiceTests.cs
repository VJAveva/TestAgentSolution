using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Index;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests.Impact;

/// <summary>
/// Backup/restore of the durable learning store. The behaviour that matters most is the guard against
/// clobbering a live local store, and refusing to restore a snapshot that does not verify.
/// </summary>
public class ImpactPersistenceServiceTests : IDisposable
{
    private readonly string _local = Path.Combine(Path.GetTempPath(), "impact-persist-local-" + Guid.NewGuid().ToString("N"));
    private readonly string _remote = Path.Combine(Path.GetTempPath(), "impact-persist-remote-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var d in new[] { _local, _remote })
        {
            if (!Directory.Exists(d)) continue;
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FixedPaths : IImpactIndexPathProvider
    {
        public FixedPaths(string root)
        {
            IndexRoot = Path.Combine(root, "ImpactIndex");
            LearningRoot = Path.Combine(root, "Learning");
            Directory.CreateDirectory(IndexRoot);
            Directory.CreateDirectory(LearningRoot);
        }

        public string IndexRoot { get; }
        public string LearningRoot { get; }
        public string IndexFilePath => Path.Combine(IndexRoot, "impact-index.db");
        public string OutcomeFilePath => Path.Combine(LearningRoot, "impact-outcomes.db");
        public bool IndexExists => File.Exists(IndexFilePath);
        public void EnsureIndexRootExists() { }
    }

    private (ImpactPersistenceService Service, FixedPaths Paths) Build(bool enabled = true)
    {
        var paths = new FixedPaths(_local);
        var options = Options.Create(new ImpactPersistenceOptions
        {
            Enabled = enabled,
            NetworkPath = _remote,
            KeepVersions = 2,
        });
        return (new ImpactPersistenceService(paths, options, new NoopAppLogger()), paths);
    }

    private static void CreateOutcomeDb(string path, int runs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var cn = new SqliteConnection($"Data Source={path}");
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "CREATE TABLE MappingRuns (Id TEXT PRIMARY KEY, AreaId TEXT);";
        cmd.ExecuteNonQuery();
        for (var i = 0; i < runs; i++)
        {
            using var ins = cn.CreateCommand();
            ins.CommandText = "INSERT INTO MappingRuns (Id, AreaId) VALUES ($id, 'area');";
            ins.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            ins.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private string RemoteOutcomePath => Path.Combine(_remote, Environment.MachineName, "impact-outcomes.db");

    [Fact]
    public async Task BackupAsync_Should_Skip_When_Disabled()
    {
        var (service, paths) = Build(enabled: false);
        CreateOutcomeDb(paths.OutcomeFilePath, 3);

        var result = await service.BackupAsync();

        Assert.True(result.Skipped);
        Assert.False(File.Exists(RemoteOutcomePath));
    }

    [Fact]
    public async Task BackupAsync_Should_WriteConsistentSnapshot_To_PerMachineFolder()
    {
        var (service, paths) = Build();
        CreateOutcomeDb(paths.OutcomeFilePath, 5);

        var result = await service.BackupAsync();

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(RemoteOutcomePath), $"expected snapshot at {RemoteOutcomePath}");
        // VACUUM INTO must produce a real database, not a byte copy of a possibly-inconsistent WAL state.
        Assert.Equal(5, CountRuns(RemoteOutcomePath));
    }

    [Fact]
    public async Task BackupAsync_Should_Skip_When_NoLocalStoreYet()
    {
        var (service, _) = Build();

        var result = await service.BackupAsync();

        Assert.True(result.Skipped);
    }

    [Fact]
    public async Task RestoreIfMissingAsync_Should_Restore_When_LocalAbsent()
    {
        var (service, paths) = Build();
        CreateOutcomeDb(paths.OutcomeFilePath, 4);
        await service.BackupAsync();

        // Simulate the post-snapshot-revert state: local store gone, share intact.
        File.Delete(paths.OutcomeFilePath);
        Assert.False(File.Exists(paths.OutcomeFilePath));

        var result = await service.RestoreIfMissingAsync();

        Assert.True(result.Succeeded);
        Assert.False(result.Skipped);
        Assert.Equal(4, CountRuns(paths.OutcomeFilePath));
    }

    [Fact]
    public async Task RestoreIfMissingAsync_Should_NeverOverwrite_LiveLocalStore()
    {
        var (service, paths) = Build();
        CreateOutcomeDb(paths.OutcomeFilePath, 2);
        await service.BackupAsync();

        // Local store has moved on since the snapshot; a restore must not roll it back.
        CreateOutcomeDb(paths.OutcomeFilePath + ".tmp", 0);
        File.Delete(paths.OutcomeFilePath);
        CreateOutcomeDb(paths.OutcomeFilePath, 9);

        var result = await service.RestoreIfMissingAsync();

        Assert.True(result.Skipped);
        Assert.Equal(9, CountRuns(paths.OutcomeFilePath));
    }

    [Fact]
    public async Task RestoreIfMissingAsync_Should_Reject_CorruptSnapshot()
    {
        var (service, paths) = Build();
        Directory.CreateDirectory(Path.GetDirectoryName(RemoteOutcomePath)!);
        File.WriteAllText(RemoteOutcomePath, "not a database");

        var result = await service.RestoreIfMissingAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("verification", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(paths.OutcomeFilePath));
    }

    [Fact]
    public async Task RestoreIfMissingAsync_Should_Skip_When_NoSnapshotExists()
    {
        var (service, _) = Build();

        var result = await service.RestoreIfMissingAsync();

        Assert.True(result.Skipped);
    }

    [Fact]
    public async Task BackupAsync_Should_KeepBoundedVersionHistory()
    {
        var (service, paths) = Build();   // KeepVersions = 2
        CreateOutcomeDb(paths.OutcomeFilePath, 1);

        for (var i = 0; i < 5; i++)
        {
            await service.BackupAsync();
            await Task.Delay(1100);   // timestamped to the second
        }

        var dir = Path.GetDirectoryName(RemoteOutcomePath)!;
        var archived = Directory.GetFiles(dir, "impact-outcomes.*.db");
        Assert.True(archived.Length <= 2, $"expected at most 2 archived copies, found {archived.Length}");
        Assert.True(File.Exists(RemoteOutcomePath));
    }

    private static int CountRuns(string path)
    {
        var csb = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var cn = new SqliteConnection(csb.ToString());
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM MappingRuns;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ── Cross-host merge ──────────────────────────────────────────────────────

    /// <summary>Full outcome schema, so the merge can be exercised across all four tables.</summary>
    private static void CreateFullOutcomeDb(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var cn = new SqliteConnection($"Data Source={path}");
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE MappingRuns (Id TEXT PRIMARY KEY, AreaId TEXT, PullRequestId INTEGER NULL, Tier INTEGER,
                CreatedUtc TEXT, ModelVersion TEXT, ScoringMode TEXT, SelectedCount INTEGER,
                BudgetUsedSeconds REAL, EarlyExit INTEGER);
            CREATE TABLE MappingSelections (Id INTEGER PRIMARY KEY AUTOINCREMENT, RunId TEXT, TestCaseId INTEGER,
                FeatureId INTEGER, FinalScore REAL, Grade INTEGER, AnchorSource INTEGER NULL, Rank INTEGER, WasExecuted INTEGER);
            CREATE TABLE ExecutionOutcomes (Id INTEGER PRIMARY KEY AUTOINCREMENT, RunId TEXT, TestCaseId INTEGER,
                Result TEXT, DurationSeconds REAL, FailureSignature TEXT NULL);
            CREATE TABLE EscapeRecords (Id TEXT PRIMARY KEY, AreaId TEXT, TestCaseId INTEGER, DetectedUtc TEXT,
                Source TEXT, Notes TEXT NULL);
            """;
        cmd.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Adds one run plus a selection and an outcome for it. Both children get rowid 1 in a fresh store,
    /// which is exactly the collision a PK-based union would mishandle.</summary>
    private static Guid AddRun(string path, int testCaseId)
    {
        var runId = Guid.NewGuid();
        using var cn = new SqliteConnection($"Data Source={path}");
        cn.Open();

        using (var run = cn.CreateCommand())
        {
            run.CommandText = """
                INSERT INTO MappingRuns VALUES ($id,'area',NULL,0,'2026-09-06T00:00:00+00:00','v1','mode',1,1.0,0);
                """;
            run.Parameters.AddWithValue("$id", runId.ToString());
            run.ExecuteNonQuery();
        }
        using (var sel = cn.CreateCommand())
        {
            sel.CommandText = "INSERT INTO MappingSelections (RunId,TestCaseId,FeatureId,FinalScore,Grade,AnchorSource,Rank,WasExecuted) VALUES ($r,$t,10,0.9,3,NULL,1,1);";
            sel.Parameters.AddWithValue("$r", runId.ToString());
            sel.Parameters.AddWithValue("$t", testCaseId);
            sel.ExecuteNonQuery();
        }
        using (var outc = cn.CreateCommand())
        {
            outc.CommandText = "INSERT INTO ExecutionOutcomes (RunId,TestCaseId,Result,DurationSeconds,FailureSignature) VALUES ($r,$t,'Passed',1.5,NULL);";
            outc.Parameters.AddWithValue("$r", runId.ToString());
            outc.Parameters.AddWithValue("$t", testCaseId);
            outc.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return runId;
    }

    private static int Count(string path, string table)
    {
        var csb = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var cn = new SqliteConnection(csb.ToString());
        cn.Open();
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private string CanonicalPath => Path.Combine(_remote, "_canonical", "impact-outcomes.db");

    /// <summary>Writes a snapshot as if it came from another machine.</summary>
    private string SeedOtherHost(string machine, int testCaseId)
    {
        var path = Path.Combine(_remote, machine, "impact-outcomes.db");
        CreateFullOutcomeDb(path);
        AddRun(path, testCaseId);
        return path;
    }

    [Fact]
    public async Task MergeAsync_Should_UnionRunsFromEveryHost()
    {
        var (service, paths) = Build();
        CreateFullOutcomeDb(paths.OutcomeFilePath);
        AddRun(paths.OutcomeFilePath, 100);
        await service.BackupAsync();

        SeedOtherHost("OTHERHOST", 200);

        MergeResult result = await service.MergeAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.SourcesMerged);
        Assert.Equal(2, Count(CanonicalPath, "MappingRuns"));
        // The union is pulled back down, so this host now knows about the other host's run.
        Assert.Equal(2, Count(paths.OutcomeFilePath, "MappingRuns"));
    }

    [Fact]
    public async Task MergeAsync_Should_NotLoseChildRows_When_AutoIncrementIdsCollide()
    {
        // Both stores have MappingSelections.Id = 1 for DIFFERENT runs. A union keyed on the primary key
        // would silently drop one; the natural key (RunId, TestCaseId) must keep both.
        var (service, paths) = Build();
        CreateFullOutcomeDb(paths.OutcomeFilePath);
        AddRun(paths.OutcomeFilePath, 100);
        await service.BackupAsync();
        SeedOtherHost("OTHERHOST", 200);

        Assert.Equal(1, Count(paths.OutcomeFilePath, "MappingSelections"));

        await service.MergeAsync();

        Assert.Equal(2, Count(CanonicalPath, "MappingSelections"));
        Assert.Equal(2, Count(CanonicalPath, "ExecutionOutcomes"));
        Assert.Equal(2, Count(paths.OutcomeFilePath, "MappingSelections"));
        Assert.Equal(2, Count(paths.OutcomeFilePath, "ExecutionOutcomes"));
    }

    [Fact]
    public async Task MergeAsync_Should_BeIdempotent_When_RunRepeatedly()
    {
        var (service, paths) = Build();
        CreateFullOutcomeDb(paths.OutcomeFilePath);
        AddRun(paths.OutcomeFilePath, 100);
        await service.BackupAsync();
        SeedOtherHost("OTHERHOST", 200);

        await service.MergeAsync();
        MergeResult second = await service.MergeAsync();
        MergeResult third = await service.MergeAsync();

        Assert.Equal(0, second.RowsIntoCanonical);
        Assert.Equal(0, second.RowsIntoLocal);
        Assert.Equal(0, third.RowsIntoCanonical);
        Assert.Equal(2, Count(CanonicalPath, "MappingRuns"));
        Assert.Equal(2, Count(paths.OutcomeFilePath, "MappingSelections"));
    }

    [Fact]
    public async Task MergeAsync_Should_Skip_When_DisabledByOption()
    {
        var paths = new FixedPaths(_local);
        var service = new ImpactPersistenceService(
            paths,
            Options.Create(new ImpactPersistenceOptions
            {
                Enabled = true,
                NetworkPath = _remote,
                MergeAcrossHosts = false,
            }),
            new NoopAppLogger());

        MergeResult result = await service.MergeAsync();

        Assert.True(result.Skipped);
    }

    [Fact]
    public async Task MergeAsync_Should_Skip_When_NoSnapshotsExist()
    {
        var (service, _) = Build();

        MergeResult result = await service.MergeAsync();

        Assert.True(result.Skipped);
    }

    [Fact]
    public async Task BackupAsync_Should_UseHostId_When_Configured()
    {
        // Two impact hosts on one machine would otherwise share a folder and overwrite each other.
        var paths = new FixedPaths(_local);
        CreateOutcomeDb(paths.OutcomeFilePath, 2);
        var service = new ImpactPersistenceService(
            paths,
            Options.Create(new ImpactPersistenceOptions
            {
                Enabled = true,
                NetworkPath = _remote,
                HostId = "WPF-CONTROLLER",
            }),
            new NoopAppLogger());

        var result = await service.BackupAsync();

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(Path.Combine(_remote, "WPF-CONTROLLER", "impact-outcomes.db")));
        Assert.False(File.Exists(Path.Combine(_remote, Environment.MachineName, "impact-outcomes.db")));
    }

    [Fact]
    public async Task BackupAsync_Should_FallBackToMachineName_When_HostIdBlank()
    {
        var (service, paths) = Build();
        CreateOutcomeDb(paths.OutcomeFilePath, 1);

        await service.BackupAsync();

        Assert.True(File.Exists(Path.Combine(_remote, Environment.MachineName, "impact-outcomes.db")));
    }

    [Fact]
    public async Task MergeAsync_Should_SurviveOtherHostSnapshotBeingCorrupt()
    {
        var (service, paths) = Build();
        CreateFullOutcomeDb(paths.OutcomeFilePath);
        AddRun(paths.OutcomeFilePath, 100);
        await service.BackupAsync();

        var bad = Path.Combine(_remote, "BADHOST", "impact-outcomes.db");
        Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
        File.WriteAllText(bad, "not a database");

        MergeResult result = await service.MergeAsync();

        // One bad snapshot must not cost the whole cycle; the canonical copy is left intact either way.
        Assert.False(result.Succeeded);
        Assert.Equal(1, Count(paths.OutcomeFilePath, "MappingRuns"));
    }

    private sealed class NoopAppLogger : IAppLogger
    {
#pragma warning disable CS0067 // event is part of the interface but unused in tests
        public event Action<AppLogEntry>? EntryAdded;
#pragma warning restore CS0067
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
    }
}

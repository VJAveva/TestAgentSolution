using TestControllerGrpc.Authorization;
using TestControllerGrpc.Features;
using TestControllerGrpc.Identity;

namespace TestControllerGrpc.Tests.Features;

public sealed class FeatureSettingsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 18, 5, 9, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"FS_{Guid.NewGuid():N}");
    private readonly string _file;
    private readonly RecordingAudit _audit = new();

    public FeatureSettingsTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "Features.json");
    }

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private (FeatureGate Gate, FeatureSettingsService Service) Create()
    {
        var gate = new FeatureGate(_file, watch: false);
        return (gate, new FeatureSettingsService(gate, _audit, clock: () => Now));
    }

    private static readonly IUserContext Admin = new SyntheticUserContext("u-1", "Ada Admin", ClientKind.Web, ["Administrator"]);

    [Fact]
    public async Task SaveAsync_Should_PersistThroughReloadAndRestart_When_AdminTurnsFeatureOff()
    {
        File.WriteAllText(_file, """{ "TeamName": "WAS-QA", "Features": { "CodeChurn": true } }""");
        var (gate, service) = Create();

        var result = await service.SaveAsync(new Dictionary<string, bool> { ["CodeChurn"] = false }, Admin, null, default);

        Assert.True(result.Success, result.Message);
        Assert.False(gate.IsEnabled(FeatureKeys.CodeChurn));
        using var restarted = new FeatureGate(_file, watch: false);
        Assert.False(restarted.IsEnabled(FeatureKeys.CodeChurn));
        Assert.Equal("WAS-QA", restarted.TeamName);
        Assert.Contains("Saved at 18:05:09", result.Message);
    }

    [Fact]
    public async Task SaveAsync_Should_BackUpAndPreserveUnknownKeys_When_Saving()
    {
        const string original = """
            {
              "TeamName": "WAS-QA",
              "Features": { "Results": true, "Future": true },
              "CodeChurn": { "Repositories": [] }
            }
            """;
        File.WriteAllText(_file, original);
        var (_, service) = Create();

        var result = await service.SaveAsync(new Dictionary<string, bool> { ["Results"] = false }, Admin, null, default);

        Assert.Equal(_file + ".bak-20261107-180509", result.BackupPath);
        Assert.Equal(original, File.ReadAllText(result.BackupPath!));
        var saved = File.ReadAllText(_file);
        Assert.Contains("\"Future\": true", saved);
        Assert.Contains("\"Repositories\"", saved);
        Assert.Contains("\"Results\": false", saved);
    }

    [Fact]
    public async Task SaveAsync_Should_WriteOneAuditEntryPerChange_When_FeaturesToggled()
    {
        File.WriteAllText(_file, """{ "Features": { } }""");
        var (_, service) = Create();

        await service.SaveAsync(new Dictionary<string, bool> { ["CodeChurn"] = false, ["Results"] = true }, Admin, null, default);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal(nameof(Permission.System_ConfigureFeatures), entry.ActionName);
        Assert.Equal("CodeChurn", entry.ResourceId);
        Assert.Equal("feature-off", entry.ReasonCode);
        Assert.Equal("u-1", entry.UserId);
    }

    [Fact]
    public async Task SaveAsync_Should_FailWithReasonAndNoAudit_When_FileIsReadOnly()
    {
        File.WriteAllText(_file, """{ "Features": { } }""");
        File.SetAttributes(_file, FileAttributes.ReadOnly);
        var (gate, service) = Create();

        var result = await service.SaveAsync(new Dictionary<string, bool> { ["Reports"] = false }, Admin, null, default);

        Assert.False(result.Success);
        Assert.Contains("Could not write", result.Message);
        Assert.Empty(_audit.Entries);
        Assert.True(gate.IsEnabled(FeatureKeys.Reports));
    }

    [Fact]
    public async Task SaveAsync_Should_CreateTheFile_When_ItDoesNotExistYet()
    {
        var (gate, service) = Create();

        var result = await service.SaveAsync(new Dictionary<string, bool> { ["GlobalVariables"] = false }, Admin, null, default);

        Assert.True(result.Success, result.Message);
        Assert.Null(result.BackupPath);
        Assert.False(gate.IsEnabled(FeatureKeys.GlobalVariables));
    }

    [Fact]
    public void FeatureFileWriter_Should_Reject_When_KeyIsNotToggleable()
    {
        var result = FeatureFileWriter.Save(_file, new Dictionary<string, bool> { ["Pipelines"] = false });

        Assert.False(result.Success);
        Assert.False(File.Exists(_file));
    }

    private sealed class RecordingAudit : IAuditWriter
    {
        public List<AuditEntry> Entries { get; } = [];
        public void Enqueue(AuditEntry entry) => Entries.Add(entry);
    }
}

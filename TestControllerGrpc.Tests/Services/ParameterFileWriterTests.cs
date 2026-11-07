using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.Services;

public sealed class ParameterFileWriterTests : IDisposable
{
    private const string Config =
        "{\r\n" +
        "  \"Version\": 1,\r\n" +
        "  \"Global\": {\r\n" +
        "    \"_ReleaseName\": \"SP2026\",\r\n" +
        "    \"_InstallFolder\": \"TestSetup\\\\Install\\\\SP2026R2\",\r\n" +
        "    \"_Installer\": \"C:\\\\TestSetup\\\\a+b&c\\\\Install-Build.bat\"\r\n" +
        "  },\r\n" +
        "  \"Profiles\": {\r\n" +
        "    \"Warm\": {\r\n" +
        "      \"_Agent1\": \"JVKPRI\",\r\n" +
        "      \"_AgentsGroup\": \"Warm\"\r\n" +
        "    },\r\n" +
        "    \"Sanity\": {\r\n" +
        "      \"_Agent1\": \"JVGR1\"\r\n" +
        "    }\r\n" +
        "  },\r\n" +
        "  \"Pipelines\": {}\r\n" +
        "}\r\n";

    private static readonly DateTimeOffset Now = new(2026, 11, 7, 15, 30, 45, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"PFW_{Guid.NewGuid():N}");
    private readonly string _file;

    public ParameterFileWriterTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "pipeline-config.json");
        File.WriteAllText(_file, Config);
    }

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }

    private ParameterSaveResult Save(params ParameterEdit[] edits) =>
        ParameterFileWriter.SaveJsonEdits(_file, "Warm", "SP2026 - WARM Tests", edits, () => Now);

    private Dictionary<string, string> Resolve(string profile = "Warm")
    {
        var ctx = new PipelineExecutionContext();
        Assert.True(ParameterResolver.TryLoadJsonConfig(ctx, _file, profile, "SP2026 - WARM Tests", out var failure), failure);
        return ctx.Parameters;
    }

    [Fact]
    public void SaveJsonEdits_Should_PersistGlobalValue_When_ReadBack()
    {
        var result = Save(new ParameterEdit("_InstallFolder", @"TestSetup\Install\SP2026", ParameterLayer.Global));

        Assert.True(result.Success, result.Message);
        Assert.Equal(@"TestSetup\Install\SP2026", Resolve()["_InstallFolder"]);
    }

    [Fact]
    public void SaveJsonEdits_Should_ChangeOnlyTheEditedLine_When_GlobalValueEdited()
    {
        Save(new ParameterEdit("_InstallFolder", @"TestSetup\Install\SP2026", ParameterLayer.Global));

        var before = Config.Split("\r\n");
        var after = File.ReadAllText(_file).Split("\r\n");
        Assert.Equal(before.Length, after.Length);
        var changed = before.Zip(after).Where(pair => pair.First != pair.Second).ToList();
        Assert.Single(changed);
        Assert.Contains("_InstallFolder", changed[0].Second);
    }

    [Fact]
    public void SaveJsonEdits_Should_WriteToTheProfile_When_ProfileValueEdited()
    {
        var result = Save(new ParameterEdit("_Agent1", "JVKBAK", ParameterLayer.Profile));

        Assert.True(result.Success, result.Message);
        var config = ParameterResolver.ReadJsonConfig(_file)!;
        Assert.Equal("JVKBAK", config.Profiles["Warm"]["_Agent1"]);
        Assert.Equal("JVGR1", config.Profiles["Sanity"]["_Agent1"]);
        Assert.False(config.Global.ContainsKey("_Agent1"));
    }

    [Fact]
    public void SaveJsonEdits_Should_CreatePipelineSection_When_PipelineValueEdited()
    {
        var result = Save(new ParameterEdit("_BuildNumber", "OAK.26.0.0.1", ParameterLayer.Pipeline));

        Assert.True(result.Success, result.Message);
        var config = ParameterResolver.ReadJsonConfig(_file)!;
        Assert.Equal("OAK.26.0.0.1", config.Pipelines["SP2026 - WARM Tests"]["_BuildNumber"]);
        Assert.False(config.Global.ContainsKey("_BuildNumber"));
    }

    [Fact]
    public void SaveJsonEdits_Should_RemoveOnlyThatKey_When_ValueIsNull()
    {
        Save(new ParameterEdit("_AgentsGroup", null, ParameterLayer.Profile));

        var config = ParameterResolver.ReadJsonConfig(_file)!;
        Assert.False(config.Profiles["Warm"].ContainsKey("_AgentsGroup"));
        Assert.Equal("JVKPRI", config.Profiles["Warm"]["_Agent1"]);
    }

    [Fact]
    public void SaveJsonEdits_Should_WriteTimestampedBackupOfThePreviousFile_When_Saving()
    {
        var result = Save(new ParameterEdit("_InstallFolder", "X", ParameterLayer.Global));

        Assert.Equal(_file + ".bak-20261107-153045", result.BackupPath);
        Assert.Equal(Config, File.ReadAllText(result.BackupPath!));
        Assert.Contains("at 15:30:45", result.Message);
        Assert.Contains(_file, result.Message);
    }

    [Fact]
    public void SaveJsonEdits_Should_FailWithReason_When_FileIsReadOnly()
    {
        File.SetAttributes(_file, FileAttributes.ReadOnly);

        var result = Save(new ParameterEdit("_InstallFolder", "X", ParameterLayer.Global));

        Assert.False(result.Success);
        Assert.Contains("Could not write", result.Message);
        Assert.Equal(Config, File.ReadAllText(_file));
        Assert.False(File.Exists(_file + ".tmp"));
    }

    [Fact]
    public void SaveJsonEdits_Should_Refuse_When_ValueComesFromGlobalVariablesFile()
    {
        var result = Save(new ParameterEdit("_BuildNumber", "X", ParameterLayer.GlobalVariables));

        Assert.False(result.Success);
        Assert.Contains("GlobalVariables.json", result.Message);
        Assert.Equal(Config, File.ReadAllText(_file));
    }

    [Fact]
    public void SaveJsonEdits_Should_FailWithReason_When_FileIsNotValidJson()
    {
        File.WriteAllText(_file, "{ \"Global\": ");

        var result = Save(new ParameterEdit("_InstallFolder", "X", ParameterLayer.Global));

        Assert.False(result.Success);
        Assert.Contains("Could not read", result.Message);
    }

    [Theory]
    [InlineData(ParameterRank.Global, ParameterLayer.Global)]
    [InlineData(ParameterRank.ParameterFile, ParameterLayer.Profile)]
    [InlineData(ParameterRank.PipelinePin, ParameterLayer.Pipeline)]
    [InlineData(ParameterRank.GlobalVars, ParameterLayer.GlobalVariables)]
    public void LayerFromRank_Should_NameTheSourceSection_When_RankKnown(ParameterRank rank, ParameterLayer expected)
    {
        Assert.Equal(expected, ParameterFileWriter.LayerFromRank((int)rank));
    }

    [Fact]
    public async Task ParameterFileMonitor_Should_RaiseChanged_When_WriterSavesTheWatchedFile()
    {
        using var monitor = new ParameterFileMonitor { Debounce = TimeSpan.FromMilliseconds(50) };
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.ParametersChanged += () => changed.TrySetResult();
        monitor.Watch([_file]);

        Save(new ParameterEdit("_InstallFolder", "X", ParameterLayer.Global));

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("X", Resolve()["_InstallFolder"]);
    }
}

public class ParameterGridEditsTests
{
    private static ParameterEntryViewModel Loaded(string key, string value, ParameterLayer layer) =>
        new() { Key = key, Value = value, OriginalKey = key, OriginalValue = value, Layer = layer };

    [Fact]
    public void Compute_Should_ReturnNothing_When_NoRowChanged()
    {
        var rows = new[] { Loaded("_A", "1", ParameterLayer.Global) };
        Assert.Empty(ParameterGridEdits.Compute(rows, [], "Warm"));
        Assert.False(ParameterGridEdits.HasChanges(rows, []));
    }

    [Fact]
    public void Compute_Should_KeepTheRowsSection_When_ValueEdited()
    {
        var row = Loaded("_Agent1", "JVKPRI", ParameterLayer.Profile);
        row.Value = "JVKBAK";

        var edit = Assert.Single(ParameterGridEdits.Compute([row], [], "Warm"));
        Assert.Equal(new ParameterEdit("_Agent1", "JVKBAK", ParameterLayer.Profile), edit);
    }

    [Fact]
    public void Compute_Should_RemoveOldKeyAndSetNew_When_KeyRenamed()
    {
        var row = Loaded("_Old", "v", ParameterLayer.Global);
        row.Key = "_New";

        Assert.Equal(
            [new ParameterEdit("_Old", null, ParameterLayer.Global), new ParameterEdit("_New", "v", ParameterLayer.Global)],
            ParameterGridEdits.Compute([row], [], "Warm"));
    }

    [Fact]
    public void Compute_Should_RemoveFromItsSection_When_RowDeleted()
    {
        var removed = Loaded("_Gone", "v", ParameterLayer.Profile);
        Assert.Equal([new ParameterEdit("_Gone", null, ParameterLayer.Profile)],
            ParameterGridEdits.Compute([], [removed], "Warm"));
    }

    [Theory]
    [InlineData("Warm", ParameterLayer.Profile)]
    [InlineData("", ParameterLayer.Global)]
    public void Compute_Should_PutNewKeyInSelectedProfile_When_ProfileSelected(string profile, ParameterLayer expected)
    {
        var row = new ParameterEntryViewModel { Key = "_NewKey", Value = "x", IsNew = true };
        Assert.Equal(expected, Assert.Single(ParameterGridEdits.Compute([row], [], profile)).Layer);
    }
}

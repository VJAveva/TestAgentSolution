using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

public class ParameterResolverTests : IDisposable
{
    private readonly string _tempDir;

    public ParameterResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ParamResolverTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string WriteTempFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ???????????????????????????????????????????????????????????????????
    // Resolve
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void Resolve_Should_ReplaceKnownTokens_When_ParametersContainKey()
    {
        // Arrange
        var ctx = new PipelineExecutionContext();
        ctx.Parameters["BuildNumber"] = "1.2.3";
        ctx.Parameters["DropLocation"] = @"\\share\drop";

        // Act
        var result = ParameterResolver.Resolve(@"[DropLocation]\Install_[BuildNumber].bat", ctx);

        // Assert
        Assert.Equal(@"\\share\drop\Install_1.2.3.bat", result);
    }

    [Fact]
    public void Resolve_Should_LeaveUnknownTokens_When_KeyNotFound()
    {
        var ctx = new PipelineExecutionContext();
        ctx.Parameters["Known"] = "value";

        var result = ParameterResolver.Resolve("[Known] and [Unknown]", ctx);

        Assert.Equal("value and [Unknown]", result);
    }

    [Fact]
    public void Resolve_Should_ReturnEmptyString_When_TemplateIsEmpty()
    {
        var ctx = new PipelineExecutionContext();
        Assert.Equal("", ParameterResolver.Resolve("", ctx));
    }

    [Fact]
    public void Resolve_Should_ReturnNull_When_TemplateIsNull()
    {
        var ctx = new PipelineExecutionContext();
        Assert.Null(ParameterResolver.Resolve(null!, ctx));
    }

    [Fact]
    public void Resolve_Should_ReturnOriginal_When_NoTokensPresent()
    {
        var ctx = new PipelineExecutionContext();
        var result = ParameterResolver.Resolve("no tokens here", ctx);
        Assert.Equal("no tokens here", result);
    }

    [Fact]
    public void Resolve_Should_BeCaseInsensitive_When_LookingUpKeys()
    {
        var ctx = new PipelineExecutionContext();
        ctx.Parameters["buildnumber"] = "42";

        var result = ParameterResolver.Resolve("[BuildNumber]", ctx);
        Assert.Equal("42", result);
    }

    // ???????????????????????????????????????????????????????????????????
    // ParseParameterFile
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void ParseParameterFile_Should_ParseCommaDelimited_When_FileHasCsvFormat()
    {
        var path = WriteTempFile("params.txt",
            "_BuildNumber,LKF_main_20260307.1\n_DropLocation,\\\\server\\share\\drop");

        var entries = ParameterResolver.ParseParameterFile(path);

        Assert.Equal(2, entries.Count);
        Assert.Equal("_BuildNumber", entries[0].Key);
        Assert.Equal("LKF_main_20260307.1", entries[0].Value);
        Assert.Equal("_DropLocation", entries[1].Key);
        Assert.Equal(@"\\server\share\drop", entries[1].Value);
    }

    [Fact]
    public void ParseParameterFile_Should_ParseEqualsDelimited_When_FileHasEqualsFormat()
    {
        var path = WriteTempFile("params.txt", "BuildNumber=42\nDropLocation=C:\\drop");

        var entries = ParameterResolver.ParseParameterFile(path);

        Assert.Equal(2, entries.Count);
        Assert.Equal("BuildNumber", entries[0].Key);
        Assert.Equal("42", entries[0].Value);
    }

    [Fact]
    public void ParseParameterFile_Should_SkipComments_When_LineStartsWithHash()
    {
        var path = WriteTempFile("params.txt", "# This is a comment\n_Key,Value\n  # Another comment");

        var entries = ParameterResolver.ParseParameterFile(path);

        Assert.Single(entries);
        Assert.Equal("_Key", entries[0].Key);
    }

    [Fact]
    public void ParseParameterFile_Should_SkipBlankLines_When_LinesAreEmpty()
    {
        var path = WriteTempFile("params.txt", "\n_Key,Value\n\n\n");

        var entries = ParameterResolver.ParseParameterFile(path);

        Assert.Single(entries);
    }

    [Fact]
    public void ParseParameterFile_Should_ReturnEmpty_When_FileDoesNotExist()
    {
        var entries = ParameterResolver.ParseParameterFile(@"C:\nonexistent\file.txt");
        Assert.Empty(entries);
    }

    [Fact]
    public void ParseParameterFile_Should_PreserveMultiValueCommas_When_LineHasMultipleCommas()
    {
        // EmailAddress,,a@b.com,c@d.com => Key=EmailAddress, Value=",a@b.com,c@d.com"
        var path = WriteTempFile("params.txt", "EmailAddress,,a@b.com,c@d.com");

        var entries = ParameterResolver.ParseParameterFile(path);

        Assert.Single(entries);
        Assert.Equal("EmailAddress", entries[0].Key);
        Assert.Equal(",a@b.com,c@d.com", entries[0].Value);
    }

    [Fact]
    public void ParseParameterFile_Should_TrimWhitespace_When_KeyOrValueHasSpaces()
    {
        var path = WriteTempFile("params.txt", "  _Key  ,  Value  ");

        var entries = ParameterResolver.ParseParameterFile(path);

        Assert.Single(entries);
        Assert.Equal("_Key", entries[0].Key);
        Assert.Equal("Value", entries[0].Value);
    }

    // ???????????????????????????????????????????????????????????????????
    // LoadParameterFile
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void LoadParameterFile_Should_PopulateContext_When_FileExists()
    {
        var path = WriteTempFile("params.txt", "_Agent1,ServerA\n_R2SetupPath,C:\\Setup");
        var ctx = new PipelineExecutionContext();

        ParameterResolver.LoadParameterFile(ctx, path);

        Assert.Equal("ServerA", ctx.Parameters["_Agent1"]);
        Assert.Equal("ServerA", ctx.Parameters["Agent1"]); // without underscore
        Assert.Equal("C:\\Setup", ctx.Parameters["_R2SetupPath"]);
        Assert.Equal("C:\\Setup", ctx.Parameters["R2SetupPath"]);
    }

    [Fact]
    public void LoadParameterFile_Should_NotThrow_When_FileDoesNotExist()
    {
        var ctx = new PipelineExecutionContext();
        ParameterResolver.LoadParameterFile(ctx, @"C:\nonexistent\file.txt");
        Assert.Empty(ctx.Parameters);
    }

    [Fact]
    public void LoadParameterFile_Should_StoreUnderscoreAndNonUnderscoreKeys_When_KeyHasUnderscore()
    {
        var path = WriteTempFile("params.txt", "_BuildNumber,99");
        var ctx = new PipelineExecutionContext();

        ParameterResolver.LoadParameterFile(ctx, path);

        Assert.Equal("99", ctx.Parameters["_BuildNumber"]);
        Assert.Equal("99", ctx.Parameters["BuildNumber"]);
    }

    [Fact]
    public void LoadParameterFile_Should_NotDuplicateKey_When_KeyHasNoUnderscore()
    {
        var path = WriteTempFile("params.txt", "NoPrefix,SomeValue");
        var ctx = new PipelineExecutionContext();

        ParameterResolver.LoadParameterFile(ctx, path);

        Assert.Equal("SomeValue", ctx.Parameters["NoPrefix"]);
        Assert.Single(ctx.Parameters); // only one key stored
    }

    // ???????????????????????????????????????????????????????????????????
    // LoadTriggerFile
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void LoadTriggerFile_Should_ParseCsvTriggerFile_When_FileHasCommaFormat()
    {
        var path = WriteTempFile("trigger.txt",
            "_BuildNumber,LKF_main_20260307.1\n_DropLocation,\\\\devtfs\\repl\\drop");
        var ctx = new PipelineExecutionContext();

        ParameterResolver.LoadTriggerFile(ctx, path);

        Assert.Equal("LKF_main_20260307.1", ctx.Parameters["_BuildNumber"]);
        Assert.Equal("LKF_main_20260307.1", ctx.Parameters["BuildNumber"]);
        Assert.Equal(@"\\devtfs\repl\drop", ctx.Parameters["_DropLocation"]);
        Assert.Equal(@"\\devtfs\repl\drop", ctx.Parameters["DropLocation"]);
    }

    [Fact]
    public void LoadTriggerFile_Should_NotThrow_When_FileDoesNotExist()
    {
        var ctx = new PipelineExecutionContext();
        ParameterResolver.LoadTriggerFile(ctx, @"C:\nonexistent\trigger.txt");
        Assert.Empty(ctx.Parameters);
    }

    // Rank-based precedence

    [Fact]
    public void LoadParameterFile_Should_NotOverwrite_When_TriggerFileSuppliedKey()
    {
        var trigger = WriteTempFile("trigger.txt", "_BuildNumber,PINNED");
        var initialize = WriteTempFile("init.txt", "_BuildNumber,FROM_FILE\n_Agent1,ServerA");
        var ctx = new PipelineExecutionContext();

        ParameterResolver.LoadTriggerFile(ctx, trigger);
        ParameterResolver.LoadParameterFile(ctx, initialize);

        Assert.Equal("PINNED", ctx.Parameters["_BuildNumber"]);
        Assert.Equal("PINNED", ctx.Parameters["BuildNumber"]);
        Assert.Equal("ServerA", ctx.Parameters["_Agent1"]);
    }

    [Fact]
    public void LoadParameterFile_Should_Overwrite_When_EarlierSourceHasSameRank()
    {
        var warm = WriteTempFile("warm.txt", "_Agent1,warmgr");
        var sanity = WriteTempFile("sanity.txt", "_Agent1,jvgr1");
        var ctx = new PipelineExecutionContext();

        ParameterResolver.LoadParameterFile(ctx, warm);
        ParameterResolver.LoadParameterFile(ctx, sanity);

        Assert.Equal("jvgr1", ctx.Parameters["_Agent1"]);
    }

    [Fact]
    public void LoadParameterFile_Should_Overwrite_When_GlobalSuppliedKey()
    {
        var global = WriteTempFile("global.txt", "_BuildNumber,GLOBAL");
        var stage = WriteTempFile("stage.txt", "_BuildNumber,STAGE");
        var ctx = new PipelineExecutionContext();

        ParameterResolver.LoadParameterFile(ctx, global, ParameterRank.Global);
        ParameterResolver.LoadParameterFile(ctx, stage);

        Assert.Equal("STAGE", ctx.Parameters["_BuildNumber"]);
    }

    [Fact]
    public void ApplyRunOverrides_Should_Win_When_TriggerFileSuppliedKey()
    {
        var trigger = WriteTempFile("trigger.txt", "_BuildNumber,PINNED");
        var ctx = new PipelineExecutionContext();

        ParameterResolver.LoadTriggerFile(ctx, trigger);
        ParameterResolver.ApplyRunOverrides(
            ctx, new Dictionary<string, string> { ["_BuildNumber"] = "THIS_RUN" });

        Assert.Equal("THIS_RUN", ctx.Parameters["_BuildNumber"]);
        Assert.Equal("THIS_RUN", ctx.Parameters["BuildNumber"]);
    }

    [Fact]
    public void SetParameter_Should_ProtectAlias_When_LowerRankWritesUnprefixedKey()
    {
        var ctx = new PipelineExecutionContext();

        ParameterResolver.SetParameter(ctx, "_BuildNumber", "PINNED", ParameterRank.TriggerFile);
        ParameterResolver.SetParameter(ctx, "BuildNumber", "FROM_FILE", ParameterRank.ParameterFile);

        Assert.Equal("PINNED", ctx.Parameters["BuildNumber"]);
    }

    // Layered JSON config

    private const string LayeredJson = """
        {
          "version": 1,
          "global":    { "_BuildNumber": "GLOBAL", "_OrgName": "AppServerPool2" },
          "profiles":  { "Warm": { "_Agent1": "warmgr" }, "Sanity": { "_Agent1": "jvgr1" } },
          "pipelines": { "Pipe A": { "_BuildNumber": "PINNED" } }
        }
        """;

    [Fact]
    public void LoadJsonConfig_Should_ApplyGlobal_When_PipelineHasNoOverride()
    {
        var path = WriteTempFile("config.json", LayeredJson);
        var ctx = new PipelineExecutionContext();

        ParameterResolver.LoadJsonConfig(ctx, path, profile: null, pipelineTag: "Pipe B");

        Assert.Equal("GLOBAL", ctx.Parameters["_BuildNumber"]);
        Assert.Equal("AppServerPool2", ctx.Parameters["OrgName"]);
    }

    [Fact]
    public void LoadJsonConfig_Should_PreferPipelinePin_When_TagHasOverride()
    {
        var path = WriteTempFile("config.json", LayeredJson);
        var ctx = new PipelineExecutionContext();

        ParameterResolver.LoadJsonConfig(ctx, path, profile: null, pipelineTag: "Pipe A");

        Assert.Equal("PINNED", ctx.Parameters["_BuildNumber"]);
        Assert.Equal("PINNED", ctx.Parameters["BuildNumber"]);
    }

    [Fact]
    public void LoadJsonConfig_Should_ApplyNamedProfile_When_ProfileRequested()
    {
        var path = WriteTempFile("config.json", LayeredJson);
        var ctx = new PipelineExecutionContext();

        ParameterResolver.LoadJsonConfig(ctx, path, profile: "Sanity", pipelineTag: null);

        Assert.Equal("jvgr1", ctx.Parameters["_Agent1"]);
    }

    [Fact]
    public void LoadJsonConfig_Should_KeepPin_When_ParameterFileLoadedAfterwards()
    {
        var path = WriteTempFile("config.json", LayeredJson);
        var stage = WriteTempFile("stage.txt", "_BuildNumber,FROM_TXT");
        var ctx = new PipelineExecutionContext();

        ParameterResolver.LoadJsonConfig(ctx, path, profile: null, pipelineTag: "Pipe A");
        ParameterResolver.LoadParameterFile(ctx, stage);

        Assert.Equal("PINNED", ctx.Parameters["_BuildNumber"]);
    }

    [Fact]
    public void LoadJsonConfig_Should_NotThrow_When_FileDoesNotExist()
    {
        var ctx = new PipelineExecutionContext();
        ParameterResolver.LoadJsonConfig(ctx, @"C:\nonexistent\config.json", null, null);
        Assert.Empty(ctx.Parameters);
    }

    [Fact]
    public void TryLoadJsonConfig_Should_ReturnFalse_When_FileIsNotValidJson()
    {
        var path = WriteTempFile("broken.json", "{ \"global\": { \"_A\": \"1\" } }\n_BuildNumber,APPENDED");

        var ctx = new PipelineExecutionContext();
        var ok = ParameterResolver.TryLoadJsonConfig(ctx, path, null, null);

        Assert.False(ok);
        Assert.Empty(ctx.Parameters);
    }

    [Fact]
    public void FindUnresolvedTokens_Should_ReportToken_When_ContextHasNoValue()
    {
        var ctx = new PipelineExecutionContext();
        ParameterResolver.SetParameter(ctx, "_Agent1", "warmgr", ParameterRank.ParameterFile);

        var action = new ActionConfig
        {
            Command = "cmd /c revert.bat",
            Parameters = "[_Agent1] [_Agent2]",
        };

        var missing = ParameterResolver.FindUnresolvedTokens(action, ctx);

        Assert.Equal(["_Agent2"], missing);
    }

    [Fact]
    public void FindUnresolvedTokens_Should_ReturnEmpty_When_EveryTokenResolves()
    {
        var ctx = new PipelineExecutionContext();
        ParameterResolver.SetParameter(ctx, "_Agent1", "warmgr", ParameterRank.ParameterFile);

        var action = new ActionConfig { Command = "run.bat", Parameters = "[_Agent1]" };

        Assert.Empty(ParameterResolver.FindUnresolvedTokens(action, ctx));
    }

    [Fact]
    public void FindUnresolvedTokens_Should_IgnoreNonExecutableFields_When_BodyHasBracketText()
    {
        var ctx = new PipelineExecutionContext();
        var action = new ActionConfig { Command = "run.bat", Body = "[INFO] build finished" };

        Assert.Empty(ParameterResolver.FindUnresolvedTokens(action, ctx));
    }

    // ???????????????????????????????????????????????????????????????????
    // ResolveAction
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void ResolveAction_Should_ResolveAllFields_When_TokensArePresent()
    {
        var ctx = new PipelineExecutionContext();
        ctx.Parameters["Agent1"] = "Vinodjhist";
        ctx.Parameters["DropLocation"] = @"\\server\drop";
        ctx.Parameters["R2SetupPath"] = @"C:\Setup";
        ctx.Parameters["R2ResponsePath"] = "auto.rsp";

        var action = new ActionConfig
        {
            Type = ActionType.RunRemoteCommand,
            AgentName = "[Agent1]",
            Command = @"[R2SetupPath]\BuildInstall\Install_WSP.bat",
            Parameters = "[DropLocation] [Agent1] [R2SetupPath]\\BuildInstall\\[R2ResponsePath]",
            Timeout = 600000,
            PollInterval = 30,
            FailAndContinue = true,
        };

        var resolved = ParameterResolver.ResolveAction(action, ctx);

        Assert.Equal("Vinodjhist", resolved.AgentName);
        Assert.Equal(@"C:\Setup\BuildInstall\Install_WSP.bat", resolved.Command);
        Assert.Contains(@"\\server\drop", resolved.Parameters);
        Assert.Contains("Vinodjhist", resolved.Parameters);
        Assert.Equal(600000, resolved.Timeout);
        Assert.Equal(30, resolved.PollInterval);
        Assert.True(resolved.FailAndContinue);
    }

    [Fact]
    public void ResolveAction_Should_NotMutateOriginal_When_Resolving()
    {
        var ctx = new PipelineExecutionContext();
        ctx.Parameters["X"] = "resolved";

        var original = new ActionConfig { AgentName = "[X]", Command = "[X]" };
        var resolved = ParameterResolver.ResolveAction(original, ctx);

        Assert.Equal("[X]", original.AgentName);
        Assert.Equal("[X]", original.Command);
        Assert.Equal("resolved", resolved.AgentName);
        Assert.Equal("resolved", resolved.Command);
    }

    // ???????????????????????????????????????????????????????????????????
    // SaveParameterFile
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void SaveParameterFile_Should_WriteCommaDelimited_When_EntriesProvided()
    {
        var path = Path.Combine(_tempDir, "output.txt");
        var entries = new List<(string Key, string Value)>
        {
            ("_BuildNumber", "1.0"),
            ("_DropLocation", @"\\share\drop"),
        };

        ParameterResolver.SaveParameterFile(path, entries);

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal("_BuildNumber,1.0", lines[0]);
        Assert.Equal(@"_DropLocation,\\share\drop", lines[1]);
    }

    [Fact]
    public void SaveParameterFile_Should_RoundTrip_When_SavedThenParsed()
    {
        var path = Path.Combine(_tempDir, "roundtrip.txt");
        var original = new List<(string Key, string Value)>
        {
            ("_Key1", "Value1"),
            ("_Key2", "Value2"),
        };

        ParameterResolver.SaveParameterFile(path, original);
        var parsed = ParameterResolver.ParseParameterFile(path);

        Assert.Equal(original.Count, parsed.Count);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].Key, parsed[i].Key);
            Assert.Equal(original[i].Value, parsed[i].Value);
        }
    }
}

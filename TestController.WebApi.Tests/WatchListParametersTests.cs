using Microsoft.AspNetCore.Mvc;
using TestController.Api.Controllers;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestController.WebApi.Tests;

/// <summary>
/// Unit tests for WatchListController.GetParameters: build-base-path exposure and secret redaction.
/// </summary>
public class WatchListParametersTests : IDisposable
{
    private const string Tag = "Revert 9 Nodes - Install SP2023R2SP2";

    private readonly string _paramFile =
        Path.Combine(Path.GetTempPath(), $"vars_{Guid.NewGuid():N}.txt");

    private readonly string _jsonFile =
        Path.Combine(Path.GetTempPath(), $"cfg_{Guid.NewGuid():N}.json");

    private sealed class FakeVocabularyMonitor : IVocabularyMonitor
    {
#pragma warning disable CS0067 // Raised by the real monitor only; tests set CurrentConfig directly.
        public event Action<WatchListConfig>? ConfigReloaded;
#pragma warning restore CS0067

        public WatchListConfig? CurrentConfig { get; set; }

        public WatchListConfig StartMonitoring(string filePath) => CurrentConfig!;

        public void SuppressNextReload() { }

        public void Dispose() { }
    }

    private WatchListController CreateController(string buildBasePath, params string[] lines)
    {
        File.WriteAllLines(_paramFile, lines);

        var watchItem = new WatchItemConfig
        {
            Tag = Tag,
            BuildBasePath = buildBasePath,
            Events =
            [
                new EventConfig
                {
                    Children = [new InitializeConfig { ParameterFile = _paramFile }],
                },
            ],
        };

        var monitor = new FakeVocabularyMonitor
        {
            CurrentConfig = new WatchListConfig { WatchItems = [watchItem] },
        };

        return new WatchListController(monitor, new ExecutionSessionManager());
    }

    private static Dictionary<string, string> ParametersOf(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = ok.Value!;
        var parameters = payload.GetType().GetProperty("parameters")!.GetValue(payload);
        return Assert.IsType<Dictionary<string, string>>(parameters);
    }

    [Fact]
    public void GetParameters_Should_ExposeBuildBasePath_When_WatchItemDeclaresIt()
    {
        var controller = CreateController(@"\\server\builds\", "_BuildNumber,20260101.1");

        var parameters = ParametersOf(controller.GetParameters(Tag));

        Assert.Equal(@"\\server\builds\", parameters["_BuildBasePath"]);
    }

    [Fact]
    public void GetParameters_Should_RedactValue_When_KeyLooksLikeSecret()
    {
        var controller = CreateController(
            "",
            "_VCloudPassword,hunter2",
            "_ApiToken,abc123",
            "_BuildNumber,20260101.1");

        var parameters = ParametersOf(controller.GetParameters(Tag));

        Assert.DoesNotContain("hunter2", string.Join("|", parameters.Values));
        Assert.DoesNotContain("abc123", string.Join("|", parameters.Values));
        Assert.Equal("20260101.1", parameters["_BuildNumber"]);
    }

    [Fact]
    public void GetParameters_Should_PreferFileValue_When_ParameterFileDeclaresBuildBasePath()
    {
        var controller = CreateController(@"\\attribute\path\", @"_BuildBasePath,\\file\path\");

        var parameters = ParametersOf(controller.GetParameters(Tag));

        Assert.Equal(@"\\file\path\", parameters["_BuildBasePath"]);
    }

    [Fact]
    public void GetParameters_Should_ResolveLayeredConfig_When_ParameterFileIsJson()
    {
        File.WriteAllText(_jsonFile, """
            {
              "version": 1,
              "global":    { "_BuildNumber": "GLOBAL", "_VCloudPassword": "s3cret-value" },
              "profiles":  { "Warm": { "_Agent1": "warmgr" } },
              "pipelines": { "Revert 9 Nodes - Install SP2023R2SP2": { "_BuildNumber": "PINNED" } }
            }
            """);

        var watchItem = new WatchItemConfig
        {
            Tag = Tag,
            Events =
            [
                new EventConfig
                {
                    Children = [new InitializeConfig { ParameterFile = _jsonFile, Profile = "Warm" }],
                },
            ],
        };
        var monitor = new FakeVocabularyMonitor
        {
            CurrentConfig = new WatchListConfig { WatchItems = [watchItem] },
        };
        var controller = new WatchListController(monitor, new ExecutionSessionManager());

        var parameters = ParametersOf(controller.GetParameters(Tag));

        Assert.Equal("PINNED", parameters["_BuildNumber"]);
        Assert.Equal("warmgr", parameters["_Agent1"]);

        // Parsed as CSV the whole JSON line becomes the key, which puts the secret somewhere
        // value-based redaction cannot reach.
        Assert.DoesNotContain("s3cret-value", string.Join("|", parameters.Values));
        Assert.DoesNotContain("s3cret-value", string.Join("|", parameters.Keys));
        Assert.DoesNotContain(parameters.Keys, k => k.Contains("version", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (File.Exists(_paramFile))
            File.Delete(_paramFile);
        if (File.Exists(_jsonFile))
            File.Delete(_jsonFile);
    }
}

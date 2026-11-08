using TestControllerGrpc.Core.PipelineBuilder;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Tests.PipelineBuilder;

public class BuilderRedactionTests
{
    private const string Secret = "red-S3cret-value-9137";

    [Fact]
    public void ConfigJson_Should_MaskSecretKeys_When_AnyLayerHoldsOne()
    {
        var config = new PipelineParameterConfig
        {
            Global = new(StringComparer.OrdinalIgnoreCase) { ["_RcloudPassword"] = Secret, ["_ReleaseName"] = "SP2026R2" },
            Profiles = new(StringComparer.OrdinalIgnoreCase) { ["Sanity"] = new(StringComparer.OrdinalIgnoreCase) { ["_ApiToken"] = Secret } },
            Pipelines = new(StringComparer.OrdinalIgnoreCase) { ["Tag"] = new(StringComparer.OrdinalIgnoreCase) { ["_Pwd"] = Secret } },
        };

        var json = BuilderRedaction.ConfigJson(config);

        Assert.DoesNotContain(Secret, json);
        Assert.Contains("SP2026R2", json);
        Assert.Equal(3, json.Split(BuilderRedaction.Mask).Length - 1);
    }

    [Fact]
    public void ConfigJson_Should_LeaveTheSourceConfigUntouched()
    {
        var config = new PipelineParameterConfig
        {
            Global = new(StringComparer.OrdinalIgnoreCase) { ["_RcloudPassword"] = Secret },
        };

        BuilderRedaction.ConfigJson(config);

        Assert.Equal(Secret, config.Global["_RcloudPassword"]);
    }

    [Fact]
    public void WatchItemXml_Should_MaskALiteralPassword_But_KeepTokenReferences()
    {
        var item = new WatchItemConfig
        {
            Tag = "T",
            Path = @"C:\Triggers",
            Filter = "t.txt",
            Events =
            [
                new EventConfig
                {
                    Type = "Renamed",
                    Children =
                    [
                        new ActionConfig { Tag = "literal", Command = "run.bat", UserName = "svc", Password = Secret },
                        new ActionConfig { Tag = "token", Command = "run.bat", UserName = "svc", Password = "[_RcloudPassword]" },
                    ],
                },
            ],
        };

        var xml = BuilderRedaction.WatchItemXml(item);

        Assert.DoesNotContain(Secret, xml);
        Assert.Contains(BuilderRedaction.Mask, xml);
        Assert.Contains("[_RcloudPassword]", xml);
        Assert.Contains("run.bat", xml);
    }

    [Theory]
    [InlineData("_VCloudPassword", "x", "***")]
    [InlineData("_ReleaseName", "SP2026R2", "SP2026R2")]
    [InlineData("_RcloudPassword", "", "")]
    public void Value_Should_UseTheParameterGridSecretList(string key, string value, string expected)
    {
        Assert.Equal(expected, BuilderRedaction.Value(key, value));
    }
}

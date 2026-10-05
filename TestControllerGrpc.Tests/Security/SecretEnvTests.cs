using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Security;

/// <summary>
/// Credentials used to travel as positional arguments to RevertRcloudMachine.bat, which put them in the
/// process table on the controller - somewhere no amount of log redaction can reach. SecretEnv moves them
/// into the child's environment block instead.
/// </summary>
[Collection("TokenState")]
public class SecretEnvTests : IDisposable
{
    private const string Secret = "hunter2xyz";

    public SecretEnvTests() => SecurityRedactor.ClearSecretValues();

    public void Dispose()
    {
        SecurityRedactor.ClearSecretValues();
        GC.SuppressFinalize(this);
    }

    // ── parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Should_ReadNameValuePairs_When_SeparatedBySemicolons()
    {
        var pairs = SecretEnvironment.Parse("RCLOUD_USER=alice;RCLOUD_PASSWORD=s3cret");

        Assert.Collection(pairs,
            p => { Assert.Equal("RCLOUD_USER", p.Key); Assert.Equal("alice", p.Value); },
            p => { Assert.Equal("RCLOUD_PASSWORD", p.Key); Assert.Equal("s3cret", p.Value); });
    }

    [Fact]
    public void Parse_Should_SplitOnTheFirstEqualsOnly_When_TheValueContainsOne()
    {
        // A generated password routinely contains '=' (base64 padding).
        var pairs = SecretEnvironment.Parse("RCLOUD_PASSWORD=ab=cd==");

        Assert.Equal("ab=cd==", Assert.Single(pairs).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("NoEqualsSign")]
    [InlineData("=novalue")]
    [InlineData(";;;")]
    public void Parse_Should_ReturnNothing_When_TheSpecIsEmptyOrMalformed(string? spec)
    {
        // A typo must surface as the script's own "credentials are not set", not as a pipeline crash.
        Assert.Empty(SecretEnvironment.Parse(spec));
    }

    [Fact]
    public void Parse_Should_KeepAnEmptyValue_When_TheNameIsPresent()
    {
        var pairs = SecretEnvironment.Parse("RCLOUD_ORG=");

        Assert.Equal("RCLOUD_ORG", Assert.Single(pairs).Key);
        Assert.Equal("", pairs[0].Value);
    }

    // ── applying ────────────────────────────────────────────────────────

    [Fact]
    public void Apply_Should_PutValuesInTheEnvironment_And_NeverInTheArguments()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd",
            Arguments = @"/c C:\TestSetup\RevertAgents\RevertRcloudMachine.bat AppServerPool2 jvgr1",
        };

        SecretEnvironment.Apply(psi, $"RCLOUD_USER=alice;RCLOUD_PASSWORD={Secret}");

        Assert.Equal(Secret, psi.Environment["RCLOUD_PASSWORD"]);
        Assert.Equal("alice", psi.Environment["RCLOUD_USER"]);
        Assert.DoesNotContain(Secret, psi.Arguments);
        Assert.DoesNotContain(Secret, psi.FileName);
    }

    [Fact]
    public void Apply_Should_RegisterTheValueForRedaction_When_ItIsApplieded()
    {
        var psi = new System.Diagnostics.ProcessStartInfo();

        SecretEnvironment.Apply(psi, $"RCLOUD_PASSWORD={Secret}");

        // A script that dumps its own environment still must not print the secret into our log.
        Assert.DoesNotContain(Secret, SecurityRedactor.Redact($"env dump RCLOUD_PASSWORD={Secret}")!);
    }

    [Fact]
    public void DescribeNames_Should_ListNamesOnly_When_Logging()
    {
        var described = SecretEnvironment.DescribeNames($"RCLOUD_USER=alice;RCLOUD_PASSWORD={Secret}");

        Assert.Equal("RCLOUD_USER, RCLOUD_PASSWORD", described);
        Assert.DoesNotContain(Secret, described);
        Assert.DoesNotContain("alice", described);
    }

    // ── end to end through the action pipeline ──────────────────────────

    private static PipelineExecutionContext ContextWithCredentials()
    {
        var ctx = new PipelineExecutionContext { WatchItemTag = "Sanity" };
        ParameterResolver.SetParameter(ctx, "_RcloudUser", "alice", ParameterRank.Global);
        ParameterResolver.SetParameter(ctx, "_RcloudPassword", Secret, ParameterRank.Global);
        ParameterResolver.SetParameter(ctx, "_Agent1", "jvgr1", ParameterRank.Global);
        return ctx;
    }

    [Fact]
    public void ResolveAction_Should_SubstituteTokensInSecretEnv_When_TheActionIsResolved()
    {
        var action = new ActionConfig
        {
            Type = ActionType.RunCommand,
            Command = "cmd",
            Parameters = @"/c C:\TestSetup\RevertAgents\RevertRcloudMachine.bat AppServerPool2 [_Agent1]",
            SecretEnv = "RCLOUD_USER=[_RcloudUser];RCLOUD_PASSWORD=[_RcloudPassword]",
        };

        var resolved = ParameterResolver.ResolveAction(action, ContextWithCredentials());

        Assert.Equal($"RCLOUD_USER=alice;RCLOUD_PASSWORD={Secret}", resolved.SecretEnv);
        // The whole point: the resolved command line carries the VM but no credential.
        Assert.Contains("jvgr1", resolved.Parameters);
        Assert.DoesNotContain(Secret, resolved.Parameters);
        Assert.DoesNotContain("alice", resolved.Parameters);
    }

    [Fact]
    public void Action_Should_KeepTheSecretOffTheCommandLine_When_ExecutedLocally()
    {
        var action = new ActionConfig
        {
            Type = ActionType.RunCommand,
            Command = "cmd",
            Parameters = @"/c C:\TestSetup\RevertAgents\RevertRcloudMachine.bat AppServerPool2 [_Agent1]",
            SecretEnv = "RCLOUD_USER=[_RcloudUser];RCLOUD_PASSWORD=[_RcloudPassword]",
        };

        var resolved = ParameterResolver.ResolveAction(action, ContextWithCredentials());
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = resolved.Command,
            Arguments = resolved.Parameters,
        };
        SecretEnvironment.Apply(psi, resolved.SecretEnv);

        var commandLine = $"{psi.FileName} {psi.Arguments}";
        Assert.DoesNotContain(Secret, commandLine);
        Assert.Equal(Secret, psi.Environment["RCLOUD_PASSWORD"]);
    }

    // ── persistence ─────────────────────────────────────────────────────

    [Fact]
    public void Parser_Should_RoundTripSecretEnv_When_TheWatchListIsSaved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wl-{Guid.NewGuid():N}.xml");
        var config = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig
                {
                    Tag = "Sanity",
                    Path = @"C:\Triggers",
                    Events =
                    [
                        new EventConfig
                        {
                            Type = "Renamed",
                            Children =
                            [
                                new ActionConfig
                                {
                                    Tag = "Revert",
                                    Type = ActionType.RunCommand,
                                    Command = "cmd",
                                    SecretEnv = "RCLOUD_USER=[_RcloudUser];RCLOUD_PASSWORD=[_RcloudPassword]",
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        try
        {
            WatchListXmlParser.Save(config, path);
            var reloaded = WatchListXmlParser.Load(path);

            var action = (ActionConfig)reloaded.WatchItems[0].Events[0].Children[0];
            Assert.Equal("RCLOUD_USER=[_RcloudUser];RCLOUD_PASSWORD=[_RcloudPassword]", action.SecretEnv);

            // Tokens must persist UNRESOLVED - saving a resolved secret would write it to disk.
            Assert.DoesNotContain(Secret, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parser_Should_OmitSecretEnv_When_TheActionDoesNotUseIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wl-{Guid.NewGuid():N}.xml");
        var config = new WatchListConfig
        {
            WatchItems =
            [
                new WatchItemConfig
                {
                    Tag = "Sanity", Path = @"C:\Triggers",
                    Events = [new EventConfig { Type = "Renamed",
                        Children = [new ActionConfig { Tag = "Plain", Type = ActionType.RunCommand, Command = "cmd" }] }],
                },
            ],
        };

        try
        {
            WatchListXmlParser.Save(config, path);
            // Clean files must round-trip byte-identically; an empty attribute would be noise in every diff.
            Assert.DoesNotContain("SecretEnv", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

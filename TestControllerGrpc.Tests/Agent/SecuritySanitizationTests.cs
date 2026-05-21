extern alias AgentAlias;
using AgentAlias::TestAgentGrpc;
using AgentAlias::TestAgentGrpc.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace TestControllerGrpc.Tests.Agent;

public sealed class SecuritySanitizationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"AuditSecurity_{Guid.NewGuid():N}");

    public SecuritySanitizationTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("password=SuperSecret123")]
    [InlineData("--token abcdefghijklmnop")]
    [InlineData("-Secret topsecret")]
    [InlineData("Server=x;Password=DbSecret;Database=y")]
    [InlineData("{\"password\":\"JsonSecret\"}")]
    public void SecurityRedactor_Redacts_CommonSecretPatterns(string input)
    {
        var redacted = SecurityRedactor.Redact(input)!;

        Assert.Contains(SecurityRedactor.Redacted, redacted);
        Assert.DoesNotContain("SuperSecret123", redacted);
        Assert.DoesNotContain("abcdefghijklmnop", redacted);
        Assert.DoesNotContain("topsecret", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DbSecret", redacted);
        Assert.DoesNotContain("JsonSecret", redacted);
    }

    [Fact]
    public void CommandPolicy_AuditOnly_FlagsSensitiveCommandLine_ButDoesNotEnforce()
    {
        var evaluator = new CommandPolicyEvaluator(new CommandPolicySettings { Mode = "AuditOnly" });

        var result = evaluator.Evaluate("install.bat", "password=DontLogMe");

        Assert.False(result.IsAllowed);
        Assert.True(evaluator.IsAuditOnly);
        Assert.False(evaluator.IsEnforced);
    }

    [Fact]
    public void CommandPolicy_Enforce_DeniesShellChaining()
    {
        var evaluator = new CommandPolicyEvaluator(new CommandPolicySettings { Mode = "Enforce" });

        var result = evaluator.Evaluate("install.bat", "ok && whoami");

        Assert.False(result.IsAllowed);
        Assert.Contains("shell", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(evaluator.IsEnforced);
    }

    [Fact]
    public async Task AuditLogger_RedactsSensitiveFields_BeforePersisting()
    {
        var settings = Options.Create(new AuditSettings
        {
            Enabled = true,
            LogDirectory = _tempDir,
            RetentionDays = 7,
            MaxFileSizeMb = 10
        });
        var logger = new AuditLogger(settings, NullLogger<AuditLogger>.Instance);

        await logger.StartAsync(CancellationToken.None);
        logger.Log("CommandStarted",
            command: "install.bat",
            arguments: "password=DontPersist token=TokenSecret",
            credentials: "DOMAIN\\user",
            detail: "connection Password=AnotherSecret; done");
        await logger.StopAsync(CancellationToken.None);

        var entries = logger.ReadEntries(null, null, null, maxEntries: 10);
        var entry = Assert.Single(entries);

        Assert.Equal(SecurityRedactor.Redacted, entry.Credentials);
        Assert.Contains(SecurityRedactor.Redacted, entry.Arguments);
        Assert.Contains(SecurityRedactor.Redacted, entry.Detail);
        Assert.DoesNotContain("DontPersist", entry.Arguments);
        Assert.DoesNotContain("TokenSecret", entry.Arguments);
        Assert.DoesNotContain("AnotherSecret", entry.Detail);
    }
}

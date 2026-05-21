using Microsoft.Extensions.Logging;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Security;

public sealed class ControllerRedactionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ControllerRedaction_{Guid.NewGuid():N}");

    public ControllerRedactionTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ControllerSecurityRedactor_Redacts_CommonSecretPatterns()
    {
        var input = "deploy.ps1 -Password MySecret token=TokenSecret Server=x;Pwd=DbSecret";

        var redacted = SecurityRedactor.Redact(input)!;

        Assert.Contains(SecurityRedactor.Redacted, redacted);
        Assert.DoesNotContain("MySecret", redacted);
        Assert.DoesNotContain("TokenSecret", redacted);
        Assert.DoesNotContain("DbSecret", redacted);
    }

    [Fact]
    public void AppLogger_Redacts_Message_And_Exception_BeforeBufferAndFile()
    {
        using var logger = new AppLogger("controller-test", _tempDir);

        logger.Log(LogLevel.Error,
            "Security",
            "Run command password=DontPersist",
            new InvalidOperationException("token=ExceptionSecret"));

        var entry = Assert.Single(logger.GetRecentEntries(10));
        Assert.Contains(SecurityRedactor.Redacted, entry.Message);
        Assert.Contains(SecurityRedactor.Redacted, entry.Exception);
        Assert.DoesNotContain("DontPersist", entry.Message);
        Assert.DoesNotContain("ExceptionSecret", entry.Exception);
    }

    [Fact]
    public async Task ExecutionSessionManager_Redacts_NodeProgress_Command_And_Error()
    {
        var events = new EventAggregator();
        var manager = new ExecutionSessionManager(events);
        var seen = new TaskCompletionSource<NodeProgressEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = events.Subscribe<NodeProgressEvent>(e =>
        {
            if (e.Status == "Failed")
                seen.TrySetResult(e);
        });

        var session = manager.BeginSession("Watch", "Event", [], []);
        manager.RecordResult(session.SessionId, new ActionExecutionResult
        {
            ActionTag = "Deploy",
            ActionType = "RunRemoteCommand",
            AgentName = "Agent1",
            Command = "deploy.ps1 -Password CommandSecret",
            Outcome = ActionOutcome.Failed,
            ErrorMessage = "failed with token=ErrorSecret"
        });

        var completed = await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(SecurityRedactor.Redacted, completed.Command);
        Assert.Contains(SecurityRedactor.Redacted, completed.ErrorMessage);
        Assert.DoesNotContain("CommandSecret", completed.Command);
        Assert.DoesNotContain("ErrorSecret", completed.ErrorMessage);
    }
}

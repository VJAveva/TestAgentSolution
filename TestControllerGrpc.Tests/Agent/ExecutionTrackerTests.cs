extern alias AgentAlias;
using Microsoft.Extensions.Options;
using Moq;
using AgentAlias::TestAgentGrpc;
using AgentAlias::TestAgentGrpc.Services;

namespace TestControllerGrpc.Tests.Agent;

public class ExecutionTrackerTests
{
    private static ExecutionTracker CreateTracker(int maxHistory = 200, int maxLines = 5000)
    {
        var settings = Options.Create(new AgentSettings
        {
            MaxExecutionHistoryCount = maxHistory,
            MaxOutputLinesPerExecution = maxLines,
        });
        return new ExecutionTracker(settings);
    }

    // ???????????????????????????????????????????????????????????????????
    // Begin
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void Begin_Should_CreateBuilder_When_Called()
    {
        var tracker = CreateTracker();

        var builder = tracker.Begin("exec1", "cmd.exe", "/c echo hello");

        Assert.NotNull(builder);
    }

    [Fact]
    public void Begin_Should_SetCurrent_When_BuilderCreated()
    {
        var tracker = CreateTracker();

        tracker.Begin("exec1", "cmd.exe", "/c echo");

        Assert.NotNull(tracker.GetCurrent());
    }

    // ???????????????????????????????????????????????????????????????????
    // BeginTracked + Complete
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void BeginTracked_Should_ArchiveOnComplete_When_ExitCodeZero()
    {
        var tracker = CreateTracker();

        var builder = tracker.BeginTracked("exec1", "cmd.exe", "/c echo");
        builder.Complete(0);

        Assert.Null(tracker.GetCurrent());
        Assert.Equal(1, tracker.CompletedCount);
        Assert.Equal(0, tracker.FailedCount);
    }

    [Fact]
    public void BeginTracked_Should_IncrementFailedCount_When_ExitCodeNonZero()
    {
        var tracker = CreateTracker();

        var builder = tracker.BeginTracked("exec1", "cmd.exe", "/c bad");
        builder.Complete(1);

        Assert.Equal(0, tracker.CompletedCount);
        Assert.Equal(1, tracker.FailedCount);
    }

    [Fact]
    public void BeginTracked_Should_IncrementFailedCount_When_FailCalled()
    {
        var tracker = CreateTracker();

        var builder = tracker.BeginTracked("exec1", "cmd.exe", "/c bad");
        builder.Fail("Something went wrong");

        Assert.Equal(0, tracker.CompletedCount);
        Assert.Equal(1, tracker.FailedCount);
    }

    [Fact]
    public void BeginTracked_Should_IncrementFailedCount_When_TerminateCalled()
    {
        var tracker = CreateTracker();

        var builder = tracker.BeginTracked("exec1", "cmd.exe", "/c hang");
        builder.Terminate();

        Assert.Equal(0, tracker.CompletedCount);
        Assert.Equal(1, tracker.FailedCount);
    }

    // ???????????????????????????????????????????????????????????????????
    // GetHistory
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void GetHistory_Should_ReturnCompletedRecords_When_RecordsExist()
    {
        var tracker = CreateTracker();

        var b1 = tracker.BeginTracked("e1", "cmd.exe", "arg1");
        b1.Complete(0);
        var b2 = tracker.BeginTracked("e2", "install.bat", "arg2");
        b2.Complete(0);

        var history = tracker.GetHistory();

        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void GetHistory_Should_RespectMaxLimit_When_MaxSpecified()
    {
        var tracker = CreateTracker();

        for (int i = 0; i < 10; i++)
        {
            var b = tracker.BeginTracked($"e{i}", "cmd.exe", $"arg{i}");
            b.Complete(0);
        }

        var history = tracker.GetHistory(max: 3);
        Assert.Equal(3, history.Count);
    }

    [Fact]
    public void GetHistory_Should_FilterByCommand_When_FilterSpecified()
    {
        var tracker = CreateTracker();

        var b1 = tracker.BeginTracked("e1", "cmd.exe", "");
        b1.Complete(0);
        var b2 = tracker.BeginTracked("e2", "install.bat", "");
        b2.Complete(0);

        var filtered = tracker.GetHistory(filterCommand: "install");
        Assert.Single(filtered);
    }

    [Fact]
    public void GetHistory_Should_ReturnEmpty_When_NoRecords()
    {
        var tracker = CreateTracker();
        var history = tracker.GetHistory();
        Assert.Empty(history);
    }

    [Fact]
    public void GetHistory_Should_TrimOldRecords_When_MaxHistoryExceeded()
    {
        var tracker = CreateTracker(maxHistory: 3);

        for (int i = 0; i < 5; i++)
        {
            var b = tracker.BeginTracked($"e{i}", "cmd.exe", $"arg{i}");
            b.Complete(0);
        }

        var history = tracker.GetHistory();
        Assert.Equal(3, history.Count);
    }

    // ???????????????????????????????????????????????????????????????????
    // Output line capping
    // ???????????????????????????????????????????????????????????????????

    [Fact]
    public void AddOutputLine_Should_CapAtMaxLines_When_TooManyLines()
    {
        var tracker = CreateTracker(maxLines: 5);

        var builder = tracker.BeginTracked("e1", "cmd.exe", "");
        for (int i = 0; i < 20; i++)
            builder.AddOutputLine(AgentAlias::TestAgentGrpc.OutputKind.OutputStdout, $"line {i}"); // OutputKind.OutputStdout = 0
        builder.Complete(0);

        var history = tracker.GetHistory();
        var record = history.First();
        Assert.Equal(5, record.StdoutLines.Count);
    }
}

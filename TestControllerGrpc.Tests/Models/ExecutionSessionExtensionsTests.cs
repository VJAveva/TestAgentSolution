using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Models;

/// <summary>
/// Tests for ExecutionSession log buffer (AddLogEntry / GetRecentLogs),
/// and the new UserId/Source/LockedAgents fields.
/// </summary>
public class ExecutionSessionExtensionsTests
{
    // ?????????????????????????????????????????????????????????????????
    // Log Buffer — AddLogEntry / GetRecentLogs
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void GetRecentLogs_Should_ReturnEmpty_When_NoEntriesAdded()
    {
        var session = new ExecutionSession();

        var logs = session.GetRecentLogs(100);

        Assert.Empty(logs);
    }

    [Fact]
    public void GetRecentLogs_Should_ReturnEntries_When_Added()
    {
        var session = new ExecutionSession();
        session.AddLogEntry(new PipelineLogEntry(DateTime.Now, "Cat", "msg1"));
        session.AddLogEntry(new PipelineLogEntry(DateTime.Now, "Cat", "msg2"));

        var logs = session.GetRecentLogs(10);

        Assert.Equal(2, logs.Count);
        Assert.Equal("msg1", logs[0].Message);
        Assert.Equal("msg2", logs[1].Message);
    }

    [Fact]
    public void GetRecentLogs_Should_LimitToCount_When_MoreExist()
    {
        var session = new ExecutionSession();
        for (int i = 0; i < 20; i++)
            session.AddLogEntry(new PipelineLogEntry(DateTime.Now, "Cat", $"msg{i}"));

        var logs = session.GetRecentLogs(5);

        Assert.Equal(5, logs.Count);
        // Should be the LAST 5
        Assert.Equal("msg15", logs[0].Message);
        Assert.Equal("msg19", logs[4].Message);
    }

    [Fact]
    public void GetRecentLogs_Should_ReturnAll_When_CountIsZero()
    {
        var session = new ExecutionSession();
        for (int i = 0; i < 10; i++)
            session.AddLogEntry(new PipelineLogEntry(DateTime.Now, "Cat", $"msg{i}"));

        var logs = session.GetRecentLogs(0);

        Assert.Equal(10, logs.Count);
    }

    [Fact]
    public void AddLogEntry_Should_TrimBuffer_When_ExceedsMax()
    {
        var session = new ExecutionSession();

        // Add 510 entries — buffer max is 500, trims to 400 when exceeded
        for (int i = 0; i < 510; i++)
            session.AddLogEntry(new PipelineLogEntry(DateTime.Now, "Cat", $"msg{i}"));

        var logs = session.GetRecentLogs(0);

        // After 500 entries, trim removes 100, then adds more
        // 500 ? trim to 400 ? then 10 more added = 410
        Assert.True(logs.Count <= 500);
        Assert.True(logs.Count > 0);
    }

    [Fact]
    public async Task GetRecentLogs_Should_ReturnSnapshot_When_CalledConcurrently()
    {
        var session = new ExecutionSession();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var writer = Task.Run(() =>
        {
            int i = 0;
            while (!cts.IsCancellationRequested)
                session.AddLogEntry(new PipelineLogEntry(DateTime.Now, "Cat", $"msg{i++}"));
        });

        var reader = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var logs = session.GetRecentLogs(10);
                Assert.True(logs.Count <= 10);
            }
        });

        cts.CancelAfter(1000);
        await Task.WhenAll([writer, reader]);
        // No exceptions = thread-safe
    }

    // ?????????????????????????????????????????????????????????????????
    // New session fields: UserId, Source, LockedAgents
    // ?????????????????????????????????????????????????????????????????

    [Fact]
    public void ExecutionSession_Should_HaveEmptyDefaults_When_Created()
    {
        var session = new ExecutionSession();

        Assert.Equal("", session.UserId);
        Assert.Equal("", session.Source);
        Assert.Empty(session.LockedAgents);
    }

    [Fact]
    public void ExecutionSession_Should_StoreUserInfo_When_Set()
    {
        var session = new ExecutionSession
        {
            UserId = "Vinod",
            Source = "WebClient",
            LockedAgents = ["agent1", "agent2"],
        };

        Assert.Equal("Vinod", session.UserId);
        Assert.Equal("WebClient", session.Source);
        Assert.Equal(2, session.LockedAgents.Length);
    }
}

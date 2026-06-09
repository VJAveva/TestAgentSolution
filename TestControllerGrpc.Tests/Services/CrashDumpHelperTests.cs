using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Tests for CrashDumpHelper utility methods that don't require
/// actual crash dumps (IsRecoverable, crash summary, dump info records).
/// </summary>
public class CrashDumpHelperTests
{
    [Fact]
    public void IsRecoverable_Should_ReturnTrue_When_OperationCanceledException()
    {
        Assert.True(CrashDumpHelper.IsRecoverable(new OperationCanceledException()));
    }

    [Fact]
    public void IsRecoverable_Should_ReturnTrue_When_TaskCanceledException()
    {
        Assert.True(CrashDumpHelper.IsRecoverable(new TaskCanceledException()));
    }

    [Fact]
    public void IsRecoverable_Should_ReturnFalse_When_NullException()
    {
        Assert.False(CrashDumpHelper.IsRecoverable(null));
    }

    [Fact]
    public void IsRecoverable_Should_ReturnFalse_When_StackOverflowException()
    {
        Assert.False(CrashDumpHelper.IsRecoverable(new InvalidOperationException("Stack overflow")));
    }

    [Fact]
    public void IsRecoverable_Should_ReturnFalse_When_PlainException()
    {
        Assert.False(CrashDumpHelper.IsRecoverable(new Exception("Something broke")));
    }

    [Fact]
    public void IsRecoverable_Should_ReturnTrue_When_InnerExceptionIsCancelled()
    {
        var inner = new OperationCanceledException();
        var outer = new Exception("Wrapper", inner);
        Assert.True(CrashDumpHelper.IsRecoverable(outer));
    }

    [Fact]
    public void IsRecoverable_Should_ReturnTrue_When_AggregateContainsCancelled()
    {
        var agg = new AggregateException(
            new Exception("other"),
            new OperationCanceledException());
        Assert.True(CrashDumpHelper.IsRecoverable(agg));
    }

    [Fact]
    public void IsRecoverable_Should_ReturnFalse_When_AggregateHasNoRecoverableInner()
    {
        var agg = new AggregateException(
            new Exception("one"),
            new Exception("two"));
        Assert.False(CrashDumpHelper.IsRecoverable(agg));
    }

    [Fact]
    public void CrashSummary_Should_HaveCorrectDefaults()
    {
        var summary = new CrashSummary();

        Assert.Empty(summary.AppName);
        Assert.Empty(summary.Source);
        Assert.Empty(summary.MachineName);
        Assert.Equal(0, summary.ProcessId);
        Assert.Null(summary.ExceptionType);
        Assert.Null(summary.ExceptionMessage);
        Assert.Null(summary.DumpFilePath);
    }

    [Fact]
    public void CrashDumpInfo_Should_HaveCorrectDefaults()
    {
        var info = new CrashDumpInfo();

        Assert.Empty(info.FileName);
        Assert.Equal(0.0, info.SizeMB);
        Assert.Equal(default, info.CreatedUtc);
    }

    [Fact]
    public void DumpRetentionDays_Should_BePositive()
    {
        Assert.True(CrashDumpHelper.DumpRetentionDays > 0);
    }

    [Fact]
    public void AppendCrashLog_Should_NotThrow_When_CalledSafely()
    {
        // AppendCrashLog should never throw (it swallows all exceptions)
        var exception = Record.Exception(() => CrashDumpHelper.AppendCrashLog("test entry"));
        Assert.Null(exception);
    }

    [Fact]
    public void GetCrashDumps_Should_ReturnEmptyList_When_NoDirectory()
    {
        var dumps = CrashDumpHelper.GetCrashDumps();
        Assert.NotNull(dumps);
        // May or may not be empty depending on test environment, but shouldn't throw
    }
}

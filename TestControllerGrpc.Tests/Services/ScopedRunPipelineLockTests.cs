using System.Text.RegularExpressions;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Guards that every WPF scoped-run command takes the single-run pipeline lock.
/// </summary>
/// <remarks>
/// The WebClient's node-run endpoint acquires this lock, so a WPF command that skips it lets the two
/// hosts run the same pipeline at once — a defect no unit test on either side alone would surface.
/// File analysis rather than a view-model test: exercising these commands needs an STA WPF app,
/// DI container and live agents, which is exactly the kind of test that flakes instead of failing.
/// </remarks>
public class ScopedRunPipelineLockTests
{
    /// <summary>Commands that run part of a pipeline and therefore occupy it for the duration.</summary>
    private static readonly string[] ScopedRunCommands =
        ["ExecuteGroup", "ExecuteTemplate", "ExecuteSingleAction"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ExecutionSource() => File.ReadAllText(Path.Combine(
        RepoRoot(), "TestControllerGrpc", "ViewModels", "MainViewModel.Execution.cs"));

    /// <summary>Body of one private async command method, from its signature to the next one.</summary>
    private static string MethodBody(string source, string name)
    {
        var start = source.IndexOf($"private async Task {name}()", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Command '{name}' was renamed or removed; update this guard deliberately.");

        var next = source.IndexOf("    private ", start + 1, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    [Theory]
    [InlineData("ExecuteGroup")]
    [InlineData("ExecuteTemplate")]
    [InlineData("ExecuteSingleAction")]
    public void ScopedRunCommand_Should_AcquireThePipelineLock_When_Started(string command)
    {
        var body = MethodBody(ExecutionSource(), command);

        Assert.Contains("AcquirePipelineLock(", body);
        Assert.Matches(new Regex(@"if\s*\(\s*pipelineToken\s+is\s+null\s*\)\s*return", RegexOptions.Singleline), body);
    }

    [Theory]
    [InlineData("ExecuteGroup")]
    [InlineData("ExecuteTemplate")]
    [InlineData("ExecuteSingleAction")]
    public void ScopedRunCommand_Should_ReleaseThePipelineLock_When_Finished(string command)
    {
        var body = MethodBody(ExecutionSource(), command);

        // Without the release the pipeline stays locked until the TTL sweeper reaps it.
        Assert.Contains("_lockRegistry.TryRelease(", body);
    }

    [Theory]
    [InlineData("ExecuteGroup")]
    [InlineData("ExecuteTemplate")]
    [InlineData("ExecuteSingleAction")]
    public void ScopedRunCommand_Should_RenewThePipelineLock_When_RunIsLong(string command)
    {
        var body = MethodBody(ExecutionSource(), command);

        // A node-run can outlast the lock TTL; without renewal the sweeper frees it mid-run.
        Assert.Contains("LockRenewalTimer(", body);
    }

    [Theory]
    [InlineData("ExecuteGroup")]
    [InlineData("ExecuteTemplate")]
    [InlineData("ExecuteSingleAction")]
    public void ScopedRunCommand_Should_FreeThePipelineLock_When_AgentLocksAreUnavailable(string command)
    {
        var body = MethodBody(ExecutionSource(), command);

        // The early return after an agent-lock conflict must not strand the pipeline lock.
        var conflictBranch = body[..body.IndexOf("AgentLocksChangedEvent", StringComparison.Ordinal)];
        Assert.Contains("_lockRegistry.TryRelease(", conflictBranch);
    }

    [Fact]
    public void ScopedRunCommands_Should_PassTheTokenToTheMaintenanceGate_When_Blocked()
    {
        var source = ExecutionSource();

        foreach (var command in ScopedRunCommands)
        {
            var body = MethodBody(source, command);

            // Passing null here was the original defect: a maintenance block returned without
            // releasing the lock the command had just taken.
            Assert.DoesNotContain("IsBlockedByMaintenance(tag, requiredAgents, null,", body);
            Assert.Contains("IsBlockedByMaintenance(tag, requiredAgents, pipelineToken,", body);
        }
    }

    [Theory]
    [InlineData("ExecuteGroup")]
    [InlineData("ExecuteSingleAction")]
    public void ScopedRunCommand_Should_AuthorizeBeforeLocking_When_NodeBelongsToAPipeline(string command)
    {
        var body = MethodBody(ExecutionSource(), command);

        var guard = body.IndexOf("_pipelineGuard.AuthorizeAsync(", StringComparison.Ordinal);
        Assert.True(guard >= 0,
            $"'{command}' dispatches real work to real agents but never authorizes the caller.");
        Assert.Contains("PipelineAuthorizationDeniedException", body);

        // Authorizing after the lock would leave a denied caller holding the pipeline.
        var acquire = body.IndexOf("AcquirePipelineLock(", StringComparison.Ordinal);
        Assert.True(guard < acquire, $"'{command}' authorizes after taking the pipeline lock.");

        // Scoped to the owning WatchItem, and skipped when there is none: AuthorizationService has no
        // unscoped Pipeline_Trigger rule, so passing null would deny every Engineer outright.
        Assert.Contains("var pipelineTag = FindWatchItemTag(node);", body);
        Assert.Contains("if (pipelineTag is not null)", body);
    }

    /// <summary>
    /// A Template has no owning pipeline, so there is no assignment to authorize against. Gating it
    /// on the unscoped permission would DENY Engineers (AuthorizationService falls through to
    /// "Engineers cannot perform Pipeline_Trigger") while CapabilityChecker would still light the
    /// button — an enabled control that fails on click. That is a policy decision, not a bug fix.
    /// </summary>
    [Fact]
    public void ExecuteTemplate_Should_NotAuthorizeOnAnUnscopedPermission_When_NoPipelineOwnsIt()
    {
        var body = MethodBody(ExecutionSource(), "ExecuteTemplate");

        Assert.DoesNotContain("_pipelineGuard.AuthorizeAsync(GetCurrentUserContext(), Permission.Pipeline_Trigger)", body);
    }

    [Theory]
    [InlineData("CanExecuteGroup")]
    [InlineData("CanExecuteSingleAction")]
    public void ScopedRunCanExecute_Should_ReflectLockAndPermission_When_Evaluated(string property)
    {
        var body = CanExecuteBody(property);

        // Otherwise the button stays lit on a locked or unauthorized pipeline and only fails on click.
        Assert.Contains("_lockStateService.HasActiveLock(", body);
        Assert.Contains("_capabilityChecker.Can(Permission.Pipeline_Trigger", body);

        // Must agree with the command's own gate, or the button lies in the Templates tree.
        Assert.Contains("pipelineTag is null ||", body);
    }

    [Fact]
    public void CanExecuteTemplate_Should_ReflectLockOnly_When_Evaluated()
    {
        var body = CanExecuteBody("CanExecuteTemplate");

        Assert.Contains("_lockStateService.HasActiveLock(", body);
        Assert.DoesNotContain("_capabilityChecker.Can(", body);
    }

    private static string CanExecuteBody(string property)
    {
        var source = ExecutionSource();
        var start = source.IndexOf($"private bool {property}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{property}' was renamed; update this guard deliberately.");

        var next = source.IndexOf("    /// <summary>", start, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }
}

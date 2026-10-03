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

        // Scoped to the owning WatchItem, or to the pipeline a Templates-library run borrows from.
        // Skipped when there is neither: AuthorizationService has no unscoped Pipeline_Trigger rule,
        // so passing null would deny every Engineer outright.
        Assert.Contains("var pipelineTag = FindWatchItemTag(node) ?? borrowed;", body);
        Assert.Contains("if (pipelineTag is not null)", body);
    }

    /// <summary>
    /// A Templates-library node has no parameters of its own, so it must borrow a pipeline's before
    /// anything is locked or dispatched. Running one without that choice is what produced a run that
    /// failed on every token while the tree previewed resolved values.
    /// </summary>
    [Theory]
    [InlineData("ExecuteGroup")]
    [InlineData("ExecuteSingleAction")]
    public void ScopedRunCommand_Should_PickAPipeline_When_NodeIsInTheTemplatesLibrary(string command)
    {
        var body = MethodBody(ExecutionSource(), command);

        var pick = body.IndexOf("TryPickTemplatePipeline(", StringComparison.Ordinal);
        Assert.True(pick >= 0,
            $"'{command}' never asks which pipeline a Templates-library node should borrow from.");

        // Before the lock, or a cancelled chooser strands the pipeline lock.
        var acquire = body.IndexOf("AcquirePipelineLock(", StringComparison.Ordinal);
        Assert.True(pick < acquire, $"'{command}' picks the pipeline after taking the lock.");

        // The borrowed pipeline supplies the parameters; CollectInitializeParameters alone would
        // return nothing for a template and every token would stay unresolved.
        Assert.Contains("ParametersForRun(node, borrowed)", body);
    }

    /// <summary>
    /// A Template used to have no owning pipeline, so it was left ungated: the unscoped permission
    /// would DENY Engineers while CapabilityChecker still lit the button. Now that a library run
    /// borrows a pipeline, there IS a resource to scope to - so it must be authorized like any other
    /// scoped run, and still never on the unscoped form.
    /// </summary>
    [Fact]
    public void ExecuteTemplate_Should_AuthorizeOnTheBorrowedPipeline_When_Run()
    {
        var body = MethodBody(ExecutionSource(), "ExecuteTemplate");

        Assert.DoesNotContain("_pipelineGuard.AuthorizeAsync(GetCurrentUserContext(), Permission.Pipeline_Trigger)", body);

        var guard = body.IndexOf("_pipelineGuard.AuthorizeAsync(", StringComparison.Ordinal);
        Assert.True(guard >= 0, "ExecuteTemplate dispatches real work but never authorizes the caller.");
        Assert.Contains("Permission.Pipeline_Trigger, borrowed", body);

        var acquire = body.IndexOf("AcquirePipelineLock(", StringComparison.Ordinal);
        Assert.True(guard < acquire, "ExecuteTemplate authorizes after taking the pipeline lock.");
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

        // An orphaned template cannot resolve a single token, so the affordance must be dead.
        Assert.Contains("IsRunnableNode(node)", body);
    }

    [Fact]
    public void CanExecuteTemplate_Should_RequireAReferencingPipeline_When_Evaluated()
    {
        var body = CanExecuteBody("CanExecuteTemplate");

        // With no pipeline Reffing it there are no parameters to borrow, so every action would fail
        // on unresolved tokens the moment it dispatched.
        Assert.Contains("TemplateUsage.PipelinesReferencing(", body);
    }

    /// <summary>
    /// A refusal has to name the pipeline AND the reason. "Pipeline is locked" tells a user who
    /// clicked Run on a template neither which pipeline nor who is holding it.
    /// </summary>
    [Theory]
    [InlineData("ExecuteGroup")]
    [InlineData("ExecuteSingleAction")]
    [InlineData("ExecuteTemplate")]
    public void ScopedRunCommand_Should_NameThePipelineAndReason_When_Refused(string command)
    {
        var body = MethodBody(ExecutionSource(), command);

        // Both refusal paths route through the describer, which carries the borrowed template.
        Assert.Contains("DescribeRunTarget(", body);

        // The lock conflict must say WHO holds it, which AcquirePipelineLock does only when it is
        // told which template borrowed the pipeline. The argument is a local in the scoped-run
        // commands and tpl.ID in ExecuteTemplate, so allow a dotted expression.
        Assert.Matches(new Regex(@"AcquirePipelineLock\(tag,\s*[\w.]+\)"), body);

        // The authorization failure must surface the service's reason verbatim
        // ("Not assigned to pipeline X"), not be replaced by a generic string.
        Assert.Contains("ex.Message", body);
    }

    [Fact]
    public void AcquirePipelineLock_Should_ReportTheOwnerAndTheRemedy_When_Conflicted()
    {
        var source = ExecutionSource();
        var start = source.IndexOf("private string? AcquirePipelineLock(", StringComparison.Ordinal);
        Assert.True(start >= 0, "AcquirePipelineLock was renamed; update this guard deliberately.");
        var body = source[start..source.IndexOf("    /// <summary>", start, StringComparison.Ordinal)];

        Assert.Contains("OwnerDisplayName", body);
        Assert.Contains("Cancel that run or wait for it to finish", body);
        Assert.Contains("DescribeRunTarget(", body);
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

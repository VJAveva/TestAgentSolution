using System.Linq;
using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// The dialog replaced a MessageBox that could not show 30 findings. These guard the decisions that
/// make the replacement worth having: the blocking reason is on screen first, the Run button cannot
/// start a run the runner refused, and secrets stay redacted on every export path.
/// </summary>
public class PreflightDialogViewModelTests
{
    private const string CheckedBy = "controller JVGR22 (wwApps)";

    private static PreflightReport Report(params PreflightCheck[] checks)
    {
        var r = new PreflightReport
        {
            Target = "pipeline 'SP2023R2SP2 - WARM 4 Nodes Install Only'",
            Scope = PreflightScope.Pipeline,
            Elapsed = TimeSpan.FromSeconds(0.4),
        };
        foreach (var c in checks) r.Checks.Add(c);
        return r;
    }

    private static PreflightCheck Pass(string name = "Watch folder", string group = PreflightGroups.ControllerFiles)
        => new(group, name, PreflightStatus.Pass, "Present.");

    private static PreflightCheck Warn(string name = "jvhist", string group = PreflightGroups.Agents)
        => new(group, name, PreflightStatus.Warn, "A reboot is pending.", "Consider rebooting first.");

    private static PreflightCheck Fail(string name = "Watch folder", string group = PreflightGroups.ControllerFiles)
        => new(group, name, PreflightStatus.Fail, "Does not exist.", "Create it on JVGR22.");

    private static PreflightDialogViewModel Vm(PreflightReport report, bool checkOnly = false, Func<PreflightReport>? recheck = null)
        => new(report, CheckedBy, checkOnly, recheck);

    private static List<PreflightCheckRow> Visible(PreflightDialogViewModel vm)
        => vm.ChecksView.Cast<PreflightCheckRow>().ToList();

    // ── ordering ────────────────────────────────────────────────────────────────

    [Fact]
    public void Checks_Should_ListFailuresFirstThenWarningsThenPasses_When_AllThreeArePresent()
    {
        var vm = Vm(Report(Pass("a"), Warn("b"), Fail("c"), Pass("d"), Warn("e")));

        var order = vm.Checks.Select(c => c.StatusText).ToList();

        Assert.Equal(["FAIL", "WARN", "WARN", "PASS", "PASS"], order);
    }

    [Fact]
    public void Checks_Should_OrderByReportGroup_When_StatusesAreEqual()
    {
        var vm = Vm(Report(
            Pass("x", PreflightGroups.Disk),
            Pass("y", PreflightGroups.Settings),
            Pass("z", PreflightGroups.Agents)));

        Assert.Equal(
            [PreflightGroups.Settings, PreflightGroups.Agents, PreflightGroups.Disk],
            vm.Checks.Select(c => c.Area));
    }

    // ── the opening view ────────────────────────────────────────────────────────

    [Fact]
    public void CheckFilter_Should_OpenOnFailed_When_AnythingFailed()
    {
        var vm = Vm(Report(Pass(), Warn(), Fail()));

        Assert.Equal("Failed", vm.CheckFilter);
        Assert.Single(Visible(vm));
        Assert.Equal("FAIL", Visible(vm)[0].StatusText);
    }

    [Fact]
    public void CheckFilter_Should_OpenOnWarnings_When_NothingFailedButSomethingWarned()
    {
        var vm = Vm(Report(Pass(), Warn()));

        Assert.Equal("Warnings", vm.CheckFilter);
        Assert.Single(Visible(vm));
        Assert.Equal("WARN", Visible(vm)[0].StatusText);
    }

    [Fact]
    public void CheckFilter_Should_OpenOnAll_When_EverythingPassed()
    {
        var vm = Vm(Report(Pass("a"), Pass("b")));

        Assert.Equal("All", vm.CheckFilter);
        Assert.Equal(2, Visible(vm).Count);
    }

    [Theory]
    [InlineData("All", 4)]
    [InlineData("Failed", 1)]
    [InlineData("Warnings", 1)]
    [InlineData("Passed", 2)]
    public void CheckFilter_Should_ShowOnlyTheChosenStatus_When_AChipIsSelected(string filter, int expected)
    {
        var vm = Vm(Report(Pass("a"), Pass("b"), Warn("c"), Fail("d")));

        vm.CheckFilter = filter;

        Assert.Equal(expected, Visible(vm).Count);
    }

    // ── the verdict ─────────────────────────────────────────────────────────────

    [Fact]
    public void CanRun_Should_BeFalse_When_AnyCheckFailed()
    {
        var vm = Vm(Report(Pass(), Pass(), Fail()));

        Assert.False(vm.CanRun);
        Assert.Equal(PreflightStatus.Fail, vm.BannerKind);
        Assert.Equal("Blocked \u2014 1 failed", vm.BannerText);
    }

    [Fact]
    public void CanRun_Should_BeTrue_When_OnlyWarningsWereFound()
    {
        var vm = Vm(Report(Pass(), Warn()));

        Assert.True(vm.CanRun);
        Assert.Equal(PreflightStatus.Warn, vm.BannerKind);
        Assert.Equal("Warnings \u2014 review", vm.BannerText);
    }

    [Fact]
    public void CanRun_Should_BeFalse_When_TheDialogIsCheckOnly_EvenThoughNothingFailed()
    {
        var vm = Vm(Report(Pass(), Pass()), checkOnly: true);

        Assert.True(vm.CheckOnly);
        Assert.False(vm.CanRun);
    }

    [Fact]
    public void RunText_Should_SayRunAnyway_When_WarningsAreTheWorstFinding()
    {
        var vm = Vm(Report(Pass(), Warn()));

        Assert.Equal("Run anyway", vm.RunText);
    }

    [Fact]
    public void RunText_Should_SayRun_When_SomethingFailed_SoADeadButtonDoesNotInviteAnOverride()
    {
        var vm = Vm(Report(Warn(), Fail()));

        Assert.False(vm.CanRun);
        Assert.Equal("Run", vm.RunText);
    }

    [Fact]
    public void BannerDetail_Should_CarryTheCountsCheckerAndDuration()
    {
        var vm = Vm(Report(Pass("a"), Pass("b"), Warn("c"), Fail("d")));

        Assert.Equal(
            $"1 failed \u00b7 1 warning \u00b7 2 passed \u00b7 checked by {CheckedBy} in 0.4 s",
            vm.BannerDetail);
    }

    [Fact]
    public void BannerDetail_Should_PluraliseWarnings_When_ThereIsMoreThanOne()
    {
        var vm = Vm(Report(Warn("a"), Warn("b")));

        Assert.Contains("2 warnings", vm.BannerDetail);
    }

    // ── parameters tab ──────────────────────────────────────────────────────────

    [Fact]
    public void Tokens_Should_BeSortedByName_And_CountedForTheTabHeader()
    {
        var report = Report(Pass());
        report.Tokens.Add(new PreflightToken("_ReleaseName", "SP2023R2SP2", "Pipeline"));
        report.Tokens.Add(new PreflightToken("_Agent1", "warmgr", "Profile"));

        var vm = Vm(report);

        Assert.Equal(["_Agent1", "_ReleaseName"], vm.Tokens.Select(t => t.Token));
        Assert.Equal(2, vm.TokenCount);
    }

    [Fact]
    public void TokenSearch_Should_MatchTokenValueOrSource_When_Typed()
    {
        var report = Report(Pass());
        report.Tokens.Add(new PreflightToken("_Agent1", "warmgr", "Profile"));
        report.Tokens.Add(new PreflightToken("_ReleaseName", "SP2023R2SP2", "Pipeline"));
        report.Tokens.Add(new PreflightToken("_BuildNumber", "OAK_SP_20260926.2", "Global"));
        var vm = Vm(report);

        vm.TokenSearch = "warmgr";          // value
        Assert.Equal(["_Agent1"], vm.TokensView.Cast<PreflightTokenRow>().Select(t => t.Token));

        vm.TokenSearch = "release";         // token name, case-insensitive
        Assert.Equal(["_ReleaseName"], vm.TokensView.Cast<PreflightTokenRow>().Select(t => t.Token));

        vm.TokenSearch = "Global";          // source layer
        Assert.Equal(["_BuildNumber"], vm.TokensView.Cast<PreflightTokenRow>().Select(t => t.Token));

        vm.TokenSearch = "";
        Assert.Equal(3, vm.TokensView.Cast<PreflightTokenRow>().Count());
    }

    [Fact]
    public void Tokens_Should_KeepSecretsMasked_On_EveryExportPath()
    {
        var report = Report(Pass());
        report.Tokens.Add(new PreflightToken("_RcloudPassword", "***REDACTED***", "Global"));
        var vm = Vm(report);

        Assert.Equal("***REDACTED***", vm.Tokens.Single().Value);
        Assert.DoesNotContain("hunter2", vm.ReportText);
        Assert.Contains("***REDACTED***", vm.ToMarkdown());
    }

    // ── export ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ToMarkdown_Should_ContainTheVerdictBothTablesAndTheFixHint()
    {
        var report = Report(Fail(), Pass("WASSmokeTest.dll"));
        report.Tokens.Add(new PreflightToken("_Agent1", "warmgr", "Profile"));
        var vm = Vm(report);

        var md = vm.ToMarkdown();

        Assert.Contains("# Pre-flight", md);
        Assert.Contains("**Blocked \u2014 1 failed**", md);
        Assert.Contains("| Status | Area | Check | Details / how to fix |", md);
        Assert.Contains("**Fix:** Create it on JVGR22.", md);
        Assert.Contains("## Parameters", md);
        Assert.Contains("| `[_Agent1]` | warmgr | Profile |", md);
    }

    [Fact]
    public void ToMarkdown_Should_EscapePipes_So_ATableCellCannotBreakTheTable()
    {
        var report = Report(new PreflightCheck(
            PreflightGroups.Settings, "Command", PreflightStatus.Fail, "run a | b", null));

        var md = Vm(report).ToMarkdown();

        Assert.Contains(@"run a \| b", md);
    }

    // ── re-check ────────────────────────────────────────────────────────────────

    [Fact]
    public void CanRecheck_Should_BeFalse_When_NoRecheckSourceWasSupplied()
    {
        var vm = Vm(Report(Pass()));

        Assert.False(vm.CanRecheck);
        Assert.False(vm.Recheck());
    }

    [Fact]
    public void Recheck_Should_RebindEverything_When_TheSecondRunIsClean()
    {
        var fixedReport = Report(Pass("a"), Pass("b"));
        var vm = Vm(Report(Fail(), Warn()), recheck: () => fixedReport);

        Assert.False(vm.CanRun);
        Assert.Equal("Failed", vm.CheckFilter);

        Assert.True(vm.Recheck());

        Assert.True(vm.CanRun);
        Assert.Equal("Run", vm.RunText);
        Assert.Equal("All", vm.CheckFilter);
        Assert.Equal(0, vm.FailedCount);
        Assert.Equal(2, vm.PassCount);
        Assert.Equal(2, Visible(vm).Count);
    }

    [Fact]
    public void Recheck_Should_NotEnableRun_When_TheDialogIsCheckOnly()
    {
        var vm = Vm(Report(Fail()), checkOnly: true, recheck: () => Report(Pass()));

        Assert.True(vm.Recheck());

        Assert.False(vm.CanRun);
    }
}

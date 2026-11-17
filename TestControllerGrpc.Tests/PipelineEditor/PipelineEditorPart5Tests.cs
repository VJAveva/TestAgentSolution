using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.Menus;
using TestControllerGrpc.Models;
using TestControllerGrpc.PipelineEditor;
using TestControllerGrpc.Services;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.Tests.PipelineEditor;

public class PipelineEditorPart5Tests
{
    private const string Pipeline =
        "<WatchItem Tag=\"P\" Path=\"C:\\drop\\\" Filter=\"t.txt\">\r\n" +
        "  <Event Type=\"Renamed\" ExecutionType=\"Sequential\">\r\n" +
        "    <!-- keep me -->\r\n" +
        "    <ActionGroup Tag=\"Install\" ExecutionType=\"Sequential\">\r\n" +
        "      <Action Tag=\"Setup\" Type=\"RunRemoteCommand\" AgentName=\"node1\" Command=\"setup.exe\"/>\r\n" +
        "      <Action Tag=\"Gone\" Type=\"RunRemoteCommand\" AgentName=\"node1\" Command=\"[_Gone]\\x.bat\"/>\r\n" +
        "    </ActionGroup>\r\n" +
        "  </Event>\r\n" +
        "</WatchItem>";

    [Fact]
    public void QuickFix_Should_ChangeOnlyTheAffectedSteps_When_RemovingStepsThatUseAToken()
    {
        var fixedXml = WatchListXmlEditorAnalyzer.ApplyQuickFix(Pipeline, XmlEditorDocumentKind.WatchItem,
            new XmlEditorQuickFix("Remove", XmlEditorQuickFixKind.RemoveStepsUsingToken, "", "[_Gone]"));

        var expected = Pipeline.Replace(
            "      <Action Tag=\"Gone\" Type=\"RunRemoteCommand\" AgentName=\"node1\" Command=\"[_Gone]\\x.bat\"/>\r\n", "");
        Assert.Equal(expected, fixedXml);
    }

    [Fact]
    public void Analyze_Should_FlagANonNumericTimeout_When_TheParserWouldReadItAsZero()
    {
        var analysis = WatchListXmlEditorAnalyzer.Analyze(Pipeline.Replace("Command=\"setup.exe\"", "Command=\"setup.exe\" Timeout=\"5m\""),
            XmlEditorDocumentKind.WatchItem, fileSystem: new AlwaysExistsFileSystem());

        var diagnostic = Assert.Single(analysis.Diagnostics, d => d.Message.StartsWith("Timeout must be a whole number", StringComparison.Ordinal));
        Assert.Equal(WatchIssueSeverity.Error, diagnostic.Severity);
        Assert.Equal(5, diagnostic.Line);
    }

    [Fact]
    public void QuickActions_Should_AddRestartAndChangeAgent_When_AppliedToAStep()
    {
        var restart = PipelineQuickActions.AddRestartAfter(Pipeline, "e0/c0/c0")!;
        Assert.Contains("<Action Tag=\"Restart node1\" Type=\"RunRemoteCommand\" AgentName=\"node1\" Command=\"shutdown\" Parameters=\"/r /t 30\" IsReboot=\"true\"", restart.Xml);
        Assert.Equal("e0/c0/c1", restart.SelectPath);
        Assert.Contains("<!-- keep me -->", restart.Xml);

        var moved = PipelineQuickActions.ChangeAgent(Pipeline, "e0/c0", "node9")!;
        Assert.DoesNotContain("node1", moved.Xml);
        Assert.Equal(Pipeline, moved.Xml.Replace("node9", "node1"));
    }

    [Fact]
    public void ApplyToAgents_Should_ReplaceTheStepWithAParallelGroup_When_AgentsAreChosen()
    {
        var result = PipelineQuickActions.ApplyToAgents(Pipeline, "e0/c0/c0", ["a1", "a2"])!;

        var map = PipelineXmlMap.TryBuild(result.Xml)!;
        var group = map.Find("e0/c0/c0")!;
        Assert.Equal(NodeKinds.ActionGroup, group.Kind);
        Assert.Contains("ExecutionType=\"Parallel\"", result.Xml);
        Assert.Equal(2, group.Children.Count);
        Assert.Equal(["a1", "a2"], group.Children.Select(c => c.Agent));
    }

    [Fact]
    public void AddStep_Should_AppendToTheEvent_When_TheEventIsSelected()
    {
        var result = PipelineQuickActions.AddStep(Pipeline, "e0")!;

        var map = PipelineXmlMap.TryBuild(result.Xml)!;
        Assert.Equal("e0/c1", result.SelectPath);
        Assert.Equal("New step", map.Find("e0/c1")!.Name);
    }

    [Fact]
    public void WhatChanged_Should_MaskSecrets_When_APasswordOrSecretEnvChanged()
    {
        var change = PipelineDiff.Compare("P",
            "<Action Password=\"old-secret\" SecretEnv=\"PW=hunter2\" />",
            "<Action Password=\"new-secret\" SecretEnv=\"PW=hunter3\" />");

        Assert.Equal(1, change.ChangedLines);
        var all = string.Join("\n", change.Rows.Select(r => r.Before + r.After));
        Assert.DoesNotContain("secret", all.Replace("SecretEnv", ""), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter", all);
        Assert.Contains("PW=", all);
    }

    [Fact]
    public void ParameterTable_Should_FlagUndefinedTokensAndListTriggerFields_When_Built()
    {
        var item = WatchListXmlParser.DeserializeWatchItem(Pipeline)!;

        var rows = PipelineParameterTable.Build(item, null);

        var gone = Assert.Single(rows, r => r.Token == "[_Gone]");
        Assert.True(gone.IsUnresolved);
        Assert.Equal("Gone", gone.UsedBy);
        Assert.Contains(rows, r => r.Token == "[_BuildNumber]" && r.Source == nameof(TokenLayer.Trigger));
    }

    [Fact]
    public void Session_Should_ListTheEditedPipeline_When_WhatChangedIsAsked()
    {
        var pipeline = new WatchItemXmlEditorViewModel(Pipeline, fileSystem: new AlwaysExistsFileSystem()) { TabTitle = "Pipeline: P" };
        var session = new PipelineEditorSessionViewModel(pipeline);
        IReadOnlyList<PipelineChange>? shown = null;
        session.ShowChanges = (_, changes) => shown = changes;

        pipeline.XmlText = Pipeline.Replace("setup.exe", "setup2.exe");
        session.WhatChangedCommand.Execute(null);

        var change = Assert.Single(shown!);
        Assert.Equal("Pipeline: P", change.Title);
        Assert.Equal(1, change.ChangedLines);
    }

    [Fact]
    public void QuickActionButtons_Should_OfferTheStepActions_When_AnActionIsSelected()
    {
        var vm = new WatchItemXmlEditorViewModel(Pipeline, fileSystem: new AlwaysExistsFileSystem(),
            agentStatuses: [new("node1", "Online"), new("node2", "Online")]);
        vm.SelectPath("e0/c0/c0");

        var ids = vm.SelectedNodeActions.Select(a => a.Id).ToList();
        Assert.Contains(PipelineEditorMenuIds.AddRestartAfter, ids);
        Assert.Contains(PipelineEditorMenuIds.ChangeAgent, ids);
        Assert.Contains(PipelineEditorMenuIds.ApplyToAgents, ids);
        Assert.Contains(NodeMenuIds.Skip, ids);

        Assert.True(vm.RunStructureCommand(PipelineEditorMenuIds.AddWaitAfter, vm.SelectedStructureNode!));
        Assert.Contains("Start-Sleep -Seconds 240", vm.XmlText);
    }

    [Fact]
    public void Ref_Should_ExpandNestedTemplateCalls_When_ATemplateCallsAnother()
    {
        var context = WatchListXmlParser.DeserializeWatchList(
            "<WatchList><Templates>" +
            "<Template ID=\"Outer\"><Ref TemplateID=\"Inner\" /></Template>" +
            "<Template ID=\"Inner\"><Action Tag=\"Deep\" Type=\"RunCommand\" Command=\"d.bat\" /></Template>" +
            "</Templates><WatchItem Tag=\"P\" Path=\"C:\\\"><Event Type=\"Renamed\"><Ref TemplateID=\"Outer\" /></Event></WatchItem></WatchList>")!;
        var vm = new WatchItemXmlEditorViewModel(WatchListXmlParser.SerializeWatchItem(context.WatchItems[0]), context,
            fileSystem: new AlwaysExistsFileSystem());

        var outerCall = vm.StructureRoots[0].Children[0].Children[0];
        var innerCall = Assert.Single(outerCall.Children);
        Assert.Equal("Deep", Assert.Single(innerCall.Children).Name);
    }

    [Fact]
    public async Task ImpactProblems_Should_SurviveTheBackgroundCheck_Until_TheTemplateChanges()
    {
        var context = WatchListXmlParser.DeserializeWatchList(
            "<WatchList><Templates><Template ID=\"T\"><Action Tag=\"A\" Type=\"RunCommand\" Command=\"a.bat\" /></Template></Templates>" +
            "<WatchItem Tag=\"P\" Path=\"C:\\\"><Event Type=\"Renamed\"><Ref TemplateID=\"T\" /></Event></WatchItem></WatchList>")!;
        var tab = new WatchItemXmlEditorViewModel(PipelineTemplateEdits.TemplateDocument(context.Templates[0]), context,
            fileSystem: new AlwaysExistsFileSystem());

        tab.ShowImpactProblems([new TemplateImpactProblem("P", "broken")]);
        await tab.RefreshDiagnosticsAsync();
        Assert.Contains(tab.ValidationErrors, e => e.ErrorType == "Impact");

        tab.XmlText = tab.XmlText.Replace("a.bat", "b.bat");
        await tab.RefreshDiagnosticsAsync();
        Assert.DoesNotContain(tab.ValidationErrors, e => e.ErrorType == "Impact");
    }

    [Fact]
    public void Revert_Should_KeepTheEdits_When_TheUserSaysNo()
    {
        var vm = new WatchItemXmlEditorViewModel(Pipeline, fileSystem: new AlwaysExistsFileSystem()) { ConfirmRevert = () => false };
        vm.XmlText = Pipeline.Replace("setup.exe", "setup2.exe");

        vm.RevertCommand.Execute(null);

        Assert.Contains("setup2.exe", vm.XmlText);
    }

    private sealed class AlwaysExistsFileSystem : IPreflightFileSystem
    {
        public PathAccess CheckFile(string path) => PathAccess.Exists;
        public PathAccess CheckDirectory(string path) => PathAccess.Exists;
        public double? FreeSpaceGb(string path) => 100;
        public string Identity => "test";
    }
}

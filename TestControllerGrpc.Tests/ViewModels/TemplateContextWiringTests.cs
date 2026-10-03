namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Guards the parts of the template-context wiring that need an STA WPF app and a modal dialog to
/// exercise: that a run reuses an existing context instead of re-asking, that an answer is
/// remembered, and that a context is never honoured for a pipeline that no longer Refs the template.
/// </summary>
public class TemplateContextWiringTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TestAgentSolution.sln")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string PickTemplatePipelineBody()
    {
        var source = Source("TestControllerGrpc", "ViewModels", "MainViewModel.cs");
        var start = source.IndexOf("private bool TryPickTemplatePipeline(", StringComparison.Ordinal);
        Assert.True(start >= 0, "TryPickTemplatePipeline was renamed; update this guard deliberately.");

        var next = source.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    [Fact]
    public void TryPickTemplatePipeline_Should_UseTheContext_BeforeAskingTheUser()
    {
        var body = PickTemplatePipelineBody();

        var context = body.IndexOf("TemplateRunContext.For(", StringComparison.Ordinal);
        var ask = body.IndexOf("RunInPipelineDialog.Ask(", StringComparison.Ordinal);

        Assert.True(context >= 0, "A run must reuse the template's chosen context instead of re-asking.");
        Assert.True(ask >= 0, "There must still be a prompt for the first run.");
        Assert.True(context < ask, "The context must be consulted BEFORE the dialog, not after.");
    }

    [Fact]
    public void TryPickTemplatePipeline_Should_RejectAContext_That_NoPipelineStillRefs()
    {
        var body = PickTemplatePipelineBody();

        Assert.Contains("candidates.Contains(context", body);
    }

    [Fact]
    public void TryPickTemplatePipeline_Should_RememberTheAnswer_When_TheUserPicks()
    {
        var body = PickTemplatePipelineBody();

        var ask = body.IndexOf("RunInPipelineDialog.Ask(", StringComparison.Ordinal);
        var remember = body.IndexOf("SetTemplateContext(", StringComparison.Ordinal);

        Assert.True(remember > ask, "The chosen pipeline must be stored so the next run does not re-ask.");
    }

    [Fact]
    public void WatchListReload_Should_ClearTemplateContexts_And_ResetSelection()
    {
        var helpers = Source("TestControllerGrpc", "ViewModels", "MainViewModel.Helpers.cs");
        var start = helpers.IndexOf("private void LoadTokensFromConfig(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var body = helpers[start..helpers.IndexOf("\n    private void LoadTokensFromChildren(", start, StringComparison.Ordinal)];

        Assert.Contains("TemplateRunContext.ClearAll()", body);
        Assert.Contains("_selection.Reset()", body);
    }

    [Fact]
    public void TemplateTree_Should_ShowTheContextChip()
    {
        var xaml = Source("TestControllerGrpc", "Views", "Styles", "TreeViewSpec.xaml");

        Assert.Contains("TemplateContextLabel", xaml);
        Assert.Contains("HasTemplateContext", xaml);
    }

    [Fact]
    public void PropertiesPane_Should_ShowTheResolvedCommand()
    {
        var xaml = Source("TestControllerGrpc", "Views", "MainWindow.xaml");

        Assert.Contains("ResolvedCommand", xaml);
    }

    [Fact]
    public void TemplateMenus_Should_OfferTheContextItems()
    {
        var menus = Source("TestControllerGrpc", "Views", "MainWindow.ContextMenus.cs");

        Assert.Contains("AddTemplateContextItems(menu, node)", menus);
        Assert.Contains("ChangeTemplateContext(node)", menus);
        Assert.Contains("ClearTemplateContext(node)", menus);
    }
}

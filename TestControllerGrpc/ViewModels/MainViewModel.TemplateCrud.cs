using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

// ?? Template CRUD + Edit XML ????????????????????????????????????????
public sealed partial class MainViewModel
{
    [RelayCommand]
    private void EditTemplateXml()
    {
        OpenTemplateXmlEditorWindow();
    }

    /// <summary>Opens the standalone Template XML Editor window for all templates.</summary>
    private void OpenTemplateXmlEditorWindow()
    {
        WriteBackAll();
        var xml = WatchListXmlParser.SerializeTemplateList(_config.Templates);

        var editorVm = new TemplateXmlEditorViewModel(xml);

        // Build window title with template IDs
        var ids = _config.Templates.Select(t => t.ID).Where(id => !string.IsNullOrEmpty(id));
        editorVm.WindowTitle = $"Template XML Editor - {string.Join(", ", ids)}";

        var editorWindow = new Views.TemplateXmlEditorWindow(editorVm);
        if (Application.Current.MainWindow is { } mainWindow)
            editorWindow.Owner = mainWindow;

        var result = editorWindow.ShowDialog();
        if (result == true && editorVm.DialogAccepted)
        {
            try
            {
                var parsed = WatchListXmlParser.DeserializeTemplateList(editorVm.ResultXml);
                if (parsed is null)
                {
                    AddLog("Template XML editor: Root element was not <Templates>", LogSeverity.Error);
                    return;
                }

                // Replace templates in model
                _config.Templates.Clear();
                _config.Templates.AddRange(parsed);

                // Rebuild template tree
                TemplateRoots.Clear();
                TemplateRoots.Add(TreeNodeViewModel.FromTemplateList(_config.Templates));
                RebuildTemplateIds();
                _executor.LoadTemplates(_config.Templates);

                AddLog($"Templates updated via XML editor: {parsed.Count} template(s)", LogSeverity.Success);
            }
            catch (Exception ex)
            {
                AddLog($"Template XML editor apply failed: {ex.Message}", LogSeverity.Error);
            }
        }
    }

    [RelayCommand]
    private void AddTemplate()
    {
        var t = new TemplateConfig { ID = $"NewTemplate{_config.Templates.Count + 1}" };
        _config.Templates.Add(t);
        if (TemplateListRoot is not null)
        {
            var node = TreeNodeViewModel.FromTemplate(t);
            node.Parent = TemplateListRoot;
            TemplateListRoot.Children.Add(node);
            TemplateListRoot.RefreshDisplayText();
        }
        RebuildTemplateIds();
    }

    [RelayCommand]
    private void DeleteTemplate()
    {
        if (SelectedTemplateNode is null || SelectedTemplateNode.NodeKind == "TemplateList") return;

        // CRITICAL: Capture before Remove triggers SelectedItemChanged
        var target = SelectedTemplateNode;
        var parent = target.Parent;
        if (parent is null) return;

        if (parent.Children.Remove(target))
        {
            switch (parent.ModelObject)
            {
                case List<TemplateConfig> tl when target.ModelObject is TemplateConfig tc: tl.Remove(tc); break;
                case TemplateConfig tc when target.ModelObject is IActionNode a: tc.Children.Remove(a); break;
                case ActionGroupConfig ag when target.ModelObject is IActionNode a: ag.Children.Remove(a); break;
            }
            parent.RefreshDisplayText();
            SelectedTemplateNode = null;
            RebuildTemplateIds();
            return;
        }
        if (TemplateListRoot is not null && RemoveDeep(TemplateListRoot, target))
        { SelectedTemplateNode = null; RebuildTemplateIds(); }
    }

    [RelayCommand]
    private void AddGroupToTemplate()
    {
        var t = SelectedTemplateNode;
        if (t?.NodeKind is not ("Template" or "TemplateList" or "ActionGroup" or "Event")) return;
        if (t.NodeKind == "TemplateList") { AddTemplate(); return; }
        AddChildT(t, new ActionGroupConfig { Tag = "NewGroup", ExecutionType = ExecutionMode.Sequential });
    }

    [RelayCommand]
    private void AddActionToTemplate()
    {
        if (SelectedTemplateNode?.NodeKind is not ("Template" or "ActionGroup")) return;
        AddChildT(SelectedTemplateNode, new ActionConfig { Type = ActionType.RunCommand, Command = "cmd", Parameters = "/c echo hello" });
    }

    [RelayCommand]
    private void AddRefToTemplate()
    {
        if (SelectedTemplateNode?.NodeKind is not ("Template" or "ActionGroup")) return;
        AddChildT(SelectedTemplateNode, new RefConfig { TemplateID = AvailableTemplateIds.Count > 0 ? AvailableTemplateIds[0] : "" });
    }

    [RelayCommand]
    private void AddInitializeToTemplate()
    {
        if (SelectedTemplateNode?.NodeKind is not ("Template" or "ActionGroup")) return;
        AddChildT(SelectedTemplateNode, new InitializeConfig { Tag = "Params" });
    }
}

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

    /// <summary>Opens the XML editor for the currently selected individual template.</summary>
    [RelayCommand]
    private void EditSingleTemplateXml()
    {
        if (SelectedTemplateNode is null || SelectedTemplateNode.NodeKind != NodeKinds.Template) return;
        if (SelectedTemplateNode.ModelObject is not TemplateConfig selectedTemplate) return;

        WriteBackAll();
        var xml = WatchListXmlParser.SerializeTemplateList(new List<TemplateConfig> { selectedTemplate });

        var editorVm = new TemplateXmlEditorViewModel(xml);
        editorVm.WindowTitle = $"Template XML Editor - {selectedTemplate.ID}";

        var editorWindow = new Views.TemplateXmlEditorWindow(editorVm);
        if (Application.Current.MainWindow is { } mainWindow)
            editorWindow.Owner = mainWindow;

        var result = editorWindow.ShowDialog();
        if (result == true && editorVm.DialogAccepted)
        {
            try
            {
                var parsed = WatchListXmlParser.DeserializeTemplateList(editorVm.ResultXml);
                if (parsed is null || parsed.Count == 0)
                {
                    AddLog("Template XML editor: could not parse template", LogSeverity.Error);
                    return;
                }

                var newTemplate = parsed[0];

                // Check if template ID was changed and warn about references
                if (!string.Equals(selectedTemplate.ID, newTemplate.ID, StringComparison.OrdinalIgnoreCase))
                {
                    var refCount = CountTemplateReferences(selectedTemplate.ID);
                    if (refCount > 0)
                    {
                        var answer = Views.Dialogs.ThemedMessageBox.Show(
                            $"Renaming template '{selectedTemplate.ID}' to '{newTemplate.ID}' will break {refCount} Ref node(s) that reference it.\n\nContinue?",
                            "Template Rename Warning",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);
                        if (answer != MessageBoxResult.Yes) return;
                    }
                }

                // Replace in config
                var idx = _config.Templates.IndexOf(selectedTemplate);
                if (idx >= 0)
                    _config.Templates[idx] = newTemplate;

                // Replace in tree
                if (SelectedTemplateNode.Parent is { } parent)
                {
                    var nodeIdx = parent.Children.IndexOf(SelectedTemplateNode);
                    parent.Children.Remove(SelectedTemplateNode);
                    var newNode = TreeNodeViewModel.FromTemplate(newTemplate);
                    newNode.Parent = parent;
                    if (nodeIdx >= 0 && nodeIdx <= parent.Children.Count)
                        parent.Children.Insert(nodeIdx, newNode);
                    else
                        parent.Children.Add(newNode);
                    SelectedTemplateNode = newNode;
                }

                RebuildTemplateIds();
                _executor.LoadTemplates(_config.Templates);
                IsDirty = true;
                AddLog($"Template '{newTemplate.ID}' updated via XML editor", LogSeverity.Success);
            }
            catch (Exception ex)
            {
                AddLog($"Template XML editor apply failed: {ex.Message}", LogSeverity.Error);
            }
        }
    }

    /// <summary>Counts how many Ref nodes in the config reference the given template ID.</summary>
    private int CountTemplateReferences(string templateId)
    {
        int count = 0;
        foreach (var wi in _config.WatchItems)
            foreach (var ev in wi.Events)
                count += CountRefsInChildren(ev.Children, templateId);
        foreach (var t in _config.Templates)
            count += CountRefsInChildren(t.Children, templateId);
        return count;
    }

    private static int CountRefsInChildren(List<IActionNode> children, string templateId)
    {
        int count = 0;
        foreach (var child in children)
        {
            if (child is RefConfig r && string.Equals(r.TemplateID, templateId, StringComparison.OrdinalIgnoreCase))
                count++;
            if (child is ActionGroupConfig ag)
                count += CountRefsInChildren(ag.Children, templateId);
        }
        return count;
    }

    [RelayCommand]
    private void DeleteTemplate()
    {
        if (SelectedTemplateNode is null || SelectedTemplateNode.NodeKind == NodeKinds.TemplateList) return;

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
        if (t?.NodeKind is not (NodeKinds.Template or NodeKinds.TemplateList or NodeKinds.ActionGroup or NodeKinds.Event)) return;
        if (t.NodeKind == NodeKinds.TemplateList) { AddTemplate(); return; }
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

using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

// ?? Import/Export WatchItems + Templates ????????????????????????????
public sealed partial class MainViewModel
{
    // ???????????????????????????????????????????????????????????????
    // IMPORT WATCHITEMS (additive merge from another XML file)
    // ???????????????????????????????????????????????????????????????

    /// <summary>
    /// Import WatchItems from another XML file, merging them into the
    /// current config. Existing items with matching tags are updated
    /// (unless currently executing). New items are added.
    /// </summary>
    [RelayCommand]
    private void ImportWatchItems()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "WatchList XML (*.xml)|*.xml|All Files|*.*",
            Title = "Import WatchItems"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var importedConfig = WatchListXmlParser.Load(dlg.FileName);
            var imported = importedConfig.WatchItems;

            if (imported.Count == 0)
            {
                AddLog("Import: no WatchItems found in file.");
                return;
            }

            int added = 0, updated = 0, skipped = 0;
            foreach (var item in imported)
            {
                var existing = _config.WatchItems
                    .FirstOrDefault(w => string.Equals(w.Tag, item.Tag, StringComparison.OrdinalIgnoreCase));

                if (existing is not null)
                {
                    // Skip items that are currently executing
                    if (_sessionManager.HasActiveExecution(item.Tag))
                    {
                        AddLog($"? Skipped '{item.Tag}' — currently executing", LogSeverity.Warning);
                        skipped++;
                        continue;
                    }
                    var idx = _config.WatchItems.IndexOf(existing);
                    _config.WatchItems[idx] = item;
                    _watcherManager.AddOrUpdateWatcher(item);
                    updated++;
                }
                else
                {
                    _config.WatchItems.Add(item);
                    _watcherManager.AddOrUpdateWatcher(item);
                    added++;
                }
            }

            // Import templates too (if present and not duplicate)
            foreach (var template in importedConfig.Templates)
            {
                if (!_config.Templates.Any(t =>
                    string.Equals(t.ID, template.ID, StringComparison.OrdinalIgnoreCase)))
                {
                    _config.Templates.Add(template);
                }
            }

            // Rebuild tree from merged config
            _executor.LoadTemplates(_config.Templates);
            Application.Current?.Dispatcher.Invoke(() =>
            {
                TreeRoots.Clear();
                TreeRoots.Add(TreeNodeViewModel.FromWatchList(_config));
                TemplateRoots.Clear();
                TemplateRoots.Add(TreeNodeViewModel.FromTemplateList(_config.Templates));
                RebuildTemplateIds();
                RebuildFilterOptions();
                LoadTokensFromConfig(_config);
                ActiveWatchers = _watcherManager.ActiveWatcherCount;
            });

            var parts = new List<string>();
            if (added > 0) parts.Add($"{added} new");
            if (updated > 0) parts.Add($"{updated} updated");
            if (skipped > 0) parts.Add($"{skipped} skipped");
            AddLog($"? Imported {string.Join(", ", parts)} WatchItem(s) from {Path.GetFileName(dlg.FileName)}",
                LogSeverity.Success);
        }
        catch (Exception ex)
        {
            AddLog($"? Import failed: {ex.Message}", LogSeverity.Error);
        }
    }

    // ???????????????????????????????????????????????????????????????
    // EXPORT WATCHITEMS
    // ???????????????????????????????????????????????????????????????

    /// <summary>
    /// Exports the selected WatchItem (or all WatchItems if the root is selected)
    /// to a standalone XML file. Includes referenced Templates automatically.
    /// </summary>
    [RelayCommand]
    private void ExportWatchItems()
    {
        List<WatchItemConfig> itemsToExport;
        string defaultFileName;

        if (SelectedNode?.NodeKind == "WatchList")
        {
            WriteBackAll();
            itemsToExport = _config.WatchItems.ToList();
            defaultFileName = "WatchItems_All.xml";
        }
        else if (SelectedNode?.NodeKind == "WatchItem" && SelectedNode.ModelObject is WatchItemConfig wi)
        {
            WriteBackAll();
            itemsToExport = [wi];
            defaultFileName = $"WatchItem_{wi.Tag}.xml";
        }
        else
        {
            AddLog("Select a WatchItem or the WatchList root to export.");
            return;
        }

        // Collect referenced templates
        var refIds = itemsToExport.SelectMany(WatchListXmlParser.CollectRefTemplateIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referencedTemplates = _config.Templates
            .Where(t => refIds.Contains(t.ID))
            .ToList();

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "WatchList XML (*.xml)|*.xml",
            Title = "Export WatchItems",
            FileName = defaultFileName
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var xml = WatchListXmlParser.SerializeWatchItemsToXml(
                itemsToExport, referencedTemplates.Count > 0 ? referencedTemplates : null);
            File.WriteAllText(dlg.FileName, xml, Encoding.UTF8);

            AddLog($"? Exported {itemsToExport.Count} WatchItem(s)" +
                   (referencedTemplates.Count > 0
                       ? $" + {referencedTemplates.Count} referenced template(s)" : "") +
                   $" ? {Path.GetFileName(dlg.FileName)}", LogSeverity.Success);
        }
        catch (Exception ex)
        {
            AddLog($"? Export failed: {ex.Message}", LogSeverity.Error);
        }
    }

    // ???????????????????????????????????????????????????????????????
    // IMPORT TEMPLATES
    // ???????????????????????????????????????????????????????????????

    /// <summary>
    /// Import Templates from an XML file, merging them into the current config.
    /// Duplicate template IDs prompt the user to skip or replace.
    /// </summary>
    [RelayCommand]
    private void ImportTemplates()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "WatchList XML (*.xml)|*.xml|All Files|*.*",
            Title = "Import Templates"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var xml = File.ReadAllText(dlg.FileName);
            var imported = WatchListXmlParser.ParseTemplatesFromXml(xml);

            if (imported.Count == 0)
            {
                AddLog("Import: no Templates found in file.");
                return;
            }

            int added = 0, skipped = 0;
            foreach (var t in imported)
            {
                if (_config.Templates.Any(e =>
                    string.Equals(e.ID, t.ID, StringComparison.OrdinalIgnoreCase)))
                {
                    var result = MessageBox.Show(
                        $"Template '{t.ID}' already exists. Replace it?",
                        "Duplicate Template",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.No)
                    { skipped++; continue; }

                    // Remove existing
                    _config.Templates.RemoveAll(e =>
                        string.Equals(e.ID, t.ID, StringComparison.OrdinalIgnoreCase));
                }

                _config.Templates.Add(t);
                added++;
            }

            if (added > 0)
            {
                _executor.LoadTemplates(_config.Templates);
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    TemplateRoots.Clear();
                    TemplateRoots.Add(TreeNodeViewModel.FromTemplateList(_config.Templates));
                    RebuildTemplateIds();
                });
            }

            AddLog($"? Imported {added} template(s)" +
                   (skipped > 0 ? $" ({skipped} skipped)" : "") +
                   $" from {Path.GetFileName(dlg.FileName)}", LogSeverity.Success);
        }
        catch (Exception ex)
        {
            AddLog($"? Template import failed: {ex.Message}", LogSeverity.Error);
        }
    }

    // ???????????????????????????????????????????????????????????????
    // EXPORT TEMPLATES
    // ???????????????????????????????????????????????????????????????

    /// <summary>
    /// Exports the selected Template (or all Templates) to a standalone XML file.
    /// </summary>
    [RelayCommand]
    private void ExportTemplates()
    {
        WriteBackAll();

        List<TemplateConfig> toExport;
        string defaultFileName;

        if (SelectedTemplateNode?.NodeKind == "Template"
            && SelectedTemplateNode.ModelObject is TemplateConfig t)
        {
            toExport = [t];
            defaultFileName = $"Template_{t.ID}.xml";
        }
        else
        {
            toExport = _config.Templates.ToList();
            defaultFileName = "Templates_All.xml";
        }

        if (toExport.Count == 0)
        {
            AddLog("No templates to export.");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "WatchList XML (*.xml)|*.xml",
            FileName = defaultFileName,
            Title = "Export Templates"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var xml = WatchListXmlParser.SerializeTemplatesToXml(toExport);
            File.WriteAllText(dlg.FileName, xml, Encoding.UTF8);
            AddLog($"? Exported {toExport.Count} template(s) ? {Path.GetFileName(dlg.FileName)}", LogSeverity.Success);
        }
        catch (Exception ex)
        {
            AddLog($"? Export failed: {ex.Message}", LogSeverity.Error);
        }
    }
}

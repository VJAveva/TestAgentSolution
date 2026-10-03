using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Pre-flight: the gate every run passes through, plus the "Check only" command that runs the same
/// checks and shows the report without starting anything.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Runs the checks and decides whether the run may proceed. Errors block outright; warnings ask.
    /// </summary>
    /// <returns>True when the caller should continue.</returns>
    private bool PassesPreflight(
        TreeNodeViewModel? node,
        string? pipelineTag,
        PreflightScope scope,
        string? templateId = null)
    {
        var preflight = _preflight;
        if (preflight is null) return true; // no service registered - never block a run on our own absence

        PreflightReport report;
        try
        {
            report = RunPreflight(preflight, node, pipelineTag, scope, templateId);
        }
        catch (Exception ex)
        {
            // A broken check must not become a broken product: log it and let the run proceed.
            _appLogger.Error("Preflight", "Pre-flight check failed to run", ex);
            return true;
        }

        LastPreflightReport = report;
        _appLogger.Info("Preflight", $"{report.Target}: {report.Summary}");

        if (report.HasErrors)
        {
            AddLog($"Run blocked by pre-flight - {report.Summary}.", LogSeverity.Error);
            foreach (var failure in report.Failures)
                AddLog($"  {failure.Name}: {failure.Detail}", LogSeverity.Error);
        }
        else if (report.HasWarnings)
        {
            foreach (var warning in report.Warnings)
                AddLog($"Pre-flight warning - {warning.Name}: {warning.Detail}", LogSeverity.Warning);
        }
        else
        {
            // Nothing to decide, so do not interrupt the run with a dialog.
            return true;
        }

        SaveReport(report);

        var proceed = ShowPreflightDialog(
            report,
            checkOnly: false,
            recheck: () => RunPreflight(preflight, node, pipelineTag, scope, templateId));

        if (!proceed && !report.HasErrors)
            AddLog("Run cancelled at the pre-flight warning prompt.", LogSeverity.Info);

        return proceed;
    }

    /// <summary>Who performed the checks - this host runs them, and that is what makes them valid.</summary>
    private static string CheckedBy =>
        $"controller {Environment.MachineName} ({Environment.UserName})";

    private bool ShowPreflightDialog(PreflightReport report, bool checkOnly, Func<PreflightReport>? recheck)
    {
        return Views.Dialogs.PreflightDialog.Show(
            report, CheckedBy, checkOnly, Application.Current?.MainWindow, recheck);
    }

    private PreflightReport RunPreflight(
        PreflightService preflight,
        TreeNodeViewModel? node,
        string? pipelineTag,
        PreflightScope scope,
        string? templateId)
    {
        var pipeline = _config.WatchItems.FirstOrDefault(w =>
            string.Equals(w.Tag, pipelineTag, StringComparison.OrdinalIgnoreCase));

        // A whole-pipeline run checks everything; anything narrower checks only what it will execute.
        var nodes = scope == PreflightScope.Pipeline || node?.ModelObject is not IActionNode model
            ? []
            : new[] { model };

        return preflight.Check(_config, pipeline, nodes, scope, templateId);
    }

    /// <summary>Most recent report, kept so the result email and the session can carry it.</summary>
    public PreflightReport? LastPreflightReport { get; private set; }

    /// <summary>Writes the full report next to the logs and returns the path, or "" on failure.</summary>
    private string SaveReport(PreflightReport report)
    {
        try
        {
            var dir = Path.Combine(AppLogger.DefaultLogDirectory, "preflight");
            Directory.CreateDirectory(dir);
            var safe = string.Join("_", report.Target.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(dir, $"preflight_{DateTime.Now:yyyyMMdd_HHmmss}_{safe}.txt");
            File.WriteAllText(path, report.ToPlainText());
            return path;
        }
        catch (Exception ex)
        {
            _appLogger.Warn("Preflight", $"Could not save the pre-flight report: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Runs every check for the selected pipeline and shows the report without starting anything.
    /// </summary>
    [RelayCommand]
    private void CheckOnly()
    {
        var preflight = _preflight;
        if (preflight is null)
        {
            AddLog("Pre-flight is not available in this build.", LogSeverity.Warning);
            return;
        }

        var node = ActiveExecNode ?? SelectedNode;
        var templateId = OwningTemplateId(node);

        string? pipelineTag;
        if (templateId is not null)
        {
            // A template borrows a pipeline's settings; check it against whichever one it will use.
            if (!TryPickTemplatePipeline(node!, out var borrowed)) return;
            pipelineTag = borrowed;
        }
        else
        {
            pipelineTag = FindWatchItemTag(node);
        }

        if (pipelineTag is null)
        {
            AddLog("Select a pipeline, or a node inside one, before running a check.", LogSeverity.Warning);
            return;
        }

        var scope = templateId is not null ? PreflightScope.Template : PreflightScope.Pipeline;
        PreflightReport report;
        try
        {
            report = RunPreflight(preflight, node, pipelineTag, scope, templateId);
        }
        catch (Exception ex)
        {
            _appLogger.Error("Preflight", "Pre-flight check failed to run", ex);
            AddLog($"Pre-flight check failed to run: {ex.Message}", LogSeverity.Error);
            return;
        }

        LastPreflightReport = report;
        AddLog($"Pre-flight for {report.Target}: {report.Summary}.",
            report.HasErrors ? LogSeverity.Error : report.HasWarnings ? LogSeverity.Warning : LogSeverity.Success);

        SaveReport(report);
        ShowPreflightDialog(
            report,
            checkOnly: true,
            recheck: () => RunPreflight(preflight, node, pipelineTag, scope, templateId));
    }
}

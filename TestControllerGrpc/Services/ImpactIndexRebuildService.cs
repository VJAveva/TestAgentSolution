using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Core.Impact.Index;

namespace TestControllerGrpc.Services;

/// <summary>
/// Owns the interactive index rebuild. Singleton, so the build survives the sign-in dialog closing and the
/// UI never has to block on it — the recovery path for when the writer host's own ADO credential is unusable.
/// </summary>
public interface IImpactIndexRebuildService
{
    bool IsRebuilding { get; }
    string StatusText { get; }

    /// <summary>Starts a full rebuild. No-op when one is already running. Must be called from the UI thread.</summary>
    void Start();

    void Cancel();
}

public sealed partial class ImpactIndexRebuildService : ObservableObject, IImpactIndexRebuildService
{
    private readonly RetrievalIndexBuilder _builder;
    private readonly IAppLogger _logger;
    private CancellationTokenSource? _cts;

    public ImpactIndexRebuildService(RetrievalIndexBuilder builder, IAppLogger logger)
    {
        _builder = builder;
        _logger = logger;
    }

    [ObservableProperty] private bool _isRebuilding;
    [ObservableProperty] private string _statusText = "";

    public void Start()
    {
        if (IsRebuilding)
            return;

        _cts = new CancellationTokenSource();
        IsRebuilding = true;
        StatusText = "Starting full rebuild\u2026 this can take a long time.";

        // Started from the UI thread so continuations marshal back for binding; the build itself is off-thread.
        _ = RunAsync(new Progress<ImpactMappingProgress>(p => StatusText = p.Message));
    }

    public void Cancel() => _cts?.Cancel();

    private async Task RunAsync(IProgress<ImpactMappingProgress> progress)
    {
        try
        {
            IndexBuildResult result = await _builder.BuildAsync(fullRebuild: true, progress, _cts!.Token);
            StatusText =
                $"Rebuilt in {result.Elapsed:hh\\:mm\\:ss} \u00b7 {result.DocumentsIndexed} indexed, " +
                $"{result.DocumentsSkipped} skipped ({result.TestCasesSeen} test cases, {result.FeaturesSeen} features).";
            _logger.Info("ImpactIndex", $"Interactive rebuild complete: {StatusText}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled. Documents written so far are kept; run it again to finish.";
            _logger.Warn("ImpactIndex", "Interactive index rebuild cancelled by the user.");
        }
        catch (Exception ex)
        {
            StatusText = $"Rebuild failed: {ex.Message}";
            _logger.Error("ImpactIndex", "Interactive index rebuild failed.", ex);
        }
        finally
        {
            IsRebuilding = false;
            _cts?.Dispose();
            _cts = null;
        }
    }
}

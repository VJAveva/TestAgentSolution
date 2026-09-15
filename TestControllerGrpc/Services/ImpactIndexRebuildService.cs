using System;
using System.ComponentModel;
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
public interface IImpactIndexRebuildService : INotifyPropertyChanged
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
    private readonly object _gate = new();
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
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_cts is not null)
                return;
            cts = _cts = new CancellationTokenSource();
        }

        IsRebuilding = true;
        StatusText = "Starting full rebuild\u2026 this can take a long time.";

        // Constructed on the UI thread so progress callbacks marshal back for binding; Task.Run keeps
        // BuildAsync's synchronous prologue (DbContext create, schema check) off the UI thread entirely.
        var progress = new Progress<ImpactMappingProgress>(p => StatusText = p.Message);
        _ = Task.Run(() => RunAsync(cts, progress));
    }

    public void Cancel()
    {
        // Cancel races RunAsync's cleanup; the gate plus the null-out stop us cancelling a disposed source.
        lock (_gate)
            _cts?.Cancel();
    }

    private async Task RunAsync(CancellationTokenSource cts, IProgress<ImpactMappingProgress> progress)
    {
        try
        {
            IndexBuildResult result = await _builder.BuildAsync(fullRebuild: true, progress, cts.Token);
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
            lock (_gate)
            {
                _cts = null;
                cts.Dispose();
            }
            IsRebuilding = false;
        }
    }
}

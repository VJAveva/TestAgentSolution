using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Core.Impact;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.Regression;

/// <summary>Flat display row for the selected-test-case grid.</summary>
public sealed record MappedTestCaseRow(
    int Id, string Title, int FeatureId, int Grade, MappingConfidence Confidence, double FinalScore,
    AnchorSource? Anchor, IReadOnlyList<ProvenanceLink> Provenance);

/// <summary>
/// Regression-tab view model for the impact-mapping engine (P30). Drives the same host-agnostic
/// <see cref="IImpactTestMappingService"/> the WebApi host uses, so both surfaces produce identical results.
/// Progress is consumed via <c>MapWithProgressAsync</c>; because the command starts on the UI thread and the
/// method never uses <c>ConfigureAwait(false)</c>, each tick resumes on the UI thread — marshalling happens at
/// the view-model boundary, exactly once, without any host type reaching Core.
/// </summary>
public sealed partial class ImpactMappingViewModel : ObservableObject
{
    private readonly IImpactTestMappingService _engine;
    private readonly IAppLogger _logger;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Idle";

    [ObservableProperty]
    private bool _earlyExit;

    [ObservableProperty]
    private string? _earlyExitReason;

    [ObservableProperty]
    private SelectionTier _selectedTier = SelectionTier.Targeted;

    [ObservableProperty]
    private MappedTestCaseRow? _selectedTestCase;

    [ObservableProperty]
    private string _budgetSummary = string.Empty;

    [ObservableProperty]
    private int _droppedByBudget;

    [ObservableProperty]
    private int _droppedByGrade;

    [ObservableProperty]
    private int _droppedByDiversity;

    /// <summary>Coverage gaps, pinned at the top of the tab, most-severe first.</summary>
    public ObservableCollection<CoverageGap> Gaps { get; } = [];

    /// <summary>The selected test cases with their scores and provenance.</summary>
    public ObservableCollection<MappedTestCaseRow> SelectedTestCases { get; } = [];

    /// <summary>The provenance chain of the currently selected row, rendered as a horizontal strip.</summary>
    public ObservableCollection<ProvenanceLink> Provenance { get; } = [];

    /// <summary>The available selection tiers for the scope selector.</summary>
    public IReadOnlyList<SelectionTier> Tiers { get; } = Enum.GetValues<SelectionTier>();

    /// <summary>The change to map; set by the tab when the operator picks an impacted area.</summary>
    public ImpactedArea? CurrentArea { get; set; }

    /// <summary>The change payload to map; set alongside <see cref="CurrentArea"/>.</summary>
    public ChangePayload? CurrentPayload { get; set; }

    /// <summary>Creates the view model over the impact-mapping engine.</summary>
    public ImpactMappingViewModel(IImpactTestMappingService engine, IAppLogger logger)
    {
        _engine = engine;
        _logger = logger;
    }

    /// <summary>Maps the currently selected change, streaming progress into the tab.</summary>
    [RelayCommand]
    private async Task Run()
    {
        if (CurrentArea is { } area && CurrentPayload is { } payload)
        {
            await RunAsync(area, payload);
        }
        else
        {
            StatusMessage = "Select an impacted area to map.";
        }
    }

    /// <summary>Runs the engine for a specific change and populates the tab from the streamed result.</summary>
    public async Task RunAsync(ImpactedArea area, ChangePayload payload, CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ClearResults();
        try
        {
            await foreach (ImpactMappingProgress tick in _engine.MapWithProgressAsync(area, payload, SelectedTier, ct))
            {
                StatusMessage = $"{tick.Stage} ({tick.StageIndex}/{tick.TotalStages}) — {tick.Message}";
                if (tick.Result is { } result)
                {
                    ApplyResult(result);
                }
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            _logger.Error("ImpactMap", "Impact mapping failed.", ex);
            StatusMessage = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyResult(ImpactMappingResult result)
    {
        Gaps.Clear();
        foreach (CoverageGap gap in result.Gaps.OrderByDescending(g => g.RiskTier))
        {
            Gaps.Add(gap);
        }

        SelectedTestCases.Clear();
        foreach (MappedTestCase mapped in result.MappedTestCases)
        {
            SelectedTestCases.Add(new MappedTestCaseRow(
                mapped.TestCase.Item.Id, mapped.TestCase.Item.Title, mapped.FeatureId,
                mapped.Judgement?.Grade ?? 0, mapped.Confidence, mapped.FinalScore, mapped.Anchor, mapped.Provenance));
        }

        EarlyExit = result.EarlyExit;
        EarlyExitReason = result.EarlyExit
            ? $"Early exit — anchored by {result.Anchors.Edges.Count} deterministic edge(s)."
            : null;

        SelectionDiagnostics diagnostics = result.Diagnostics;
        DroppedByBudget = diagnostics.DroppedByBudget;
        DroppedByGrade = diagnostics.DroppedByGrade;
        DroppedByDiversity = diagnostics.DroppedByDiversity;
        string allowed = diagnostics.BudgetAllowed == TimeSpan.MaxValue ? "∞" : diagnostics.BudgetAllowed.ToString(@"hh\:mm\:ss");
        BudgetSummary = $"Budget {diagnostics.BudgetUsed:hh\\:mm\\:ss} / {allowed}";
    }

    private void ClearResults()
    {
        Gaps.Clear();
        SelectedTestCases.Clear();
        Provenance.Clear();
        SelectedTestCase = null;
        EarlyExit = false;
        EarlyExitReason = null;
        BudgetSummary = string.Empty;
        DroppedByBudget = 0;
        DroppedByGrade = 0;
        DroppedByDiversity = 0;
    }

    partial void OnSelectedTestCaseChanged(MappedTestCaseRow? value)
    {
        Provenance.Clear();
        if (value is null)
        {
            return;
        }

        foreach (ProvenanceLink link in value.Provenance)
        {
            Provenance.Add(link);
        }
    }
}

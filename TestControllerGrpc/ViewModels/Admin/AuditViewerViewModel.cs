using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.Admin;

/// <summary>
/// ViewModel for Audit Viewer (Mockup 8). Admin-only.
/// CommunityToolkit.Mvvm source-generated. Singleton DI.
/// </summary>
public sealed partial class AuditViewerViewModel : ObservableObject
{
    private readonly AuditClient _auditClient;
    private readonly IAppLogger _logger;

    // ── Filters ──────────────────────────────────────────────────────────
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private string _userIdFilter = "";
    [ObservableProperty] private string _actionFilter = "";
    [ObservableProperty] private string _decisionFilter = "All";   // "All", "Allowed", "Denied"
    [ObservableProperty] private string _resourceFilter = "";
    [ObservableProperty] private string _reasonFilter = "";

    // ── Pagination ───────────────────────────────────────────────────────
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _pageSize = 200;
    [ObservableProperty] private int _totalPages;

    // ── UI state ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private ObservableCollection<AuditEntryDto> _entries = [];

    public AuditViewerViewModel(AuditClient auditClient, IAppLogger logger)
    {
        _auditClient = auditClient;
        _logger = logger;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            var request = BuildRequest();
            var page = await _auditClient.QueryAsync(request);
            if (page is null)
            {
                ErrorMessage = "Failed to query audit log";
                return;
            }

            Entries = new ObservableCollection<AuditEntryDto>(page.Items);
            TotalCount = page.Total;
            CurrentPage = page.Page;
            TotalPages = (int)Math.Ceiling((double)page.Total / PageSize);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Unexpected error";
            _logger.Error("AuditViewer", "Search failed", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CurrentPage >= TotalPages) return;
        CurrentPage++;
        await SearchAsync();
    }

    [RelayCommand]
    private async Task PrevPageAsync()
    {
        if (CurrentPage <= 1) return;
        CurrentPage--;
        await SearchAsync();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        FromDate = null;
        ToDate = null;
        UserIdFilter = "";
        ActionFilter = "";
        DecisionFilter = "All";
        ResourceFilter = "";
        ReasonFilter = "";
        CurrentPage = 1;
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"audit_export_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dialog.ShowDialog() != true) return;

        IsLoading = true;
        try
        {
            var request = BuildRequest();
            var success = await _auditClient.ExportCsvAsync(request, dialog.FileName);
            if (!success)
                ErrorMessage = "Export failed";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private AuditQueryRequest BuildRequest() => new()
    {
        From = FromDate,
        To = ToDate,
        UserId = string.IsNullOrWhiteSpace(UserIdFilter) ? null : UserIdFilter.Trim(),
        ActionName = string.IsNullOrWhiteSpace(ActionFilter) ? null : ActionFilter.Trim(),
        Decision = DecisionFilter switch
        {
            "Allowed" => true,
            "Denied" => false,
            _ => null
        },
        ResourceId = string.IsNullOrWhiteSpace(ResourceFilter) ? null : ResourceFilter.Trim(),
        ReasonCode = string.IsNullOrWhiteSpace(ReasonFilter) ? null : ReasonFilter.Trim(),
        Page = CurrentPage,
        PageSize = PageSize,
    };
}

using System.Net.Http;
using System.Net.Http.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// ViewModel for the Force-Release Reason Dialog (Mockup 9).
/// Enforces reason >= 10 chars + affirmation checkbox before submit is enabled.
/// </summary>
public sealed partial class ForceReleaseReasonDialogViewModel : ObservableObject
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AuthClient _authClient;
    private readonly IAppLogger _logger;

    private string _pipelineId = "";
    private string _ownerDisplayName = "";

    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private bool _isAffirmationChecked;
    [ObservableProperty] private bool _canSubmit;
    [ObservableProperty] private string _characterCounter = "0 / 10 minimum";
    [ObservableProperty] private string _affirmationLabel = "";
    [ObservableProperty] private bool _isSubmitting;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _submitSucceeded;

    public ForceReleaseReasonDialogViewModel(
        IHttpClientFactory httpClientFactory,
        AuthClient authClient,
        IAppLogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _authClient = authClient;
        _logger = logger;
    }

    /// <summary>Initialize with the lock context from the conflict dialog.</summary>
    public void Initialize(string pipelineId, string ownerDisplayName)
    {
        _pipelineId = pipelineId;
        _ownerDisplayName = ownerDisplayName;
        AffirmationLabel = $"I confirm that {ownerDisplayName}'s run will be cancelled";
        UpdateCanSubmit();
    }

    partial void OnReasonChanged(string value)
    {
        CharacterCounter = $"{value.Length} / 10 minimum";
        UpdateCanSubmit();
    }

    partial void OnIsAffirmationCheckedChanged(bool value)
    {
        UpdateCanSubmit();
    }

    private void UpdateCanSubmit()
    {
        CanSubmit = Reason.Length >= 10 && IsAffirmationChecked && !IsSubmitting;
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        if (Reason.Length < 10) return;

        IsSubmitting = true;
        ErrorMessage = null;
        UpdateCanSubmit();

        try
        {
            var http = _httpClientFactory.CreateClient("SystemMode");
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/locks/{Uri.EscapeDataString(_pipelineId)}/force-release");
            if (_authClient.Token is not null)
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authClient.Token);
            request.Content = JsonContent.Create(new { reason = Reason });

            var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                SubmitSucceeded = true;
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync();
                ErrorMessage = $"Force-release failed: {response.StatusCode}";
                _logger.Error("LockForceRelease", $"Force-release failed for {_pipelineId}: {response.StatusCode} — {body}");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Request failed: {ex.Message}";
            _logger.Error("LockForceRelease", "Force-release request exception", ex);
        }
        finally
        {
            IsSubmitting = false;
            UpdateCanSubmit();
        }
    }
}

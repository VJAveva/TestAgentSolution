using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.ViewModels.AgentWorkspace;

public partial class RegistryVM : ObservableObject
{
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly AgentLockManager _lockManager;

    public ObservableCollection<RegistryRowVM> Rows { get; } = new();

    [ObservableProperty] private RegistryRowVM? _selectedRow;
    [ObservableProperty] private bool _isEditing;

    // Edit form fields
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editAddress = "http://localhost:5200";
    [ObservableProperty] private string _testResult = "";

    /// <summary>True when editing an existing agent (not adding new).</summary>
    private bool _isEditingExisting;
    public bool IsEditingExisting
    {
        get => _isEditingExisting;
        set => SetProperty(ref _isEditingExisting, value);
    }

    // Diagnostic
    private string _diagnosticResult = "";
    public string DiagnosticResult
    {
        get => _diagnosticResult;
        set => SetProperty(ref _diagnosticResult, value);
    }

    private bool _hasDiagnosticResult;
    public bool HasDiagnosticResult
    {
        get => _hasDiagnosticResult;
        set => SetProperty(ref _hasDiagnosticResult, value);
    }

    private readonly Dispatcher _uiDispatcher;
    private readonly DispatcherTimer _healthTimer;
    private bool _isPolling;

    public RegistryVM(IAgentGrpcDispatcher dispatcher, AgentLockManager lockManager, IEventAggregator events, Dispatcher uiDispatcher)
    {
        _dispatcher = dispatcher;
        _lockManager = lockManager;
        _uiDispatcher = uiDispatcher;

        events.Subscribe<AgentLocksChangedEvent>(_ => uiDispatcher.InvokeAsync(Refresh));
        events.Subscribe<ExecutionStartedEvent>(_ => uiDispatcher.InvokeAsync(Refresh));
        events.Subscribe<ExecutionCompletedEvent>(_ => uiDispatcher.InvokeAsync(Refresh));

        // 5-second periodic health check: probes all agents for live status + latency
        _healthTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(5),
            DispatcherPriority.Background,
            async (_, _) => await PollHealthAsync(),
            uiDispatcher);
        _healthTimer.Start();

        Refresh();
    }

    private async Task PollHealthAsync()
    {
        // Prevent overlapping polls (e.g. when multiple agents are timing out)
        if (_isPolling) return;
        _isPolling = true;
        try
        {
            foreach (var row in Rows.ToList())
            {
                // Skip agents that are currently locked (executing) — no need to probe them
                if (_lockManager.GetLock(row.Name) != null)
                {
                    row.Status = "Busy";
                    continue;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (snapshot, _) = await _dispatcher.TestConnectionAsync(row.Name);
                sw.Stop();

                if (snapshot != null)
                {
                    row.Status = "Online";
                    row.LatencyMs = (int)sw.ElapsedMilliseconds;
                }
                else
                {
                    row.Status = "Offline";
                    row.LatencyMs = -1;
                }
            }
        }
        finally
        {
            _isPolling = false;
        }
    }

    public void Refresh()
    {
        Rows.Clear();
        var allHealth = _dispatcher.GetAllAgentHealth();
        var allLocks = _lockManager.GetAllLocks();

        foreach (var name in _dispatcher.RegisteredAgents)
        {
            var address = _dispatcher.GetAgentAddress(name) ?? "";
            allHealth.TryGetValue(name, out var health);

            var hasLock = allLocks.Any(l =>
                string.Equals(l.AgentName, name, StringComparison.OrdinalIgnoreCase));

            string status;
            if (hasLock)
                status = "Busy";
            else if (health?.IsHealthy == false)
                status = "Offline";
            else
                status = "Online";

            Rows.Add(new RegistryRowVM
            {
                Name = name,
                Address = address,
                Status = status,
            });
        }
    }

    partial void OnSelectedRowChanged(RegistryRowVM? value)
    {
        if (value != null)
        {
            EditName = value.Name;
            EditAddress = value.Address;
            IsEditing = true;
            IsEditingExisting = true;
            TestResult = "";
        }
    }

    public void StartAddNew()
    {
        SelectedRow = null;
        EditName = "";
        EditAddress = "http://localhost:5200";
        TestResult = "";
        IsEditing = true;
        IsEditingExisting = false;
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            MessageBox.Show("Agent name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(EditAddress))
        {
            MessageBox.Show("Address is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var isNewAgent = SelectedRow == null;
        var agentName = EditName.Trim();
        var agentAddress = EditAddress.Trim();

        // Prevent duplicate: another agent already uses this address
        var existingWithAddress = Rows.FirstOrDefault(r =>
            string.Equals(r.Address, agentAddress, StringComparison.OrdinalIgnoreCase) &&
            (SelectedRow == null || !string.Equals(r.Name, SelectedRow.Name, StringComparison.OrdinalIgnoreCase)));
        if (existingWithAddress != null)
        {
            MessageBox.Show(
                $"Address '{agentAddress}' is already registered to agent '{existingWithAddress.Name}'.",
                "Duplicate Address", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (SelectedRow != null)
        {
            // Update: unregister old, register new
            if (SelectedRow.Name != EditName || SelectedRow.Address != EditAddress)
            {
                _dispatcher.UnregisterAgent(SelectedRow.Name);
                _dispatcher.RegisterAgent(EditName, EditAddress);
            }
        }
        else
        {
            // New registration
            _dispatcher.RegisterAgent(EditName, EditAddress);
        }

        Refresh();
        ClearForm();

        // Show success message
        var message = isNewAgent 
            ? $"Agent '{agentName}' registered successfully." 
            : $"Agent '{agentName}' updated successfully.";
        MessageBox.Show(message, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private async Task TestConnection()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            TestResult = "Enter agent name first";
            return;
        }

        TestResult = "Testing...";

        // Temporarily register to test, then unregister if it wasn't already registered
        bool wasRegistered = _dispatcher.GetAgentAddress(EditName) != null;
        if (!wasRegistered)
            _dispatcher.RegisterAgent(EditName, EditAddress);

        var (snapshot, error) = await _dispatcher.TestConnectionAsync(EditName);

        if (!wasRegistered)
            _dispatcher.UnregisterAgent(EditName);

        TestResult = snapshot != null
            ? $"\u2713 Connected \u2014 {snapshot.AgentName}"
            : $"\u2717 Failed: {error}";
    }

    [RelayCommand]
    private async Task DiagnoseAgent()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            TestResult = "Enter agent name first";
            return;
        }

        TestResult = "Diagnosing...";

        // Temporarily register if needed so DiagnoseAgentAsync can work
        bool wasRegistered = _dispatcher.GetAgentAddress(EditName) != null;
        if (!wasRegistered)
            _dispatcher.RegisterAgent(EditName, EditAddress);

        var steps = await _dispatcher.DiagnoseAgentAsync(EditName);

        if (!wasRegistered)
            _dispatcher.UnregisterAgent(EditName);

        var sb = new StringBuilder();
        sb.AppendLine($"Diagnostic for: {EditName}");
        sb.AppendLine(new string('\u2500', 30));
        foreach (var step in steps)
        {
            var icon = step.Passed ? "\u2713" : "\u2717";
            sb.AppendLine($"  {icon} [{step.Name}] {step.Detail}");
        }

        var allPassed = steps.All(s => s.Passed || !s.IsFatal);
        sb.AppendLine();
        sb.AppendLine(allPassed ? "\u2713 All checks passed" : "\u2717 One or more checks failed");

        TestResult = sb.ToString().TrimEnd();
    }

    [RelayCommand]
    private void CopyDetails()
    {
        if (string.IsNullOrWhiteSpace(TestResult))
            return;

        var details = $"Agent: {EditName}\nAddress: {EditAddress}\n\n{TestResult}";
        Clipboard.SetText(details);
    }

    [RelayCommand]
    private void Unregister()
    {
        if (SelectedRow == null) return;

        var agentName = SelectedRow.Name;
        var result = MessageBox.Show(
            $"Unregister agent '{agentName}'?\n\nPipelines using this agent will fail.",
            "Unregister Agent", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _dispatcher.UnregisterAgent(agentName);
        Refresh();
        ClearForm();

        MessageBox.Show($"Agent '{agentName}' unregistered successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void AddNew() => StartAddNew();

    [RelayCommand]
    private void Cancel()
    {
        ClearForm();
    }

    [RelayCommand]
    private async Task TestAll()
    {
        if (Rows.Count == 0)
        {
            DiagnosticResult = "No agents registered.";
            HasDiagnosticResult = true;
            return;
        }

        DiagnosticResult = "Testing all agents...";
        HasDiagnosticResult = true;

        var sb = new StringBuilder();
        int online = 0, offline = 0;

        foreach (var row in Rows)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (snapshot, error) = await _dispatcher.TestConnectionAsync(row.Name);
            sw.Stop();

            if (snapshot != null)
            {
                row.Status = "Online";
                row.LatencyMs = (int)sw.ElapsedMilliseconds;
                online++;
                sb.AppendLine($"  \u2713 {row.Name} \u2014 Online ({sw.ElapsedMilliseconds}ms)");
            }
            else
            {
                row.Status = "Offline";
                row.LatencyMs = -1;
                offline++;
                sb.AppendLine($"  \u2717 {row.Name} \u2014 {error}");
            }
        }

        sb.Insert(0, $"Test complete: {online} online, {offline} offline\n");
        DiagnosticResult = sb.ToString().TrimEnd();
        HasDiagnosticResult = true;
    }

    [RelayCommand]
    private void ClearDiagnostic()
    {
        DiagnosticResult = "";
        HasDiagnosticResult = false;
    }

    /// <summary>Clears the form and hides the edit panel.</summary>
    private void ClearForm()
    {
        IsEditing = false;
        IsEditingExisting = false;
        SelectedRow = null;
        EditName = "";
        EditAddress = "http://localhost:5200";
        TestResult = "";
    }

    private static string FormatLastSeen(DateTime? lastSeen)
    {
        if (!lastSeen.HasValue) return "never";
        var diff = DateTime.UtcNow - lastSeen.Value;
        if (diff.TotalSeconds < 60) return $"{(int)diff.TotalSeconds}s ago";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        return $"{(int)diff.TotalDays}d ago";
    }
}

public partial class RegistryRowVM : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _lastSeen = "";
    [ObservableProperty] private int _latencyMs = -1;  // -1 = not measured
}

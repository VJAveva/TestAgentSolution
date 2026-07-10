using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestControllerGrpc.Services;

using TestControllerGrpc.Views.Dialogs;

namespace TestControllerGrpc.ViewModels.AgentWorkspace;

public partial class RegistryVM : ObservableObject, IDisposable
{
    private bool _disposed;
    private readonly IAgentGrpcDispatcher _dispatcher;
    private readonly AgentLockManager _lockManager;

    public ObservableCollection<RegistryRowVM> Rows { get; } = new();

    [ObservableProperty] private RegistryRowVM? _selectedRow;
    [ObservableProperty] private bool _isEditing;

    // Edit form fields
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editAddress = "http://localhost:5200";
    [ObservableProperty] private string _testResult = "";

    private const int DefaultGrpcPort = 5200;
    private bool _addressManuallyEdited;

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _healthTimer.Stop();
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
            _addressManuallyEdited = true; // existing agent has its own address — don't overwrite
            EditName = value.Name;
            EditAddress = value.Address;
            IsEditing = true;
            IsEditingExisting = true;
            TestResult = "";
        }
    }

    /// <summary>Auto-fill gRPC address when hostname changes (unless manually edited).</summary>
    partial void OnEditNameChanged(string value)
    {
        if (_addressManuallyEdited) return;
        if (string.IsNullOrWhiteSpace(value))
        {
            EditAddress = $"http://localhost:{DefaultGrpcPort}";
        }
        else
        {
            EditAddress = $"http://{value.Trim()}:{DefaultGrpcPort}";
        }
    }

    /// <summary>Track manual edits to the address field.</summary>
    partial void OnEditAddressChanged(string value)
    {
        // If the new value doesn't match the auto-generated pattern, mark as manually edited
        var expectedAuto = string.IsNullOrWhiteSpace(EditName)
            ? $"http://localhost:{DefaultGrpcPort}"
            : $"http://{EditName.Trim()}:{DefaultGrpcPort}";
        if (!string.Equals(value, expectedAuto, StringComparison.OrdinalIgnoreCase))
            _addressManuallyEdited = true;
    }

    public void StartAddNew()
    {
        SelectedRow = null;
        _addressManuallyEdited = false;
        EditName = "";
        EditAddress = $"http://localhost:{DefaultGrpcPort}";
        TestResult = "";
        IsEditing = true;
        IsEditingExisting = false;
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            ThemedMessageBox.Show("Agent name is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(EditAddress))
        {
            ThemedMessageBox.Show("Address is required.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            ThemedMessageBox.Show(
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
        ThemedMessageBox.Show(message, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var result = ThemedMessageBox.Show(
            $"Unregister agent '{agentName}'?\n\nPipelines using this agent will fail.",
            "Unregister Agent", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _dispatcher.UnregisterAgent(agentName);
        Refresh();
        ClearForm();

        ThemedMessageBox.Show($"Agent '{agentName}' unregistered successfully.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
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

    // ── Bulk fleet operations ───────────────────────────────────────────

    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Exports every registered agent (name + address) to a JSON file.</summary>
    [RelayCommand]
    private void ExportAgents()
    {
        if (Rows.Count == 0)
        {
            ThemedMessageBox.Show("No agents to export.", "Export Agents", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "JSON Files|*.json|All Files|*.*",
            Title = "Export Agents to JSON",
            DefaultExt = ".json",
            FileName = $"agents-{DateTime.Now:yyyy-MM-dd}.json",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var export = new AgentExportFile
            {
                ExportedUtc = DateTime.UtcNow,
                Agents = Rows.Select(r => new AgentExportEntry { Name = r.Name, Address = r.Address }).ToList(),
            };
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(export, ExportJsonOptions));
            ThemedMessageBox.Show(
                $"Exported {export.Agents.Count} agent(s) to:\n{dlg.FileName}",
                "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Imports an agent list from JSON and registers every entry simultaneously.</summary>
    [RelayCommand]
    private void ImportAgents()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON Files|*.json|All Files|*.*",
            Title = "Import Agents from JSON",
        };
        if (dlg.ShowDialog() != true) return;

        List<AgentExportEntry> entries;
        try
        {
            entries = ParseAgentImport(File.ReadAllText(dlg.FileName));
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"Import failed: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (entries.Count == 0)
        {
            ThemedMessageBox.Show(
                "No valid agents found in file.\nExpected { \"agents\": [ { \"name\", \"address\" } ] }.",
                "Import Agents", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int added = 0, updated = 0;
        foreach (var e in entries)
        {
            var existed = _dispatcher.GetAgentAddress(e.Name) != null;
            _dispatcher.RegisterAgent(e.Name, e.Address);
            if (existed) updated++; else added++;
        }

        Refresh();
        ThemedMessageBox.Show(
            $"Import complete: {added} added, {updated} updated.",
            "Import Agents", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Re-registers all currently listed agents simultaneously.</summary>
    [RelayCommand]
    private void RegisterAll()
    {
        if (Rows.Count == 0)
        {
            ThemedMessageBox.Show("No agents to register.", "Register All", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var snapshot = Rows.Select(r => (r.Name, r.Address)).ToList();
        foreach (var (name, address) in snapshot)
            _dispatcher.RegisterAgent(name, address);

        Refresh();
        ThemedMessageBox.Show($"Registered {snapshot.Count} agent(s).", "Register All", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Unregisters every listed agent simultaneously (confirmed, destructive).</summary>
    [RelayCommand]
    private void UnregisterAll()
    {
        if (Rows.Count == 0)
        {
            ThemedMessageBox.Show("No agents to unregister.", "Unregister All", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = ThemedMessageBox.Show(
            $"Unregister all {Rows.Count} agent(s)?\n\nPipelines using these agents will fail until they are re-registered. Export the list first if you want a backup.",
            "Unregister All Agents", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var names = Rows.Select(r => r.Name).ToList();
        int removed = 0;
        foreach (var name in names)
            if (_dispatcher.UnregisterAgent(name)) removed++;

        Refresh();
        ClearForm();
        ThemedMessageBox.Show($"Unregistered {removed} agent(s).", "Unregister All", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Parses an exported agent file. Accepts { "agents": [...] } or a bare [...] array.</summary>
    private static List<AgentExportEntry> ParseAgentImport(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        JsonElement arr;
        if (root.ValueKind == JsonValueKind.Array)
            arr = root;
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("agents", out var a) && a.ValueKind == JsonValueKind.Array)
            arr = a;
        else
            return new List<AgentExportEntry>();

        static string? GetProp(JsonElement el, string name)
        {
            foreach (var p in el.EnumerateObject())
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) &&
                    p.Value.ValueKind == JsonValueKind.String)
                    return p.Value.GetString();
            return null;
        }

        var list = new List<AgentExportEntry>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var name = GetProp(item, "name")?.Trim();
            var address = GetProp(item, "address")?.Trim();
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(address))
                list.Add(new AgentExportEntry { Name = name!, Address = address! });
        }
        return list;
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

/// <summary>Serialized shape of an exported agent list file.</summary>
public sealed class AgentExportFile
{
    public DateTime ExportedUtc { get; set; }
    public List<AgentExportEntry> Agents { get; set; } = new();
}

/// <summary>A single agent entry (name + gRPC address) within an export file.</summary>
public sealed class AgentExportEntry
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
}

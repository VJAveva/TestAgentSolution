using CommunityToolkit.Mvvm.ComponentModel;

namespace TestControllerGrpc.ViewModels;

/// <summary>
/// Represents a registered agent with live connectivity status.
/// Displayed in the Agent Registration panel with color-coded indicators.
/// </summary>
public sealed partial class AgentInfoViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _address = "";

    // Connection status: "Unknown", "Testing", "Online", "Offline", "Error"
    [ObservableProperty] private string _connectionStatus = "Unknown";
    [ObservableProperty] private string _statusColor = "#FF9399B2";   // TextS gray
    [ObservableProperty] private string _statusIcon = "\u2022";       // bullet

    // Agent details (populated after successful TestConnection)
    [ObservableProperty] private string _agentState = "";
    [ObservableProperty] private string _cpuUsage = "";
    [ObservableProperty] private string _memoryUsage = "";
    [ObservableProperty] private string _diskFree = "";
    [ObservableProperty] private string _lastChecked = "";
    [ObservableProperty] private string _detailLine = "";
    [ObservableProperty] private string _errorDetail = "";       // detailed failure reason
    [ObservableProperty] private bool   _isDiagnosing = false;   // spinner state
    [ObservableProperty] private int    _latencyMs = -1;         // round-trip latency; -1 = not measured

    partial void OnConnectionStatusChanged(string value)
    {
        switch (value)
        {
            case "Online":
                StatusIcon = "\u2714";          // ✔
                StatusColor = "#FFA6E3A1";      // Green
                ErrorDetail = "";
                break;
            case "Testing":
                StatusIcon = "\u25B6";          // ▶
                StatusColor = "#FF89B4FA";      // Blue
                break;
            case "Offline":
                StatusIcon = "\u2716";          // ✖
                StatusColor = "#FFF38BA8";      // Red
                break;
            case "Error":
                StatusIcon = "\u26A0";          // ⚠
                StatusColor = "#FFFAB387";      // Peach/orange
                break;
            default: // Unknown
                StatusIcon = "\u2022";          // •
                StatusColor = "#FF9399B2";      // Gray
                break;
        }
    }

    /// <summary>Format a one-line summary for display.</summary>
    public void UpdateDetailLine()
    {
        var latencyPart = LatencyMs >= 0 ? $"  Latency: {LatencyMs}ms" : "";
        DetailLine = ConnectionStatus switch
        {
            "Online" => $"{AgentState}  |  CPU: {CpuUsage}  Mem: {MemoryUsage}  Disk: {DiskFree}{latencyPart}",
            "Testing" => "Connecting...",
            "Offline" => string.IsNullOrEmpty(ErrorDetail) ? "Unreachable" : ErrorDetail,
            "Error" => string.IsNullOrEmpty(ErrorDetail) ? "Connection error" : ErrorDetail,
            _ => "Not tested"
        };
        LastChecked = DateTime.Now.ToString("HH:mm:ss");
    }
}

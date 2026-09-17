using System.Windows;
using System.Windows.Media;

namespace TestAgentDisplay.Services;

/// <summary>
/// Semantic brush keys and the single place that resolves them. View models name a MEANING
/// ("agent is running"), never a colour, so the palette lives entirely in App.xaml and can be
/// re-themed without touching C#.
/// </summary>
public static class ThemeBrushes
{
    public const string AgentReady = "AgentStateReadyBrush";
    public const string AgentRunning = "AgentStateRunningBrush";
    public const string AgentInactive = "AgentStateInactiveBrush";
    public const string AgentOffline = "AgentStateOfflineBrush";
    public const string AgentUpdatePending = "AgentStateUpdatePendingBrush";
    public const string AgentRebootRequired = "AgentStateRebootRequiredBrush";

    public const string OutputMuted = "OutputMutedBrush";
    public const string OutputCommand = "OutputCommandBrush";
    public const string OutputStdout = "OutputStdoutBrush";
    public const string OutputWarn = "OutputWarnBrush";
    public const string OutputSuccess = "OutputSuccessBrush";
    public const string OutputError = "OutputErrorBrush";
    public const string OutputSeparator = "OutputSeparatorBrush";

    /// <summary>
    /// Resolves a resource key to a frozen brush. Returns <paramref name="fallback"/> when there is
    /// no Application (unit tests) or the key is missing, so a view model never depends on WPF being up.
    /// </summary>
    public static Brush Resolve(string key, Brush fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush found)
        {
            if (found.IsFrozen) return found;

            // Clone before freezing: freezing the dictionary's own instance would block re-theming.
            var copy = found.Clone();
            copy.Freeze();
            return copy;
        }
        return fallback;
    }
}

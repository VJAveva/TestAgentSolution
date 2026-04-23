using TestControllerGrpc.Models;

namespace TestController.Api;

/// <summary>
/// Shared utilities for API controllers and endpoints.
/// Eliminates duplication of helper methods across WPF and standalone hosts.
/// </summary>
public static class WatchListHelpers
{
    /// <summary>
    /// Finds the first Initialize node's ParameterFile in a WatchItem's event tree.
    /// Returns the resolved file path, or null if none found.
    /// </summary>
    public static string? FindInitializeFile(WatchItemConfig watchItem)
    {
        foreach (var ev in watchItem.Events)
        {
            var file = FindInitializeFileInChildren(ev.Children);
            if (file != null) return file;
        }
        return null;
    }

    private static string? FindInitializeFileInChildren(List<IActionNode> children)
    {
        foreach (var child in children)
        {
            if (child is InitializeConfig init && !string.IsNullOrWhiteSpace(init.ParameterFile))
                return init.ParameterFile;
            if (child is ActionGroupConfig group)
            {
                var file = FindInitializeFileInChildren(group.Children);
                if (file != null) return file;
            }
        }
        return null;
    }
}

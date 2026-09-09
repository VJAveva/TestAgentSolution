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
        => FindInitializeFiles(watchItem).FirstOrDefault();

    /// <summary>
    /// Every distinct ParameterFile in a WatchItem's event tree, in declaration order.
    /// A pipeline may initialise several files (e.g. Warm and Sanity stages); applying a build
    /// number to only the first leaves the others on a stale build.
    /// </summary>
    public static IReadOnlyList<string> FindInitializeFiles(WatchItemConfig watchItem)
    {
        var files = new List<string>();
        foreach (var ev in watchItem.Events)
            CollectInitializeFiles(ev.Children, files);
        return files;
    }

    private static void CollectInitializeFiles(List<IActionNode> children, List<string> into)
    {
        foreach (var child in children)
        {
            if (child is InitializeConfig init && !string.IsNullOrWhiteSpace(init.ParameterFile))
            {
                if (!into.Contains(init.ParameterFile, StringComparer.OrdinalIgnoreCase))
                    into.Add(init.ParameterFile);
            }
            else if (child is ActionGroupConfig group)
            {
                CollectInitializeFiles(group.Children, into);
            }
        }
    }
}

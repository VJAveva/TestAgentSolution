using System.Text.RegularExpressions;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Silent, idempotent normalizer that upgrades a loaded <see cref="WatchListConfig"/>
/// to the canonical shape before it is edited or saved (WatchItem Builder — Phase 5).
///
/// These are safe, non-destructive fixes for real-world inconsistencies that have
/// crept into hand-edited vocabulary files. Running it twice produces no further
/// changes. The builder runs this on load and reports the applied migrations in the
/// log panel; it never silently drops meaningful content.
///
/// Adapted to the real model: <see cref="EventConfig.ExecutionType"/> and
/// <see cref="ActionGroupConfig.ExecutionType"/> are the <see cref="ExecutionMode"/>
/// enum (never blank), so the "default blank ExecutionType" migration from the spec
/// is a no-op here and is intentionally omitted.
/// </summary>
public static partial class WatchListMigrator
{
    [GeneratedRegex(@"^[A-Za-z]+_\w+_\d{8}\.\d+$")]
    private static partial Regex LiteralBuildRegex();

    /// <summary>
    /// Applies all migrations in place and reports whether anything changed.
    /// </summary>
    /// <returns>
    /// <paramref name="config"/> (the same instance, mutated), a flag indicating
    /// whether any migration applied, and human-readable notes for the log panel.
    /// </returns>
    public static (WatchListConfig Config, bool Changed, IReadOnlyList<string> Notes) Upgrade(
        WatchListConfig config)
    {
        var notes = new List<string>();

        foreach (var wi in config.WatchItems)
        {
            // 1. A literal build number sitting in the FIELD-NAME slot → reset to the key name.
            if (LiteralBuildRegex().IsMatch(wi.BuildNumberField))
            {
                notes.Add($"WatchItem[{wi.Tag}]: BuildNumberField '{wi.BuildNumberField}' " +
                          "looked like a literal build number; reset to 'BuildNumber'.");
                wi.BuildNumberField = "BuildNumber";
            }

            // 2. A literal UNC path in the DropLocation FIELD-NAME slot → reset to the key name.
            if (!string.IsNullOrEmpty(wi.DropLocationField) &&
                wi.DropLocationField.StartsWith(@"\\", StringComparison.Ordinal))
            {
                notes.Add($"WatchItem[{wi.Tag}]: DropLocationField '{wi.DropLocationField}' " +
                          "looked like a literal path; reset to 'DropLocation'.");
                wi.DropLocationField = "DropLocation";
            }

            // 3. Remove empty ActionGroups (recursively) — they do nothing and clutter the tree.
            foreach (var ev in wi.Events)
                RemoveEmptyGroups(ev.Children, $"WatchItem[{wi.Tag}].Event[{ev.Type}]", notes);
        }

        return (config, notes.Count > 0, notes);
    }

    /// <summary>
    /// Recursively prunes <see cref="ActionGroupConfig"/> nodes that have no children.
    /// Recurses first so nested empties bubble up and are removed in a single pass.
    /// </summary>
    private static void RemoveEmptyGroups(List<IActionNode> children, string context, List<string> notes)
    {
        for (var i = children.Count - 1; i >= 0; i--)
        {
            if (children[i] is not ActionGroupConfig group)
                continue;

            RemoveEmptyGroups(group.Children, $"{context}.Group[{group.Tag}]", notes);

            if (group.Children.Count == 0)
            {
                notes.Add($"{context}: removed empty ActionGroup '{group.Tag}'.");
                children.RemoveAt(i);
            }
        }
    }
}

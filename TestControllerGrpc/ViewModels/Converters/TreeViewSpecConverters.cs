using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using TestControllerGrpc.ViewModels;

namespace TestControllerGrpc.ViewModels.Converters;

/// <summary>
/// Extracts just the filename from a full path for compact file-pill display.
/// The full path is shown in ToolTip via a separate binding.
/// </summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrWhiteSpace(path))
        {
            try { return Path.GetFileName(path); }
            catch { return path; }
        }
        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Produces the tree label for non-Action nodes by cleaning ResolvedDisplayText.
/// For Action nodes, use <see cref="ActionLabelConverter"/> instead.
/// </summary>
public sealed class StripExecutionModeConverter : IValueConverter
{
    private static readonly Regex ExecutionModeBrackets = new(
        @"\[(Sequential|Parallel)\]\s*", RegexOptions.Compiled);

    private static readonly Regex ExecutionModeParens = new(
        @"\s*\((Sequential|Parallel)\)", RegexOptions.Compiled);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text))
            return value ?? string.Empty;

        // Strip bracketed and parenthesized execution modes
        text = ExecutionModeBrackets.Replace(text, "");
        text = ExecutionModeParens.Replace(text, "");

        // "WatchList (name — N items)" → "Test Plans (name — N items)"
        if (text.StartsWith("WatchList"))
            text = "Test Plans" + text["WatchList".Length..];

        // "Event: Type" → "Type"
        if (text.StartsWith("Event: "))
            text = text["Event: ".Length..];

        // "Initialize: C:\...\file.txt" → "Initialize"
        if (text.StartsWith("Initialize:"))
            text = "Initialize";

        // "Ref > ID" → "ID"
        if (text.StartsWith("Ref > "))
            text = text["Ref > ".Length..];

        // Fix grammar: "— 1 items" → "— 1 item"
        text = Regex.Replace(text, @"\b1 items\b", "1 item");

        return text.Trim();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Multi-value converter for Action node labels. Produces:
///   Remote: "AgentName:Tag"   (or "AgentName:&lt;unnamed&gt;" if tag looks auto-generated)
///   Local:  "Tag"
///   Email:  "Recipient:Tag"
/// Values binding order: [0]=Tag, [1]=AgentName, [2]=NodeKind, [3]=ActionTypeText, [4]=Command, [5]=To, [6]=Title, [7]=ResolvedDisplayText
/// </summary>
public sealed class ActionLabelConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 8) return string.Empty;

        var tag = values[0] as string ?? "";
        var agentName = values[1] as string ?? "";
        var nodeKind = values[2] as string ?? "";
        var actionType = values[3] as string ?? "";
        var command = values[4] as string ?? "";
        var to = values[5] as string ?? "";
        var title = values[6] as string ?? "";
        var displayText = values[7] as string ?? "";

        // Only apply special formatting for Action nodes
        if (nodeKind != "Action")
        {
            // Fallback to StripMode logic for non-Action nodes
            return CleanNonActionLabel(displayText);
        }

        // Derive a meaningful tag if Tag is empty or is just the type name
        var effectiveTag = GetEffectiveTag(tag, actionType, command, to, title);

        return actionType switch
        {
            "RunRemoteCommand" => FormatRemoteLabel(effectiveTag, agentName),
            "SendMail" or "SendEmail" => FormatEmailLabel(to, effectiveTag),
            _ => effectiveTag, // RunCommand and others
        };
    }

    private static string FormatRemoteLabel(string tag, string agentName)
    {
        if (string.IsNullOrWhiteSpace(agentName))
            return tag;

        // Detect agent-name doubling: if tag already contains the agent name, render tag alone
        if (tag.Contains(agentName, StringComparison.OrdinalIgnoreCase))
            return tag;

        // Format: Tag : AgentName (agent is secondary)
        return $"{tag} : {agentName}";
    }

    private static string GetEffectiveTag(string tag, string actionType, string command, string to, string title)
    {
        // If tag is non-empty and isn't just the raw type name, use it
        if (!string.IsNullOrWhiteSpace(tag) && tag != actionType && tag != "Action")
            return tag;

        // Auto-derive from command filename (Issue 6b)
        if (!string.IsNullOrWhiteSpace(command))
        {
            try
            {
                var trimmed = command.Trim().Trim('"');
                var fileName = Path.GetFileNameWithoutExtension(trimmed);
                if (!string.IsNullOrWhiteSpace(fileName))
                    return PascalCaseFromDashes(fileName);
            }
            catch { /* fall through */ }
        }

        // Email: derive from title or recipient
        if ((actionType == "SendMail" || actionType == "SendEmail") && !string.IsNullOrWhiteSpace(title))
            return title.Length > 30 ? title[..30] : title;

        return "\u2039unnamed\u203A"; // ‹unnamed› in muted style
    }

    private static string PascalCaseFromDashes(string fileName)
    {
        // "Prepare-Agent" → "PrepareAgent", "install_build" → "InstallBuild"
        var parts = fileName.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1) return fileName;
        return string.Concat(parts.Select(p =>
            char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p[1..] : "")));
    }

    private static string FormatEmailLabel(string to, string tag)
    {
        if (string.IsNullOrWhiteSpace(to)) return tag;
        var recipients = to.Split([';', ','], StringSplitOptions.RemoveEmptyEntries);
        var first = recipients[0].Trim();
        // Shorten email to just the local part before @
        var atIdx = first.IndexOf('@');
        var shortRecipient = atIdx > 0 ? first[..atIdx] : first;
        if (recipients.Length > 1)
            return $"{shortRecipient}+{recipients.Length - 1}:{tag}";
        return $"{shortRecipient}:{tag}";
    }

    private static string CleanNonActionLabel(string text)
    {
        // Strip execution mode prefixes/suffixes
        text = Regex.Replace(text, @"\[(Sequential|Parallel)\]\s*", "");
        text = Regex.Replace(text, @"\s*\((Sequential|Parallel)\)", "");

        // "WatchList (filename — N items)" → "Test Plans (filename — N items)"
        if (text.StartsWith("WatchList")) text = "Test Plans" + text["WatchList".Length..];

        // "Event: Type" → "Type" (EVT pill already shows the kind)
        if (text.StartsWith("Event: ")) text = text["Event: ".Length..];

        // "Initialize: path" → "Initialize"
        if (text.StartsWith("Initialize:")) text = "Initialize";

        // "Ref > ID" → "ID"
        if (text.StartsWith("Ref > ")) text = text["Ref > ".Length..];

        // Strip file path embedded in WatchItem labels: "Tag  (C:\path\file)" → "Tag"
        // Match pattern: text followed by 2+ spaces and a parenthesized path
        var pathMatch = Regex.Match(text, @"^(.+?)\s{2,}\(.*[\\/].*\)$");
        if (pathMatch.Success)
            text = pathMatch.Groups[1].Value;
        // Also handle "C:\path\file" alone (no tag prefix) → just the filename
        else if (Regex.IsMatch(text, @"^[A-Za-z]:\\") || text.StartsWith("\\\\"))
        {
            try { text = Path.GetFileName(text.TrimEnd(')', ' ')); }
            catch { /* leave as-is */ }
        }

        // Fix grammar: "1 items" → "1 item"
        text = Regex.Replace(text, @"\b1 items\b", "1 item");

        return text.Trim();
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Produces the content for the script/subject file-pill on Action nodes.
/// Returns the filename for RUN/RMT, or truncated subject for EML.
/// Returns empty string (hidden via HasPathToVis) for non-applicable nodes.
/// Values: [0]=NodeKind, [1]=ActionTypeText, [2]=Command, [3]=Title
/// </summary>
public sealed class ActionFilePillConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 4) return string.Empty;

        var nodeKind = values[0] as string ?? "";
        var actionType = values[1] as string ?? "";
        var command = values[2] as string ?? "";
        var title = values[3] as string ?? "";

        if (nodeKind != "Action") return string.Empty;

        return actionType switch
        {
            "RunCommand" or "RunRemoteCommand" => ExtractFileName(command),
            "SendMail" or "SendEmail" => TruncateSubject(title),
            _ => string.Empty,
        };
    }

    private static string ExtractFileName(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return string.Empty;
        try
        {
            var trimmed = command.Trim().Trim('"');
            // Take just the first token (before any space/params)
            var spaceIdx = trimmed.IndexOf(' ');
            var exe = spaceIdx > 0 ? trimmed[..spaceIdx] : trimmed;
            return Path.GetFileName(exe);
        }
        catch { return command.Length > 30 ? command[..30] : command; }
    }

    private static string TruncateSubject(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        return title.Length > 40 ? title[..37] + "\u2026" : title;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Tooltip converter for the file pill — shows full command path for RUN/RMT.
/// Values: [0]=NodeKind, [1]=ActionTypeText, [2]=Command, [3]=Title
/// </summary>
public sealed class ActionFilePillTooltipConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 4) return string.Empty;
        var actionType = values[1] as string ?? "";
        var command = values[2] as string ?? "";
        var title = values[3] as string ?? "";

        return actionType switch
        {
            "RunCommand" or "RunRemoteCommand" => command,
            "SendMail" or "SendEmail" => title,
            _ => string.Empty,
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Multi-line tooltip for Action nodes showing Cmd/Args/Agent details.
/// For non-Action nodes, returns StatusTooltip.
/// Values: [0]=NodeKind, [1]=ActionTypeText, [2]=Command, [3]=Parameters, [4]=AgentName, [5]=StatusTooltip
/// </summary>
public sealed class NodeTooltipConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 6) return string.Empty;
        var nodeKind = values[0] as string ?? "";
        var actionType = values[1] as string ?? "";
        var command = values[2] as string ?? "";
        var parameters = values[3] as string ?? "";
        var agentName = values[4] as string ?? "";
        var statusTooltip = values[5] as string ?? "";

        if (nodeKind != "Action")
            return string.IsNullOrWhiteSpace(statusTooltip) ? null! : statusTooltip;

        // Resolve [Token] placeholders for tooltip display
        command = TreeNodeViewModel.ResolveTokens(command);
        parameters = TreeNodeViewModel.ResolveTokens(parameters);
        agentName = TreeNodeViewModel.ResolveTokens(agentName);

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(command))
            lines.Add($"Cmd:    {command}");
        if (!string.IsNullOrWhiteSpace(parameters))
            lines.Add($"Args:   {parameters}");
        if (!string.IsNullOrWhiteSpace(agentName))
            lines.Add($"Agent:  {agentName}");
        if (!string.IsNullOrWhiteSpace(statusTooltip))
            lines.Add($"Status: {statusTooltip}");

        return lines.Count > 0 ? string.Join("\n", lines) : null!;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns Visibility.Visible when the bound value represents a non-zero count.
/// Used for count badges.
/// </summary>
public sealed class NonZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int n && n > 0) return Visibility.Visible;
        if (value is string s && int.TryParse(s, out var sn) && sn > 0) return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Returns Visibility.Visible when the bound string is not null/empty.
/// </summary>
public sealed class HasPathToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Pluralizes the count badge label based on NodeKind.
/// WatchList: "N tests · M steps" (where M = total descendant Action nodes)
/// ActionGroup/Event: "N actions"
/// Others: "N items"
/// Values: [0]=Children.Count (int), [1]=NodeKind (string), [2]=TreeNodeViewModel (for recursive count)
/// Falls back to single-value mode if called as IValueConverter.
/// </summary>
public sealed class PluralizeActionsConverter : IValueConverter, IMultiValueConverter
{
    // IValueConverter: simple " action"/" actions" suffix
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int n)
            return n == 1 ? " action" : " actions";
        return " actions";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    // IMultiValueConverter: full badge text including count
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return "";
        var count = values[0] is int n ? n : 0;
        var nodeKind = values[1] as string ?? "";

        return nodeKind switch
        {
            "WatchList" => FormatWatchListBadge(count, values.Length > 2 ? values[2] : null),
            "ActionGroup" or "Event" => $"{count} {(count == 1 ? "action" : "actions")}",
            _ => $"{count} {(count == 1 ? "item" : "items")}",
        };
    }

    private static string FormatWatchListBadge(int testCount, object? nodeVm)
    {
        var testLabel = testCount == 1 ? "test" : "tests";
        // Count total Action descendants for "steps"
        var steps = 0;
        if (nodeVm is TestControllerGrpc.ViewModels.TreeNodeViewModel root)
            steps = CountActions(root);
        var stepLabel = steps == 1 ? "step" : "steps";
        return $"{testCount} {testLabel} \u00B7 {steps} {stepLabel}";
    }

    private static int CountActions(TestControllerGrpc.ViewModels.TreeNodeViewModel node)
    {
        var count = 0;
        foreach (var child in node.Children)
        {
            if (child.NodeKind == "Action") count++;
            count += CountActions(child);
        }
        return count;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

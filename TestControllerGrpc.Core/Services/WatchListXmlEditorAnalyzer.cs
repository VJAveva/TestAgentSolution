using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using TestControllerGrpc.Core.Preflight;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

public enum XmlEditorDocumentKind { WatchItem, WatchList, Templates }
public enum XmlEditorQuickFixKind
{
    SetProfile,
    ReplaceBracketedText,
    MoveCredentialsToSecretEnv,
    CreateMissingTemplate,
    OpenParameterFileGlobal,
    RemoveStepsUsingToken,
}

public sealed record XmlEditorQuickFix(
    string Title,
    XmlEditorQuickFixKind Kind,
    string NodePath,
    string Value = "");

public sealed record XmlEditorDiagnostic(
    int Line,
    int Column,
    int Length,
    WatchIssueSeverity Severity,
    string Code,
    string Message,
    string NodePath,
    IReadOnlyList<XmlEditorQuickFix> QuickFixes);

public sealed record XmlEditorAnalysis(
    IReadOnlyList<XmlEditorDiagnostic> Diagnostics,
    WatchListConfig? ParsedConfig);

public sealed record XmlEditorCompletion(string Text, string Description);
public sealed record XmlEditorAgent(string Name, string Status);

/// <summary>Parses XML editor drafts and delegates semantic rules to the shared WatchList validator.</summary>
public static class WatchListXmlEditorAnalyzer
{
    private static readonly Regex NodeSegment = new(
        @"(?<name>WatchItem|Template|Event|Group|Action|Initialize|Ref)\[(?<value>[^\]]*)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MissingReference = new(
        @"Ref references unknown template '(?<id>[^']+)'",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MissingToken = new(
        @"Token \[(?<name>_?\w+)\] is not defined",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BracketedText = new(
        @"\[(?<name>[A-Za-z][A-Za-z0-9_-]*)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ParameterFileAttribute = new(
        "ParameterFile\\s*=\\s*\"(?<path>[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex InitializeElement = new(
        "<Initialize\\b(?<attributes>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static IReadOnlyList<XmlEditorCompletion> GetElementCompletions(XmlEditorDocumentKind kind)
    {
        var root = kind switch
        {
            XmlEditorDocumentKind.WatchItem => nameof(WatchItemConfig).Replace("Config", "", StringComparison.Ordinal),
            XmlEditorDocumentKind.WatchList => nameof(WatchListConfig).Replace("Config", "", StringComparison.Ordinal),
            _ => "Templates",
        };
        var names = kind == XmlEditorDocumentKind.WatchList
            ? new[] { root, "WatchItem", "Templates", "Template", "Event", "ActionGroup", "Action", "Initialize", "Ref" }
            : kind == XmlEditorDocumentKind.Templates
                ? new[] { root, "Template", "ActionGroup", "Action", "Initialize", "Ref" }
                : new[] { root, "Event", "ActionGroup", "Action", "Initialize", "Ref" };
        return names.Select(name => new XmlEditorCompletion(name, "WatchList model element")).ToArray();
    }

    public static IReadOnlyList<XmlEditorCompletion> GetAttributeCompletions(string elementName)
    {
        var modelType = elementName switch
        {
            "WatchList" => typeof(WatchListConfig),
            "WatchItem" => typeof(WatchItemConfig),
            "Templates" or "Template" => typeof(TemplateConfig),
            "Event" => typeof(EventConfig),
            "ActionGroup" => typeof(ActionGroupConfig),
            "Action" => typeof(ActionConfig),
            "Initialize" => typeof(InitializeConfig),
            "Ref" => typeof(RefConfig),
            _ => null,
        };
        if (modelType is null) return [];

        return modelType.GetProperties()
            .Where(property => property.PropertyType.IsPrimitive
                || property.PropertyType.IsEnum
                || property.PropertyType == typeof(string))
            .Where(property => property.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).Length == 0
                && property.GetCustomAttributes(typeof(System.Xml.Serialization.XmlIgnoreAttribute), true).Length == 0
                && property.Name is not ("SkipReason" or "Comment" or "SkippedAtUtc" or "SkippedBy"))
            .Select(property => new XmlEditorCompletion(
                $"{property.Name}=\"\"", $"{modelType.Name}.{property.Name}"))
            .ToArray();
    }

    public static IReadOnlyList<XmlEditorCompletion> GetValueCompletions(
        string xml,
        XmlEditorDocumentKind kind,
        string attributeName,
        bool insideEvent,
        WatchListConfig? context = null,
        IReadOnlyCollection<XmlEditorAgent>? agents = null)
    {
        var config = TryParseForCompletion(xml, kind, context);
        return attributeName switch
        {
            "AgentName" => (agents ?? [])
                .OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase)
                .Select(agent => new XmlEditorCompletion(agent.Name, $"Agent status: {agent.Status}"))
                .ToArray(),
            "TemplateID" => (config?.Templates ?? [])
                .Select(template => template.ID)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Select(id => new XmlEditorCompletion(id, "Template library"))
                .ToArray(),
            "Profile" => GetProfiles(xml, context),
            "ParameterFile" => GetParameterFiles(),
            "ExecutionType" => Enum.GetNames<ExecutionMode>()
                .Select(value => new XmlEditorCompletion(value, "Execution mode"))
                .ToArray(),
            "Type" when insideEvent => new[] { "Renamed", "Created", "Changed" }
                .Select(value => new XmlEditorCompletion(value, "Watch event"))
                .ToArray(),
            "Type" => Enum.GetNames<ActionType>()
                .Select(value => new XmlEditorCompletion(value, "Action type"))
                .ToArray(),
            "FailAndContinue" or "IsReboot" => new[] { "true", "false" }
                .Select(value => new XmlEditorCompletion(value, "Boolean"))
                .ToArray(),
            _ => [],
        };
    }

    public static IReadOnlyList<XmlEditorCompletion> GetTokenCompletions(
        string xml, XmlEditorDocumentKind kind, WatchListConfig? context = null)
    {
        var config = TryParseForCompletion(xml, kind, context);
        if (config is null) return [];

        var completions = new Dictionary<string, XmlEditorCompletion>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in config.WatchItems)
        {
            var execution = new PipelineExecutionContext
            {
                WatchItemTag = item.Tag,
                GlobalVariablesFile = config.GlobalVariablesFile,
            };
            ParameterResolver.LoadForWatchItem(execution, item);

            foreach (var pair in execution.Parameters)
            {
                if (!pair.Key.StartsWith('_') || !completions.TryAdd(pair.Key,
                        new XmlEditorCompletion($"[{pair.Key}]", DescribeToken(execution, pair.Key))))
                    continue;
            }

            foreach (var field in new[] { item.BuildNumberField, item.DropLocationField })
            {
                if (string.IsNullOrWhiteSpace(field)) continue;
                var name = field.StartsWith('_') ? field : "_" + field;
                completions.TryAdd(name, new XmlEditorCompletion(
                    $"[{name}]", "Provided by the trigger file (Trigger layer; value is assigned at run time)"));
            }
        }

        return completions.Values.OrderBy(entry => entry.Text, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string? GetHoverText(
        string xml,
        XmlEditorDocumentKind kind,
        int offset,
        WatchListConfig? context = null,
        IReadOnlyCollection<XmlEditorAgent>? agents = null)
    {
        var token = Regex.Matches(xml, @"\[_\w+\]")
            .Cast<Match>()
            .FirstOrDefault(match => offset >= match.Index && offset <= match.Index + match.Length);
        if (token is not null)
        {
            var completion = GetTokenCompletions(xml, kind, context)
                .FirstOrDefault(entry => string.Equals(entry.Text, token.Value, StringComparison.OrdinalIgnoreCase));
            return completion?.Description;
        }

        var agentName = GetAttributeValueAt(xml, "AgentName", offset);
        if (agentName is not null)
        {
            var agent = agents?.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, agentName, StringComparison.OrdinalIgnoreCase));
            return agent is null ? $"Agent '{agentName}' is not registered." : $"{agent.Name}: {agent.Status}";
        }

        var templateId = GetAttributeValueAt(xml, "TemplateID", offset);
        if (templateId is not null)
        {
            var config = TryParseForCompletion(xml, kind, context);
            var template = config?.Templates.FirstOrDefault(candidate =>
                string.Equals(candidate.ID, templateId, StringComparison.OrdinalIgnoreCase));
            return template is null ? $"Template '{templateId}' is not defined." : DescribeTemplate(template);
        }

        var initialize = InitializeElement.Matches(xml).Cast<Match>()
            .FirstOrDefault(match => offset >= match.Index && offset <= match.Index + match.Length);
        if (initialize is null) return null;

        var parameterFile = AttributeValue(initialize.Groups["attributes"].Value, "ParameterFile");
        var profile = AttributeValue(initialize.Groups["attributes"].Value, "Profile") ?? "(default)";
        if (string.IsNullOrWhiteSpace(parameterFile))
            return $"Initialize profile: {profile}; ParameterFile is not set.";
        if (!ParameterResolver.TryReadJsonConfig(parameterFile, out var parsed, out var failure))
        {
            var lastGood = ParameterResolver.LastGoodJsonReadUtc(parameterFile);
            return lastGood is null
                ? $"Parameter file is unavailable: {failure}"
                : $"Parameter file is currently invalid: {failure}\nLast good copy was read at {lastGood:O}.";
        }

        var availableProfiles = parsed!.Profiles.Count == 0
            ? "no named profiles"
            : string.Join(", ", parsed.Profiles.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        return $"Loaded: {parameterFile}\nProfile: {profile}\nAvailable profiles: {availableProfiles}";
    }

    private static string DescribeToken(PipelineExecutionContext context, string name)
    {
        var display = TokenDisplay.Resolve(
            $"[{name}]",
            token => context.Parameters.TryGetValue(token, out var value)
                ? (value, context.ParameterRanks.TryGetValue(token, out var rank)
                    ? TokenDisplay.LayerFromRank((ParameterRank)rank)
                    : TokenLayer.Unknown)
                : null,
            showRawTokens: true);
        var hit = display.Tokens.FirstOrDefault();
        return hit.IsResolved
            ? $"{hit.Value} ({hit.Layer})"
            : "Not defined in the loaded parameter layers";
    }

    private static string? GetAttributeValueAt(string xml, string attributeName, int offset)
    {
        var regex = new Regex(
            $@"\b{Regex.Escape(attributeName)}\s*=\s*""(?<value>[^""]*)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var match = regex.Matches(xml).Cast<Match>().FirstOrDefault(candidate =>
            offset >= candidate.Groups["value"].Index
            && offset <= candidate.Groups["value"].Index + candidate.Groups["value"].Length);
        return match?.Groups["value"].Value;
    }

    private static string? AttributeValue(string attributes, string attributeName)
    {
        var match = Regex.Match(attributes,
            $@"\b{Regex.Escape(attributeName)}\s*=\s*""(?<value>[^""]*)""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static int OffsetAt(string text, int line, int column)
    {
        var currentLine = 1;
        var offset = 0;
        while (currentLine < line && offset < text.Length)
        {
            if (text[offset++] == '\n') currentLine++;
        }
        return Math.Min(text.Length, offset + Math.Max(0, column - 1));
    }

    private static (int Line, int Column) LineColumnAt(string text, int offset)
    {
        var line = 1;
        var column = 1;
        for (var index = 0; index < offset && index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }
        return (line, column);
    }

    private static string DescribeTemplate(TemplateConfig template)
    {
        var lines = new List<string> { $"Template {template.ID}" };
        Walk(template.Children, 0);
        return string.Join(Environment.NewLine, lines);

        void Walk(IEnumerable<IActionNode> nodes, int depth)
        {
            foreach (var node in nodes)
            {
                var indent = new string(' ', (depth + 1) * 2);
                switch (node)
                {
                    case ActionConfig action:
                        lines.Add($"{indent}{action.Tag}: {action.Type}");
                        break;
                    case ActionGroupConfig group:
                        lines.Add($"{indent}Group {group.Tag}");
                        Walk(group.Children, depth + 1);
                        break;
                    case InitializeConfig initialize:
                        lines.Add($"{indent}Initialize {Path.GetFileName(initialize.ParameterFile)} ({initialize.Profile})");
                        break;
                    case RefConfig reference:
                        lines.Add($"{indent}Ref {reference.TemplateID}");
                        break;
                }
            }
        }
    }

    private static IReadOnlyList<XmlEditorCompletion> GetProfiles(string xml, WatchListConfig? context)
    {
        var parameterFile = ParameterFileAttribute.Matches(xml)
            .Cast<Match>()
            .Select(match => match.Groups["path"].Value)
            .LastOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (parameterFile is null && context is not null)
            parameterFile = context.WatchItems.SelectMany(ParameterResolver.CollectInitializeNodes)
                .Select(node => node.ParameterFile)
                .LastOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (parameterFile is null
            || !ParameterResolver.TryReadJsonConfig(parameterFile, out var parsed, out _))
            return [];

        return parsed!.Profiles.Keys.OrderBy(profile => profile, StringComparer.OrdinalIgnoreCase)
            .Select(profile => new XmlEditorCompletion(profile, $"Profile in {Path.GetFileName(parameterFile)}"))
            .ToArray();
    }

    private static IReadOnlyList<XmlEditorCompletion> GetParameterFiles()
    {
        var directories = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Parameters"),
            Path.Combine(Environment.CurrentDirectory, "Parameters"),
        }.Distinct(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory)) continue;
            files.AddRange(Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(file => file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    || file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)));
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Select(file => new XmlEditorCompletion(file, "Parameter file"))
            .ToArray();
    }

    private static WatchListConfig? TryParseForCompletion(
        string xml, XmlEditorDocumentKind kind, WatchListConfig? context)
    {
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            return ParseDocument(document, kind, context);
        }
        catch (XmlException)
        {
            return context;
        }
    }

    public static XmlEditorAnalysis Analyze(
        string xml,
        XmlEditorDocumentKind kind,
        WatchListConfig? context = null,
        IReadOnlyCollection<string>? knownAgents = null,
        IPreflightFileSystem? fileSystem = null)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            return new XmlEditorAnalysis(
                [new XmlEditorDiagnostic(ex.LineNumber, ex.LinePosition, 1, WatchIssueSeverity.Error,
                    "XmlSyntax", ex.Message, "", [])],
                null);
        }

        var config = ParseDocument(document, kind, context);
        if (config is null)
        {
            var root = document.Root;
            var lineInfo = root as IXmlLineInfo;
            var line = lineInfo?.HasLineInfo() == true ? lineInfo.LineNumber : 1;
            var column = lineInfo?.HasLineInfo() == true ? lineInfo.LinePosition : 1;
            var expected = kind switch
            {
                XmlEditorDocumentKind.WatchItem => "<WatchItem>",
                XmlEditorDocumentKind.WatchList => "<WatchList>",
                _ => "<Templates>",
            };
            return new XmlEditorAnalysis(
                [new XmlEditorDiagnostic(line, column, 1, WatchIssueSeverity.Error, "RootElement",
                    $"Root element must be {expected}.", "", [])],
                null);
        }

        fileSystem ??= new PreflightFileSystem();
        var issues = WatchListValidator.Analyze(config, knownAgents, fileSystem);
        if (kind == XmlEditorDocumentKind.Templates)
            issues = issues.Where(issue => issue.NodePath.StartsWith("Template[", StringComparison.Ordinal)
                || issue.Message.Contains("Ref references unknown template", StringComparison.Ordinal)).ToArray();
        var diagnostics = issues.Select(issue =>
        {
            var element = FindElement(document, issue.NodePath);
            var location = element as IXmlLineInfo;
            var line = location?.HasLineInfo() == true ? location.LineNumber : 1;
            var column = location?.HasLineInfo() == true ? location.LinePosition : 1;
            var length = Math.Max(1, element?.Name.LocalName.Length ?? 1);
            var highlight = MissingToken.Match(issue.Message);
            if (!highlight.Success) highlight = BracketedText.Match(issue.Message);
            if (highlight.Success && element is not null)
            {
                var start = OffsetAt(xml, line, column);
                var occurrence = xml.IndexOf(highlight.Value, start, StringComparison.Ordinal);
                if (occurrence >= 0)
                {
                    (line, column) = LineColumnAt(xml, occurrence);
                    length = highlight.Length;
                }
            }
            return new XmlEditorDiagnostic(line, column, length,
                issue.Severity, issue.Severity == WatchIssueSeverity.Error ? "Validation" : "Advisory",
                issue.Message, issue.NodePath, QuickFixes(issue, element, config));
        }).ToList();

        return new XmlEditorAnalysis(diagnostics, config);
    }

    public static string? ApplyQuickFix(
        string xml,
        XmlEditorDocumentKind kind,
        XmlEditorQuickFix fix,
        WatchListConfig? context = null)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var config = ParseDocument(document, kind, context)
            ?? throw new InvalidOperationException("The XML root does not match the editor document type.");

        switch (fix.Kind)
        {
            case XmlEditorQuickFixKind.SetProfile:
                var initialize = FindInitialize(config, fix.NodePath);
                if (initialize is null) return null;
                initialize.Profile = fix.Value;
                break;

            case XmlEditorQuickFixKind.ReplaceBracketedText:
                var action = FindAction(config, fix.NodePath);
                if (action is null) return null;
                var replacement = fix.Value.Equals("[PASS]", StringComparison.OrdinalIgnoreCase)
                    ? "PASS:"
                    : fix.Value.Trim('[', ']');
                action.Command = action.Command.Replace(fix.Value, replacement, StringComparison.Ordinal);
                action.Parameters = action.Parameters.Replace(fix.Value, replacement, StringComparison.Ordinal);
                break;

            case XmlEditorQuickFixKind.MoveCredentialsToSecretEnv:
                var credentialAction = FindAction(config, fix.NodePath);
                if (credentialAction is null
                    || !InlineCredentialFinder.MoveToSecretEnvironment(credentialAction))
                    return null;
                break;

            case XmlEditorQuickFixKind.CreateMissingTemplate:
                if (kind == XmlEditorDocumentKind.WatchItem) return null;
                if (!config.Templates.Any(t => string.Equals(t.ID, fix.Value, StringComparison.OrdinalIgnoreCase)))
                    config.Templates.Add(new TemplateConfig { ID = fix.Value });
                break;

            case XmlEditorQuickFixKind.RemoveStepsUsingToken:
                RemoveActionsUsingToken(config, fix.Value);
                break;

            case XmlEditorQuickFixKind.OpenParameterFileGlobal:
                return null;
        }

        return kind switch
        {
            XmlEditorDocumentKind.WatchItem => WatchListXmlParser.SerializeWatchItem(config.WatchItems.Single()),
            XmlEditorDocumentKind.WatchList => WatchListXmlParser.SerializeWatchList(config),
            XmlEditorDocumentKind.Templates => WatchListXmlParser.SerializeTemplateList(config.Templates),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static WatchListConfig? ParseDocument(
        XDocument document,
        XmlEditorDocumentKind kind,
        WatchListConfig? context)
    {
        if (document.Root is null) return null;
        var xml = document.ToString(SaveOptions.DisableFormatting);

        switch (kind)
        {
            case XmlEditorDocumentKind.WatchItem:
                var item = WatchListXmlParser.DeserializeWatchItem(xml);
                if (item is null) return null;
                var itemConfig = new WatchListConfig
                {
                    GlobalVariablesFile = context?.GlobalVariablesFile ?? "",
                    Templates = context?.Templates ?? [],
                    WatchItems = [item],
                    XmlSourceDocument = new XDocument(document),
                };
                return itemConfig;

            case XmlEditorDocumentKind.WatchList:
                return WatchListXmlParser.DeserializeWatchList(xml);

            case XmlEditorDocumentKind.Templates:
                var templates = WatchListXmlParser.DeserializeTemplateList(xml);
                if (templates is null) return null;
                return new WatchListConfig
                {
                    GlobalVariablesFile = context?.GlobalVariablesFile ?? "",
                    Templates = templates,
                    WatchItems = context?.WatchItems ?? [],
                    XmlSourceDocument = new XDocument(document),
                };

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static List<XmlEditorQuickFix> QuickFixes(
        ValidationIssue issue, XElement? element, WatchListConfig config)
    {
        var fixes = new List<XmlEditorQuickFix>();

        if (issue.Message.Contains("Profile", StringComparison.OrdinalIgnoreCase)
            && element?.Name.LocalName == "Initialize")
        {
            var file = element.Attribute("ParameterFile")?.Value;
            if (!string.IsNullOrWhiteSpace(file)
                && ParameterResolver.TryReadJsonConfig(file, out var parameterConfig, out _))
            {
                fixes.AddRange(parameterConfig!.Profiles.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .Select(profile => new XmlEditorQuickFix(
                        $"Set Profile to {profile}", XmlEditorQuickFixKind.SetProfile, issue.NodePath, profile)));
            }
        }

        var missingRef = MissingReference.Match(issue.Message);
        if (missingRef.Success)
            fixes.Add(new XmlEditorQuickFix(
                $"Create missing template '{missingRef.Groups["id"].Value}'",
                XmlEditorQuickFixKind.CreateMissingTemplate, issue.NodePath, missingRef.Groups["id"].Value));

        var token = MissingToken.Match(issue.Message);
        if (token.Success)
        {
            var name = token.Groups["name"].Value;
            fixes.Add(new XmlEditorQuickFix(
                $"Remove steps that use [{name}]", XmlEditorQuickFixKind.RemoveStepsUsingToken,
                issue.NodePath, $"[{name}]"));
            var parameterFile = config.WatchItems.SelectMany(ParameterResolver.CollectInitializeNodes)
                .Select(init => init.ParameterFile)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (parameterFile is not null)
                fixes.Add(new XmlEditorQuickFix(
                    "Open parameter file at the Global section",
                    XmlEditorQuickFixKind.OpenParameterFileGlobal, issue.NodePath, parameterFile));
        }

        if (issue.Message.Contains("Bracketed text [", StringComparison.Ordinal))
        {
            var match = BracketedText.Match(issue.Message);
            if (match.Success)
            {
                var value = $"[{match.Groups["name"].Value}]";
                fixes.Add(new XmlEditorQuickFix(
                    value == "[PASS]" ? "Replace [PASS] with PASS:" : $"Replace {value} with {match.Groups["name"].Value}",
                    XmlEditorQuickFixKind.ReplaceBracketedText, issue.NodePath, value));
            }
        }

        var credentialAction = FindAction(config, issue.NodePath);
        if (issue.Message.Contains("Credential-like value appears", StringComparison.Ordinal)
            && credentialAction is not null
            && InlineCredentialFinder.CanMoveToSecretEnvironment(credentialAction))
            fixes.Add(new XmlEditorQuickFix(
                "Move credentials to SecretEnv", XmlEditorQuickFixKind.MoveCredentialsToSecretEnv, issue.NodePath));

        return fixes;
    }

    private static XElement? FindElement(XDocument document, string path)
    {
        if (document.Root is null) return null;
        XElement current = document.Root;
        foreach (Match segment in NodeSegment.Matches(path))
        {
            var name = segment.Groups["name"].Value switch
            {
                "Group" => "ActionGroup",
                var value => value,
            };
            var key = segment.Groups["value"].Value;
            var attribute = name switch
            {
                "WatchItem" or "ActionGroup" or "Action" or "Initialize" => "Tag",
                "Template" => "ID",
                "Event" => "Type",
                "Ref" => "TemplateID",
                _ => "",
            };
            var candidate = current.DescendantsAndSelf(name)
                .FirstOrDefault(element => string.Equals(element.Attribute(attribute)?.Value, key, StringComparison.OrdinalIgnoreCase));
            if (candidate is null)
                candidate = document.Descendants(name).FirstOrDefault(element =>
                    string.Equals(element.Attribute(attribute)?.Value, key, StringComparison.OrdinalIgnoreCase));
            if (candidate is null) break;
            current = candidate;
        }
        return current == document.Root ? null : current;
    }

    private static InitializeConfig? FindInitialize(WatchListConfig config, string path)
    {
        var tag = NodeSegment.Matches(path).Cast<Match>()
            .LastOrDefault(match => match.Groups["name"].Value == "Initialize")?.Groups["value"].Value;
        return tag is null ? null : AllInitializers(config)
            .FirstOrDefault(node => string.Equals(node.Tag, tag, StringComparison.OrdinalIgnoreCase));
    }

    private static ActionConfig? FindAction(WatchListConfig config, string path)
    {
        var tag = NodeSegment.Matches(path).Cast<Match>()
            .LastOrDefault(match => match.Groups["name"].Value == "Action")?.Groups["value"].Value;
        return tag is null ? null : AllActions(config)
            .FirstOrDefault(node => string.Equals(node.Tag, tag, StringComparison.OrdinalIgnoreCase)
                || string.Equals(node.ResolvedTag, tag, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<InitializeConfig> AllInitializers(WatchListConfig config) =>
        config.WatchItems.SelectMany(ParameterResolver.CollectInitializeNodes);

    private static IEnumerable<ActionConfig> AllActions(WatchListConfig config)
    {
        foreach (var nodes in config.WatchItems.SelectMany(item => item.Events).Select(evt => evt.Children)
                     .Concat(config.Templates.Select(template => template.Children)))
        {
            foreach (var action in Walk(nodes)) yield return action;
        }

        static IEnumerable<ActionConfig> Walk(IEnumerable<IActionNode> nodes)
        {
            foreach (var node in nodes)
            {
                if (node is ActionConfig action) yield return action;
                if (node is ActionGroupConfig group)
                    foreach (var child in Walk(group.Children)) yield return child;
            }
        }
    }

    private static void RemoveActionsUsingToken(WatchListConfig config, string token)
    {
        foreach (var item in config.WatchItems)
            foreach (var evt in item.Events)
                RemoveFrom(evt.Children, token);
        foreach (var template in config.Templates)
            RemoveFrom(template.Children, token);

        static void RemoveFrom(List<IActionNode> nodes, string value)
        {
            nodes.RemoveAll(node => node is ActionConfig action
                && (action.Command.Contains(value, StringComparison.OrdinalIgnoreCase)
                    || action.Parameters.Contains(value, StringComparison.OrdinalIgnoreCase)));
            foreach (var group in nodes.OfType<ActionGroupConfig>())
                RemoveFrom(group.Children, value);
        }
    }
}

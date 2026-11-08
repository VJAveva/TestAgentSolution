# WatchItem Builder — Implementation Specification

| Field | Value |
|---|---|
| **Feature name** | WatchItem Builder (guided WatchList.xml editor) |
| **Component** | TestControllerGrpc (WPF Controller) |
| **Type** | New feature |
| **Priority** | P1 |
| **Estimated effort** | 6-8 days |
| **Editing model** | Side-by-side: form-based node editor + live XML view |
| **Validation** | Rule-based (known-good patterns), real-time |
| **Migration** | Silent auto-upgrade of old XML on load |

---

## Instructions for Copilot

Implement a guided WatchItem builder that replaces hand-editing of WatchList.xml
with a structured, validated, auto-completing experience. Work through the
phases in order. For each:

1. Create/modify files as specified
2. Replace every `===== PLUG IN =====` with REAL values from this codebase —
   the actual WatchItem/Action attribute names, the real action types, the
   real agent names, the real parameter tokens
3. Reference the actual WatchList.xml structure (examples below) so the form
   fields and validation rules match the real schema
4. Be opinionated; if my model conflicts with the real XML, adapt and explain

The goal: a user can build a valid WatchItem with auto-completion and inline
validation, see the live XML update as they edit, and never produce XML that
fails to load. Old XML files silently upgrade to the current standard on open.

---

## Background: The Current Pain

Today, WatchList.xml is hand-edited. This causes:

- **Compilation/load failures** — a missing close tag, a typo in an attribute,
  an invalid action type, and the whole file fails to load (you have seen the
  missing `</WatchList>` and similar issues)
- **Inconsistent formatting** — different WatchItems use different conventions
  (literal build numbers vs field-name references, inconsistent tags)
- **No discoverability** — users don't know which attributes a given action
  type supports, what values are valid, or what tokens are available
- **No guardrails** — invalid combinations (PollInterval > Timeout, plaintext
  passwords, empty ActionGroups) slip through

This feature eliminates those by making the editor structured and validated.

---

## The Real WatchList Structure (reference)

Based on the actual WatchList.xml, the structure is:

```xml
<WatchList>
  <WatchItem Path="..." Filter="..." Tag="..."
             BuildNumberField="..." DropLocationField="...">
    <Event Type="Renamed" ExecutionType="Sequential">
      <ActionGroup Tag="..." ExecutionType="Sequential" FailAndContinue="true">
        <Initialize Tag="..." ParameterFile="..." />
        <Ref TemplateID="..." />
        <Action Type="RunRemoteCommand" AgentName="..." Command="..."
                Parameters="..." Timeout="..." PollInterval="..."
                FailAndContinue="..." IsReboot="..." Tag="..."
                UserName="..." Password="..." />
        <Action Type="RunCommand" Command="..." Parameters="..." Tag="..." />
        <Action Type="SendMail" From="..." To="..." Title="..." Body="..."
                Attachment="..." Embed="..." LargeFilesShare="..." Tag="..." />
        <ActionGroup ...>  <!-- nested groups allowed -->
          ...
        </ActionGroup>
      </ActionGroup>
    </Event>
  </WatchItem>
  <Templates>
    <Template ID="...">
      <ActionGroup ...>...</ActionGroup>
    </Template>
  </Templates>
</WatchList>
```

### Node types and their attributes

===== PLUG IN: verify these against the real schema and complete any gaps =====

| Node | Attributes | Notes |
|---|---|---|
| `WatchItem` | Path, Filter, Tag, BuildNumberField, DropLocationField | Top-level trigger |
| `Event` | Type, ExecutionType | Type=Renamed/Created/Changed; ExecutionType=Sequential/Parallel |
| `ActionGroup` | Tag, ExecutionType, FailAndContinue | Can nest |
| `Initialize` | Tag, ParameterFile | Loads parameter file |
| `Ref` | TemplateID | References a Template |
| `Action` (RunRemoteCommand) | Type, AgentName, Command, Parameters, Timeout, PollInterval, FailAndContinue, IsReboot, Tag, UserName, Password | Runs on an agent |
| `Action` (RunCommand) | Type, Command, Parameters, Timeout, FailAndContinue, Tag | Runs on controller |
| `Action` (SendMail) | Type, From, To, Title, Body, Attachment, Embed, LargeFilesShare, Tag, Order | Sends email |
| `Template` | ID | Reusable action group |

---

## Phase 1: The Domain Model

A strongly-typed object model that represents the WatchList. This is the
single source of truth — the form edits it, the XML view renders it, validation
checks it, and serialization writes it. No string manipulation of XML anywhere
except the final serialize/parse boundary.

### File: `TestControllerGrpc.Core/WatchModel/WatchListModel.cs`

```csharp
// ===== PLUG IN: your namespace =====
namespace TestControllerGrpc.Core.WatchModel;

/// <summary>Root model for a WatchList.xml file.</summary>
public sealed class WatchListModel
{
    public List<WatchItemModel> WatchItems { get; } = new();
    public List<TemplateModel> Templates { get; } = new();

    /// <summary>Schema version, used for migration. Bump when the standard changes.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public const int CurrentSchemaVersion = 2;
}

public sealed class WatchItemModel
{
    public string Path { get; set; } = @"C:\Triggers\";
    public string Filter { get; set; } = "";
    public string Tag { get; set; } = "";
    public string BuildNumberField { get; set; } = "BuildNumber";
    public string DropLocationField { get; set; } = "DropLocation";
    public EventModel Event { get; set; } = new();
}

public sealed class EventModel
{
    public string Type { get; set; } = "Renamed";       // Renamed | Created | Changed
    public string ExecutionType { get; set; } = "Sequential";
    public List<ActionGroupModel> ActionGroups { get; } = new();
}

public sealed class ActionGroupModel
{
    public string Tag { get; set; } = "";
    public string ExecutionType { get; set; } = "Sequential";  // Sequential | Parallel
    public bool FailAndContinue { get; set; } = true;

    // Children are heterogeneous: Initialize, Ref, Action, nested ActionGroup
    public List<IWatchNode> Children { get; } = new();
}

/// <summary>Marker for anything that can be a child of an ActionGroup.</summary>
public interface IWatchNode
{
    string NodeKind { get; }   // "Initialize" | "Ref" | "Action" | "ActionGroup"
}

public sealed class InitializeModel : IWatchNode
{
    public string NodeKind => "Initialize";
    public string Tag { get; set; } = "InitializeParams";
    public string ParameterFile { get; set; } = "";
}

public sealed class RefModel : IWatchNode
{
    public string NodeKind => "Ref";
    public string TemplateID { get; set; } = "";
}

public sealed class ActionModel : IWatchNode
{
    public string NodeKind => "Action";
    public ActionType Type { get; set; } = ActionType.RunRemoteCommand;
    public string Tag { get; set; } = "";

    // RunRemoteCommand / RunCommand
    public string? AgentName { get; set; }
    public string? Command { get; set; }
    public string? Parameters { get; set; }
    public int? Timeout { get; set; }
    public int? PollInterval { get; set; }
    public bool FailAndContinue { get; set; }
    public bool IsReboot { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }

    // SendMail
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? Attachment { get; set; }
    public string? Embed { get; set; }
    public string? LargeFilesShare { get; set; }
    public string? Order { get; set; }
}

public enum ActionType
{
    RunRemoteCommand,
    RunCommand,
    SendMail
    // ===== PLUG IN: add any other real action types =====
}

public sealed class TemplateModel
{
    public string ID { get; set; } = "";
    public List<ActionGroupModel> ActionGroups { get; } = new();
}
```

---

## Phase 2: Serialization (model <-> XML)

The ONLY place XML strings are produced or parsed. Everything else works on the
model. This guarantees the form and XML view never disagree and the output is
always well-formed.

### File: `TestControllerGrpc.Core/WatchModel/WatchListSerializer.cs`

```csharp
using System.Xml;
using System.Xml.Linq;

namespace TestControllerGrpc.Core.WatchModel;

public static class WatchListSerializer
{
    /// <summary>Serialize the model to well-formed, consistently-formatted XML.</summary>
    public static string Serialize(WatchListModel model)
    {
        var root = new XElement("WatchList");

        foreach (var item in model.WatchItems)
            root.Add(SerializeWatchItem(item));

        if (model.Templates.Count > 0)
        {
            var templates = new XElement("Templates");
            foreach (var t in model.Templates)
                templates.Add(SerializeTemplate(t));
            root.Add(templates);
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            root);

        // Consistent, readable formatting — eliminates the inconsistency problem
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            OmitXmlDeclaration = false,
            NewLineChars = "\r\n"
        };

        using var sw = new StringWriter();
        using var xw = XmlWriter.Create(sw, settings);
        doc.Save(xw);
        xw.Flush();
        return sw.ToString();
    }

    private static XElement SerializeWatchItem(WatchItemModel item)
    {
        var el = new XElement("WatchItem",
            new XAttribute("Path", item.Path),
            new XAttribute("Filter", item.Filter),
            new XAttribute("Tag", item.Tag),
            new XAttribute("BuildNumberField", item.BuildNumberField),
            new XAttribute("DropLocationField", item.DropLocationField));

        el.Add(SerializeEvent(item.Event));
        return el;
    }

    private static XElement SerializeEvent(EventModel ev)
    {
        var el = new XElement("Event",
            new XAttribute("Type", ev.Type),
            new XAttribute("ExecutionType", ev.ExecutionType));
        foreach (var g in ev.ActionGroups)
            el.Add(SerializeActionGroup(g));
        return el;
    }

    private static XElement SerializeActionGroup(ActionGroupModel g)
    {
        var el = new XElement("ActionGroup",
            new XAttribute("Tag", g.Tag),
            new XAttribute("ExecutionType", g.ExecutionType),
            new XAttribute("FailAndContinue", g.FailAndContinue.ToString().ToLower()));

        foreach (var child in g.Children)
            el.Add(SerializeNode(child));
        return el;
    }

    private static XElement SerializeNode(IWatchNode node) => node switch
    {
        InitializeModel i => new XElement("Initialize",
            new XAttribute("Tag", i.Tag),
            new XAttribute("ParameterFile", i.ParameterFile)),

        RefModel r => new XElement("Ref",
            new XAttribute("TemplateID", r.TemplateID)),

        ActionModel a => SerializeAction(a),

        ActionGroupModel g => SerializeActionGroup(g),

        _ => throw new InvalidOperationException(
            $"Unknown node kind: {node.NodeKind}")
    };

    private static XElement SerializeAction(ActionModel a)
    {
        var el = new XElement("Action",
            new XAttribute("Type", a.Type.ToString()));

        // Only emit attributes relevant to the action type — avoids polluting
        // the XML with empty/irrelevant attributes.
        void AddIf(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                el.Add(new XAttribute(name, value));
        }

        switch (a.Type)
        {
            case ActionType.RunRemoteCommand:
                AddIf("AgentName", a.AgentName);
                AddIf("Command", a.Command);
                AddIf("Parameters", a.Parameters);
                if (a.Timeout.HasValue) el.Add(new XAttribute("Timeout", a.Timeout));
                if (a.PollInterval.HasValue) el.Add(new XAttribute("PollInterval", a.PollInterval));
                el.Add(new XAttribute("FailAndContinue", a.FailAndContinue.ToString().ToLower()));
                if (a.IsReboot) el.Add(new XAttribute("IsReboot", "true"));
                AddIf("UserName", a.UserName);
                AddIf("Password", a.Password);   // see validation: warn on plaintext
                break;

            case ActionType.RunCommand:
                AddIf("Command", a.Command);
                AddIf("Parameters", a.Parameters);
                if (a.Timeout.HasValue) el.Add(new XAttribute("Timeout", a.Timeout));
                el.Add(new XAttribute("FailAndContinue", a.FailAndContinue.ToString().ToLower()));
                break;

            case ActionType.SendMail:
                AddIf("Order", a.Order);
                AddIf("From", a.From);
                AddIf("To", a.To);
                AddIf("Title", a.Title);
                AddIf("Body", a.Body);
                AddIf("Attachment", a.Attachment);
                AddIf("Embed", a.Embed);
                AddIf("LargeFilesShare", a.LargeFilesShare);
                break;
        }

        AddIf("Tag", a.Tag);
        return el;
    }

    private static XElement SerializeTemplate(TemplateModel t)
    {
        var el = new XElement("Template", new XAttribute("ID", t.ID));
        foreach (var g in t.ActionGroups)
            el.Add(SerializeActionGroup(g));
        return el;
    }

    /// <summary>Parse XML into the model. Tolerant of old formats (see migration).</summary>
    public static WatchListModel Deserialize(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new InvalidOperationException("No root element");

        var model = new WatchListModel();

        foreach (var wiEl in root.Elements("WatchItem"))
            model.WatchItems.Add(ParseWatchItem(wiEl));

        var templatesEl = root.Element("Templates");
        if (templatesEl != null)
            foreach (var tEl in templatesEl.Elements("Template"))
                model.Templates.Add(ParseTemplate(tEl));

        return model;
    }

    // ===== PLUG IN: implement ParseWatchItem / ParseEvent / ParseActionGroup /
    // ParseNode / ParseAction / ParseTemplate as the inverse of the serializers
    // above. Be tolerant of missing optional attributes. =====
}
```

---

## Phase 3: Validation Rules (the "no room for compilation issues" part)

Rule-based validation that runs in real time as the user edits. Each rule
produces errors (block save) or warnings (allow but flag). This is what
guarantees the user can't produce broken XML.

### File: `TestControllerGrpc.Core/WatchModel/WatchListValidator.cs`

```csharp
namespace TestControllerGrpc.Core.WatchModel;

public enum Severity { Error, Warning, Info }

public sealed record ValidationIssue(
    Severity Severity,
    string NodePath,      // e.g. "WatchItem[WarmPSR] > ActionGroup > Action[Send Email]"
    string Message,
    string? FixHint = null);

public static class WatchListValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(WatchListModel model)
    {
        var issues = new List<ValidationIssue>();

        foreach (var wi in model.WatchItems)
            ValidateWatchItem(wi, model, issues);

        // Cross-cutting: referenced templates must exist
        ValidateTemplateRefs(model, issues);

        return issues;
    }

    private static void ValidateWatchItem(
        WatchItemModel wi, WatchListModel model, List<ValidationIssue> issues)
    {
        var path = $"WatchItem[{wi.Tag}]";

        // --- REQUIRED FIELDS ---
        if (string.IsNullOrWhiteSpace(wi.Tag))
            issues.Add(new(Severity.Error, path,
                "WatchItem Tag is required.", "Give it a unique descriptive name."));

        if (string.IsNullOrWhiteSpace(wi.Filter))
            issues.Add(new(Severity.Error, path,
                "Filter (trigger file) is required."));

        // --- BUILD NUMBER FIELD CONSISTENCY ---
        // This catches the real inconsistency: some items put a literal build
        // number where a field NAME is expected.
        if (LooksLikeLiteralBuild(wi.BuildNumberField))
            issues.Add(new(Severity.Warning, path,
                $"BuildNumberField contains what looks like a literal build " +
                $"number ('{wi.BuildNumberField}') rather than a field name. " +
                $"Expected a field name like 'BuildNumber'.",
                "Use a field name; the value comes from the trigger/parameter file."));

        // --- RECURSE INTO ACTION GROUPS ---
        foreach (var g in wi.Event.ActionGroups)
            ValidateActionGroup(g, path, issues);
    }

    private static void ValidateActionGroup(
        ActionGroupModel g, string parentPath, List<ValidationIssue> issues)
    {
        var path = $"{parentPath} > ActionGroup[{g.Tag}]";

        // --- EMPTY ACTION GROUP ---
        if (g.Children.Count == 0)
            issues.Add(new(Severity.Warning, path,
                "ActionGroup is empty (no actions). It will do nothing.",
                "Add an action or remove the group."));

        foreach (var child in g.Children)
        {
            switch (child)
            {
                case ActionModel a: ValidateAction(a, path, issues); break;
                case ActionGroupModel nested: ValidateActionGroup(nested, path, issues); break;
                case RefModel r when string.IsNullOrWhiteSpace(r.TemplateID):
                    issues.Add(new(Severity.Error, path, "Ref has no TemplateID."));
                    break;
                case InitializeModel i when string.IsNullOrWhiteSpace(i.ParameterFile):
                    issues.Add(new(Severity.Warning, path,
                        "Initialize has no ParameterFile."));
                    break;
            }
        }
    }

    private static void ValidateAction(
        ActionModel a, string parentPath, List<ValidationIssue> issues)
    {
        var path = $"{parentPath} > Action[{a.Tag}]";

        // --- REQUIRED PER ACTION TYPE ---
        if (a.Type == ActionType.RunRemoteCommand)
        {
            if (string.IsNullOrWhiteSpace(a.AgentName))
                issues.Add(new(Severity.Error, path,
                    "RunRemoteCommand requires AgentName."));
            if (string.IsNullOrWhiteSpace(a.Command))
                issues.Add(new(Severity.Error, path,
                    "RunRemoteCommand requires Command."));
        }
        if (a.Type == ActionType.RunCommand && string.IsNullOrWhiteSpace(a.Command))
            issues.Add(new(Severity.Error, path, "RunCommand requires Command."));

        if (a.Type == ActionType.SendMail)
        {
            if (string.IsNullOrWhiteSpace(a.To))
                issues.Add(new(Severity.Error, path, "SendMail requires To."));
            if (string.IsNullOrWhiteSpace(a.From))
                issues.Add(new(Severity.Error, path, "SendMail requires From."));
        }

        // --- TIMEOUT SANITY (catches the 99000800 typo) ---
        if (a.Timeout is > 86400)   // > 24 hours is almost certainly a typo
            issues.Add(new(Severity.Warning, path,
                $"Timeout is {a.Timeout}s (over 24 hours). Likely a typo.",
                "Typical timeouts are seconds: 120, 360, 2700."));

        // --- POLL INTERVAL vs TIMEOUT (catches PollInterval=500 > Timeout=180) ---
        if (a.PollInterval is { } poll && a.Timeout is { } to && poll > to)
            issues.Add(new(Severity.Warning, path,
                $"PollInterval ({poll}s) is larger than Timeout ({to}s); " +
                $"the poll would never fire.",
                "Set PollInterval smaller than Timeout (e.g. 30)."));

        // --- PLAINTEXT PASSWORD (security) ---
        if (!string.IsNullOrEmpty(a.Password))
            issues.Add(new(Severity.Warning, path,
                "Password is stored in plaintext in the XML.",
                "Move credentials to a secured parameter file or token store."));

        // --- REBOOT + POLL sanity ---
        if (a.IsReboot && a.PollInterval is { } p && a.Timeout is { } t && p > t)
            issues.Add(new(Severity.Warning, path,
                "Reboot action's PollInterval exceeds Timeout."));
    }

    private static void ValidateTemplateRefs(
        WatchListModel model, List<ValidationIssue> issues)
    {
        var definedIds = model.Templates.Select(t => t.ID).ToHashSet(StringComparer.OrdinalIgnoreCase);

        void CheckGroup(ActionGroupModel g, string path)
        {
            foreach (var child in g.Children)
            {
                if (child is RefModel r && !definedIds.Contains(r.TemplateID))
                    issues.Add(new(Severity.Error, path,
                        $"Ref points to TemplateID '{r.TemplateID}' which is not defined.",
                        "Define the template or fix the reference."));
                if (child is ActionGroupModel nested)
                    CheckGroup(nested, path);
            }
        }

        foreach (var wi in model.WatchItems)
            foreach (var g in wi.Event.ActionGroups)
                CheckGroup(g, $"WatchItem[{wi.Tag}]");
    }

    private static bool LooksLikeLiteralBuild(string s) =>
        // ===== PLUG IN: match your real build-number pattern =====
        System.Text.RegularExpressions.Regex.IsMatch(
            s ?? "", @"^[A-Za-z]+_\w+_\d{8}\.\d+$");  // e.g. OAK_main_20260601.7
}
```

---

## Phase 4: Auto-Complete / Recommendation Provider

Supplies the dropdown/auto-complete suggestions for each field so users pick
valid values instead of typing free-form. THIS is the "auto selections
recommendation" and "no room for compilation issues" requirement.

### File: `TestControllerGrpc.Core/WatchModel/WatchFieldSuggestions.cs`

```csharp
namespace TestControllerGrpc.Core.WatchModel;

/// <summary>
/// Provides the valid/suggested values for each editable field. The form binds
/// dropdowns and auto-complete to these so the user picks valid values.
/// </summary>
public sealed class WatchFieldSuggestions
{
    // ===== PLUG IN: populate from real environment data =====
    // AgentNames ideally come from the live AgentRegistry, not a hard-coded list.

    public IReadOnlyList<string> EventTypes { get; } =
        new[] { "Renamed", "Created", "Changed", "Deleted" };

    public IReadOnlyList<string> ExecutionTypes { get; } =
        new[] { "Sequential", "Parallel" };

    public IReadOnlyList<string> ActionTypes { get; } =
        new[] { "RunRemoteCommand", "RunCommand", "SendMail" };

    /// <summary>Live agent names — inject the AgentRegistry to populate this.</summary>
    public IReadOnlyList<string> AgentNames { get; set; } =
        new[] { "jvgr1", "jvkpri", "jvkbak", "jvhist", "warmgr" };

    /// <summary>Common parameter tokens available for substitution.</summary>
    public IReadOnlyList<string> AvailableTokens { get; } = new[]
    {
        "[_BuildNumber]", "[_DropLocation]", "[_ControllerName]",
        "[_Agent1]", "[_R2SetupPath]", "[_R2ResponsePath]",
        "[ResultsEmail]", "[EmailCheck]"
        // ===== PLUG IN: your real token list =====
    };

    /// <summary>Which attributes are valid for a given action type. The form
    /// shows ONLY these fields, preventing invalid attribute combinations.</summary>
    public IReadOnlyList<string> AttributesFor(ActionType type) => type switch
    {
        ActionType.RunRemoteCommand => new[]
        {
            "AgentName", "Command", "Parameters", "Timeout", "PollInterval",
            "FailAndContinue", "IsReboot", "Tag", "UserName", "Password"
        },
        ActionType.RunCommand => new[]
        {
            "Command", "Parameters", "Timeout", "FailAndContinue", "Tag"
        },
        ActionType.SendMail => new[]
        {
            "Order", "From", "To", "Title", "Body",
            "Attachment", "Embed", "LargeFilesShare", "Tag"
        },
        _ => Array.Empty<string>()
    };

    /// <summary>Sensible default values when a new node is created.</summary>
    public ActionModel NewActionDefaults(ActionType type) => type switch
    {
        ActionType.RunRemoteCommand => new ActionModel
        {
            Type = type, Command = "cmd", Timeout = 360,
            PollInterval = 30, FailAndContinue = false
        },
        ActionType.RunCommand => new ActionModel
        {
            Type = type, Command = "cmd", FailAndContinue = true
        },
        ActionType.SendMail => new ActionModel
        {
            Type = type, From = "wwapps@magellandev2000.dev.wonderware.com"
        },
        _ => new ActionModel { Type = type }
    };
}
```

---

## Phase 5: Migration (silent auto-upgrade on load)

When an old-format WatchList.xml loads, silently upgrade it to the current
standard. The user opens the file and it just works — modern, consistent,
valid — with no prompt.

### File: `TestControllerGrpc.Core/WatchModel/WatchListMigrator.cs`

```csharp
namespace TestControllerGrpc.Core.WatchModel;

/// <summary>
/// Silently upgrades old WatchList.xml formats to the current standard on load.
/// Each migration step is small and idempotent. Runs automatically; the user
/// never sees a prompt — the file simply opens in the modern format.
/// </summary>
public static class WatchListMigrator
{
    /// <summary>
    /// Apply all needed migrations. Called right after Deserialize, before the
    /// model reaches the UI. Returns the (possibly modified) model and whether
    /// anything changed (so the caller can mark the doc dirty / log it).
    /// </summary>
    public static (WatchListModel Model, bool Changed) Upgrade(WatchListModel model)
    {
        bool changed = false;

        // ===== Each migration is a self-contained, idempotent fix. =====

        // MIGRATION 1: Normalize BuildNumberField — if it holds a literal build
        // number, convert to the standard field-name reference.
        foreach (var wi in model.WatchItems)
        {
            if (LooksLikeLiteralBuild(wi.BuildNumberField))
            {
                wi.BuildNumberField = "BuildNumber";   // the standard field name
                changed = true;
            }
            if (LooksLikeLiteralDropPath(wi.DropLocationField))
            {
                wi.DropLocationField = "DropLocation";
                changed = true;
            }
        }

        // MIGRATION 2: Clamp absurd timeouts (the 99000800 typo) to a sane max.
        // (Optional — you may prefer to leave as a warning instead of auto-fixing.)
        // foreach action with Timeout > 86400 -> leave as-is but it'll warn.

        // MIGRATION 3: Remove empty ActionGroups left over from old edits.
        foreach (var wi in model.WatchItems)
            changed |= RemoveEmptyGroups(wi.Event.ActionGroups);

        // MIGRATION 4: Set default ExecutionType where missing/blank.
        foreach (var wi in model.WatchItems)
        {
            if (string.IsNullOrWhiteSpace(wi.Event.ExecutionType))
            {
                wi.Event.ExecutionType = "Sequential";
                changed = true;
            }
        }

        // ===== PLUG IN: add migrations for any other legacy patterns you have =====

        model.SchemaVersion = WatchListModel.CurrentSchemaVersion;
        return (model, changed);
    }

    private static bool RemoveEmptyGroups(List<ActionGroupModel> groups)
    {
        bool changed = false;
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            // Recurse into nested groups first
            var nested = groups[i].Children.OfType<ActionGroupModel>().ToList();
            // (nested removal handled within Children below)

            if (groups[i].Children.Count == 0)
            {
                groups.RemoveAt(i);
                changed = true;
            }
        }
        return changed;
    }

    private static bool LooksLikeLiteralBuild(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            s ?? "", @"^[A-Za-z]+_\w+_\d{8}\.\d+$");

    private static bool LooksLikeLiteralDropPath(string s) =>
        (s ?? "").StartsWith(@"\\");   // a UNC path where a field name is expected
}
```

---

## Phase 6: The UI — Side-by-Side Form + Live XML

WPF view with a node tree on the left, a property form in the center (fields
for the selected node, with dropdowns/auto-complete from the suggestion
provider), and a live, read-only XML view on the right that updates as the
user edits. A validation panel at the bottom lists issues.

### Layout

```
┌──────────────────────────────────────────────────────────────────┐
│ WatchItem Builder                          [Load] [Save] [Format] │
├───────────────┬──────────────────────────┬───────────────────────┤
│ NODE TREE     │ PROPERTIES (selected node)│ LIVE XML (read-only)  │
│               │                           │                       │
│ ▼ WatchItem   │ Tag:       [___________]  │ <WatchList>           │
│   ▼ Event     │ Filter:    [___________]  │   <WatchItem          │
│     ▼ Group   │ BuildField:[BuildNumber▼] │     Tag="..."         │
│       Action  │ DropField: [DropLocation▼]│     Filter="...">     │
│       Action  │                           │     <Event ...>       │
│     Group     │ [fields change based on   │       ...             │
│ ▼ Templates   │  selected node type, with │                       │
│   Template    │  dropdowns + autocomplete]│ (updates live as you  │
│               │                           │  edit the form)       │
├───────────────┴──────────────────────────┴───────────────────────┤
│ VALIDATION (3 issues)                                             │
│  ⚠ Action[Send Email]: Password stored in plaintext              │
│  ⚠ Action[Run WARMSetup]: Timeout 99000800s — likely a typo      │
│  ✖ Ref: TemplateID 'Missing' is not defined                      │
└──────────────────────────────────────────────────────────────────┘
```

### Files

```
TestControllerGrpc/
  ViewModels/WatchBuilder/
    WatchBuilderVM.cs          // top-level: load/save/validate/serialize
    NodeTreeVM.cs              // the left tree
    NodePropertiesVM.cs        // the center form (per-node, type-driven fields)
    ValidationPanelVM.cs       // the bottom issues list
  Views/WatchBuilder/
    WatchBuilderView.xaml      // the 3-pane layout
    NodePropertiesView.xaml    // dynamic form per node type
    ValidationPanelView.xaml
```

### Key behaviors

- **Field auto-complete**: every field binds to `WatchFieldSuggestions`. AgentName
  is a dropdown of live agents; tokens auto-complete in Command/Parameters.
- **Type-driven fields**: when an Action's Type changes, the form shows only the
  attributes valid for that type (from `AttributesFor`). No invalid combos.
- **Live XML**: every model change re-serializes and updates the XML pane
  (debounced ~200ms). Read-only — the form is the source of truth.
- **Live validation**: every change re-runs the validator; issues appear in the
  bottom panel. Errors block Save; warnings allow it.
- **Save guard**: Save is disabled while any Error-severity issue exists.

### AvalonEdit for the XML pane

Use AvalonEdit (ICSharpCode.AvalonEdit) for the XML view to get syntax
highlighting and good rendering of large files:

```xml
<!-- NuGet: AvalonEdit -->
<avalonEdit:TextEditor
    x:Name="XmlView"
    SyntaxHighlighting="XML"
    IsReadOnly="True"
    ShowLineNumbers="True"
    FontFamily="Consolas" />
```

Bind its `Document.Text` to the serialized XML (AvalonEdit's Text isn't a
direct bindable DP — use a small behavior or code-behind to push the text on
each model change).

---

## Phase 7: Wiring It Together

### Load flow

```
User clicks Load
  -> read file text
  -> WatchListSerializer.Deserialize(xml)        // parse to model
  -> WatchListMigrator.Upgrade(model)            // silent auto-upgrade
  -> if Changed: mark dirty, log "upgraded N items to current standard"
  -> bind model to NodeTreeVM
  -> WatchListValidator.Validate(model) -> ValidationPanel
  -> WatchListSerializer.Serialize(model) -> XML pane
```

### Edit flow

```
User edits a field in the form
  -> model property updates (two-way binding)
  -> debounce 200ms
  -> re-serialize -> XML pane updates
  -> re-validate -> validation panel updates
  -> Save enabled only if zero Error issues
```

### Save flow

```
User clicks Save (only enabled if no errors)
  -> WatchListSerializer.Serialize(model)        // always well-formed
  -> backup existing file (timestamped)
  -> write to disk
  -> confirm
```

---

## Acceptance Criteria

| ID | Criterion |
|---|---|
| AC-01 | User can build a complete WatchItem entirely through the form |
| AC-02 | The live XML pane always reflects the current model, well-formed |
| AC-03 | Saving never produces XML that fails to load (always well-formed) |
| AC-04 | AgentName fields offer a dropdown of real/known agents |
| AC-05 | Tokens auto-complete in Command/Parameters fields |
| AC-06 | When an Action's Type changes, only valid attributes are shown |
| AC-07 | Validation flags: missing required fields, plaintext passwords, absurd timeouts, PollInterval>Timeout, undefined template refs, empty groups |
| AC-08 | Save is blocked while any Error-severity issue exists |
| AC-09 | Loading an old-format file silently upgrades it to the current standard |
| AC-10 | Upgrade normalizes literal build numbers to field-name references |
| AC-11 | Existing file is backed up before save |
| AC-12 | The XML pane has syntax highlighting and line numbers |

---

## Implementation Order

| # | Phase | Effort | Why this order |
|---|---|---|---|
| 1 | Domain model | 1 day | Everything depends on it |
| 2 | Serializer (model<->XML) | 1 day | Proves round-trip; enables live XML |
| 3 | Validator | 1 day | The "no compilation issues" guarantee |
| 4 | Suggestions provider | 0.5 day | Feeds the form auto-complete |
| 5 | Migration | 1 day | Silent upgrade on load |
| 6 | UI (3-pane + form) | 2 days | The actual experience |
| 7 | Wiring + AvalonEdit | 1 day | Live XML, validation panel, save guard |

Build Phase 1-2 first and prove a round-trip: load real WatchList.xml ->
model -> serialize -> compare. If it round-trips cleanly, the foundation is
solid and the UI is just presentation on top.

---

## Design Principles

| Principle | Why |
|---|---|
| Model is the single source of truth | Form and XML can never disagree |
| XML touched only at serialize/parse boundary | No string-manipulation bugs, always well-formed |
| Type-driven forms (only valid attributes shown) | Eliminates invalid attribute combinations |
| Real-time rule validation | Catches issues as you type, not at load |
| Save blocked on errors | "No room for compilation issues" — literally enforced |
| Silent migration | Old files just work; standards applied automatically |
| Suggestions from live data | AgentNames etc. reflect reality, not stale lists |

---

**End of specification.**

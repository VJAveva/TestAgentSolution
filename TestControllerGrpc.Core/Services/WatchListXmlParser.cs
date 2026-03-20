using System.Xml.Linq;
using TestControllerGrpc.Models;

namespace TestControllerGrpc.Services;

/// <summary>
/// Parses and serializes the WatchList XML vocabulary file.
/// Handles all elements: WatchItem, Event, ActionGroup, Action,
/// Initialize, Ref, Template — with full attribute mapping.
/// </summary>
public static class WatchListXmlParser
{
    // ── Read ───────────────────────────────────────────────────────────

    public static WatchListConfig Load(string filePath)
    {
        var doc = XDocument.Load(filePath);
        var root = doc.Root ?? throw new InvalidOperationException("Empty XML");

        var config = new WatchListConfig { FilePath = filePath };

        // Parse <Templates> section first (needed for Ref resolution)
        var templatesEl = root.Element("Templates");
        if (templatesEl is not null)
        {
            foreach (var tEl in templatesEl.Elements("Template"))
            {
                config.Templates.Add(new TemplateConfig
                {
                    ID = Attr(tEl, "ID"),
                    Children = ParseChildren(tEl),
                });
            }
        }

        // Parse <WatchItem> elements
        foreach (var wiEl in root.Elements("WatchItem"))
        {
            var wi = new WatchItemConfig
            {
                Tag = Attr(wiEl, "Tag"),
                Path = Attr(wiEl, "Path"),
                Filter = Attr(wiEl, "Filter", "*.*"),
                BuildNumberField = Attr(wiEl, "BuildNumberField", "BuildNumber"),
                DropLocationField = Attr(wiEl, "DropLocationField", "DropLocation"),
            };
            foreach (var evEl in wiEl.Elements("Event"))
            {
                wi.Events.Add(new EventConfig
                {
                    Type = Attr(evEl, "Type", "Renamed"),
                    ExecutionType = ParseExecMode(Attr(evEl, "ExecutionType")),
                    Children = ParseChildren(evEl),
                });
            }
            config.WatchItems.Add(wi);
        }

        return config;
    }

    private static List<IActionNode> ParseChildren(XElement parent)
    {
        var list = new List<IActionNode>();
        foreach (var el in parent.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "ActionGroup":
                    list.Add(new ActionGroupConfig
                    {
                        Tag = Attr(el, "Tag"),
                        ExecutionType = ParseExecMode(Attr(el, "ExecutionType")),
                        FailAndContinue = AttrBool(el, "FailAndContinue"),
                        Children = ParseChildren(el),
                    });
                    break;

                case "Action":
                    list.Add(new ActionConfig
                    {
                        Type = ParseActionType(Attr(el, "Type")),
                        AgentName = Attr(el, "AgentName"),
                        Command = Attr(el, "Command"),
                        Parameters = Attr(el, "Parameters"),
                        Timeout = AttrInt(el, "Timeout"),
                        PollInterval = AttrInt(el, "PollInterval", 1000),
                        FailAndContinue = AttrBool(el, "FailAndContinue"),
                        IsReboot = AttrBool(el, "IsReboot"),
                        Order = Attr(el, "Order"),
                        CompletionCheckCommand = Attr(el, "CompletionCheckCommand"),
                        CompletionPollIntervalSeconds = AttrInt(el, "CompletionPollIntervalSeconds", 30),
                        UserName = Attr(el, "UserName"),
                        Password = Attr(el, "Password"),
                        From = Attr(el, "From"),
                        To = Attr(el, "To"),
                        Title = Attr(el, "Title"),
                        Body = Attr(el, "Body"),
                        Attachment = Attr(el, "Attachment"),
                        Embed = Attr(el, "Embed"),
                        LargeFilesShare = Attr(el, "LargeFilesShare"),
                    });
                    break;

                case "Initialize":
                    list.Add(new InitializeConfig
                    {
                        Tag = Attr(el, "Tag"),
                        ParameterFile = Attr(el, "ParameterFile"),
                    });
                    break;

                case "Ref":
                    list.Add(new RefConfig
                    {
                        TemplateID = Attr(el, "TemplateID"),
                    });
                    break;
            }
        }
        return list;
    }

    // ── Write ──────────────────────────────────────────────────────────

    public static void Save(WatchListConfig config, string filePath)
    {
        var root = new XElement("WatchList");

        foreach (var wi in config.WatchItems)
        {
            var wiEl = new XElement("WatchItem",
                new XAttribute("Path", wi.Path),
                new XAttribute("Filter", wi.Filter));
            if (!string.IsNullOrEmpty(wi.Tag))
                wiEl.Add(new XAttribute("Tag", wi.Tag));
            AddIfNotEmpty(wiEl, "BuildNumberField", wi.BuildNumberField);
            AddIfNotEmpty(wiEl, "DropLocationField", wi.DropLocationField);

            foreach (var ev in wi.Events)
            {
                var evEl = new XElement("Event",
                    new XAttribute("Type", ev.Type),
                    new XAttribute("ExecutionType", ev.ExecutionType.ToString()));
                WriteChildren(evEl, ev.Children);
                wiEl.Add(evEl);
            }
            root.Add(wiEl);
        }

        if (config.Templates.Count > 0)
        {
            var templatesEl = new XElement("Templates");
            foreach (var t in config.Templates)
            {
                var tEl = new XElement("Template", new XAttribute("ID", t.ID));
                WriteChildren(tEl, t.Children);
                templatesEl.Add(tEl);
            }
            root.Add(templatesEl);
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        doc.Save(filePath);
    }

    private static void WriteChildren(XElement parent, List<IActionNode> children)
    {
        foreach (var child in children)
        {
            switch (child)
            {
                case ActionGroupConfig ag:
                    var agEl = new XElement("ActionGroup",
                        new XAttribute("Tag", ag.Tag),
                        new XAttribute("ExecutionType", ag.ExecutionType.ToString()));
                    if (ag.FailAndContinue) agEl.Add(new XAttribute("FailAndContinue", "true"));
                    WriteChildren(agEl, ag.Children);
                    parent.Add(agEl);
                    break;

                case ActionConfig a:
                    var aEl = new XElement("Action",
                        new XAttribute("Type", a.Type.ToString()));
                    AddIfNotEmpty(aEl, "AgentName", a.AgentName);
                    AddIfNotEmpty(aEl, "Command", a.Command);
                    AddIfNotEmpty(aEl, "Parameters", a.Parameters);
                    if (a.Timeout > 0) aEl.Add(new XAttribute("Timeout", a.Timeout));
                    if (a.PollInterval != 1000) aEl.Add(new XAttribute("PollInterval", a.PollInterval));
                    if (a.FailAndContinue) aEl.Add(new XAttribute("FailAndContinue", "true"));
                    if (a.IsReboot) aEl.Add(new XAttribute("IsReboot", "true"));
                    AddIfNotEmpty(aEl, "Order", a.Order);
                    AddIfNotEmpty(aEl, "CompletionCheckCommand", a.CompletionCheckCommand);
                    if (a.CompletionPollIntervalSeconds != 30 && !string.IsNullOrEmpty(a.CompletionCheckCommand))
                        aEl.Add(new XAttribute("CompletionPollIntervalSeconds", a.CompletionPollIntervalSeconds));
                    AddIfNotEmpty(aEl, "UserName", a.UserName);
                    AddIfNotEmpty(aEl, "Password", a.Password);
                    AddIfNotEmpty(aEl, "From", a.From);
                    AddIfNotEmpty(aEl, "To", a.To);
                    AddIfNotEmpty(aEl, "Title", a.Title);
                    AddIfNotEmpty(aEl, "Body", a.Body);
                    AddIfNotEmpty(aEl, "Attachment", a.Attachment);
                    AddIfNotEmpty(aEl, "Embed", a.Embed);
                    AddIfNotEmpty(aEl, "LargeFilesShare", a.LargeFilesShare);
                    parent.Add(aEl);
                    break;

                case InitializeConfig init:
                    parent.Add(new XElement("Initialize",
                        new XAttribute("Tag", init.Tag),
                        new XAttribute("ParameterFile", init.ParameterFile)));
                    break;

                case RefConfig r:
                    parent.Add(new XElement("Ref",
                        new XAttribute("TemplateID", r.TemplateID)));
                    break;
            }
        }
    }

    // ── Single WatchItem serialization (for XML Editor dialog) ────────

    public static string SerializeWatchItem(WatchItemConfig wi)
    {
        // Build all attributes up front — AddFirst throws for XAttribute
        var attrs = new List<object>();
        if (!string.IsNullOrEmpty(wi.Tag))
            attrs.Add(new XAttribute("Tag", wi.Tag));
        attrs.Add(new XAttribute("Path", wi.Path));
        attrs.Add(new XAttribute("Filter", wi.Filter));

        var wiEl = new XElement("WatchItem", attrs.ToArray());
        AddIfNotEmpty(wiEl, "BuildNumberField", wi.BuildNumberField);
        AddIfNotEmpty(wiEl, "DropLocationField", wi.DropLocationField);

        foreach (var ev in wi.Events)
        {
            var evEl = new XElement("Event",
                new XAttribute("Type", ev.Type),
                new XAttribute("ExecutionType", ev.ExecutionType.ToString()));
            WriteChildren(evEl, ev.Children);
            wiEl.Add(evEl);
        }
        return wiEl.ToString(SaveOptions.None);
    }

    public static WatchItemConfig? DeserializeWatchItem(string xml)
    {
        var wiEl = XElement.Parse(xml);
        if (wiEl.Name.LocalName != "WatchItem") return null;

        var wi = new WatchItemConfig
        {
            Tag = Attr(wiEl, "Tag"),
            Path = Attr(wiEl, "Path"),
            Filter = Attr(wiEl, "Filter", "*.*"),
            BuildNumberField = Attr(wiEl, "BuildNumberField", "BuildNumber"),
            DropLocationField = Attr(wiEl, "DropLocationField", "DropLocation"),
        };
        foreach (var evEl in wiEl.Elements("Event"))
        {
            wi.Events.Add(new EventConfig
            {
                Type = Attr(evEl, "Type", "Renamed"),
                ExecutionType = ParseExecMode(Attr(evEl, "ExecutionType")),
                Children = ParseChildren(evEl),
            });
        }
        return wi;
    }

    // ── Template list serialization (for Template XML Editor dialog) ──

    public static string SerializeTemplateList(List<TemplateConfig> templates)
    {
        var templatesEl = new XElement("Templates");
        foreach (var t in templates)
        {
            var tEl = new XElement("Template", new XAttribute("ID", t.ID));
            WriteChildren(tEl, t.Children);
            templatesEl.Add(tEl);
        }
        return templatesEl.ToString(SaveOptions.None);
    }

    public static List<TemplateConfig>? DeserializeTemplateList(string xml)
    {
        var root = XElement.Parse(xml);
        if (root.Name.LocalName != "Templates") return null;

        var templates = new List<TemplateConfig>();
        foreach (var tEl in root.Elements("Template"))
        {
            templates.Add(new TemplateConfig
            {
                ID = Attr(tEl, "ID"),
                Children = ParseChildren(tEl),
            });
        }
        return templates;
    }

    // ── Full WatchList serialization (for inline XML editor) ───────────

    public static string SerializeWatchList(WatchListConfig config)
    {
        var root = new XElement("WatchList");

        foreach (var wi in config.WatchItems)
        {
            var wiEl = new XElement("WatchItem",
                new XAttribute("Path", wi.Path),
                new XAttribute("Filter", wi.Filter));
            if (!string.IsNullOrEmpty(wi.Tag))
                wiEl.Add(new XAttribute("Tag", wi.Tag));
            AddIfNotEmpty(wiEl, "BuildNumberField", wi.BuildNumberField);
            AddIfNotEmpty(wiEl, "DropLocationField", wi.DropLocationField);

            foreach (var ev in wi.Events)
            {
                var evEl = new XElement("Event",
                    new XAttribute("Type", ev.Type),
                    new XAttribute("ExecutionType", ev.ExecutionType.ToString()));
                WriteChildren(evEl, ev.Children);
                wiEl.Add(evEl);
            }
            root.Add(wiEl);
        }

        if (config.Templates.Count > 0)
        {
            var templatesEl = new XElement("Templates");
            foreach (var t in config.Templates)
            {
                var tEl = new XElement("Template", new XAttribute("ID", t.ID));
                WriteChildren(tEl, t.Children);
                templatesEl.Add(tEl);
            }
            root.Add(templatesEl);
        }

        return root.ToString(SaveOptions.None);
    }

    public static WatchListConfig? DeserializeWatchList(string xml)
    {
        var root = XElement.Parse(xml);
        if (root.Name.LocalName != "WatchList") return null;

        var config = new WatchListConfig();

        var templatesEl = root.Element("Templates");
        if (templatesEl is not null)
        {
            foreach (var tEl in templatesEl.Elements("Template"))
            {
                config.Templates.Add(new TemplateConfig
                {
                    ID = Attr(tEl, "ID"),
                    Children = ParseChildren(tEl),
                });
            }
        }

        foreach (var wiEl in root.Elements("WatchItem"))
        {
            var wi = new WatchItemConfig
            {
                Tag = Attr(wiEl, "Tag"),
                Path = Attr(wiEl, "Path"),
                Filter = Attr(wiEl, "Filter", "*.*"),
                BuildNumberField = Attr(wiEl, "BuildNumberField", "BuildNumber"),
                DropLocationField = Attr(wiEl, "DropLocationField", "DropLocation"),
            };
            foreach (var evEl in wiEl.Elements("Event"))
            {
                wi.Events.Add(new EventConfig
                {
                    Type = Attr(evEl, "Type", "Renamed"),
                    ExecutionType = ParseExecMode(Attr(evEl, "ExecutionType")),
                    Children = ParseChildren(evEl),
                });
            }
            config.WatchItems.Add(wi);
        }

        return config;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string Attr(XElement el, string name, string def = "")
        => el.Attribute(name)?.Value?.Trim() ?? def;

    private static bool AttrBool(XElement el, string name)
        => string.Equals(Attr(el, name), "true", StringComparison.OrdinalIgnoreCase);

    private static int AttrInt(XElement el, string name, int def = 0)
        => int.TryParse(Attr(el, name), out var v) ? v : def;

    private static ExecutionMode ParseExecMode(string val)
        => string.Equals(val, "Parallel", StringComparison.OrdinalIgnoreCase)
            ? ExecutionMode.Parallel : ExecutionMode.Sequential;

    private static ActionType ParseActionType(string val) => val switch
    {
        "RunRemoteCommand" => ActionType.RunRemoteCommand,
        "SendMail" => ActionType.SendMail,
        _ => ActionType.RunCommand,
    };

    private static void AddIfNotEmpty(XElement el, string name, string val)
    {
        if (!string.IsNullOrEmpty(val))
            el.Add(new XAttribute(name, val));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Partial serialization — export/import selected items
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Serializes a subset of WatchItems (and optionally their referenced Templates)
    /// to a standalone &lt;WatchList&gt; XML string that can be re-imported.
    /// </summary>
    public static string SerializeWatchItemsToXml(
        List<WatchItemConfig> items,
        List<TemplateConfig>? referencedTemplates = null)
    {
        var root = new XElement("WatchList");

        foreach (var wi in items)
        {
            var wiEl = BuildWatchItemElement(wi);
            root.Add(wiEl);
        }

        if (referencedTemplates is { Count: > 0 })
        {
            var templatesEl = new XElement("Templates");
            foreach (var t in referencedTemplates)
            {
                var tEl = new XElement("Template", new XAttribute("ID", t.ID));
                WriteChildren(tEl, t.Children);
                templatesEl.Add(tEl);
            }
            root.Add(templatesEl);
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root)
            .ToString(SaveOptions.None);
    }

    /// <summary>
    /// Serializes a list of Templates to a standalone &lt;WatchList&gt; XML string.
    /// </summary>
    public static string SerializeTemplatesToXml(List<TemplateConfig> templates)
    {
        var root = new XElement("WatchList");
        var templatesEl = new XElement("Templates");
        foreach (var t in templates)
        {
            var tEl = new XElement("Template", new XAttribute("ID", t.ID));
            WriteChildren(tEl, t.Children);
            templatesEl.Add(tEl);
        }
        root.Add(templatesEl);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root)
            .ToString(SaveOptions.None);
    }

    /// <summary>
    /// Parses WatchItems and any bundled Templates from a standalone XML string.
    /// Accepts both full &lt;WatchList&gt; documents and single &lt;WatchItem&gt; fragments.
    /// </summary>
    public static (List<WatchItemConfig> Items, List<TemplateConfig> Templates)
        ParseWatchItemsFromXml(string xml)
    {
        var root = XElement.Parse(xml);

        // Handle a single <WatchItem> element directly
        if (root.Name.LocalName == "WatchItem")
        {
            var single = ParseSingleWatchItemElement(root);
            return (new List<WatchItemConfig> { single }, []);
        }

        var items = root.Elements("WatchItem")
            .Select(ParseSingleWatchItemElement)
            .ToList();

        var templates = root.Element("Templates")?.Elements("Template")
            .Select(tEl => new TemplateConfig
            {
                ID = Attr(tEl, "ID"),
                Children = ParseChildren(tEl),
            }).ToList() ?? [];

        return (items, templates);
    }

    /// <summary>
    /// Parses Templates from a standalone XML string.
    /// Accepts &lt;WatchList&gt; with &lt;Templates&gt; or bare &lt;Templates&gt; element.
    /// </summary>
    public static List<TemplateConfig> ParseTemplatesFromXml(string xml)
    {
        var root = XElement.Parse(xml);

        // Bare <Templates> root
        if (root.Name.LocalName == "Templates")
            return root.Elements("Template")
                .Select(tEl => new TemplateConfig
                {
                    ID = Attr(tEl, "ID"),
                    Children = ParseChildren(tEl),
                }).ToList();

        // <WatchList> with nested <Templates>
        return root.Element("Templates")?.Elements("Template")
            .Select(tEl => new TemplateConfig
            {
                ID = Attr(tEl, "ID"),
                Children = ParseChildren(tEl),
            }).ToList() ?? [];
    }

    // ═══════════════════════════════════════════════════════════════════
    // Template dependency detection
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Collects all Ref TemplateID values referenced by a WatchItem,
    /// so export can bundle the necessary Templates.
    /// </summary>
    public static HashSet<string> CollectRefTemplateIds(WatchItemConfig item)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectRefs(item.Events.SelectMany(e => e.Children), ids);
        return ids;
    }

    private static void CollectRefs(IEnumerable<IActionNode> nodes, HashSet<string> ids)
    {
        foreach (var node in nodes)
        {
            if (node is RefConfig r && !string.IsNullOrEmpty(r.TemplateID))
                ids.Add(r.TemplateID);
            if (node is ActionGroupConfig g)
                CollectRefs(g.Children, ids);
        }
    }

    // ── Internal element builders (shared by multiple serializers) ────

    private static XElement BuildWatchItemElement(WatchItemConfig wi)
    {
        var attrs = new List<object>();
        if (!string.IsNullOrEmpty(wi.Tag))
            attrs.Add(new XAttribute("Tag", wi.Tag));
        attrs.Add(new XAttribute("Path", wi.Path));
        attrs.Add(new XAttribute("Filter", wi.Filter));

        var wiEl = new XElement("WatchItem", attrs.ToArray());
        AddIfNotEmpty(wiEl, "BuildNumberField", wi.BuildNumberField);
        AddIfNotEmpty(wiEl, "DropLocationField", wi.DropLocationField);

        foreach (var ev in wi.Events)
        {
            var evEl = new XElement("Event",
                new XAttribute("Type", ev.Type),
                new XAttribute("ExecutionType", ev.ExecutionType.ToString()));
            WriteChildren(evEl, ev.Children);
            wiEl.Add(evEl);
        }
        return wiEl;
    }

    private static WatchItemConfig ParseSingleWatchItemElement(XElement wiEl)
    {
        var wi = new WatchItemConfig
        {
            Tag = Attr(wiEl, "Tag"),
            Path = Attr(wiEl, "Path"),
            Filter = Attr(wiEl, "Filter", "*.*"),
            BuildNumberField = Attr(wiEl, "BuildNumberField", "BuildNumber"),
            DropLocationField = Attr(wiEl, "DropLocationField", "DropLocation"),
        };
        foreach (var evEl in wiEl.Elements("Event"))
        {
            wi.Events.Add(new EventConfig
            {
                Type = Attr(evEl, "Type", "Renamed"),
                ExecutionType = ParseExecMode(Attr(evEl, "ExecutionType")),
                Children = ParseChildren(evEl),
            });
        }
        return wi;
    }
}

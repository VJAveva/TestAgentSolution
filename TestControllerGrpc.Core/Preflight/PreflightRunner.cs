using System.Diagnostics;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Core.Preflight;

/// <summary>What is about to run, and which pipeline supplies its settings.</summary>
public sealed class PreflightRequest
{
    public required WatchListConfig Config { get; init; }

    /// <summary>Pipeline whose Initialize supplies the values. Null only when nothing can.</summary>
    public WatchItemConfig? Pipeline { get; init; }

    /// <summary>The node subtree actually being run. Empty means "the whole pipeline".</summary>
    public IReadOnlyList<IActionNode> Nodes { get; init; } = [];

    public PreflightScope Scope { get; init; } = PreflightScope.Pipeline;

    /// <summary>Template being borrowed, for the report title.</summary>
    public string? TemplateId { get; init; }

    /// <summary>Values a trigger file supplies, which the pipeline's own files do not carry.</summary>
    public IReadOnlyDictionary<string, string>? TriggerValues { get; init; }

    /// <summary>Who already holds this pipeline's lock, when somebody does. Null means it is free.</summary>
    public string? PipelineLockedBy { get; init; }

    public double MinimumDiskGb { get; init; } = 15;
}

/// <summary>
/// Answers "will this run fail for a reason we could have seen first?" without touching a machine.
/// </summary>
/// <remarks>
/// Built after a production run died with every action at exit -1 because a WatchItem Path pointed
/// at a folder that never existed, and a second run failed because an installer had been
/// consolidated out of the folder the pipeline copies from. Both were visible on disk beforehand.
///
/// Everything here is a read: existence probes on the controller, plus already-cached agent facts.
/// Agent state comes from the heartbeat the agents already report - pre-flight opens no connection,
/// which is what keeps it well inside the 60s budget regardless of fleet size.
/// </remarks>
public sealed class PreflightRunner
{
    private readonly IPreflightFileSystem _fs;
    private readonly Func<string, PreflightAgentFacts> _agentFacts;

    /// <summary>Token names whose values must never reach a report or an email.</summary>
    private static readonly string[] SecretMarkers =
        ["password", "pwd", "secret", "token", "apikey", "api_key", "credential", "connectionstring"];

    public PreflightRunner(IPreflightFileSystem fileSystem, Func<string, PreflightAgentFacts> agentFacts)
    {
        _fs = fileSystem;
        _agentFacts = agentFacts;
    }

    public PreflightReport Run(PreflightRequest request)
    {
        var sw = Stopwatch.StartNew();
        var report = new PreflightReport
        {
            Target = DescribeTarget(request),
            Scope = request.Scope,
        };

        var ctx = new PipelineExecutionContext
        {
            WatchItemTag = request.Pipeline?.Tag ?? "",
            WatchItemPath = request.Pipeline?.Path ?? "",
        };

        CheckSettings(request, ctx, report);
        CheckControllerFiles(request, ctx, report);
        CheckInstallSources(request, ctx, report);
        CheckAgents(request, ctx, report);
        CheckStructure(request, report);
        CheckDisk(request, ctx, report);

        sw.Stop();
        report.Elapsed = sw.Elapsed;
        return report;
    }

    private static string DescribeTarget(PreflightRequest request)
    {
        var pipeline = request.Pipeline?.Tag ?? "(no pipeline)";
        return request.TemplateId is { Length: > 0 } tpl
            ? $"template '{tpl}' using pipeline '{pipeline}'"
            : $"pipeline '{pipeline}'";
    }

    // ── 1. Settings ─────────────────────────────────────────────────

    private void CheckSettings(PreflightRequest request, PipelineExecutionContext ctx, PreflightReport report)
    {
        const string G = PreflightGroups.Settings;

        if (request.Pipeline is null)
        {
            report.Checks.Add(new PreflightCheck(G, "Parameter source", PreflightStatus.Fail,
                "No pipeline supplies this run's settings.",
                "Run this template from a pipeline that Refs it, or set its pipeline context."));
            return;
        }

        var pipeline = request.Pipeline;
        var initializers = ParameterResolver.CollectInitializeNodes(pipeline);

        if (initializers.Count == 0)
        {
            report.Checks.Add(new PreflightCheck(G, "Initialize", PreflightStatus.Warn,
                $"Pipeline '{pipeline.Tag}' has no Initialize node, so it has no parameter file.",
                "Add an Initialize node naming the parameter file this pipeline should load."));
        }

        foreach (var init in initializers)
        {
            if (string.IsNullOrWhiteSpace(init.ParameterFile))
            {
                report.Checks.Add(new PreflightCheck(G, $"Initialize '{init.Tag}'", PreflightStatus.Fail,
                    "The Initialize node names no parameter file.",
                    "Set its ParameterFile attribute in the WatchList."));
                continue;
            }

            var reason = ParameterResolver.TryLoadInitializeSource(ctx, init.ParameterFile, init.Profile, pipeline.Tag);
            if (reason is not null)
            {
                report.Checks.Add(new PreflightCheck(G, $"Parameter file '{Path.GetFileName(init.ParameterFile)}'",
                    PreflightStatus.Fail, reason,
                    $"Check '{init.ParameterFile}' exists and is valid, and that profile '{init.Profile}' is defined in it."));
                continue;
            }

            report.Checks.Add(new PreflightCheck(G, $"Parameter file '{Path.GetFileName(init.ParameterFile)}'",
                PreflightStatus.Pass,
                string.IsNullOrWhiteSpace(init.Profile) ? "Loaded." : $"Loaded, profile '{init.Profile}'."));

            CheckPipelinesEntry(init, pipeline, report);
        }

        // A trigger file outranks every file layer, so it must be applied before tokens are judged.
        if (request.TriggerValues is { Count: > 0 })
        {
            foreach (var (key, value) in request.TriggerValues)
                ParameterResolver.SetParameter(ctx, key, value, ParameterRank.TriggerFile);
        }

        RecordTokens(ctx, report);
        CheckUnresolvedTokens(request, ctx, report);
    }

    private void CheckPipelinesEntry(InitializeConfig init, WatchItemConfig pipeline, PreflightReport report)
    {
        if (!ParameterResolver.IsLayeredConfig(init.ParameterFile)) return;

        var config = ParameterResolver.ReadJsonConfig(init.ParameterFile);
        if (config is null) return;

        if (config.Pipelines.ContainsKey(pipeline.Tag))
        {
            report.Checks.Add(new PreflightCheck(PreflightGroups.Settings, $"Pipelines entry '{pipeline.Tag}'",
                PreflightStatus.Pass, "Present."));
            return;
        }

        report.Checks.Add(new PreflightCheck(PreflightGroups.Settings, $"Pipelines entry '{pipeline.Tag}'",
            PreflightStatus.Warn,
            $"No 'pipelines' entry is keyed '{pipeline.Tag}' in '{Path.GetFileName(init.ParameterFile)}'.",
            $"Add a \"{pipeline.Tag}\" block under \"pipelines\" if this pipeline needs its own build pin."));
    }

    private static void RecordTokens(PipelineExecutionContext ctx, PreflightReport report)
    {
        foreach (var (key, value) in ctx.Parameters)
        {
            // The underscore-less aliases are the same value twice; one row per token is enough.
            if (!key.StartsWith('_')) continue;

            var rank = ctx.ParameterRanks.TryGetValue(key, out var r) ? (ParameterRank)r : ParameterRank.ParameterFile;
            report.Tokens.Add(new PreflightToken(key, Mask(key, value), LayerName(rank)));
        }
    }

    /// <summary>The layer names operators use, not the internal rank names.</summary>
    private static string LayerName(ParameterRank rank) => rank switch
    {
        ParameterRank.Global => "Global",
        ParameterRank.ParameterFile => "Profile",
        ParameterRank.PipelinePin => "Pipeline",
        ParameterRank.TriggerFile => "Trigger",
        ParameterRank.RunOverride => "Run override",
        _ => rank.ToString(),
    };

    private static string Mask(string key, string value)
    {
        foreach (var marker in SecretMarkers)
        {
            if (key.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return SecurityRedactor.Redacted;
        }
        return value;
    }

    private void CheckUnresolvedTokens(PreflightRequest request, PipelineExecutionContext ctx, PreflightReport report)
    {
        var unresolved = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in ActionsInScope(request))
            foreach (var token in ParameterResolver.FindUnresolvedTokens(action, ctx))
                unresolved.Add(token);

        if (unresolved.Count == 0)
        {
            report.Checks.Add(new PreflightCheck(PreflightGroups.Settings, "Token resolution",
                PreflightStatus.Pass, "Every token used by this run resolves."));
            return;
        }

        report.Checks.Add(new PreflightCheck(PreflightGroups.Settings, "Token resolution", PreflightStatus.Fail,
            $"Unresolved: {string.Join(", ", unresolved)}.",
            "Define these in the parameter file's global, profile or pipelines layer, or in the trigger file."));
    }

    // ── 2. Controller files ─────────────────────────────────────────

    private void CheckControllerFiles(PreflightRequest request, PipelineExecutionContext ctx, PreflightReport report)
    {
        const string G = PreflightGroups.ControllerFiles;

        if (request.Pipeline is { } pipeline && !string.IsNullOrWhiteSpace(pipeline.Path))
        {
            var path = ParameterResolver.Resolve(pipeline.Path, ctx);
            Probe(report, G, "Watch folder", path, isDirectory: true,
                $"Create '{path}', or correct the Path attribute on WatchItem '{pipeline.Tag}'.");
        }

        foreach (var token in new[] { "_DropLocation", "_SetupFolder", "_BinariesFolder", "_InstallFolder" })
        {
            if (!ctx.Parameters.TryGetValue(token, out var raw) || string.IsNullOrWhiteSpace(raw)) continue;

            var path = ControllerPath(ParameterResolver.Resolve(raw, ctx), ctx);
            Probe(report, G, $"[{token}]", path, isDirectory: true,
                $"Create '{path}', or correct [{token}] in the parameter file.");
        }

        CheckSmokeTestBinaries(ctx, report);
        CheckControllerScripts(request, ctx, report);
    }

    /// <summary>
    /// A folder-relative value such as <c>TestSetup\Common</c> is relative to the controller's C$
    /// share; the executor builds the same UNC path.
    /// </summary>
    private static string ControllerPath(string value, PipelineExecutionContext ctx)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        if (Path.IsPathRooted(value) || value.StartsWith(@"\\", StringComparison.Ordinal)) return value;

        var controller = ctx.Parameters.TryGetValue("_ControllerName", out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : Environment.MachineName;

        return $@"\\{controller}\c$\{value.TrimStart('\\', '/')}";
    }

    private void CheckSmokeTestBinaries(PipelineExecutionContext ctx, PreflightReport report)
    {
        if (!ctx.Parameters.TryGetValue("_BinariesFolder", out var raw) || string.IsNullOrWhiteSpace(raw)) return;

        var folder = ControllerPath(ParameterResolver.Resolve(raw, ctx), ctx);
        if (_fs.CheckDirectory(folder) != PathAccess.Exists) return; // the folder probe already reported it

        var dll = Path.Combine(folder, "WASSmokeTest.dll");
        if (_fs.CheckFile(dll) == PathAccess.Exists)
        {
            report.Checks.Add(new PreflightCheck(PreflightGroups.ControllerFiles, "WASSmokeTest.dll",
                PreflightStatus.Pass, "Present with its folder contents."));
            return;
        }

        report.Checks.Add(new PreflightCheck(PreflightGroups.ControllerFiles, "WASSmokeTest.dll",
            PreflightStatus.Fail, $"Not found in '{folder}'.",
            "Copy the smoke-test build output - the DLL and its dependencies - into [_BinariesFolder]."));
    }

    /// <summary>
    /// Controller-local commands run from the controller's own disk, so a missing .bat/.ps1 is
    /// visible now rather than as exit -1 later.
    /// </summary>
    private void CheckControllerScripts(PreflightRequest request, PipelineExecutionContext ctx, PreflightReport report)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in ActionsInScope(request))
        {
            if (action.Type != ActionType.RunCommand) continue;

            var command = ParameterResolver.Resolve(action.Command, ctx).Trim().Trim('"');
            if (!LooksLikeScriptPath(command) || !seen.Add(command)) continue;

            Probe(report, PreflightGroups.ControllerFiles, $"Script '{Path.GetFileName(command)}'",
                command, isDirectory: false,
                $"Restore '{command}' on the controller, or correct the Command on action '{action.Tag}'.");
        }
    }

    private static bool LooksLikeScriptPath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        if (command.Contains('[')) return false; // still unresolved - the token check owns that
        if (!Path.IsPathRooted(command) && !command.StartsWith(@"\\", StringComparison.Ordinal)) return false;

        var ext = Path.GetExtension(command);
        return ext is ".bat" or ".cmd" or ".ps1" or ".exe";
    }

    // ── 3. Install sources on the controller ────────────────────────

    /// <summary>
    /// Agent folders are empty until the prepare step copies into them, so there is nothing to
    /// check on the agent. What CAN be wrong is the controller-side source: every agent path under
    /// <c>C:\TestSetup\[_ReleaseName]\</c> must exist at the same relative path inside
    /// <c>[_InstallFolder]</c>, because that is the folder the pipeline copies from.
    /// </summary>
    private void CheckInstallSources(PreflightRequest request, PipelineExecutionContext ctx, PreflightReport report)
    {
        const string G = PreflightGroups.InstallSources;

        if (!ctx.Parameters.TryGetValue("_InstallFolder", out var installRaw) || string.IsNullOrWhiteSpace(installRaw))
            return;

        var source = ControllerPath(ParameterResolver.Resolve(installRaw, ctx), ctx);
        if (_fs.CheckDirectory(source) != PathAccess.Exists)
            return; // the folder probe already reported it; one message is enough

        var release = ctx.Parameters.TryGetValue("_ReleaseName", out var r) ? r : null;
        var agentRoot = $@"C:\TestSetup\{release}";
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = 0;

        foreach (var (key, value) in ctx.Parameters)
        {
            if (!key.StartsWith('_') || string.IsNullOrWhiteSpace(value)) continue;
            if (string.IsNullOrWhiteSpace(release)) continue;
            if (!value.StartsWith(agentRoot, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = value[agentRoot.Length..].TrimStart('\\', '/');
            if (relative.Length == 0 || !checkedPaths.Add(relative)) continue;

            var expected = Path.Combine(source, relative);
            if (_fs.CheckFile(expected) == PathAccess.Exists || _fs.CheckDirectory(expected) == PathAccess.Exists)
            {
                found++;
                continue;
            }

            report.Checks.Add(new PreflightCheck(G, $"[{key}]", PreflightStatus.Fail,
                $"Agents will look for '{value}', but '{expected}' does not exist on the controller to copy from.",
                $"Restore '{relative}' under '{source}'. Consolidating a shared copy elsewhere breaks this - " +
                "the agent copy is made from this folder."));
        }

        if (found > 0 && !report.Checks.Any(c => c.Group == G && c.Status == PreflightStatus.Fail))
        {
            report.Checks.Add(new PreflightCheck(G, "Install source files", PreflightStatus.Pass,
                $"All {found} agent-side path(s) exist in '{source}'."));
        }
    }

    // ── 4. Agents ───────────────────────────────────────────────────

    private void CheckAgents(PreflightRequest request, PipelineExecutionContext ctx, PreflightReport report)
    {
        const string G = PreflightGroups.Agents;

        if (request.PipelineLockedBy is { Length: > 0 } owner)
        {
            report.Checks.Add(new PreflightCheck(G, $"Pipeline '{request.Pipeline?.Tag}'", PreflightStatus.Fail,
                $"Already running - locked by {owner}.",
                "Cancel that run or wait for it to finish."));
        }

        var agents = AgentsInScope(request, ctx);

        if (agents.Count == 0)
        {
            report.Checks.Add(new PreflightCheck(G, "Agents", PreflightStatus.Pass,
                "This run uses no remote agents."));
            return;
        }

        foreach (var agent in agents)
        {
            var facts = _agentFacts(agent);

            if (!facts.IsRegistered)
            {
                report.Checks.Add(new PreflightCheck(G, agent, PreflightStatus.Fail,
                    "Not registered with the controller.",
                    $"Start the agent service on '{agent}', or correct the AgentName in the WatchList."));
                continue;
            }

            if (!facts.IsOnline)
            {
                report.Checks.Add(new PreflightCheck(G, agent, PreflightStatus.Fail,
                    "Registered but offline - the controller has no live connection.",
                    $"Check the TestAgent service and port 5200 on '{agent}'."));
                continue;
            }

            if (!string.IsNullOrEmpty(facts.MaintenanceState) &&
                !facts.MaintenanceState.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                report.Checks.Add(new PreflightCheck(G, agent, PreflightStatus.Fail,
                    $"Held out for maintenance: {facts.MaintenanceState}.",
                    $"Wait for the operation to finish, or clear quarantine on '{agent}' in Fleet Maintenance."));
                continue;
            }

            if (facts.LockedBy is { Length: > 0 } holder)
            {
                report.Checks.Add(new PreflightCheck(G, agent, PreflightStatus.Fail,
                    $"Locked by {holder}.",
                    "Wait for that run to finish, or release the lock if it is stale."));
                continue;
            }

            if (facts.IsBusy)
            {
                report.Checks.Add(new PreflightCheck(G, agent, PreflightStatus.Fail,
                    "Busy running another command.",
                    "Wait for the current execution to finish."));
                continue;
            }

            if (facts.RebootRequired)
            {
                report.Checks.Add(new PreflightCheck(G, agent, PreflightStatus.Warn,
                    "A reboot is pending. Installs may behave unpredictably until it happens.",
                    $"Reboot '{agent}' from Fleet Maintenance before a long install."));
                continue;
            }

            report.Checks.Add(new PreflightCheck(G, agent, PreflightStatus.Pass, "Online and idle."));
        }
    }

    // ── 5. Structure ────────────────────────────────────────────────

    private static void CheckStructure(PreflightRequest request, PreflightReport report)
    {
        const string G = PreflightGroups.Structure;

        foreach (var issue in WatchListValidator.Analyze(request.Config))
        {
            // Analyze covers the whole WatchList; only the pipeline being run can block it.
            if (request.Pipeline is { } p && !MentionsPipeline(issue.NodePath, p.Tag)) continue;

            var status = issue.Severity switch
            {
                WatchIssueSeverity.Error => PreflightStatus.Fail,
                WatchIssueSeverity.Warning => PreflightStatus.Warn,
                _ => PreflightStatus.Pass,
            };

            report.Checks.Add(new PreflightCheck(G, issue.NodePath, status, issue.Message, issue.FixHint));
        }

        if (!report.Checks.Any(c => c.Group == G))
        {
            report.Checks.Add(new PreflightCheck(G, "WatchList structure", PreflightStatus.Pass,
                "Initialize ordering, Ref targets and node shape all valid."));
        }
    }

    private static bool MentionsPipeline(string nodePath, string tag) =>
        string.IsNullOrEmpty(nodePath) || nodePath.Contains(tag, StringComparison.OrdinalIgnoreCase);

    // ── 6. Disk ─────────────────────────────────────────────────────

    private void CheckDisk(PreflightRequest request, PipelineExecutionContext ctx, PreflightReport report)
    {
        const string G = PreflightGroups.Disk;
        var minimum = request.MinimumDiskGb;

        var controllerFree = _fs.FreeSpaceGb(AppContext.BaseDirectory);
        if (controllerFree is { } free)
        {
            report.Checks.Add(free < minimum
                ? new PreflightCheck(G, "Controller", PreflightStatus.Warn,
                    $"{free:F1} GB free, below the {minimum:F0} GB minimum.",
                    "Free space on the controller before starting a long run.")
                : new PreflightCheck(G, "Controller", PreflightStatus.Pass, $"{free:F1} GB free."));
        }

        foreach (var agent in AgentsInScope(request, ctx))
        {
            var facts = _agentFacts(agent);
            if (facts.DiskFreeGb is not { } agentFree) continue;

            report.Checks.Add(agentFree < minimum
                ? new PreflightCheck(G, agent, PreflightStatus.Warn,
                    $"{agentFree:F1} GB free, below the {minimum:F0} GB minimum.",
                    $"Free space on '{agent}', or revert it to its baseline snapshot.")
                : new PreflightCheck(G, agent, PreflightStatus.Pass, $"{agentFree:F1} GB free."));
        }
    }

    // ── Scope helpers ───────────────────────────────────────────────

    /// <summary>
    /// Every action this run will reach, with Refs expanded. A node-level run checks only its own
    /// subtree; a pipeline run checks all of it.
    /// </summary>
    private IEnumerable<ActionConfig> ActionsInScope(PreflightRequest request)
    {
        var roots = request.Nodes.Count > 0
            ? request.Nodes
            : request.Pipeline?.Events.SelectMany(e => e.Children).ToList() ?? [];

        var actions = new List<ActionConfig>();
        Walk(roots, actions, request.Config, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return actions;

        static void Walk(IEnumerable<IActionNode> nodes, List<ActionConfig> into,
            WatchListConfig config, HashSet<string> visitedTemplates)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case ActionConfig action:
                        into.Add(action);
                        break;
                    case ActionGroupConfig group:
                        Walk(group.Children, into, config, visitedTemplates);
                        break;
                    case RefConfig reference:
                        // A template that Refs itself would otherwise recurse forever.
                        if (!visitedTemplates.Add(reference.TemplateID)) break;
                        var template = config.Templates.FirstOrDefault(t =>
                            string.Equals(t.ID, reference.TemplateID, StringComparison.OrdinalIgnoreCase));
                        if (template is not null) Walk(template.Children, into, config, visitedTemplates);
                        visitedTemplates.Remove(reference.TemplateID);
                        break;
                }
            }
        }
    }

    private IReadOnlyList<string> AgentsInScope(PreflightRequest request, PipelineExecutionContext ctx)
    {
        var agents = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in ActionsInScope(request))
        {
            if (action.Type != ActionType.RunRemoteCommand) continue;

            foreach (var name in AgentResolver.ExtractAgentNames(action, ctx.Parameters))
            {
                if (!string.IsNullOrWhiteSpace(name) && !name.Contains('['))
                    agents.Add(name);
            }
        }

        return [.. agents];
    }

    private void Probe(PreflightReport report, string group, string name, string path, bool isDirectory, string fixHint)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (path.Contains('['))
        {
            report.Checks.Add(new PreflightCheck(group, name, PreflightStatus.Fail,
                $"Still contains an unresolved token: '{path}'.",
                "Define the missing token before this path can be checked."));
            return;
        }

        var access = isDirectory ? _fs.CheckDirectory(path) : _fs.CheckFile(path);
        report.Checks.Add(access switch
        {
            PathAccess.Exists => new PreflightCheck(group, name, PreflightStatus.Pass, path),

            // Never a Fail: this identity cannot look, which says nothing about whether the host
            // that actually runs the pipeline can. Blocking here would refuse a good run.
            PathAccess.Denied => new PreflightCheck(group, name, PreflightStatus.Warn,
                $"'{path}' could not be checked - {_fs.Identity} is denied access.",
                $"Could not verify from here. Confirm '{path}' is reachable by the account that runs the pipeline."),

            _ => new PreflightCheck(group, name, PreflightStatus.Fail, $"'{path}' does not exist.", fixHint),
        });
    }
}

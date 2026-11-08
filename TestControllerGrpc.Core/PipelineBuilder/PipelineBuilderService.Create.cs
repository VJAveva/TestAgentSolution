using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;
using System.Text.Json;

namespace TestControllerGrpc.Core.PipelineBuilder;

/// <summary>
/// The authoring gate, split from the run-time gate.
/// </summary>
/// <remarks>
/// <see cref="Blocking"/> holds what AUTHORING controls - structure, identity, and tokens the
/// generated config should define. <see cref="Informational"/> holds everything that can only be
/// judged once a build is selected. A newly authored pipeline deliberately has no build (it comes
/// from GlobalVariables or the trigger), so gating Create on those would leave the button disabled
/// forever.
/// </remarks>
public sealed record BuilderValidation(
    IReadOnlyList<BuilderIssue> Blocking,
    IReadOnlyList<BuilderIssue> Informational)
{
    public bool CanCreate => Blocking.Count == 0;
    public IReadOnlyList<BuilderTokenReview> Tokens { get; init; } = [];
}

public sealed record CreateOutcome(
    bool Written,
    string ConfigPath,
    string FragmentPath,
    string TriggerFolder,
    IReadOnlyList<BuilderIssue> Issues);

public sealed partial class PipelineBuilderService
{
    /// <summary>Checks a preview against the live WatchList. Pure - writes nothing.</summary>
    public BuilderValidation Validate(BuilderResult preview, WatchListConfig live, string? fragmentDir = null)
    {
        var blocking = new List<BuilderIssue>();
        var info = new List<BuilderIssue>();

        foreach (var issue in preview.Issues)
            (issue.Severity == BuilderSeverity.Error ? blocking : info).Add(issue);

        if (preview.WatchItem is null)
        {
            blocking.Add(new(BuilderSeverity.Error, "Choose a target, recipe and agents before creating a pipeline."));
            return new BuilderValidation(blocking, info);
        }

        if (!PipelineTag.IsValid(preview.Tag))
            blocking.Add(new(BuilderSeverity.Error,
                $"Tag '{preview.Tag}' is not a safe identity key: max {PipelineTag.MaxLength} characters from [A-Za-z0-9._-]."));

        if (preview.WatchItem.Tag != preview.Tag
            || preview.WatchItem.Path != preview.TriggerFolder
            || preview.WatchItem.Filter != preview.TriggerFilter)
            blocking.Add(new(BuilderSeverity.Error, "The preview identity or trigger changed. Review the pipeline again."));

        var plan = WatchListFragmentImport.Plan(live, [preview.WatchItem], []);
        foreach (var conflict in plan.Conflicts)
            blocking.Add(new BuilderIssue(BuilderSeverity.Error, conflict.Message));

        // The generated config is a NEW file; silently overwriting one another pipeline may already
        // point at would change that pipeline's paths.
        if (File.Exists(preview.ConfigPath))
        {
            blocking.Add(new BuilderIssue(BuilderSeverity.Error,
                $"'{preview.ConfigPath}' already exists. Nothing will be overwritten; choose a different recipe or edit the existing pipeline."));
        }
        if (fragmentDir is not null && File.Exists(Path.Combine(fragmentDir, $"{preview.Tag}.watchitem.xml")))
            blocking.Add(new(BuilderSeverity.Error,
                $"'{Path.Combine(fragmentDir, $"{preview.Tag}.watchitem.xml")}' already exists. Import the existing fragment or choose a different recipe."));

        var candidate = new WatchListConfig { WatchItems = [preview.WatchItem] };
        foreach (var issue in WatchListValidator.AnalyzeStructure(candidate))
        {
            var builderIssue = new BuilderIssue(
                issue.Severity == WatchIssueSeverity.Error ? BuilderSeverity.Error : BuilderSeverity.Warning,
                $"{issue.NodePath}: {issue.Message}");

            (issue.Severity == WatchIssueSeverity.Error ? blocking : info).Add(builderIssue);
        }

        var tokens = ReviewTokens(preview, blocking, info);
        return new BuilderValidation(blocking, info) { Tokens = tokens };
    }

    /// <summary>
    /// Splits unresolved tokens into "authoring forgot to define it" and "a build supplies it".
    /// </summary>
    private static IReadOnlyList<BuilderTokenReview> ReviewTokens(
        BuilderResult preview, List<BuilderIssue> blocking, List<BuilderIssue> info)
    {
        var ctx = new PipelineExecutionContext { WatchItemTag = preview.Tag };
        var initializers = ParameterResolver.CollectInitializeNodes(preview.WatchItem!);
        if (initializers.Count == 0)
        {
            blocking.Add(new(BuilderSeverity.Error, "The generated pipeline needs an Initialize node for its parameter file."));
            return [];
        }
        var sourceFailed = false;
        foreach (var init in initializers)
        {
            var failure = string.Equals(init.ParameterFile, preview.ConfigPath, StringComparison.OrdinalIgnoreCase)
                ? ParameterResolver.TryApplyJsonConfig(ctx, preview.Config, init.Profile, preview.Tag, preview.ConfigPath)
                : ParameterResolver.TryLoadInitializeSource(ctx, init.ParameterFile, init.Profile, preview.Tag);
            if (failure is not null)
            {
                blocking.Add(new(BuilderSeverity.Error, $"Initialize '{init.Tag}': {failure}"));
                sourceFailed = true;
            }
        }
        if (sourceFailed) return [];

        var references = Actions(preview.WatchItem!)
            .SelectMany(a => ParameterResolver.ReferencedTokens(a.Action, ctx)
                .Select(t => (t.Name, Consumer: $"{a.Path} / {t.Field}")));
        var rows = new List<BuilderTokenReview>();
        foreach (var group in references.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var name = group.Key;
            var consumers = string.Join("; ", group.Select(r => r.Consumer).Distinct(StringComparer.Ordinal));
            if (ctx.Parameters.TryGetValue(name, out var value))
            {
                var layer = ctx.ParameterRanks.TryGetValue(name, out var rank)
                    ? TokenDisplay.LayerFromRank((ParameterRank)rank).ToString()
                    : "Configuration";
                rows.Add(new(name, TokenDisplay.MaskIfSecret(name, string.IsNullOrEmpty(value) ? "(empty)" : value),
                    layer, BuilderTokenStatus.Defined, consumers));
            }
            else if (DerivedTargetCatalog.SuppliedAtRunTime.Contains(name))
            {
                rows.Add(new(name, "Supplied at run time", "GlobalVariables / trigger", BuilderTokenStatus.Runtime, consumers));
                info.Add(new(BuilderSeverity.Info,
                    $"[{name}] is supplied at run time by GlobalVariables.json or the trigger file. Used by: {consumers}."));
            }
            else
            {
                rows.Add(new(name, "Not defined", "Missing", BuilderTokenStatus.Missing, consumers));
                blocking.Add(new(BuilderSeverity.Error,
                    $"[{name}] is not defined. Used by: {consumers}. Add it to the target config or the selected profile, then review again."));
            }
        }
        return rows;
    }

    private static IEnumerable<(ActionConfig Action, string Path)> Actions(WatchItemConfig item)
    {
        foreach (var ev in item.Events)
            foreach (var action in Walk(ev.Children, $"Event[{ev.Type}]"))
                yield return action;

        static IEnumerable<(ActionConfig Action, string Path)> Walk(List<IActionNode> nodes, string path)
        {
            foreach (var node in nodes)
            {
                if (node is ActionConfig action)
                    yield return (action, $"{path} / Action[{(string.IsNullOrWhiteSpace(action.Tag) ? action.Type.ToString() : action.Tag)}]");
                if (node is ActionGroupConfig group)
                    foreach (var child in Walk(group.Children, $"{path} / Group[{group.Tag}]"))
                        yield return child;
            }
        }
    }

    /// <summary>
    /// Writes the generated config beside its release config and the WatchList fragment to
    /// <paramref name="fragmentDir"/>. The live WatchList is never touched - importing the fragment
    /// is a separate, explicit step.
    /// </summary>
    /// <remarks>
    /// Authorizes <paramref name="caller"/> for <see cref="Permission.Pipeline_Author"/> HERE rather
    /// than trusting the caller to have checked: the view model's gate is a UX hint, and the web path
    /// will reach this method from a different process entirely.
    /// </remarks>
    public async Task<CreateOutcome> CreateAsync(
        BuilderResult preview,
        BuilderValidation validation,
        string fragmentDir,
        IUserContext caller,
        CancellationToken ct = default,
        WatchListConfig? live = null)
    {
        var decision = await authorization.CanAsync(caller, Permission.Pipeline_Author, resourceId: null, ct);
        if (!decision.Allowed)
        {
            return new CreateOutcome(false, "", "", preview.TriggerFolder,
            [
                new BuilderIssue(BuilderSeverity.Error,
                    decision.HumanReadable ?? "You do not have permission to author pipelines."),
            ]);
        }

        if (!validation.CanCreate || preview.WatchItem is null)
        {
            return new CreateOutcome(false, "", "", preview.TriggerFolder,
                [new BuilderIssue(BuilderSeverity.Error, "Create refused: validation is not green.")]);
        }

        var current = Validate(preview, live ?? new WatchListConfig(), fragmentDir);
        if (!current.CanCreate)
            return new CreateOutcome(false, "", "", preview.TriggerFolder, current.Blocking);

        ct.ThrowIfCancellationRequested();
        var issues = new List<BuilderIssue>();
        var fragmentPath = Path.Combine(fragmentDir, $"{preview.Tag}.watchitem.xml");
        var configTemp = preview.ConfigPath + $".{Guid.NewGuid():N}.tmp";
        var fragmentTemp = fragmentPath + $".{Guid.NewGuid():N}.tmp";
        var configPublished = false;
        var fragmentPublished = false;
        var written = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(preview.ConfigPath))!);
            Directory.CreateDirectory(fragmentDir);
            Directory.CreateDirectory(preview.TriggerFolder);

            // Persist precisely the in-memory config we validated, not a potentially stale JSON preview.
            File.WriteAllText(configTemp, JsonSerializer.Serialize(preview.Config, WriteOptions));
            File.WriteAllText(fragmentTemp, WatchListXmlParser.SerializeWatchItemsToXml([preview.WatchItem], []));
            File.Move(configTemp, preview.ConfigPath, overwrite: false);
            configPublished = true;
            File.Move(fragmentTemp, fragmentPath, overwrite: false);
            fragmentPublished = true;
            written = true;
            audit.Enqueue(new AuditEntry
            {
                UserId = caller.UserId,
                GuestId = caller.GuestId,
                RoleAtTime = Enum.TryParse<Role>(caller.Roles.FirstOrDefault(), ignoreCase: true, out var role) ? role : null,
                ActionName = nameof(Permission.Pipeline_Author),
                ResourceId = preview.Tag,
                Allowed = true,
                ReasonCode = "pipeline-fragment-created",
                ClientKind = caller.ClientKind,
            });
            issues.Add(new(BuilderSeverity.Info,
                $"Import '{fragmentPath}' into the WatchList to activate '{preview.Tag}'. No pipeline was activated or run."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            issues.Add(new(BuilderSeverity.Error, $"Could not create pipeline files: {ex.Message}"));
        }
        finally
        {
            if (!written)
            {
                if (fragmentPublished) CleanupFile(fragmentPath, issues);
                if (configPublished) CleanupFile(preview.ConfigPath, issues);
            }
            CleanupFile(configTemp, issues);
            CleanupFile(fragmentTemp, issues);
        }
        return new CreateOutcome(written, written ? preview.ConfigPath : "", written ? fragmentPath : "",
            preview.TriggerFolder, issues);
    }

    private static void CleanupFile(string path, List<BuilderIssue> issues)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new(BuilderSeverity.Warning, $"Could not remove '{path}': {ex.Message}. Check this file before retrying."));
        }
    }
}

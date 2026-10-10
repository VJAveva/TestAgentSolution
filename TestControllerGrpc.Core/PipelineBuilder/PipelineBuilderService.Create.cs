using TestControllerGrpc.Authorization;
using TestControllerGrpc.Identity;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

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
    public BuilderValidation Validate(BuilderResult preview, WatchListConfig live)
    {
        var blocking = new List<BuilderIssue>();
        var info = new List<BuilderIssue>();

        foreach (var issue in preview.Issues)
            (issue.Severity == BuilderSeverity.Error ? blocking : info).Add(issue);

        if (preview.WatchItem is null)
            return new BuilderValidation(blocking, info);

        var plan = WatchListFragmentImport.Plan(live, [preview.WatchItem], []);
        foreach (var conflict in plan.Conflicts)
            blocking.Add(new BuilderIssue(BuilderSeverity.Error, conflict.Message));

        // The generated config is a NEW file; silently overwriting one another pipeline may already
        // point at would change that pipeline's paths.
        if (File.Exists(preview.ConfigPath))
        {
            blocking.Add(new BuilderIssue(BuilderSeverity.Error,
                $"'{preview.ConfigFileName}' already exists. Rename the recipe or remove the old config."));
        }

        var candidate = new WatchListConfig { WatchItems = [preview.WatchItem] };
        foreach (var issue in WatchListValidator.Analyze(candidate))
        {
            var builderIssue = new BuilderIssue(
                issue.Severity == WatchIssueSeverity.Error ? BuilderSeverity.Error : BuilderSeverity.Warning,
                issue.Message);

            (issue.Severity == WatchIssueSeverity.Error ? blocking : info).Add(builderIssue);
        }

        AddTokenIssues(preview, blocking, info);

        return new BuilderValidation(blocking, info);
    }

    /// <summary>
    /// Splits unresolved tokens into "authoring forgot to define it" and "a build supplies it".
    /// </summary>
    private static void AddTokenIssues(
        BuilderResult preview, List<BuilderIssue> blocking, List<BuilderIssue> info)
    {
        var ctx = new PipelineExecutionContext { WatchItemTag = preview.Tag };

        foreach (var entry in preview.Config.Global)
            ParameterResolver.SetParameter(ctx, entry.Key, entry.Value, ParameterRank.Global);

        foreach (var profile in preview.Config.Profiles.Values)
            foreach (var entry in profile)
                ParameterResolver.SetParameter(ctx, entry.Key, entry.Value, ParameterRank.ParameterFile);

        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in Actions(preview.WatchItem!))
            foreach (var token in ParameterResolver.FindUnresolvedTokens(action, ctx))
                missing.Add(token);

        foreach (var token in missing.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            // The catalog drops exactly these keys from a target, so the two sets cannot drift.
            var suppliedLater = DerivedTargetCatalog.SuppliedAtRunTime.Contains("_" + token.TrimStart('_'));

            if (suppliedLater)
            {
                info.Add(new BuilderIssue(BuilderSeverity.Info,
                    $"[{token}] is supplied at run time by GlobalVariables.json or the trigger file."));
            }
            else
            {
                blocking.Add(new BuilderIssue(BuilderSeverity.Error,
                    $"[{token}] is used by this pipeline but no layer defines it."));
            }
        }
    }

    private static IEnumerable<ActionConfig> Actions(WatchItemConfig item)
    {
        foreach (var ev in item.Events)
            foreach (var node in Walk(ev.Children))
                if (node is ActionConfig action)
                    yield return action;

        static IEnumerable<IActionNode> Walk(List<IActionNode> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;
                if (node is ActionGroupConfig group)
                    foreach (var child in Walk(group.Children))
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
        CancellationToken ct = default)
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

        // Re-checked at the write boundary: BuilderResult is a record a caller can hand-build, and an
        // unsafe tag would reach PipelineAssignments and every tag-in-path REST route.
        if (!PipelineTag.IsValid(preview.Tag))
        {
            return new CreateOutcome(false, "", "", preview.TriggerFolder,
            [
                new BuilderIssue(BuilderSeverity.Error,
                    $"Tag '{preview.Tag}' is not a safe identity key: max {PipelineTag.MaxLength} "
                    + "characters from [A-Za-z0-9._-]."),
            ]);
        }

        var issues = new List<BuilderIssue>();

        Directory.CreateDirectory(Path.GetDirectoryName(preview.ConfigPath)!);
        WriteAtomic(preview.ConfigPath, preview.ConfigJson);

        Directory.CreateDirectory(fragmentDir);
        var fragmentPath = Path.Combine(fragmentDir, $"{preview.Tag}.watchitem.xml");
        WriteAtomic(fragmentPath, WatchListXmlParser.SerializeWatchItemsToXml([preview.WatchItem], []));

        // A WatchItem whose Path does not exist fails the run before any action dispatches, so the
        // folder is part of creating the pipeline, not a later manual step.
        Directory.CreateDirectory(preview.TriggerFolder);

        issues.Add(new BuilderIssue(BuilderSeverity.Info,
            $"Import '{Path.GetFileName(fragmentPath)}' into the WatchList to activate '{preview.Tag}'."));

        return new CreateOutcome(true, preview.ConfigPath, fragmentPath, preview.TriggerFolder, issues);
    }

    private static void WriteAtomic(string path, string content)
    {
        if (File.Exists(path))
            File.Copy(path, $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);

        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}

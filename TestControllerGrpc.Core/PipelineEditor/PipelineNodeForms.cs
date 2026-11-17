using TestControllerGrpc.Models;

namespace TestControllerGrpc.PipelineEditor;

public enum PipelineFieldKind { Text, MultiLine, Choice, Bool, Number, File, Folder, ReadOnly }

/// <summary>One field of a Properties form: which XML attribute it edits and how.</summary>
/// <param name="Default">Value the parser assumes when the attribute is absent (shown, not written).</param>
/// <param name="ReadOnlyReason">Why the field cannot be edited here; null when it can.</param>
public sealed record PipelineFieldSpec(
    string Attribute,
    string Label,
    PipelineFieldKind Kind,
    string? Default = null,
    string? Hint = null,
    string? ReadOnlyReason = null);

/// <summary>The Properties form of each node kind (PipelineEditor_Requirement.md section 3); attribute names are the parser's.</summary>
public static class PipelineNodeForms
{
    public const string NameIsIdReason = "The name is the pipeline's ID. Rename it in the XML; assigned engineers are unassigned.";
    public const string SecretEnvReason = "Names only - values are never shown. Edit in the XML.";
    public const string PasswordReason = "Never shown. Move the credential to SecretEnv (quick fix in Problems).";

    private static readonly PipelineFieldSpec[] Skip =
    [
        new("Skip", "Skip", PipelineFieldKind.Bool, Default: "false"),
        new("SkipReason", "Skip reason", PipelineFieldKind.Text),
    ];

    public static IReadOnlyList<PipelineFieldSpec> For(string kind, Func<string, string?> attribute) => kind switch
    {
        NodeKinds.WatchItem =>
        [
            new("Tag", "Name", PipelineFieldKind.ReadOnly, ReadOnlyReason: NameIsIdReason),
            new("Path", "Trigger folder", PipelineFieldKind.Folder),
            new("Filter", "Trigger file", PipelineFieldKind.Text, Default: "*.*"),
            new("BuildNumberField", "Build-number field", PipelineFieldKind.Text, Default: "BuildNumber"),
            new("DropLocationField", "Drop-location field", PipelineFieldKind.Text, Default: "DropLocation"),
            new("BuildBasePath", "Build base path", PipelineFieldKind.Folder),
            new("IsEnabled", "Enabled (included in Trigger All)", PipelineFieldKind.Bool, Default: "true"),
        ],
        NodeKinds.Event =>
        [
            new("Type", "Type", PipelineFieldKind.Choice, Default: "Renamed"),
            new("ExecutionType", "Execution", PipelineFieldKind.Choice, Default: nameof(ExecutionMode.Sequential)),
            .. Skip,
        ],
        NodeKinds.Initialize =>
        [
            new("Tag", "Name", PipelineFieldKind.Text),
            new("ParameterFile", "Parameter file", PipelineFieldKind.File, Hint: "A .json or .txt file under Parameters\\"),
            new("Profile", "Profile", PipelineFieldKind.Choice, Hint: "Profiles come from the parameter file"),
        ],
        NodeKinds.ActionGroup =>
        [
            new("Tag", "Name", PipelineFieldKind.Text),
            new("ExecutionType", "Execution", PipelineFieldKind.Choice, Default: nameof(ExecutionMode.Sequential)),
            new("FailAndContinue", "Continue on failure", PipelineFieldKind.Bool, Default: "false"),
            .. Skip,
        ],
        NodeKinds.Action when attribute("Type") == nameof(ActionType.SendMail) =>
        [
            new("Tag", "Name", PipelineFieldKind.Text),
            new("Type", "Type", PipelineFieldKind.Choice, Default: nameof(ActionType.RunCommand)),
            new("To", "To", PipelineFieldKind.Text, Hint: "Separate addresses with ;"),
            new("Title", "Subject", PipelineFieldKind.Text),
            new("Body", "Body", PipelineFieldKind.MultiLine),
            new("From", "From", PipelineFieldKind.Text),
            new("FailAndContinue", "Continue on failure", PipelineFieldKind.Bool, Default: "false"),
            .. Skip,
        ],
        NodeKinds.Action =>
        [
            new("Tag", "Name", PipelineFieldKind.Text),
            new("Type", "Type", PipelineFieldKind.Choice, Default: nameof(ActionType.RunCommand)),
            new("AgentName", "Agent", PipelineFieldKind.Choice),
            new("Command", "Command", PipelineFieldKind.Text),
            new("Parameters", "Parameters", PipelineFieldKind.MultiLine),
            new("Timeout", "Timeout (seconds, 0 = none)", PipelineFieldKind.Number, Default: "0"),
            new("PollInterval", "Poll interval (ms)", PipelineFieldKind.Number, Default: "1000"),
            new("FailAndContinue", "Continue on failure", PipelineFieldKind.Bool, Default: "false"),
            new("IsReboot", "Restart step", PipelineFieldKind.Bool, Default: "false"),
            new("SecretEnv", "Secrets", PipelineFieldKind.ReadOnly, ReadOnlyReason: SecretEnvReason),
            .. attribute("Password") is null ? Array.Empty<PipelineFieldSpec>()
                : [new("Password", "Password", PipelineFieldKind.ReadOnly, ReadOnlyReason: PasswordReason)],
            .. Skip,
        ],
        NodeKinds.Ref =>
        [
            new("TemplateID", "Template", PipelineFieldKind.Choice),
        ],
        NodeKinds.Template =>
        [
            new("ID", "Template ID", PipelineFieldKind.ReadOnly, ReadOnlyReason: "Rename templates from the Library."),
        ],
        _ => [],
    };

    /// <summary>How a field's stored value is shown: SecretEnv lists names only, Password is masked.</summary>
    public static string DisplayValue(PipelineFieldSpec field, string? stored) => field.Attribute switch
    {
        "SecretEnv" => string.Join(", ", (stored ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split('=', 2)[0].Trim())),
        "Password" => string.IsNullOrEmpty(stored) ? "" : Services.TokenDisplay.Mask,
        _ => stored ?? field.Default ?? "",
    };
}

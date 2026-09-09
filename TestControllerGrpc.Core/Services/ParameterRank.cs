namespace TestControllerGrpc.Services;

/// <summary>
/// Precedence of the source that supplied a parameter value. Resolution order used to be an
/// accident of execution order - Initialize nodes run last, so a parameter file always beat the
/// trigger file. Rank makes precedence explicit and independent of when a source is read.
/// Values are spaced so a new source can be slotted in without renumbering the rest.
/// </summary>
public enum ParameterRank
{
    /// <summary>Global variables shared by every pipeline.</summary>
    Global = 10,

    /// <summary>An Initialize node's parameter file, or a named profile inside the JSON config.</summary>
    ParameterFile = 20,

    /// <summary>A build pinned to one pipeline and persisted in the JSON config.</summary>
    PipelinePin = 30,

    /// <summary>The trigger file that fired this run.</summary>
    TriggerFile = 40,

    /// <summary>Chosen in the UI for this run only. Never persisted.</summary>
    RunOverride = 50,
}

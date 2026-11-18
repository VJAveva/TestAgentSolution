namespace TestControllerGrpc.Services;

/// <summary>
/// Precedence of the source that supplied a parameter value. Resolution order used to be an
/// accident of execution order - Initialize nodes run last, so a parameter file always beat the
/// trigger file. Rank makes precedence explicit and independent of when a source is read.
/// Values are spaced so a new source can be slotted in without renumbering the rest.
/// Order: Global 10 -> Profile 20 -> GlobalVars 30 -> Pipeline entry 35 -> Trigger 40 -> Run 50.
/// </summary>
public enum ParameterRank
{
    /// <summary>Global variables shared by every pipeline.</summary>
    Global = 10,

    /// <summary>An Initialize node's parameter file, or a named profile inside the JSON config.</summary>
    ParameterFile = 20,

    /// <summary>
    /// The shared <c>GlobalVariables.json</c>: one build selected once and used by every pipeline of
    /// the matching release, unless that pipeline has its own entry.
    /// </summary>
    GlobalVars = 30,

    /// <summary>
    /// The pipeline's own entry in the "pipelines" section of the JSON config. Outranks GlobalVars so a
    /// pipeline that deliberately pins a value keeps it when the shared build moves on.
    /// </summary>
    PipelinePin = 35,

    /// <summary>The trigger file that fired this run.</summary>
    TriggerFile = 40,

    /// <summary>Chosen in the UI for this run only. Never persisted.</summary>
    RunOverride = 50,
}

namespace TestControllerGrpc.Core.PipelineBuilder;

public enum BuilderTokenStatus { Defined, Runtime, Missing }

/// <summary>Display-only token evidence. Values must be masked before entering this record.</summary>
public sealed record BuilderTokenReview(
    string Name,
    string DisplayValue,
    string Source,
    BuilderTokenStatus Status,
    string UsedBy);

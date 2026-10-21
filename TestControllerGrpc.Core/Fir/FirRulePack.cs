namespace TestControllerGrpc.Core.Fir;

public enum FirPackKind
{
    Framework,
    Product,
}

/// <summary>
/// What a pack claims to cover. Any populated group matching is enough, EXCEPT
/// <see cref="OutputContains"/>, where every substring must be present - it is a fingerprint, and an
/// "any" rule there would let a pack claim a test that merely mentions one of its words.
/// </summary>
public sealed record FirPackAppliesTo
{
    public IReadOnlyList<string> TestAssembly { get; init; } = [];
    public IReadOnlyList<string> ClassName { get; init; } = [];
    public IReadOnlyList<string> OutputContains { get; init; } = [];

    public bool IsEmpty => TestAssembly.Count == 0 && ClassName.Count == 0 && OutputContains.Count == 0;
}

/// <summary>
/// One rules file. A team adds support for its framework or product by dropping a *.fir.json next to
/// the others - no code change. Everything here is data; nothing names a product in the engine.
/// </summary>
public sealed record FirRulePack
{
    public string Id { get; init; } = "";
    public FirPackKind Kind { get; init; } = FirPackKind.Framework;
    public int Version { get; init; } = 1;
    public FirPackAppliesTo AppliesTo { get; init; } = new();

    // Framework-layer rules: how this test framework writes its trace.
    public string? RowMarker { get; init; }
    public string? TestDataLine { get; init; }
    public string? StepLine { get; init; }
    public IReadOnlyList<string> FailLevels { get; init; } = [];
    public string? ReasonLine { get; init; }
    public IReadOnlyList<string> SecretLines { get; init; } = [];
    public int SlowStepSeconds { get; init; } = 180;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TestDataKeywordMap { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Where the component, keyword and parameters sit in this framework's data sheet.</summary>
    public FirTestDataLayout? TestData { get; init; }

    // Product-layer rules: messages that mean failure even when a step says Passed.
    public IReadOnlyList<string> ProductError { get; init; } = [];
    public string? ProductCount { get; init; }
    public IReadOnlyList<string> Noise { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Categories { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Collapses generated object names to a readable prefix; must capture a "short" group.</summary>
    public string? ObjectShortName { get; init; }

    public string Label => $"{Id}@{Version}";
}

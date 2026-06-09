namespace TestControllerGrpc.Tests;

/// <summary>
/// Test category constants for organizing tests by purpose.
/// Usage: [Trait("Category", TestCategory.Safety)]
/// </summary>
public static class TestCategory
{
    public const string Safety = "Safety";
    public const string Security = "Security";
    public const string Contract = "Contract";
    public const string Integration = "Integration";
    public const string Regression = "Regression";
}

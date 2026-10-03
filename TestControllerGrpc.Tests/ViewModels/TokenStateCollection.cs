namespace TestControllerGrpc.Tests.ViewModels;

/// <summary>
/// Serialises the test classes that mutate TreeNodeViewModel's static token scopes and
/// TemplateRunContext. xUnit runs classes in parallel by default, so without this they clear each
/// other's tokens mid-test and fail a different one on each run.
/// </summary>
[CollectionDefinition("TokenState", DisableParallelization = true)]
public sealed class TokenStateCollection;

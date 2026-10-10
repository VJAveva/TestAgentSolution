namespace TestControllerGrpc.Tests.Scaling;

/// <summary>
/// Serialises the test classes that reconfigure the process-wide
/// <see cref="TestControllerGrpc.Services.PipelineConcurrency"/> cap. xUnit runs classes in
/// parallel by default, so without this a test that lowers the cap to prove it binds would also
/// throttle every other class dispatching actions at that moment.
/// </summary>
[CollectionDefinition("PipelineConcurrency", DisableParallelization = true)]
public sealed class PipelineConcurrencyCollection;

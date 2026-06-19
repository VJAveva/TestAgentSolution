namespace TestController.ApiTests.Infrastructure;

/// <summary>
/// Shared per-test-class fixture. In InMemory mode it owns a
/// <see cref="ApiTestWebFactory"/> (the WebApi hosted in-process). In Live mode it
/// is a plain <see cref="HttpClient"/> pointed at API_TEST_BASEURL. Tests depend on
/// it via <c>IClassFixture&lt;ApiTestFixture&gt;</c>.
/// </summary>
public sealed class ApiTestFixture : IDisposable
{
    private readonly ApiTestWebFactory? _factory;

    public HttpClient Client { get; }
    public ApiClient Api { get; }

    /// <summary>The agent seam — only present in InMemory mode (null when Live).</summary>
    public FakeAgentDispatcher? Fake { get; }

    public bool IsLive => TestConfig.IsLive;

    public ApiTestFixture()
    {
        if (TestConfig.IsLive)
        {
            Client = new HttpClient
            {
                BaseAddress = new Uri(TestConfig.BaseUrl),
                Timeout = TestConfig.DefaultTimeout,
            };
        }
        else
        {
            _factory = new ApiTestWebFactory();
            Client = _factory.CreateClient();
            Fake = _factory.Fake;
        }

        Api = new ApiClient(Client);
    }

    /// <summary>Resets injected fake behavior. No-op in Live mode.</summary>
    public void ResetFake() => Fake?.Reset();

    public void Dispose()
    {
        Client.Dispose();
        _factory?.Dispose();
    }
}

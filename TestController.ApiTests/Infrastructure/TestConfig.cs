namespace TestController.ApiTests.Infrastructure;

/// <summary>
/// Runtime configuration for the API test framework. Controlled entirely by
/// environment variables so the same test suite runs in two modes:
///
///   API_TEST_MODE=InMemory  (default) - hosts TestController.WebApi in-process
///                                        via WebApplicationFactory, fakes agents.
///   API_TEST_MODE=Live                 - drives a real running WebApi over HTTP.
///
/// Live mode reads API_TEST_BASEURL (default https://localhost:7240).
/// </summary>
public static class TestConfig
{
    public static string Mode =>
        Environment.GetEnvironmentVariable("API_TEST_MODE") ?? "InMemory";

    public static bool IsLive =>
        string.Equals(Mode, "Live", StringComparison.OrdinalIgnoreCase);

    public static bool IsInMemory => !IsLive;

    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("API_TEST_BASEURL") ?? "https://localhost:7240";

    public static TimeSpan DefaultTimeout =>
        TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("API_TEST_TIMEOUT_SECONDS"), out var s) && s > 0
                ? s
                : 30);
}

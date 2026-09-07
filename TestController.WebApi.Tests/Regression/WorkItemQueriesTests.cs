using System.Net;
using Microsoft.Extensions.Options;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Ado.Dto;
using TestControllerGrpc.Services;
using Microsoft.Extensions.Logging;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// P20 batch boundary. ADO rejects more than 200 ids in one work-item request, so the split has to happen
/// exactly at 200 — a bug here only shows up on a large report, which is the worst time to find it.
/// </summary>
public class WorkItemQueriesTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"count":0,"value":[]}""", System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class FakeToken : IAdoTokenProvider
    {
        public Task<string> GetAuthHeaderAsync(CancellationToken ct) => Task.FromResult("Basic test");
        public string Describe() => "test";
    }

    private sealed class NoopLogger : IAppLogger
    {
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(LogLevel level, string category, string message, string? agent = null, string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) { }
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
        public event Action<AppLogEntry>? EntryAdded;
    }

    private static WorkItemQueries Build(out CapturingHandler handler)
    {
        handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://dev.azure.com/org/") };
        var options = Options.Create(new AdoOptions { Organization = "org", Project = "proj" });
        return new WorkItemQueries(new AdoClient(http, new FakeToken(), options, new NoopLogger()));
    }

    [Theory]
    [InlineData(200, 1)]
    [InlineData(201, 2)]
    [InlineData(400, 2)]
    [InlineData(401, 3)]
    public async Task GetWithRelationsAsync_Should_ChunkAt200(int idCount, int expectedRequests)
    {
        WorkItemQueries sut = Build(out CapturingHandler handler);
        int[] ids = Enumerable.Range(1, idCount).ToArray();

        await sut.GetWithRelationsAsync(ids, CancellationToken.None);

        Assert.Equal(expectedRequests, handler.Requests.Count);
    }

    [Fact]
    public async Task GetWithRelationsAsync_Should_RequestRelations()
    {
        WorkItemQueries sut = Build(out CapturingHandler handler);

        await sut.GetWithRelationsAsync([1, 2], CancellationToken.None);

        // Without $expand=relations the parent edge is absent and every item silently looks like an orphan.
        Assert.Contains("expand=relations", Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task GetWithRelationsAsync_Should_NotCallAdo_When_NoIds()
    {
        WorkItemQueries sut = Build(out CapturingHandler handler);

        await sut.GetWithRelationsAsync([], CancellationToken.None);

        Assert.Empty(handler.Requests);
    }
}

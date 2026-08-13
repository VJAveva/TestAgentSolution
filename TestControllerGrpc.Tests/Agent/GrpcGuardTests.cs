using Grpc.Core;
using Microsoft.Extensions.Logging;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests;

/// <summary>
/// Tests for <see cref="GrpcGuard"/> — the error-unmasking wrapper (Work Item C).
/// Verifies that the REAL server-side exception is logged with its correlation id
/// before a sanitized <see cref="RpcException"/> is returned, while benign
/// cancellations and already-structured <see cref="RpcException"/>s pass through.
/// </summary>
public class GrpcGuardTests
{
    private sealed record LoggedEntry(LogLevel Level, string Category, string Message, string? CorrelationId, Exception? Ex);

    private sealed class CapturingAppLogger : IAppLogger
    {
        public List<LoggedEntry> Entries { get; } = new();

        public void Log(LogLevel level, string category, string message, Exception? ex = null)
            => Entries.Add(new LoggedEntry(level, category, message, null, ex));

        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null)
            => Entries.Add(new LoggedEntry(level, category, message, correlationId, ex));

        public void LogStructured(LogLevel level, string category, string message,
            string? agent = null, string? runId = null, string? pipeline = null,
            string? action = null, long elapsedMs = 0, Exception? ex = null)
            => Entries.Add(new LoggedEntry(level, category, message, runId, ex));

        public void Info(string category, string message) => Log(LogLevel.Information, category, message);
        public void Warn(string category, string message) => Log(LogLevel.Warning, category, message);
        public void Error(string category, string message, Exception? ex = null) => Log(LogLevel.Error, category, message, ex);
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => Array.Empty<AppLogEntry>();
        public event Action<AppLogEntry>? EntryAdded { add { } remove { } }
    }

    /// <summary>
    /// Minimal in-memory <see cref="ServerCallContext"/> for unit tests.
    /// Avoids the deprecated Grpc.Core.Testing package; the solution uses Grpc.Core.Api.
    /// </summary>
    private sealed class FakeServerCallContext : ServerCallContext
    {
        private readonly Metadata _requestHeaders;
        private readonly CancellationToken _ct;

        public FakeServerCallContext(Metadata requestHeaders, CancellationToken ct)
        {
            _requestHeaders = requestHeaders;
            _ct = ct;
        }

        protected override string MethodCore => "/Test.Service/Method";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "ipv4:127.0.0.1:0";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
        protected override Metadata RequestHeadersCore => _requestHeaders;
        protected override CancellationToken CancellationTokenCore => _ct;
        protected override Metadata ResponseTrailersCore { get; } = new Metadata();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore { get; } =
            new AuthContext(null, new Dictionary<string, List<AuthProperty>>());
        protected override IDictionary<object, object> UserStateCore { get; } = new Dictionary<object, object>();

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
            => throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
            => Task.CompletedTask;
    }

    private static ServerCallContext MakeContext(string? correlationId = null, CancellationToken ct = default)
    {
        var headers = new Metadata();
        if (correlationId is not null)
            headers.Add("x-correlation-id", correlationId);

        return new FakeServerCallContext(headers, ct);
    }

    [Fact]
    public async Task GrpcGuard_Should_LogAndRethrowInternal_When_BodyThrows()
    {
        var logger = new CapturingAppLogger();
        var ctx = MakeContext(correlationId: "corr-123");

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            GrpcGuard.RunAsync<int>(logger, "Test.Category", ctx,
                () => throw new InvalidOperationException("boom")));

        Assert.Equal(StatusCode.Internal, ex.StatusCode);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("Test.Category", entry.Category);
        Assert.Equal("corr-123", entry.CorrelationId);
        Assert.IsType<InvalidOperationException>(entry.Ex);
        Assert.Contains("InvalidOperationException", entry.Message);
        Assert.Contains("boom", entry.Message);
    }

    [Fact]
    public async Task GrpcGuard_Should_PassThrough_When_BodyThrowsRpcException()
    {
        var logger = new CapturingAppLogger();
        var ctx = MakeContext();
        var original = new RpcException(new Status(StatusCode.NotFound, "missing"));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            GrpcGuard.RunAsync<int>(logger, "Test.Category", ctx, () => throw original));

        Assert.Same(original, ex);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task GrpcGuard_Should_NotLogError_When_OperationCanceled()
    {
        var logger = new CapturingAppLogger();
        var ctx = MakeContext();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            GrpcGuard.RunAsync<int>(logger, "Test.Category", ctx,
                () => throw new OperationCanceledException()));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task GrpcGuard_Should_ReturnBodyResult_When_NoException()
    {
        var logger = new CapturingAppLogger();
        var ctx = MakeContext();

        var result = await GrpcGuard.RunAsync(logger, "Test.Category", ctx, () => Task.FromResult(42));

        Assert.Equal(42, result);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task GrpcGuard_Should_LogAndRethrowInternal_When_VoidBodyThrows()
    {
        var logger = new CapturingAppLogger();
        var ctx = MakeContext(correlationId: "corr-void");

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            GrpcGuard.RunAsync(logger, "Test.Category", ctx,
                () => throw new InvalidDataException("frame error")));

        Assert.Equal(StatusCode.Internal, ex.StatusCode);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal("corr-void", entry.CorrelationId);
        Assert.Contains("InvalidDataException", entry.Message);
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TestController.Api.Security;
using TestControllerGrpc.Ado;
using TestControllerGrpc.Services;
using Xunit;

namespace TestController.WebApi.Tests.Regression;

/// <summary>
/// Guards the delegated ADO credential path: the host credential must stay the default, a web user's token must
/// only be adopted over TLS, and cache keys must never treat two different credentials as interchangeable.
/// </summary>
public sealed class AdoUserTokenTests
{
    private sealed class FakeHostProvider : IAdoTokenProvider
    {
        public Task<string> GetAuthHeaderAsync(CancellationToken ct) => Task.FromResult("Basic host-pat");
        public string Describe() => "PAT (Basic auth)";
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Warnings { get; } = [];
        public void Log(LogLevel level, string category, string message, Exception? ex = null) { }
        public void Log(LogLevel level, string category, string message, string? correlationId, long elapsedMs = 0, Exception? ex = null) { }
        public void LogStructured(LogLevel level, string category, string message, string? agent = null,
            string? runId = null, string? pipeline = null, string? action = null, long elapsedMs = 0, Exception? ex = null) { }
        public void Info(string category, string message) { }
        public void Warn(string category, string message) => Warnings.Add(message);
        public void Error(string category, string message, Exception? ex = null) { }
        public IReadOnlyList<AppLogEntry> GetRecentEntries(int count = 500) => [];
        public event Action<AppLogEntry>? EntryAdded;
    }

    private static AdoUserTokenMiddleware Middleware(RequestDelegate next, IAppLogger logger, bool requireSecure = true) =>
        new(next, Options.Create(new AdoOptions { RequireSecureUserToken = requireSecure }), logger);

    [Fact]
    public async Task GetAuthHeaderAsync_Should_UseHostCredential_When_NoUserTokenPresent()
    {
        var provider = new AmbientAdoTokenProvider(new AsyncLocalAdoUserTokenAccessor(), new FakeHostProvider());

        Assert.Equal("Basic host-pat", await provider.GetAuthHeaderAsync(CancellationToken.None));
        Assert.Equal("PAT (Basic auth)", provider.Describe());
    }

    [Fact]
    public async Task GetAuthHeaderAsync_Should_UseDelegatedToken_When_UserTokenPresent()
    {
        var accessor = new AsyncLocalAdoUserTokenAccessor { Token = "entra-token" };
        var provider = new AmbientAdoTokenProvider(accessor, new FakeHostProvider());

        Assert.Equal("Bearer entra-token", await provider.GetAuthHeaderAsync(CancellationToken.None));
        Assert.Equal("Delegated web user (Entra)", provider.Describe());
    }

    [Fact]
    public void CredentialFingerprint_Should_DistinguishUsers_And_NeverRevealTheToken()
    {
        var accessor = new AsyncLocalAdoUserTokenAccessor();
        Assert.Equal("host", accessor.CredentialFingerprint);

        accessor.Token = "token-for-alice";
        string alice = accessor.CredentialFingerprint;
        accessor.Token = "token-for-bob";
        string bob = accessor.CredentialFingerprint;

        Assert.NotEqual(alice, bob);
        Assert.DoesNotContain("token-for", alice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Should_AdoptToken_When_ConnectionIsHttps()
    {
        var accessor = new AsyncLocalAdoUserTokenAccessor();
        string? seen = null;
        var middleware = Middleware(_ => { seen = accessor.Token; return Task.CompletedTask; }, new RecordingLogger());

        var context = new DefaultHttpContext();
        context.Request.IsHttps = true;
        context.Request.Headers[AdoUserTokenMiddleware.HeaderName] = "entra-token";

        await middleware.InvokeAsync(context, accessor);

        Assert.Equal("entra-token", seen);
    }

    [Fact]
    public async Task InvokeAsync_Should_RefuseTokenAndWarn_When_ConnectionIsPlaintext()
    {
        var accessor = new AsyncLocalAdoUserTokenAccessor();
        var logger = new RecordingLogger();
        string? seen = "sentinel";
        var middleware = Middleware(_ => { seen = accessor.Token; return Task.CompletedTask; }, logger);

        var context = new DefaultHttpContext();
        context.Request.IsHttps = false;
        context.Request.Headers[AdoUserTokenMiddleware.HeaderName] = "entra-token";

        await middleware.InvokeAsync(context, accessor);

        Assert.Null(seen);
        Assert.Contains(logger.Warnings, w => w.Contains("HTTPS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvokeAsync_Should_AdoptTokenOverPlaintext_When_SecureTransportNotRequired()
    {
        var accessor = new AsyncLocalAdoUserTokenAccessor();
        string? seen = null;
        var middleware = Middleware(_ => { seen = accessor.Token; return Task.CompletedTask; }, new RecordingLogger(), requireSecure: false);

        var context = new DefaultHttpContext();
        context.Request.IsHttps = false;
        context.Request.Headers[AdoUserTokenMiddleware.HeaderName] = "entra-token";

        await middleware.InvokeAsync(context, accessor);

        Assert.Equal("entra-token", seen);
    }

    [Fact]
    public async Task InvokeAsync_Should_ClearToken_When_RequestCompletes()
    {
        var accessor = new AsyncLocalAdoUserTokenAccessor();
        var middleware = Middleware(_ => Task.CompletedTask, new RecordingLogger());

        var context = new DefaultHttpContext();
        context.Request.IsHttps = true;
        context.Request.Headers[AdoUserTokenMiddleware.HeaderName] = "entra-token";

        await middleware.InvokeAsync(context, accessor);

        Assert.Null(accessor.Token);
    }
}

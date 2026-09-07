using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TestController.Api;
using TestController.Api.Security;
using TestControllerGrpc.Authorization;

namespace TestController.WebApi.Tests.Rbac;

/// <summary>
/// <see cref="RbacGate"/> on the standalone WebApi, which registers a no-op session store because the WPF
/// controller owns the database. The gate therefore never resolves a bearer token locally, and denying on
/// that basis returned 403 from every permission-gated endpoint this host serves itself (Code Churn, Report
/// Card) for every user, including administrators.
/// </summary>
public sealed class RbacGateProxyHostTests
{
    private static ServiceProvider SecondaryHost(IRemoteCapabilityResolver? resolver)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["RBAC:Enabled"] = "true" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRbacFeature(config, isPrimaryHost: false);
        if (resolver is not null)
            services.AddSingleton(resolver);
        return services.BuildServiceProvider();
    }

    private static HttpContext Request(ServiceProvider sp)
    {
        var http = new DefaultHttpContext { RequestServices = sp };
        http.Request.Headers.Authorization = "Bearer token-that-only-the-primary-host-can-resolve";
        return http;
    }

    [Fact]
    public async Task IsAuthorized_Should_Allow_When_PrimaryHostReportsThePermission()
    {
        using ServiceProvider sp = SecondaryHost(new FakeResolver(["Pipeline_View", "CodeChurn_View"]));

        bool allowed = await RbacGate.IsAuthorizedAsync(
            Request(sp), Permission.CodeChurn_View, resourceId: null, CancellationToken.None);

        Assert.True(allowed);
    }

    [Fact]
    public async Task IsAuthorized_Should_Deny_When_PrimaryHostOmitsThePermission()
    {
        using ServiceProvider sp = SecondaryHost(new FakeResolver(["Pipeline_View"]));

        bool allowed = await RbacGate.IsAuthorizedAsync(
            Request(sp), Permission.CodeChurn_View, resourceId: null, CancellationToken.None);

        Assert.False(allowed);
    }

    [Fact]
    public async Task IsAuthorized_Should_Deny_When_PrimaryHostCannotIdentifyCaller()
    {
        using ServiceProvider sp = SecondaryHost(new FakeResolver(null));

        bool allowed = await RbacGate.IsAuthorizedAsync(
            Request(sp), Permission.CodeChurn_View, resourceId: null, CancellationToken.None);

        Assert.False(allowed);
    }

    [Fact]
    public async Task IsAuthorized_Should_Deny_When_NoRemoteResolverIsRegistered()
    {
        using ServiceProvider sp = SecondaryHost(resolver: null);

        bool allowed = await RbacGate.IsAuthorizedAsync(
            Request(sp), Permission.CodeChurn_View, resourceId: null, CancellationToken.None);

        Assert.False(allowed);
    }

    private sealed class FakeResolver(IEnumerable<string>? capabilities) : IRemoteCapabilityResolver
    {
        public Task<IReadOnlySet<string>?> GetCapabilitiesAsync(string? authorizationHeader, CancellationToken ct)
            => Task.FromResult<IReadOnlySet<string>?>(
                capabilities is null ? null : new HashSet<string>(capabilities, StringComparer.OrdinalIgnoreCase));
    }
}

using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;

namespace TestController.WebApi.Tests;

/// <summary>
/// Controllers live in the shared TestController.Api assembly, which both hosts load, but several of their
/// dependencies are registered only under <c>AddRbacFeature(isPrimaryHost: true)</c>. Nothing catches that
/// mismatch at compile time — it surfaces as a 500 on the first request in production, and has done so more
/// than once (RbacGate, then MuteService/NotificationsController). This asserts that every controller the
/// standalone WebApi actually hosts can be activated from its own container.
/// </summary>
public sealed class SharedControllerActivationTests : IDisposable
{
    private readonly TestWebAppFactory _factory;
    private readonly HttpClient _client;

    public SharedControllerActivationTests()
    {
        _factory = new TestWebAppFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public void EveryHostedController_Should_ResolveAllDependencies_When_RunningOnTheSecondaryHost()
    {
        // PopulateFeature runs the real feature providers, so ProxiedControllerExclusionProvider has already
        // removed the controllers this host deliberately forwards to the WPF controller instead of hosting.
        var partManager = _factory.Services.GetRequiredService<ApplicationPartManager>();
        var feature = new ControllerFeature();
        partManager.PopulateFeature(feature);

        Assert.NotEmpty(feature.Controllers);

        using IServiceScope scope = _factory.Services.CreateScope();
        IServiceProvider sp = scope.ServiceProvider;

        var failures = new List<string>();
        foreach (var controller in feature.Controllers)
        {
            foreach (var ctor in controller.AsType().GetConstructors())
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    if (parameter.HasDefaultValue) continue;
                    if (sp.GetService(parameter.ParameterType) is not null) continue;

                    failures.Add(
                        $"{controller.Name} cannot be activated: '{parameter.ParameterType.FullName}' is not " +
                        "registered on this host. Either register it for isPrimaryHost:false, or exclude the " +
                        "controller in ProxiedControllerExclusionProvider and add a proxy endpoint.");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void ProxiedControllers_Should_BeExcluded_When_RunningOnTheSecondaryHost()
    {
        var partManager = _factory.Services.GetRequiredService<ApplicationPartManager>();
        var feature = new ControllerFeature();
        partManager.PopulateFeature(feature);

        string[] hosted = [.. feature.Controllers.Select(c => c.Name)];

        Assert.DoesNotContain("AuthController", hosted);
        Assert.DoesNotContain("LocksController", hosted);
        Assert.DoesNotContain("NotificationsController", hosted);
    }
}

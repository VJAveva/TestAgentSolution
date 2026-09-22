using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TestController.Api;
using TestControllerGrpc.Core.Maintenance;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Maintenance;

/// <summary>
/// The provider takes options and capabilities that DI cannot construct on its own, so a plain
/// AddSingleton&lt;IVirtualizationProvider, ScriptBackedVirtualizationProvider&gt;() compiles and then throws on
/// first resolve. These pin the factory that replaces it, including the path it derives.
/// </summary>
public class VirtualizationProviderRegistrationTests
{
    private const string RevertScript = @"C:\TestSetup\RevertAgents\Revert-AgentVM.ps1";

    private sealed class FakeRunner : IPowerShellScriptRunner
    {
        public List<ScriptInvocation> Invocations { get; } = [];

        public Task<ScriptResult> RunAsync(
            ScriptInvocation invocation, IProgress<ScriptOutputLine> output, CancellationToken cancellationToken)
        {
            Invocations.Add(invocation);
            output.Report(new ScriptOutputLine(
                DateTimeOffset.UtcNow, ScriptStream.Stdout,
                """{"ok":true,"platform":"vcloud","results":[]}"""));
            return Task.FromResult(new ScriptResult(0, TimeSpan.Zero, false));
        }
    }

    private static ServiceProvider Build(
        Dictionary<string, string?> settings, FakeRunner? runner = null)
    {
        settings.TryAdd("Maintenance:RevertScriptPath", RevertScript);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IAppLogger>().Object);
        services.AddFleetMaintenanceServices(configuration);

        // Registered last so it wins: lets the test see the ScriptInvocation the provider builds.
        if (runner is not null)
            services.AddSingleton<IPowerShellScriptRunner>(runner);

        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddFleetMaintenanceServices_Should_ResolveVCloudProvider_When_PlatformIsNotConfigured()
    {
        var provider = Build([]).GetRequiredService<IVirtualizationProvider>();

        Assert.Equal("vcloud", provider.PlatformId);
        Assert.Equal(1, provider.Capabilities.MaxSnapshotsPerVm);
        Assert.False(provider.Capabilities.CanRetainPreviousBaseline);
    }

    [Fact]
    public void AddFleetMaintenanceServices_Should_SelectHyperVCapabilities_When_PlatformIsHyperV()
    {
        var provider = Build(new Dictionary<string, string?>
        {
            ["Maintenance:Virtualization:PlatformId"] = "hyperv",
        }).GetRequiredService<IVirtualizationProvider>();

        Assert.Equal("hyperv", provider.PlatformId);
        Assert.True(provider.Capabilities.CanRetainPreviousBaseline);
    }

    [Fact]
    public void AddFleetMaintenanceServices_Should_Throw_When_PlatformIdIsUnknown()
    {
        var sp = Build(new Dictionary<string, string?>
        {
            ["Maintenance:Virtualization:PlatformId"] = "xen",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<IVirtualizationProvider>());
        Assert.Contains("xen", ex.Message);
        Assert.Contains("vcloud", ex.Message);
    }

    [Fact]
    public void AddFleetMaintenanceServices_Should_Throw_When_NeitherScriptPathNorRevertPathIsSet()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Maintenance:RevertScriptPath"] = "" }).Build();

        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IAppLogger>().Object);
        services.AddFleetMaintenanceServices(configuration);

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.BuildServiceProvider().GetRequiredService<IVirtualizationProvider>());
        Assert.Contains("RevertScriptPath", ex.Message);
    }

    [Fact]
    public async Task Provider_Should_DeriveScriptPathBesideRevertScript_When_ScriptPathIsNotConfigured()
    {
        var runner = new FakeRunner();
        var provider = Build([], runner).GetRequiredService<IVirtualizationProvider>();

        await provider.ListSnapshotsAsync("JVGR2", CancellationToken.None);

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(@"C:\TestSetup\RevertAgents\Vm-Ops.vcloud.ps1", invocation.ScriptPath);
    }

    [Fact]
    public async Task Provider_Should_UseConfiguredScriptPath_When_ItIsSet()
    {
        var runner = new FakeRunner();
        var provider = Build(new Dictionary<string, string?>
        {
            ["Maintenance:Virtualization:ScriptPath"] = @"D:\ops\Custom-VmOps.ps1",
        }, runner).GetRequiredService<IVirtualizationProvider>();

        await provider.ListSnapshotsAsync("JVGR2", CancellationToken.None);

        var invocation = Assert.Single(runner.Invocations);
        Assert.Equal(@"D:\ops\Custom-VmOps.ps1", invocation.ScriptPath);
    }
}

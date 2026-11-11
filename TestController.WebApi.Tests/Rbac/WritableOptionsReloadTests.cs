using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TestController.Api;
using TestControllerGrpc.Configuration;

namespace TestController.WebApi.Tests.Rbac;

/// <summary>
/// A mode switch writes RBAC:Enabled to appsettings.json. Every reader goes through IOptionsMonitor, so the write
/// must reload configuration itself; the file watcher is late or silent, which left the Settings panel and the
/// server guards on the old mode. The watcher is OFF here, so only the explicit reload can make these pass.
/// </summary>
public class WritableOptionsReloadTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"rbac_{Guid.NewGuid():N}.json");

    public WritableOptionsReloadTests() => File.WriteAllText(_file, """{ "RBAC": { "Enabled": true } }""");

    public void Dispose() => File.Delete(_file);

    /// <summary>The embedded API host's shape: its configuration chains the WPF host's.</summary>
    private (IOptionsMonitor<RbacOptions> Monitor, IConfiguration Outer) ChainedHost()
    {
        var inner = new ConfigurationBuilder().AddJsonFile(_file, optional: false, reloadOnChange: false).Build();
        var outer = new ConfigurationManager();
        outer.AddConfiguration(inner);

        var services = new ServiceCollection();
        services.Configure<RbacOptions>(outer.GetSection(RbacOptions.SectionName));
        var monitor = services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<RbacOptions>>();
        Assert.True(monitor.CurrentValue.Enabled);
        return (monitor, outer);
    }

    [Fact]
    public void Update_Should_ChangeTheMonitoredValueAtOnce_When_ConfigurationIsReloaded()
    {
        var (monitor, outer) = ChainedHost();
        var writable = new WritableOptions<RbacOptions>(monitor, RbacOptions.SectionName, _file,
            () => RbacFeatureExtensions.ReloadConfiguration(outer));

        writable.Update(o => o.Enabled = false);

        Assert.False(monitor.CurrentValue.Enabled);
        Assert.False(writable.Value.Enabled);
    }

    [Fact]
    public void Update_Should_LeaveTheValueStale_When_NothingReloads()
    {
        var (monitor, _) = ChainedHost();
        var writable = new WritableOptions<RbacOptions>(monitor, RbacOptions.SectionName, _file);

        writable.Update(o => o.Enabled = false);

        Assert.Contains("false", File.ReadAllText(_file));
        Assert.True(monitor.CurrentValue.Enabled);
    }
}

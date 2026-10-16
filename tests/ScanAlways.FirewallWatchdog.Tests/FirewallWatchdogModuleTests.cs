using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ScanAlways.Common.Modules;
using Xunit;

namespace ScanAlways.FirewallWatchdog.Tests;

public class FirewallWatchdogModuleTests
{
    [Fact]
    public void Discover_FindsTheWatchdogModule_EnabledByDefault()
    {
        var catalog = ModuleCatalog.Discover(typeof(FirewallWatchdogModule).Assembly);

        var module = Assert.Single(catalog.Modules);
        Assert.Equal("FirewallWatchdog", module.Id);
        Assert.True(module.EnabledByDefault);
    }

    [Fact]
    public void ConfigureServices_RegistersTheWatchdog_WithItsOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FirewallWatchdog:CheckIntervalSeconds"] = "120",
                ["FirewallWatchdog:MonitorInboundAllowRules"] = "true",
                ["FirewallWatchdog:IgnoredInboundRules:0"] = "Steam*",
            })
            .Build();

        var services = new ServiceCollection().AddLogging();
        new FirewallWatchdogModule().ConfigureServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<FirewallWatchdog>());
        var options = provider.GetRequiredService<IOptions<FirewallWatchdogOptions>>().Value;
        Assert.Equal(120, options.CheckIntervalSeconds);
        Assert.True(options.MonitorInboundAllowRules);
        Assert.Equal(new[] { "Steam*" }, options.IgnoredInboundRules);
    }

    [Fact]
    public void Defaults_KeepRuleMonitoringOff()
    {
        Assert.False(new FirewallWatchdogOptions().MonitorInboundAllowRules);
    }
}

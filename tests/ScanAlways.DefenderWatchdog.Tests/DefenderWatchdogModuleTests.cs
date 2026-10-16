using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScanAlways.Common.Modules;
using Xunit;

namespace ScanAlways.DefenderWatchdog.Tests;

public class DefenderWatchdogModuleTests
{
    [Fact]
    public void Discover_FindsTheWatchdogModule_EnabledByDefault()
    {
        var catalog = ModuleCatalog.Discover(typeof(DefenderWatchdogModule).Assembly);

        var module = Assert.Single(catalog.Modules);
        Assert.Equal("DefenderWatchdog", module.Id);
        Assert.True(module.EnabledByDefault);
    }

    [Fact]
    public void ConfigureServices_RegistersTheWatchdog_WithItsOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DefenderWatchdog:CheckIntervalSeconds"] = "120",
            })
            .Build();

        var services = new ServiceCollection().AddLogging();
        new DefenderWatchdogModule().ConfigureServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<DefenderWatchdog>());
        Assert.Equal(120, provider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<DefenderWatchdogOptions>>()
            .Value.CheckIntervalSeconds);
    }
}

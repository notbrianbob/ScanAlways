using Microsoft.Extensions.Configuration;
using WatchLattice.Common.Modules;
using Xunit;

namespace WatchLattice.Common.Tests;

public class ModuleCatalogTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void Discover_FindsModulesWithParameterlessConstructors()
    {
        var catalog = ModuleCatalog.Discover(typeof(ModuleCatalogTests).Assembly);

        Assert.Contains(catalog.Modules, m => m.GetType() == typeof(FakeModule));
        Assert.DoesNotContain(catalog.Modules, m => m is NeedsArgumentsModule);
    }

    [Fact]
    public void Constructor_RejectsDuplicateIds_CaseInsensitively()
    {
        Assert.Throws<ArgumentException>(() =>
            new ModuleCatalog(new[] { new FakeModule("Canary"), new FakeModule("canary") }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1Decoy")]
    [InlineData("Has Space")]
    [InlineData("Has:Colon")]
    public void Constructor_RejectsInvalidIds(string id)
    {
        Assert.Throws<ArgumentException>(() => new ModuleCatalog(new[] { new FakeModule(id) }));
    }

    [Fact]
    public void Modules_AreOrderedById()
    {
        var catalog = new ModuleCatalog(new[] { new FakeModule("Watchdog"), new FakeModule("Canary") });

        Assert.Equal(new[] { "Canary", "Watchdog" }, catalog.Modules.Select(m => m.Id));
    }

    [Fact]
    public void IsEnabled_UsesConfigWhenPresent_AndDefaultOtherwise()
    {
        var onByDefault = new FakeModule("A", enabledByDefault: true);
        var offByDefault = new FakeModule("B", enabledByDefault: false);

        Assert.True(ModuleCatalog.IsEnabled(onByDefault, Config()));
        Assert.False(ModuleCatalog.IsEnabled(offByDefault, Config()));
        Assert.False(ModuleCatalog.IsEnabled(onByDefault, Config(("Modules:A:Enabled", "false"))));
        Assert.True(ModuleCatalog.IsEnabled(offByDefault, Config(("modules:b:enabled", "True"))));
        Assert.True(ModuleCatalog.IsEnabled(onByDefault, Config(("Modules:A:Enabled", "not-a-bool"))));
    }

    [Fact]
    public void ResolveEnabled_ReturnsOnlyEnabledModules()
    {
        var catalog = new ModuleCatalog(new[] { new FakeModule("A"), new FakeModule("B") });

        var enabled = catalog.ResolveEnabled(Config(("Modules:B:Enabled", "false")));

        Assert.Equal(new[] { "A" }, enabled.Select(m => m.Id));
    }

    [Fact]
    public void AddWatchLatticeHub_ConfiguresOnlyEnabledModules()
    {
        var on = new FakeModule("On");
        var off = new FakeModule("Off");
        var catalog = new ModuleCatalog(new[] { on, off });
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        services.AddWatchLatticeHub(Config(("Modules:Off:Enabled", "false")), catalog);

        Assert.Equal(1, on.ConfigureCalls);
        Assert.Equal(0, off.ConfigureCalls);
    }
}

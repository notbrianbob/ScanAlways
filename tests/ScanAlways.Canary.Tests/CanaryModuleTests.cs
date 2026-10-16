using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScanAlways.Common.Modules;
using Xunit;

namespace ScanAlways.Canary.Tests;

public class CanaryModuleTests
{
    [Fact]
    public void Discover_FindsTheCanaryModule_EnabledByDefault()
    {
        var catalog = ModuleCatalog.Discover(typeof(CanaryModule).Assembly);

        var module = Assert.Single(catalog.Modules);
        Assert.Equal("Canary", module.Id);
        Assert.True(module.EnabledByDefault);
    }

    [Fact]
    public async Task RunsTheWatcher_WithSettingsFromTheCanarySection()
    {
        using var temp = new TempFolder();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Canary:Folders:0"] = temp.Watched,
                ["Canary:FileNames:0"] = "!000_Test_Canary.docx",
                ["Canary:ManifestPath"] = Path.Join(temp.Path, "manifest.json"),
                ["Canary:AlertLogPath"] = Path.Join(temp.Path, "alerts.log"),
                ["Canary:CanarySizeBytes"] = "4096",
            })
            .Build();

        var services = new ServiceCollection().AddLogging();
        var module = new CanaryModule();
        module.ConfigureServices(services, configuration);
        using var provider = services.BuildServiceProvider();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await module.RunAsync(provider, cts.Token);

        Assert.True(File.Exists(Path.Join(temp.Watched, "!000_Test_Canary.docx")));
    }
}

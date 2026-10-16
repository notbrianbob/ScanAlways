using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WatchLattice.Common;
using WatchLattice.Common.Modules;
using Xunit;

namespace WatchLattice.Decoy.Tests;

public class DecoyModuleTests
{
    [Fact]
    public void Discover_FindsTheDecoyModule_EnabledByDefault()
    {
        var catalog = ModuleCatalog.Discover(typeof(DecoyModule).Assembly);

        var module = Assert.Single(catalog.Modules);
        Assert.Equal("Decoy", module.Id);
        Assert.True(module.EnabledByDefault);
    }

    [Fact]
    public async Task RunsTheDecoyEngine_WithSettingsFromTheDecoySection()
    {
        var dir = Directory.CreateTempSubdirectory("watchlattice-decoy-module");
        try
        {
            await File.WriteAllTextAsync(Path.Join(dir.FullName, "sample.txt"), "hello");

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Decoy:ScanRoots:0"] = dir.FullName,
                    ["Decoy:Intensity"] = "FullScan",
                    ["Decoy:MaxFilesPerSecond"] = "100",
                    ["Decoy:WriteScanLog"] = "false",
                })
                .Build();

            var services = new ServiceCollection().AddLogging();
            var module = new DecoyModule();
            module.ConfigureServices(services, configuration);
            using var provider = services.BuildServiceProvider();

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            await module.RunAsync(provider, cts.Token);

            Assert.True(provider.GetRequiredService<DecoyEngine>().Statistics.FilesTouched >= 1);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}

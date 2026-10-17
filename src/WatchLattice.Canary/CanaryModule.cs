using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WatchLattice.Common.Modules;

namespace WatchLattice.Canary;

/// <summary>
/// Registers the canary file watcher with the hub. The behavior lives in
/// <see cref="CanaryWatcher"/>; this class only wires it up and runs it, and
/// its settings stay in the top-level "Canary" config section.
/// </summary>
public sealed class CanaryModule : IWatchLatticeModule
{
    public string Id => "Canary";

    public string DisplayName => "Canary File Watcher";

    public string Description =>
        "Plants decoy files in key folders and alerts when anything modifies, renames, or deletes one.";

    public bool EnabledByDefault => true;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddWatchLatticeCanary(configuration);

    public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        services.GetRequiredService<ILogger<CanaryModule>>().LogInformation("WatchLattice canary watcher online.");
        await services.GetRequiredService<CanaryWatcher>().RunAsync(cancellationToken);
    }
}

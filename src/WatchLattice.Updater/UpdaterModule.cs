using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WatchLattice.Common.Modules;

namespace WatchLattice.Updater;

/// <summary>
/// Registers the update checker with the hub. Checking is the only thing that
/// runs inside the hub; installing is <c>--apply-update</c>, started by the user.
/// Settings stay in the top-level "Updater" config section.
/// </summary>
public sealed class UpdaterModule : IWatchLatticeModule
{
    public string Id => UpdateMonitor.ModuleId;

    public string DisplayName => "Update Checker";

    public string Description =>
        "Checks GitHub Releases for a newer WatchLattice and announces it in the tray; installs only when you choose to.";

    public bool EnabledByDefault => true;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddWatchLatticeUpdater(configuration);

    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<UpdateMonitor>().RunAsync(cancellationToken);
}

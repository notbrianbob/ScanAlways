using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WatchLattice.Common.Alerts;

namespace WatchLattice.Updater;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the update checker and binds <see cref="UpdaterOptions"/> from
    /// the "Updater" configuration section. <see cref="UpdaterModule"/> runs it
    /// inside the hub.
    /// </summary>
    public static IServiceCollection AddWatchLatticeUpdater(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<UpdaterOptions>().Bind(configuration.GetSection(UpdaterOptions.SectionName));

        var current = ReleaseVersion.FromAssembly(typeof(UpdaterModule).Assembly);
        services.TryAddSingleton<IReleaseSource>(sp => new GitHubReleaseSource(UpdateHttp.CreateClient(current),
            sp.GetRequiredService<IOptions<UpdaterOptions>>().Value.ResolveRepository()));
        services.AddSingleton(sp => new UpdateMonitor(
            sp.GetRequiredService<IReleaseSource>(),
            sp.GetRequiredService<IOptions<UpdaterOptions>>(),
            sp.GetRequiredService<ILogger<UpdateMonitor>>(),
            current,
            alerts: sp.GetService<IAlertPublisher>(),
            status: sp.GetService<IUpdateStatusPublisher>()));
        return services;
    }
}

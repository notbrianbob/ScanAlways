using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WatchLattice.DefenderWatchdog;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Defender watchdog and binds
    /// <see cref="DefenderWatchdogOptions"/> from the "DefenderWatchdog"
    /// configuration section. <see cref="DefenderWatchdogModule"/> runs it
    /// inside the hub.
    /// </summary>
    public static IServiceCollection AddWatchLatticeDefenderWatchdog(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DefenderWatchdogOptions>()
            .Bind(configuration.GetSection(DefenderWatchdogOptions.SectionName));

        services.TryAddSingleton<IDefenderStatusProvider, PowerShellDefenderStatusProvider>();
        services.AddSingleton(sp => new DefenderWatchdog(
            sp.GetRequiredService<IDefenderStatusProvider>(),
            sp.GetRequiredService<IOptions<DefenderWatchdogOptions>>(),
            sp.GetRequiredService<ILogger<DefenderWatchdog>>()));
        return services;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ScanAlways.DefenderWatchdog;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Defender watchdog and binds
    /// <see cref="DefenderWatchdogOptions"/> from the "DefenderWatchdog"
    /// configuration section. A host adds <see cref="DefenderWatchdogWorker"/>
    /// (or drives <see cref="DefenderWatchdog"/> itself) to run it.
    /// </summary>
    public static IServiceCollection AddScanAlwaysDefenderWatchdog(
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

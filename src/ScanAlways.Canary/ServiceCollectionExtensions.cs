using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ScanAlways.Canary;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the canary watcher and binds <see cref="CanaryOptions"/> from
    /// the "Canary" configuration section. <see cref="CanaryModule"/> runs it
    /// inside the hub.
    /// </summary>
    public static IServiceCollection AddScanAlwaysCanary(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<CanaryOptions>()
            .Bind(configuration.GetSection(CanaryOptions.SectionName));

        services.AddSingleton<CanaryWatcher>();
        return services;
    }
}

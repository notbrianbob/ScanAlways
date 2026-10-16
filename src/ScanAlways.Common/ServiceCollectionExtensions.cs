using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ScanAlways.Common;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the decoy engine and binds <see cref="DecoyOptions"/> from the
    /// "Decoy" configuration section.
    /// </summary>
    public static IServiceCollection AddScanAlwaysDecoy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DecoyOptions>()
            .Bind(configuration.GetSection(DecoyOptions.SectionName));

        services.AddSingleton<DecoyEngine>();
        return services;
    }
}

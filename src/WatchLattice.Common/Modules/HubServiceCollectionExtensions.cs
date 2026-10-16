using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WatchLattice.Common.Modules;

public static class HubServiceCollectionExtensions
{
    /// <summary>
    /// Wires up the hub: binds <see cref="HubOptions"/>, lets each enabled
    /// module register its services, and adds the <see cref="ModuleSupervisor"/>
    /// that runs them. Disabled modules are never configured, so they cost
    /// nothing at runtime.
    /// </summary>
    public static IServiceCollection AddWatchLatticeHub(
        this IServiceCollection services,
        IConfiguration configuration,
        ModuleCatalog catalog)
    {
        services.AddOptions<HubOptions>()
            .Bind(configuration.GetSection(HubOptions.SectionName));

        var enabled = catalog.ResolveEnabled(configuration);
        foreach (var module in enabled)
        {
            module.ConfigureServices(services, configuration);
        }

        services.AddSingleton(catalog);
        services.AddSingleton(new EnabledModules(enabled));
        services.AddHostedService<ModuleSupervisor>();
        return services;
    }
}

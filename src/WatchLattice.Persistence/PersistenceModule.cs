using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WatchLattice.Common.Modules;

namespace WatchLattice.Persistence;

public sealed class PersistenceModule : IWatchLatticeModule
{
    public string Id => "Persistence";
    public string DisplayName => "Persistence & Autostart Auditor";
    public string Description => "Reports changes to Run keys, Startup folders, and visible scheduled tasks (read-only).";
    public bool EnabledByDefault => true;
    public Type SettingsType => typeof(PersistenceOptions);

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PersistenceOptions>().Bind(configuration.GetSection(Id));
        services.TryAddSingleton<IPersistenceProvider, WindowsPersistenceProvider>();
        services.AddSingleton<PersistenceAuditor>();
    }

    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<PersistenceAuditor>().RunAsync(cancellationToken);
}

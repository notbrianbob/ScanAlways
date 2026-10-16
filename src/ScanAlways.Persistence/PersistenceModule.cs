using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScanAlways.Common.Modules;

namespace ScanAlways.Persistence;

public sealed class PersistenceModule : IScanAlwaysModule
{
    public string Id => "Persistence";
    public string DisplayName => "Persistence & Autostart Auditor";
    public string Description => "Reports changes to Run keys, Startup folders, and visible scheduled tasks (read-only).";
    public bool EnabledByDefault => true;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PersistenceOptions>().Bind(configuration.GetSection(Id));
        services.TryAddSingleton<IPersistenceProvider, WindowsPersistenceProvider>();
        services.AddSingleton<PersistenceAuditor>();
    }

    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<PersistenceAuditor>().RunAsync(cancellationToken);
}

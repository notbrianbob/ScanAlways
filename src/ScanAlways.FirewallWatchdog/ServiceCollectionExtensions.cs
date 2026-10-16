using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScanAlways.Common.Alerts;

namespace ScanAlways.FirewallWatchdog;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the firewall watchdog and binds
    /// <see cref="FirewallWatchdogOptions"/> from the "FirewallWatchdog"
    /// configuration section. <see cref="FirewallWatchdogModule"/> runs it
    /// inside the hub.
    /// </summary>
    public static IServiceCollection AddScanAlwaysFirewallWatchdog(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<FirewallWatchdogOptions>()
            .Bind(configuration.GetSection(FirewallWatchdogOptions.SectionName));

        services.TryAddSingleton<IFirewallStatusProvider, PowerShellFirewallStatusProvider>();
        services.AddSingleton(sp => new FirewallWatchdog(
            sp.GetRequiredService<IFirewallStatusProvider>(),
            sp.GetRequiredService<IOptions<FirewallWatchdogOptions>>(),
            sp.GetRequiredService<ILogger<FirewallWatchdog>>(),
            alerts: sp.GetService<IAlertPublisher>()));
        return services;
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScanAlways.Common.Modules;

namespace ScanAlways.FirewallWatchdog;

/// <summary>
/// Registers the firewall health watchdog with the hub. The behavior lives in
/// <see cref="FirewallWatchdog"/>; this class only wires it up and runs it,
/// and its settings stay in the top-level "FirewallWatchdog" config section.
/// </summary>
public sealed class FirewallWatchdogModule : IScanAlwaysModule
{
    public string Id => FirewallWatchdog.ModuleId;

    public string DisplayName => "Firewall Health Watchdog";

    public string Description =>
        "Reports disabled Windows Firewall profiles and changes to inbound default policy; optionally new inbound allow rules (read-only).";

    public bool EnabledByDefault => true;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddScanAlwaysFirewallWatchdog(configuration);

    public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        services.GetRequiredService<ILogger<FirewallWatchdogModule>>().LogInformation("ScanAlways firewall watchdog online.");
        await services.GetRequiredService<FirewallWatchdog>().RunAsync(cancellationToken);
    }
}

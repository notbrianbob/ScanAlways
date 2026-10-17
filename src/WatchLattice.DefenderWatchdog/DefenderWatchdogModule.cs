using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WatchLattice.Common.Modules;

namespace WatchLattice.DefenderWatchdog;

/// <summary>
/// Registers the Defender health watchdog with the hub. The behavior lives in
/// <see cref="DefenderWatchdog"/>; this class only wires it up and runs it,
/// and its settings stay in the top-level "DefenderWatchdog" config section.
/// </summary>
public sealed class DefenderWatchdogModule : IWatchLatticeModule
{
    public string Id => "DefenderWatchdog";

    public string DisplayName => "Defender Health Watchdog";

    public string Description =>
        "Checks that Defender's real-time protection, tamper protection and signatures stay on and current, and reports new exclusions and weakened ASR or cloud protection (read-only).";

    public bool EnabledByDefault => true;

    public Type SettingsType => typeof(DefenderWatchdogOptions);

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddWatchLatticeDefenderWatchdog(configuration);

    public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        services.GetRequiredService<ILogger<DefenderWatchdogModule>>().LogInformation("WatchLattice Defender watchdog online.");
        await services.GetRequiredService<DefenderWatchdog>().RunAsync(cancellationToken);
    }
}

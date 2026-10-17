using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WatchLattice.Common;
using WatchLattice.Common.Modules;

namespace WatchLattice.Decoy;

/// <summary>
/// Registers the always-scanning decoy with the hub. The behavior lives in
/// <see cref="DecoyEngine"/>; this class only wires it up and runs it, and its
/// settings stay in the top-level "Decoy" config section.
/// </summary>
public sealed class DecoyModule : IWatchLatticeModule
{
    public string Id => "Decoy";

    public string DisplayName => "Always-Scanning Decoy";

    public string Description =>
        "Makes the machine look like an anti-malware scan is always running (read-only, detects nothing).";

    public bool EnabledByDefault => true;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddWatchLatticeDecoy(configuration);

    public Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<DecoyEngine>().RunAsync(cancellationToken);
}

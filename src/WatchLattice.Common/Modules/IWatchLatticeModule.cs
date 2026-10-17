using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WatchLattice.Common.Modules;

/// <summary>
/// The contract every WatchLattice tool implements so the hub can run it.
///
/// A module is a small, self-describing unit: it registers its own services,
/// then runs until the hub asks it to stop. The hub owns process lifetime,
/// logging, configuration, and the Windows-service plumbing; modules own only
/// their behavior. Implementations must have a public parameterless
/// constructor so <see cref="ModuleCatalog.Discover"/> can create them.
/// </summary>
public interface IWatchLatticeModule
{
    /// <summary>
    /// Stable identifier, used as the config key under <c>Modules:&lt;Id&gt;</c>
    /// (for example <c>Modules:Decoy:Enabled</c>). Letters, digits, '-' and '_',
    /// starting with a letter. Never change it once shipped: users' config
    /// depends on it.
    /// </summary>
    string Id { get; }

    /// <summary>Human-readable name shown in logs and <c>--list-modules</c>.</summary>
    string DisplayName { get; }

    /// <summary>One sentence on what the module does.</summary>
    string Description { get; }

    /// <summary>
    /// Whether the module runs when the config does not say either way.
    /// </summary>
    bool EnabledByDefault { get; }

    /// <summary>
    /// The options class bound from the module's top-level config section,
    /// which is named after <see cref="Id"/>. The dashboard's settings editor
    /// builds its fields from this type's public properties, and saving checks
    /// edits against it. Null when the module has no settings.
    /// </summary>
    Type? SettingsType => null;

    /// <summary>
    /// Registers the module's services and binds its options. Called once at
    /// startup, and only for enabled modules.
    /// </summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Runs the module until <paramref name="cancellationToken"/> is cancelled.
    /// Returning early is fine (the module is simply finished); throwing is
    /// treated as a failure, logged, and — if the hub is configured to — retried
    /// with back-off. A failing module never stops the other modules.
    /// </summary>
    Task RunAsync(IServiceProvider services, CancellationToken cancellationToken);
}

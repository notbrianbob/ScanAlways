using System.Reflection;
using WatchLattice.Canary;
using WatchLattice.Decoy;
using WatchLattice.DefenderWatchdog;

namespace WatchLattice.Hub;

/// <summary>
/// The tool assemblies the hub searches for modules. Adding a tool to the
/// suite means a ProjectReference in WatchLattice.Hub.csproj plus one line here.
/// </summary>
internal static class ModuleAssemblies
{
    public static readonly Assembly[] All =
    {
        typeof(DecoyModule).Assembly,
        typeof(CanaryModule).Assembly,
        typeof(DefenderWatchdogModule).Assembly,
    };
}

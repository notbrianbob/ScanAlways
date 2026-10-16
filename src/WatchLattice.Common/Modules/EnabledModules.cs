namespace WatchLattice.Common.Modules;

/// <summary>The modules the hub resolved as enabled at startup.</summary>
public sealed class EnabledModules
{
    public EnabledModules(IReadOnlyList<IWatchLatticeModule> modules)
    {
        Modules = modules;
    }

    public IReadOnlyList<IWatchLatticeModule> Modules { get; }
}

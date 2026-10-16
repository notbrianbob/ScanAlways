namespace ScanAlways.Common.Modules;

/// <summary>The modules the hub resolved as enabled at startup.</summary>
public sealed class EnabledModules
{
    public EnabledModules(IReadOnlyList<IScanAlwaysModule> modules)
    {
        Modules = modules;
    }

    public IReadOnlyList<IScanAlwaysModule> Modules { get; }
}

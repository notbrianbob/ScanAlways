using System.Reflection;
using ScanAlways.Decoy;

namespace ScanAlways.Hub;

/// <summary>
/// The tool assemblies the hub searches for modules. Adding a tool to the
/// suite means a ProjectReference in ScanAlways.Hub.csproj plus one line here.
/// </summary>
internal static class ModuleAssemblies
{
    public static readonly Assembly[] All =
    {
        typeof(DecoyModule).Assembly,
    };
}

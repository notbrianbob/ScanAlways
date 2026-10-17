using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WatchLattice.Canary;

namespace WatchLattice.Hub;

/// <summary>
/// <c>--remove-canaries</c> deletes the canaries this hub planted that are
/// still intact, using the same manifest as the Canary module. uninstall.ps1
/// runs it after stopping the service, so removal never trips an alert.
/// Changed canaries are left where they are as evidence and listed. Exits 0
/// when none are left behind, 1 otherwise.
/// </summary>
internal static class CanaryCommand
{
    public static int Run(IConfiguration configuration)
    {
        var options = new CanaryOptions();
        configuration.GetSection(CanaryOptions.SectionName).Bind(options);
        var manifest = options.ResolveManifestPath();
        if (!File.Exists(manifest))
        {
            Console.WriteLine($"No canary manifest at {manifest}; nothing to remove.");
            return 0;
        }

        using var logging = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
        var deployer = new CanaryDeployer(options, logging.CreateLogger("Canary"));
        var removed = deployer.RemoveAll();
        Console.WriteLine($"Removed {removed} intact canar{(removed == 1 ? "y" : "ies")}.");

        var left = deployer.PlantedFiles;
        if (left.Count == 0) return 0;
        Console.WriteLine("Left in place because they were changed since they were planted, or could not be deleted:");
        foreach (var path in left) Console.WriteLine("  " + path);
        return 1;
    }
}

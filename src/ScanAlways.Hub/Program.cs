using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScanAlways.Common.Modules;
using ScanAlways.Hub;

const string ListModulesSwitch = "--list-modules";

var listOnly = args.Any(a => string.Equals(a, ListModulesSwitch, StringComparison.OrdinalIgnoreCase));
var hostArgs = args.Where(a => !string.Equals(a, ListModulesSwitch, StringComparison.OrdinalIgnoreCase)).ToArray();

var catalog = ModuleCatalog.Discover(ModuleAssemblies.All);

// Always read appsettings.json from next to the binary, whether started from
// a console, `dotnet run`, or the Windows service manager.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = hostArgs,
    ContentRootPath = AppContext.BaseDirectory,
});

if (listOnly)
{
    foreach (var module in catalog.Modules)
    {
        var state = ModuleCatalog.IsEnabled(module, builder.Configuration) ? "enabled " : "disabled";
        Console.WriteLine($"{state}  {module.Id,-16} {module.DisplayName}: {module.Description}");
    }
    return;
}

// Let the same binary run as a Windows service or a plain console process.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "ScanAlways Protection";
});

builder.Services.AddScanAlwaysHub(builder.Configuration, catalog);

var host = builder.Build();
host.Run();

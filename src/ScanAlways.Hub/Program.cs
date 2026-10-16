using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScanAlways.Common.Modules;
using ScanAlways.Hub;

const string ListModulesSwitch = "--list-modules";
const string CheckUpdateSwitch = "--check-update";
const string ApplyUpdateSwitch = "--apply-update";
const string PauseOnFailureSwitch = "--pause-on-failure";
string[] commandSwitches = { ListModulesSwitch, CheckUpdateSwitch, ApplyUpdateSwitch, PauseOnFailureSwitch };

bool HasSwitch(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

var listOnly = HasSwitch(ListModulesSwitch);
var hostArgs = args.Where(a => !commandSwitches.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray();

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
    return 0;
}

// One-shot update commands: they never start the hub's modules.
if (HasSwitch(CheckUpdateSwitch) || HasSwitch(ApplyUpdateSwitch))
{
    return await UpdateCommand.RunAsync(builder.Configuration, apply: HasSwitch(ApplyUpdateSwitch),
        pauseOnFailure: HasSwitch(PauseOnFailureSwitch));
}

// Let the same binary run as a Windows service or a plain console process.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = UpdateCommand.ServiceName;
});

builder.Services.AddScanAlwaysHub(builder.Configuration, catalog);

var host = builder.Build();
host.Run();
return 0;

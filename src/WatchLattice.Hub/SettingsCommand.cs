using System.Text.Json;
using WatchLattice.Common;
using WatchLattice.Common.Modules;
using WatchLattice.Common.Settings;

namespace WatchLattice.Hub;

/// <summary>
/// <c>--describe-settings</c> prints the settings schema and the current
/// appsettings.json as JSON, for the dashboard; it changes nothing and needs no
/// elevation. <c>--apply-settings</c> saves the settings the editor staged as
/// appsettings.json.pending and restarts the service; it needs administrator
/// rights, which is why the editor that stages it runs elevated. Exit codes are
/// <see cref="SettingsOutcome"/>.
/// </summary>
internal static class SettingsCommand
{
    public static int Describe(ModuleCatalog catalog)
    {
        var path = Path.Combine(AppContext.BaseDirectory, SettingsApplier.SettingsFileName);
        try
        {
            var values = SettingValues.Parse(File.ReadAllText(path));
            var description = SettingsSchema.Describe(catalog, path, values);
            Console.Out.Write(JsonSerializer.Serialize(description, AtomicJsonFile.JsonOptions));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Console.Error.WriteLine($"Could not read {path}: {ex.Message}");
            return (int)SettingsOutcome.Failed;
        }
    }

    public static async Task<int> ApplyAsync(ModuleCatalog catalog)
    {
        IServiceControl? service = OperatingSystem.IsWindows() ? new WindowsHubServiceControl(UpdateCommand.ServiceName) : null;
        var applier = new SettingsApplier(SettingsSchema.Build(catalog), SettingsSchema.BindableSections(catalog), service, Console.WriteLine);
        return (int)await applier.ApplyAsync(AppContext.BaseDirectory);
    }
}

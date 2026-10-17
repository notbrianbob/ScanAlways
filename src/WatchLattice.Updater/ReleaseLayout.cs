namespace WatchLattice.Updater;

/// <summary>
/// The shape of a release, shared by the release workflow, the checker and the
/// installer: one zip with Hub/ and Tray/ folders, plus a SHA256SUMS.txt.
/// </summary>
public static class ReleaseLayout
{
    public const string ChecksumFileName = "SHA256SUMS.txt";
    public const string HubFolder = "Hub";
    public const string TrayFolder = "Tray";
    public const string HubExecutable = "ScanAlways.Protection.Service.exe";
    public const string TrayExecutable = "ScanAlways.Tray.exe";
    public const string HubSettingsFile = "appsettings.json";

    public static string PackageFileName(ReleaseVersion version) => $"WatchLattice-{version}-win-x64.zip";

    /// <summary>The tray installed next to the hub (…\Hub and …\Tray), or null for any other layout.</summary>
    public static string? FindTrayDirectory(string hubDirectory)
    {
        var tray = Path.GetFullPath(Path.Combine(hubDirectory, "..", TrayFolder));
        return File.Exists(Path.Combine(tray, TrayExecutable)) ? tray : null;
    }

    /// <summary>The hub installed next to the tray, or null for any other layout.</summary>
    public static string? FindHubExecutable(string trayDirectory)
    {
        var hub = Path.GetFullPath(Path.Combine(trayDirectory, "..", HubFolder, HubExecutable));
        return File.Exists(hub) ? hub : null;
    }
}

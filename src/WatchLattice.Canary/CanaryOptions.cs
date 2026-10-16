namespace WatchLattice.Canary;

/// <summary>
/// Configuration for the canary file watcher. Plain values so they bind
/// straight from appsettings.json, environment variables, or the command line.
/// </summary>
public sealed class CanaryOptions
{
    public const string SectionName = "Canary";

    /// <summary>
    /// File names used when none are configured. The leading "!" and "zzz"
    /// prefixes put a canary near the start and the end of an alphabetical
    /// walk, which is how a lot of ransomware enumerates a folder.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultFileNames = new[]
    {
        "!000_Financial_Records.docx",
        "!000_Passwords_Backup.xlsx",
        "zzz_Tax_Return_Archive.pdf",
    };

    /// <summary>
    /// Folders to plant canaries in. When empty, <see cref="ResolveFolders"/>
    /// falls back to Desktop, Documents and Pictures, the folders ransomware
    /// goes after first. Folders that do not exist are skipped, never created.
    /// </summary>
    public List<string> Folders { get; set; } = new();

    /// <summary>
    /// Canary file names (no paths). When empty, <see cref="DefaultFileNames"/>
    /// is used. Names containing path separators or invalid characters are
    /// ignored.
    /// </summary>
    public List<string> FileNames { get; set; } = new();

    /// <summary>
    /// Mark canaries hidden so they don't clutter Explorer. Ransomware
    /// enumerates hidden files just the same, so this does not weaken the trap.
    /// </summary>
    public bool MarkHidden { get; set; } = true;

    /// <summary>Size of each canary file in bytes.</summary>
    public int CanarySizeBytes { get; set; } = 24 * 1024;

    /// <summary>
    /// How often every canary is re-hashed as a backstop for events the file
    /// watcher missed (buffer overflow, network folders, the service being
    /// paused). Minimum 10 seconds.
    /// </summary>
    public int IntegrityCheckSeconds { get; set; } = 60;

    /// <summary>
    /// Duplicate alerts for the same canary and change are suppressed for this
    /// many seconds, since one save often produces several file-system events.
    /// </summary>
    public int AlertDebounceSeconds { get; set; } = 5;

    /// <summary>
    /// When true, canaries that are still intact are deleted when the watcher
    /// stops. Off by default so the tripwire keeps working across restarts and
    /// any tripped canary is left in place as evidence.
    /// </summary>
    public bool RemoveCanariesOnStop { get; set; }

    /// <summary>Rolling alert log. Null picks %LOCALAPPDATA%\ScanAlways\canary-alerts.log.</summary>
    public string? AlertLogPath { get; set; }

    /// <summary>
    /// Where the watcher records which files it planted and their hashes.
    /// Null picks %LOCALAPPDATA%\ScanAlways\canary-manifest.json.
    /// </summary>
    public string? ManifestPath { get; set; }

    public IReadOnlyList<string> ResolveFolders()
    {
        var folders = Folders
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Environment.ExpandEnvironmentVariables)
            .ToList();

        if (folders.Count == 0)
        {
            foreach (var folder in new[]
            {
                Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyPictures,
            })
            {
                var path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(path))
                {
                    folders.Add(path);
                }
            }
        }

        return folders
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(CanaryPaths.Comparer)
            .ToList();
    }

    public IReadOnlyList<string> ResolveFileNames()
    {
        var invalid = Path.GetInvalidFileNameChars();
        var names = FileNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Where(n => n.IndexOfAny(invalid) < 0 && n != "." && n != "..")
            .Distinct(CanaryPaths.Comparer)
            .ToList();

        return names.Count > 0 ? names : DefaultFileNames.ToList();
    }

    public string ResolveAlertLogPath() => ResolveDataPath(AlertLogPath, "canary-alerts.log");

    public string ResolveManifestPath() => ResolveDataPath(ManifestPath, "canary-manifest.json");

    private static string ResolveDataPath(string? configured, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Environment.ExpandEnvironmentVariables(configured);
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetTempPath();
        }
        return Path.Combine(root, "ScanAlways", fileName);
    }
}

/// <summary>Path comparison that matches the host file system's case rules.</summary>
public static class CanaryPaths
{
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static string Normalize(string path) => Path.GetFullPath(path);
}

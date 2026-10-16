namespace WatchLattice.Persistence;

public sealed class PersistenceOptions
{
    public int CheckIntervalSeconds { get; set; } = 300;
    public bool WatchRegistry { get; set; } = true;
    public bool WatchStartupFolders { get; set; } = true;
    public bool WatchScheduledTasks { get; set; } = true;
    public string[] StartupFolders { get; set; } = Array.Empty<string>();
    public string[] IgnoredEntries { get; set; } = Array.Empty<string>();
    public string? BaselinePath { get; set; }
    public int MaxStartupFileBytes { get; set; } = 4 * 1024 * 1024;

    public string ResolveBaselinePath() => Path.GetFullPath(Environment.ExpandEnvironmentVariables(
        string.IsNullOrWhiteSpace(BaselinePath) ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanAlways", "persistence-baseline.json")
            : BaselinePath));

    public IReadOnlyList<string> ResolveStartupFolders() => (StartupFolders.Length > 0 ? StartupFolders : new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
    }).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(Environment.ExpandEnvironmentVariables(p)))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

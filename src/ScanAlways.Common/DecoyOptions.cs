namespace ScanAlways.Common;

/// <summary>
/// How aggressively the decoy emits its (harmless) scanner-like signals.
/// Higher profiles touch more files per minute and narrate more often. None of
/// the profiles modify, quarantine, or delete anything.
/// </summary>
public enum DecoyIntensity
{
    /// <summary>A light background hum: a file touched every few seconds.</summary>
    Idle,

    /// <summary>A steady "real-time protection" style trickle. The sensible default.</summary>
    Balanced,

    /// <summary>Looks like an on-demand full-system scan in progress.</summary>
    FullScan,
}

/// <summary>
/// Configuration for the always-scanning decoy. These are deliberately plain
/// values so they can be bound straight from appsettings.json, environment
/// variables, or command-line switches.
/// </summary>
public sealed class DecoyOptions
{
    public const string SectionName = "Decoy";

    /// <summary>
    /// Directories the decoy reads from to produce believable file-access
    /// activity. Every path is opened read-only; nothing is written back.
    /// When empty, <see cref="ResolveScanRoots"/> falls back to common
    /// user-profile locations that an antivirus would plausibly scan.
    /// </summary>
    public List<string> ScanRoots { get; set; } = new();

    /// <summary>The overall intensity profile.</summary>
    public DecoyIntensity Intensity { get; set; } = DecoyIntensity.Balanced;

    /// <summary>
    /// Hard ceiling on how many files are opened per second, regardless of
    /// intensity. Keeps disk and CPU overhead modest.
    /// </summary>
    public int MaxFilesPerSecond { get; set; } = 8;

    /// <summary>
    /// How many bytes to read from the front of each file before moving on.
    /// A real scanner reads headers and signatures; reading a small prefix is
    /// enough to generate representative I/O without churning the whole disk.
    /// </summary>
    public int BytesReadPerFile { get; set; } = 64 * 1024;

    /// <summary>
    /// Upper bound on a single file's size (bytes) before the decoy skips its
    /// body and just records the name. Avoids thrashing on huge files.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>When true, the decoy writes a rolling, scanner-style log file.</summary>
    public bool WriteScanLog { get; set; } = true;

    /// <summary>
    /// Path of the scanner-style log. When null, the engine picks a per-user
    /// temp location. The log is purely cosmetic telemetry.
    /// </summary>
    public string? ScanLogPath { get; set; }

    /// <summary>
    /// A fixed RNG seed for reproducible narration in tests. Null means a
    /// time-based seed (normal operation).
    /// </summary>
    public int? RandomSeed { get; set; }

    /// <summary>
    /// Returns the configured scan roots, or a set of sensible, always-present
    /// fallbacks if none were configured. Non-existent paths are dropped.
    /// </summary>
    public IReadOnlyList<string> ResolveScanRoots()
    {
        var roots = ScanRoots
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Environment.ExpandEnvironmentVariables)
            .ToList();

        if (roots.Count == 0)
        {
            void Add(Environment.SpecialFolder folder)
            {
                var path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(path))
                {
                    roots.Add(path);
                }
            }

            Add(Environment.SpecialFolder.UserProfile);
            Add(Environment.SpecialFolder.MyDocuments);
            Add(Environment.SpecialFolder.MyPictures);
            Add(Environment.SpecialFolder.ApplicationData);
            Add(Environment.SpecialFolder.LocalApplicationData);
        }

        return roots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Per-intensity pacing used by the engine.</summary>
    public (int FilesPerSecond, double NarrateEverySeconds) Pace() => Intensity switch
    {
        DecoyIntensity.Idle     => (Math.Min(1, MaxFilesPerSecond), 12.0),
        DecoyIntensity.Balanced => (Math.Min(3, MaxFilesPerSecond), 6.0),
        DecoyIntensity.FullScan => (MaxFilesPerSecond, 2.5),
        _ => (1, 10.0),
    };
}

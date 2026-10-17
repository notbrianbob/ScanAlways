namespace WatchLattice.DefenderWatchdog;

/// <summary>Configuration for the Defender health watchdog.</summary>
public sealed class DefenderWatchdogOptions
{
    public const string SectionName = "DefenderWatchdog";

    /// <summary>Seconds between checks. Minimum 30.</summary>
    public int CheckIntervalSeconds { get; set; } = 300;

    /// <summary>Signatures older than this raise a warning. Defender normally updates several times a day.</summary>
    public int MaxSignatureAgeHours { get; set; } = 48;

    /// <summary>Signatures older than this raise a critical alert.</summary>
    public int CriticalSignatureAgeHours { get; set; } = 168;

    /// <summary>
    /// Treat Tamper Protection being off as critical instead of a warning.
    /// Off by default because some managed (Intune / GPO) machines turn it off
    /// on purpose.
    /// </summary>
    public bool TamperProtectionOffIsCritical { get; set; }

    /// <summary>
    /// Also watch Defender's exclusions, attack surface reduction rules and
    /// cloud-delivered protection, and report changes to them. Read-only, like
    /// everything else here.
    /// </summary>
    public bool MonitorSettings { get; set; } = true;

    /// <summary>
    /// Raise a standing warning for exclusions that cover a lot of ground: a
    /// whole drive, Windows or temp folders, executable or script extensions,
    /// or script hosts such as powershell.exe.
    /// </summary>
    public bool ReportBroadExclusions { get; set; } = true;

    /// <summary>Exclusion values (wildcards * and ?) you added on purpose; they never raise an alert.</summary>
    public string[] IgnoredExclusions { get; set; } = Array.Empty<string>();

    /// <summary>At most this many individual setting-change alerts per check; the rest are summarized in one alert.</summary>
    public int MaxChangeAlertsPerCheck { get; set; } = 10;

    /// <summary>Last observed exclusions and ASR rules. Null picks %LOCALAPPDATA%\ScanAlways\defender-settings.json.</summary>
    public string? StatePath { get; set; }

    /// <summary>Rolling alert log. Null picks %LOCALAPPDATA%\ScanAlways\defender-watchdog.log.</summary>
    public string? AlertLogPath { get; set; }

    public string ResolveAlertLogPath() => Resolve(AlertLogPath, "defender-watchdog.log");

    public string ResolveStatePath() => Resolve(StatePath, "defender-settings.json");

    private static string Resolve(string? configured, string fileName)
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
        return Path.Join(root, "ScanAlways", fileName);
    }
}

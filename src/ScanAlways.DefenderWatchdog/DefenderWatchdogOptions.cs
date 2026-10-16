namespace ScanAlways.DefenderWatchdog;

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

    /// <summary>Rolling alert log. Null picks %LOCALAPPDATA%\ScanAlways\defender-watchdog.log.</summary>
    public string? AlertLogPath { get; set; }

    public string ResolveAlertLogPath()
    {
        if (!string.IsNullOrWhiteSpace(AlertLogPath))
        {
            return Environment.ExpandEnvironmentVariables(AlertLogPath);
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetTempPath();
        }
        return Path.Combine(root, "ScanAlways", "defender-watchdog.log");
    }
}

namespace ScanAlways.FirewallWatchdog;

/// <summary>Configuration for the firewall health watchdog.</summary>
public sealed class FirewallWatchdogOptions
{
    public const string SectionName = "FirewallWatchdog";

    /// <summary>Seconds between checks. Minimum 30.</summary>
    public int CheckIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Also report enabled inbound "allow" rules that were not there on the
    /// previous check. Off by default: installers and Windows updates add rules
    /// routinely, and enumerating every rule costs a few seconds of PowerShell.
    /// </summary>
    public bool MonitorInboundAllowRules { get; set; }

    /// <summary>Rule names or display names (wildcards * and ?) that never raise a new-rule alert.</summary>
    public string[] IgnoredInboundRules { get; set; } = Array.Empty<string>();

    /// <summary>At most this many individual new-rule alerts per check; the rest are summarized in one alert.</summary>
    public int MaxRuleAlertsPerCheck { get; set; } = 10;

    /// <summary>Last observed policy and rule baseline. Null picks %LOCALAPPDATA%\ScanAlways\firewall-state.json.</summary>
    public string? StatePath { get; set; }

    /// <summary>Rolling alert log. Null picks %LOCALAPPDATA%\ScanAlways\firewall-watchdog.log.</summary>
    public string? AlertLogPath { get; set; }

    public string ResolveStatePath() => Resolve(StatePath, "firewall-state.json");

    public string ResolveAlertLogPath() => Resolve(AlertLogPath, "firewall-watchdog.log");

    private static string Resolve(string? configured, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        }

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.GetTempPath();
        }
        return Path.Join(root, "ScanAlways", fileName);
    }
}

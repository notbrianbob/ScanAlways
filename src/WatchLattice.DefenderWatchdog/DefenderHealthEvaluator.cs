namespace WatchLattice.DefenderWatchdog;

public enum FindingSeverity
{
    Info,
    Warning,
    Critical,
}

public enum DefenderHealth
{
    Healthy,
    Degraded,
    Critical,

    /// <summary>The status could not be read, which on its own is worth a look.</summary>
    Unknown,
}

/// <summary>One problem found in Defender's state. <see cref="Code"/> is stable, for de-duplication.</summary>
public sealed record DefenderFinding(string Code, FindingSeverity Severity, string Message)
{
    public override string ToString() => $"[{Severity}] {Message}";
}

public sealed record DefenderHealthReport(
    DateTimeOffset CheckedAt,
    DefenderHealth Health,
    IReadOnlyList<DefenderFinding> Findings,
    DefenderStatus Status)
{
    public string Summary => Health switch
    {
        DefenderHealth.Healthy => "Defender healthy: real-time protection on, tamper protection on, signatures current.",
        _ => $"Defender {Health.ToString().ToLowerInvariant()}: " +
             string.Join("; ", Findings.Where(f => f.Severity > FindingSeverity.Info).Select(f => f.Message)),
    };
}

/// <summary>A one-off change between two checks, such as a new exclusion or a weakened ASR rule.</summary>
public sealed record DefenderChange(string Code, FindingSeverity Severity, string Title, string Message, string? Resource = null)
{
    public override string ToString() => $"[{Severity}] {Message}";
}

/// <summary>
/// Pure rules that turn a <see cref="DefenderStatus"/> into findings. No I/O,
/// so every rule is unit-tested without Defender.
/// </summary>
public static partial class DefenderHealthEvaluator
{
    public const string StatusUnavailable = "STATUS_UNAVAILABLE";
    public const string NotRunning = "NOT_RUNNING";
    public const string PassiveMode = "PASSIVE_MODE";
    public const string ServiceDisabled = "SERVICE_DISABLED";
    public const string AntivirusDisabled = "ANTIVIRUS_DISABLED";
    public const string RealTimeOff = "REALTIME_OFF";
    public const string BehaviorMonitorOff = "BEHAVIOR_MONITOR_OFF";
    public const string DownloadScanOff = "IOAV_OFF";
    public const string OnAccessOff = "ON_ACCESS_OFF";
    public const string TamperProtectionOff = "TAMPER_PROTECTION_OFF";
    public const string SignaturesStale = "SIGNATURES_STALE";
    public const string SignaturesVeryStale = "SIGNATURES_VERY_STALE";
    public const string SignatureDateUnknown = "SIGNATURE_DATE_UNKNOWN";

    public static DefenderHealthReport Evaluate(DefenderStatus status, DefenderWatchdogOptions options, DateTimeOffset now)
    {
        var findings = new List<DefenderFinding>();

        if (!status.Available)
        {
            findings.Add(new(StatusUnavailable, FindingSeverity.Warning,
                $"Could not read Defender status: {status.Error ?? "unknown error"}"));
            return new DefenderHealthReport(now, DefenderHealth.Unknown, findings, status);
        }

        var mode = status.RunningMode ?? "";
        var notRunning = mode.Contains("not running", StringComparison.OrdinalIgnoreCase);
        var passive = !notRunning && (mode.Contains("passive", StringComparison.OrdinalIgnoreCase)
                                      || mode.Contains("EDR Block", StringComparison.OrdinalIgnoreCase));

        if (notRunning)
        {
            findings.Add(new(NotRunning, FindingSeverity.Critical, "Defender antivirus is not running."));
        }
        else if (passive)
        {
            // Another antivirus is registered as primary, so Defender's real-time
            // engines are off by design. Flag it once instead of a cascade.
            findings.Add(new(PassiveMode, FindingSeverity.Warning,
                $"Defender is in {mode}; another antivirus product is providing real-time protection."));
        }
        else
        {
            if (status.AMServiceEnabled == false)
            {
                findings.Add(new(ServiceDisabled, FindingSeverity.Critical, "The Defender antimalware service is disabled."));
            }
            if (status.AntivirusEnabled == false)
            {
                findings.Add(new(AntivirusDisabled, FindingSeverity.Critical, "Defender antivirus is disabled."));
            }
            if (status.RealTimeProtectionEnabled == false)
            {
                findings.Add(new(RealTimeOff, FindingSeverity.Critical, "Real-time protection is OFF."));
            }
            if (status.BehaviorMonitorEnabled == false)
            {
                findings.Add(new(BehaviorMonitorOff, FindingSeverity.Warning, "Behavior monitoring is off."));
            }
            if (status.IoavProtectionEnabled == false)
            {
                findings.Add(new(DownloadScanOff, FindingSeverity.Warning, "Scanning of downloaded files and attachments is off."));
            }
            if (status.OnAccessProtectionEnabled == false)
            {
                findings.Add(new(OnAccessOff, FindingSeverity.Warning, "On-access protection is off."));
            }
        }

        if (status.IsTamperProtected == false)
        {
            findings.Add(new(TamperProtectionOff,
                options.TamperProtectionOffIsCritical ? FindingSeverity.Critical : FindingSeverity.Warning,
                "Tamper protection is OFF, so other programs can change Defender's settings."));
        }

        if (!passive && !notRunning)
        {
            AddSignatureFindings(status, options, now, findings);
        }

        if (options.MonitorSettings)
        {
            AddSettingsFindings(status, options, defenderActive: !passive && !notRunning, findings);
        }

        var health = findings.Any(f => f.Severity == FindingSeverity.Critical) ? DefenderHealth.Critical
            : findings.Any(f => f.Severity == FindingSeverity.Warning) ? DefenderHealth.Degraded
            : DefenderHealth.Healthy;

        return new DefenderHealthReport(now, health, findings, status);
    }

    private static void AddSignatureFindings(
        DefenderStatus status, DefenderWatchdogOptions options, DateTimeOffset now, List<DefenderFinding> findings)
    {
        if (status.SignatureLastUpdated is not { } updated)
        {
            findings.Add(new(SignatureDateUnknown, FindingSeverity.Info, "Signature update time was not reported."));
            return;
        }

        var age = now - updated;
        var critical = TimeSpan.FromHours(Math.Max(1, options.CriticalSignatureAgeHours));
        var warning = TimeSpan.FromHours(Math.Max(1, options.MaxSignatureAgeHours));

        if (age >= critical)
        {
            findings.Add(new(SignaturesVeryStale, FindingSeverity.Critical,
                $"Antivirus signatures are {FormatAge(age)} old (last updated {updated:yyyy-MM-dd HH:mm} UTC)."));
        }
        else if (age >= warning)
        {
            findings.Add(new(SignaturesStale, FindingSeverity.Warning,
                $"Antivirus signatures are {FormatAge(age)} old (last updated {updated:yyyy-MM-dd HH:mm} UTC)."));
        }
    }

    private static string FormatAge(TimeSpan age) =>
        age.TotalDays >= 2 ? $"{(int)age.TotalDays} days" : $"{(int)age.TotalHours} hours";
}

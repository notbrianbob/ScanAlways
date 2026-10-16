namespace ScanAlways.FirewallWatchdog;

/// <summary>What one profile looked like at the last check.</summary>
public sealed record FirewallProfileSnapshot(bool? Enabled, string? DefaultInboundAction);

/// <summary>
/// The watchdog's memory between checks and across restarts: the last observed
/// profile policy, and (when rule monitoring is on) the inbound allow rules
/// already seen. Kept on disk so a change made while the hub was stopped is
/// still reported at the next start.
/// </summary>
public sealed class FirewallState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public Dictionary<string, FirewallProfileSnapshot> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null until rule monitoring records its first baseline.</summary>
    public List<FirewallRule>? InboundAllowRules { get; set; }

    public bool IsValid() =>
        Version == CurrentVersion
        && Profiles is not null
        && Profiles.All(p => FirewallStatusParser.NormalizeProfile(p.Key) is not null && p.Value is not null)
        && (InboundAllowRules is null || InboundAllowRules.All(r => r is not null && !string.IsNullOrEmpty(r.Id)));
}

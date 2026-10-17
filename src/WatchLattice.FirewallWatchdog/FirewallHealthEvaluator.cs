using System.IO.Enumeration;
using WatchLattice.Common.Alerts;

namespace WatchLattice.FirewallWatchdog;

public enum FirewallHealth
{
    Healthy,
    Degraded,
    Critical,

    /// <summary>The status could not be read, which on its own is worth a look.</summary>
    Unknown,
}

/// <summary>A standing problem in the firewall's state. <see cref="Code"/> is stable, for de-duplication.</summary>
public sealed record FirewallFinding(string Code, AlertSeverity Severity, string Message, string? Resource = null)
{
    public override string ToString() => $"[{Severity}] {Message}";
}

/// <summary>A one-off change between two checks, such as a new default inbound action or a new allow rule.</summary>
public sealed record FirewallChange(string Code, AlertSeverity Severity, string Title, string Message, string? Resource = null)
{
    public override string ToString() => $"[{Severity}] {Message}";
}

public sealed record FirewallHealthReport(
    DateTimeOffset CheckedAt,
    FirewallHealth Health,
    IReadOnlyList<FirewallFinding> Findings,
    FirewallStatus Status)
{
    public string Summary => Health switch
    {
        FirewallHealth.Healthy => "Firewall healthy: every profile is on and blocks unsolicited inbound traffic by default.",
        _ => $"Firewall {Health.ToString().ToLowerInvariant()}: " + string.Join("; ", Findings.Select(f => f.Message)),
    };
}

/// <summary>
/// Pure rules that turn a <see cref="FirewallStatus"/> into standing findings,
/// and two snapshots into changes. No I/O, so every rule is unit-tested
/// without Windows.
/// </summary>
public static class FirewallHealthEvaluator
{
    public const string StatusUnavailable = "STATUS_UNAVAILABLE";
    public const string ProfileDisabledPrefix = "PROFILE_DISABLED_";
    public const string InboundDefaultAllowPrefix = "INBOUND_DEFAULT_ALLOW_";
    public const string InboundDefaultChangedPrefix = "INBOUND_DEFAULT_CHANGED_";
    public const string RulesUnavailable = "RULES_UNAVAILABLE";
    public const string RuleAdded = "INBOUND_ALLOW_RULE_ADDED";
    public const string RulesAddedSummary = "INBOUND_ALLOW_RULES_ADDED";
    public const string RuleBaselineCreated = "RULE_BASELINE_CREATED";

    public static FirewallHealthReport Evaluate(FirewallStatus status, FirewallWatchdogOptions options, DateTimeOffset now)
    {
        var findings = new List<FirewallFinding>();

        if (!status.Available)
        {
            findings.Add(new(StatusUnavailable, AlertSeverity.Warning,
                $"Could not read Windows Firewall status: {status.Error ?? "unknown error"}"));
            return new FirewallHealthReport(now, FirewallHealth.Unknown, findings, status);
        }

        foreach (var profile in status.Profiles)
        {
            // A problem on a profile no connected network uses is real but not
            // exposing the machine right now, so it is a warning, not critical.
            var severity = status.MayBeActive(profile.Name) ? AlertSeverity.Critical : AlertSeverity.Warning;
            var inUse = status.MayBeActive(profile.Name) && status.ActiveProfiles is not null ? " (in use now)" : "";

            if (profile.Enabled == false)
            {
                findings.Add(new(ProfileDisabledPrefix + profile.Name.ToUpperInvariant(), severity,
                    $"The {profile.Name} firewall profile is OFF{inUse}.", profile.Name));
            }
            else if (profile.InboundAllowsByDefault)
            {
                findings.Add(new(InboundDefaultAllowPrefix + profile.Name.ToUpperInvariant(), severity,
                    $"The {profile.Name} firewall profile allows unsolicited inbound connections by default{inUse}.",
                    profile.Name));
            }
        }

        if (options.MonitorInboundAllowRules && status.InboundAllowRules is null)
        {
            findings.Add(new(RulesUnavailable, AlertSeverity.Warning,
                $"Could not read inbound firewall rules: {status.RulesError ?? "no rules were reported"}"));
        }

        var health = findings.Any(f => f.Severity == AlertSeverity.Critical) ? FirewallHealth.Critical
            : findings.Any(f => f.Severity == AlertSeverity.Warning) ? FirewallHealth.Degraded
            : FirewallHealth.Healthy;

        return new FirewallHealthReport(now, health, findings, status);
    }

    /// <summary>
    /// Reports default inbound actions that differ from the previous snapshot.
    /// A change to Allow is a warning; any other change (usually back to Block)
    /// is informational. Profiles missing from either side are not compared.
    /// </summary>
    public static IReadOnlyList<FirewallChange> CompareProfiles(
        IReadOnlyDictionary<string, FirewallProfileSnapshot> previous, IReadOnlyList<FirewallProfileState> current)
    {
        var changes = new List<FirewallChange>();
        foreach (var profile in current)
        {
            if (!previous.TryGetValue(profile.Name, out var before)
                || before.DefaultInboundAction is null || profile.DefaultInboundAction is null
                || string.Equals(before.DefaultInboundAction, profile.DefaultInboundAction, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            changes.Add(new(InboundDefaultChangedPrefix + profile.Name.ToUpperInvariant(),
                profile.InboundAllowsByDefault ? AlertSeverity.Warning : AlertSeverity.Info,
                "Firewall inbound policy changed",
                $"The {profile.Name} profile's default inbound action changed from {before.DefaultInboundAction} to {profile.DefaultInboundAction}.",
                profile.Name));
        }
        return changes;
    }

    /// <summary>
    /// Reports enabled inbound allow rules that are not in the baseline (a new
    /// rule, or an existing one that was enabled or switched to allow), minus
    /// ignored ones. At most <see cref="FirewallWatchdogOptions.MaxRuleAlertsPerCheck"/>
    /// are listed individually; the rest are summarized so a large install
    /// doesn't flood the alert feed.
    /// </summary>
    public static IReadOnlyList<FirewallChange> CompareRules(
        IReadOnlyCollection<string> baselineIds, IReadOnlyList<FirewallRule> current, FirewallWatchdogOptions options)
    {
        var known = new HashSet<string>(baselineIds, StringComparer.OrdinalIgnoreCase);
        var added = current
            .Where(r => !known.Contains(r.Id) && !IsIgnored(r, options.IgnoredInboundRules))
            .ToList();

        var limit = Math.Max(0, options.MaxRuleAlertsPerCheck);
        var changes = added.Take(limit)
            .Select(r => new FirewallChange(RuleAdded, AlertSeverity.Warning, "New inbound firewall allow rule",
                $"Inbound allow rule added or enabled: {Describe(r)}.", r.Id))
            .ToList();

        if (added.Count > limit)
        {
            changes.Add(new(RulesAddedSummary, AlertSeverity.Warning, "New inbound firewall allow rules",
                $"{added.Count - limit} more inbound allow rules were added or enabled. Review them in Windows Defender Firewall with Advanced Security."));
        }
        return changes;
    }

    public static bool IsIgnored(FirewallRule rule, IEnumerable<string> patterns) => patterns
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Any(p => FileSystemName.MatchesSimpleExpression(p, rule.Id, ignoreCase: true)
                  || (rule.DisplayName is { Length: > 0 } name && FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true)));

    private static string Describe(FirewallRule rule)
    {
        var name = string.IsNullOrWhiteSpace(rule.DisplayName) || rule.DisplayName == rule.Id
            ? $"\"{Excerpt(rule.Id)}\""
            : $"\"{Excerpt(rule.DisplayName)}\" ({Excerpt(rule.Id)})";
        return string.IsNullOrWhiteSpace(rule.Profile) ? name : $"{name}, profiles: {rule.Profile}";
    }

    private static string Excerpt(string value) => value.Length <= 200 ? value : value[..200] + "…";
}

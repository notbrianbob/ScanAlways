namespace ScanAlways.FirewallWatchdog.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    public static FirewallStatus Healthy(params FirewallRule[] rules) => new()
    {
        Profiles = new[]
        {
            new FirewallProfileState("Domain", true, "Block"),
            new FirewallProfileState("Private", true, "Block"),
            new FirewallProfileState("Public", true, "Block"),
        },
        ActiveProfiles = new[] { "Private" },
        InboundAllowRules = rules,
    };

    public static FirewallStatus With(this FirewallStatus status, string profile, bool? enabled = null, string? inbound = null) =>
        status with
        {
            Profiles = status.Profiles
                .Select(p => p.Name == profile
                    ? p with { Enabled = enabled ?? p.Enabled, DefaultInboundAction = inbound ?? p.DefaultInboundAction }
                    : p)
                .ToArray(),
        };

    public static FirewallRule Rule(string id, string? displayName = null) => new(id, displayName ?? id, "Any");
}

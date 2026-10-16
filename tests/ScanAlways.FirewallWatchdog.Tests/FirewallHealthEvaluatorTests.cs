using ScanAlways.Common.Alerts;
using Xunit;
using static ScanAlways.FirewallWatchdog.FirewallHealthEvaluator;

namespace ScanAlways.FirewallWatchdog.Tests;

public class FirewallHealthEvaluatorTests
{
    private static readonly FirewallWatchdogOptions Options = new();

    [Fact]
    public void AllProfilesOnAndBlocking_IsHealthy()
    {
        var report = Evaluate(TestData.Healthy(), Options, TestData.Now);

        Assert.Equal(FirewallHealth.Healthy, report.Health);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void DisabledActiveProfile_IsCritical()
    {
        var report = Evaluate(TestData.Healthy().With("Private", enabled: false), Options, TestData.Now);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ProfileDisabledPrefix + "PRIVATE", finding.Code);
        Assert.Equal(AlertSeverity.Critical, finding.Severity);
        Assert.Contains("in use now", finding.Message);
        Assert.Equal(FirewallHealth.Critical, report.Health);
    }

    [Fact]
    public void DisabledInactiveProfile_IsAWarning()
    {
        var report = Evaluate(TestData.Healthy().With("Domain", enabled: false), Options, TestData.Now);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ProfileDisabledPrefix + "DOMAIN", finding.Code);
        Assert.Equal(AlertSeverity.Warning, finding.Severity);
        Assert.Equal(FirewallHealth.Degraded, report.Health);
    }

    [Fact]
    public void UnknownActiveProfiles_DoNotDownplayAProblem()
    {
        var status = TestData.Healthy().With("Domain", enabled: false) with { ActiveProfiles = null };

        var finding = Assert.Single(Evaluate(status, Options, TestData.Now).Findings);

        Assert.Equal(AlertSeverity.Critical, finding.Severity);
        Assert.DoesNotContain("in use now", finding.Message);
    }

    [Fact]
    public void InboundAllowByDefault_IsReported()
    {
        var report = Evaluate(TestData.Healthy().With("Public", inbound: "Allow"), Options, TestData.Now);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(InboundDefaultAllowPrefix + "PUBLIC", finding.Code);
        Assert.Equal(AlertSeverity.Warning, finding.Severity);
    }

    [Fact]
    public void DisabledProfile_IsNotAlsoReportedForItsInboundDefault()
    {
        var status = TestData.Healthy().With("Private", enabled: false, inbound: "Allow");

        Assert.Equal(ProfileDisabledPrefix + "PRIVATE", Assert.Single(Evaluate(status, Options, TestData.Now).Findings).Code);
    }

    [Fact]
    public void UnknownFields_RaiseNothing()
    {
        var status = new FirewallStatus
        {
            Profiles = new[] { new FirewallProfileState("Public", null, null) },
            ActiveProfiles = new[] { "Public" },
        };

        Assert.Empty(Evaluate(status, Options, TestData.Now).Findings);
    }

    [Fact]
    public void UnavailableStatus_IsUnknown()
    {
        var report = Evaluate(FirewallStatus.Unavailable("boom"), Options, TestData.Now);

        Assert.Equal(FirewallHealth.Unknown, report.Health);
        Assert.Equal(StatusUnavailable, Assert.Single(report.Findings).Code);
    }

    [Fact]
    public void UnreadableRules_AreReportedOnlyWhenMonitoring()
    {
        var status = TestData.Healthy() with { InboundAllowRules = null, RulesError = "access denied" };

        Assert.Empty(Evaluate(status, Options, TestData.Now).Findings);

        var finding = Assert.Single(Evaluate(status, new FirewallWatchdogOptions { MonitorInboundAllowRules = true }, TestData.Now).Findings);
        Assert.Equal(RulesUnavailable, finding.Code);
        Assert.Contains("access denied", finding.Message);
    }

    [Fact]
    public void CompareProfiles_ReportsInboundDefaultChanges()
    {
        var previous = new Dictionary<string, FirewallProfileSnapshot>
        {
            ["Public"] = new(true, "Block"),
            ["Private"] = new(true, "Allow"),
            ["Domain"] = new(true, "Block"),
        };
        var current = TestData.Healthy().With("Public", inbound: "Allow").Profiles;

        var changes = CompareProfiles(previous, current);

        Assert.Equal(2, changes.Count);
        var toAllow = Assert.Single(changes, c => c.Code == InboundDefaultChangedPrefix + "PUBLIC");
        Assert.Equal(AlertSeverity.Warning, toAllow.Severity);
        Assert.Contains("from Block to Allow", toAllow.Message);
        var toBlock = Assert.Single(changes, c => c.Code == InboundDefaultChangedPrefix + "PRIVATE");
        Assert.Equal(AlertSeverity.Info, toBlock.Severity);
    }

    [Fact]
    public void CompareProfiles_IgnoresUnknownAndMissingValues()
    {
        var previous = new Dictionary<string, FirewallProfileSnapshot> { ["Public"] = new(true, null) };
        var current = TestData.Healthy().With("Public", inbound: "Allow").Profiles;

        Assert.Empty(CompareProfiles(previous, current));
    }

    [Fact]
    public void CompareRules_ReportsOnlyNewNonIgnoredRules()
    {
        var options = new FirewallWatchdogOptions { IgnoredInboundRules = new[] { "Steam*" } };
        var current = new[]
        {
            TestData.Rule("CoreNet-1"),
            TestData.Rule("{A1B2}", "Steam Web Helper"),
            TestData.Rule("TCP-Query-User-{9}", "evil.exe"),
        };

        var change = Assert.Single(CompareRules(new[] { "corenet-1" }, current, options));

        Assert.Equal(RuleAdded, change.Code);
        Assert.Equal(AlertSeverity.Warning, change.Severity);
        Assert.Equal("TCP-Query-User-{9}", change.Resource);
        Assert.Contains("evil.exe", change.Message);
    }

    [Fact]
    public void CompareRules_SummarizesFloods()
    {
        var current = Enumerable.Range(0, 25).Select(i => TestData.Rule($"rule-{i}")).ToList();

        var changes = CompareRules(Array.Empty<string>(), current, new FirewallWatchdogOptions { MaxRuleAlertsPerCheck = 3 });

        Assert.Equal(4, changes.Count);
        Assert.Equal(3, changes.Count(c => c.Code == RuleAdded));
        Assert.Contains("22 more", Assert.Single(changes, c => c.Code == RulesAddedSummary).Message);
    }
}

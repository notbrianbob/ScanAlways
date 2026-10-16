using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScanAlways.Common.Alerts;
using Xunit;
using static ScanAlways.FirewallWatchdog.FirewallHealthEvaluator;

namespace ScanAlways.FirewallWatchdog.Tests;

public class FirewallWatchdogTests : IDisposable
{
    private readonly string _folder = Path.Join(Path.GetTempPath(), $"scanalways-firewall-{Guid.NewGuid():N}");
    private readonly FakeProvider _provider = new();

    private FirewallWatchdogOptions Options(bool monitorRules = false) => new()
    {
        MonitorInboundAllowRules = monitorRules,
        StatePath = Path.Join(_folder, "state.json"),
        AlertLogPath = Path.Join(_folder, "firewall.log"),
    };

    private FirewallWatchdog Create(FirewallWatchdogOptions? options = null, IAlertPublisher? alerts = null) => new(
        _provider,
        Microsoft.Extensions.Options.Options.Create(options ?? Options()),
        NullLogger<FirewallWatchdog>.Instance,
        () => TestData.Now,
        alerts);

    [Fact]
    public async Task HealthyChecks_RaiseNothing()
    {
        using var watchdog = Create();
        var raised = new List<FirewallFinding>();
        var changes = new List<FirewallChange>();
        watchdog.AlertRaised += raised.Add;
        watchdog.ChangeDetected += changes.Add;

        await watchdog.CheckOnceAsync(default);
        await watchdog.CheckOnceAsync(default);

        Assert.Empty(raised);
        Assert.Empty(changes);
        Assert.Equal(FirewallHealth.Healthy, watchdog.LastReport?.Health);
    }

    [Fact]
    public async Task DisabledProfile_AlertsOnce_ThenRecoveryIsReported_InSharedAlerts()
    {
        var shared = new AlertHub(Microsoft.Extensions.Options.Options.Create(new AlertDeliveryOptions()));
        using var watchdog = Create(alerts: shared);
        var raised = new List<FirewallFinding>();
        var cleared = new List<FirewallFinding>();
        watchdog.AlertRaised += raised.Add;
        watchdog.AlertCleared += cleared.Add;

        await watchdog.CheckOnceAsync(default);
        _provider.Status = TestData.Healthy().With("Private", enabled: false);
        await watchdog.CheckOnceAsync(default);
        await watchdog.CheckOnceAsync(default);

        Assert.Equal(ProfileDisabledPrefix + "PRIVATE", Assert.Single(raised).Code);
        var alert = Assert.Single(shared.Snapshot().Alerts);
        Assert.Equal("FirewallWatchdog", alert.Module);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);

        _provider.Status = TestData.Healthy();
        await watchdog.CheckOnceAsync(default);

        Assert.Equal(ProfileDisabledPrefix + "PRIVATE", Assert.Single(cleared).Code);
        Assert.Contains(shared.Snapshot().Alerts,
            a => a.Code == ProfileDisabledPrefix + "PRIVATE_CLEARED" && a.Severity == AlertSeverity.Info);
    }

    [Fact]
    public async Task InboundDefaultChange_IsReported_AndSurvivesARestart()
    {
        using (var first = Create())
        {
            await first.CheckOnceAsync(default);
        }

        // The policy changes while the hub is stopped.
        _provider.Status = TestData.Healthy().With("Public", inbound: "Allow");
        using var second = Create();
        var changes = new List<FirewallChange>();
        var raised = new List<FirewallFinding>();
        second.ChangeDetected += changes.Add;
        second.AlertRaised += raised.Add;

        await second.CheckOnceAsync(default);
        await second.CheckOnceAsync(default);

        var change = Assert.Single(changes);
        Assert.Equal(InboundDefaultChangedPrefix + "PUBLIC", change.Code);
        Assert.Equal(AlertSeverity.Warning, change.Severity);
        Assert.Equal(InboundDefaultAllowPrefix + "PUBLIC", Assert.Single(raised).Code);
    }

    [Fact]
    public async Task RuleMonitoring_IsOffByDefault()
    {
        _provider.Status = TestData.Healthy(TestData.Rule("a"));
        using var watchdog = Create();
        var changes = new List<FirewallChange>();
        watchdog.ChangeDetected += changes.Add;

        await watchdog.CheckOnceAsync(default);
        _provider.Status = TestData.Healthy(TestData.Rule("a"), TestData.Rule("b"));
        await watchdog.CheckOnceAsync(default);

        Assert.Empty(changes);
        Assert.All(_provider.Requests, includeRules => Assert.False(includeRules));
    }

    [Fact]
    public async Task RuleMonitoring_RecordsABaseline_ThenReportsNewRules()
    {
        _provider.Status = TestData.Healthy(TestData.Rule("a"));
        using var watchdog = Create(Options(monitorRules: true));
        var changes = new List<FirewallChange>();
        watchdog.ChangeDetected += changes.Add;

        await watchdog.CheckOnceAsync(default);
        Assert.Equal(RuleBaselineCreated, Assert.Single(changes).Code);

        _provider.Status = TestData.Healthy(TestData.Rule("a"), TestData.Rule("b", "Backdoor listener"));
        await watchdog.CheckOnceAsync(default);
        await watchdog.CheckOnceAsync(default);

        Assert.Equal(2, changes.Count);
        Assert.Equal(RuleAdded, changes[1].Code);
        Assert.Equal("b", changes[1].Resource);
        Assert.True(Assert.Single(_provider.Requests.Distinct()));
    }

    [Fact]
    public async Task ProviderException_IsReportedAsUnknown()
    {
        _provider.Throw = new InvalidOperationException("CIM exploded");
        using var watchdog = Create();
        var raised = new List<FirewallFinding>();
        watchdog.AlertRaised += raised.Add;

        var report = await watchdog.CheckOnceAsync(default);

        Assert.Equal(FirewallHealth.Unknown, report.Health);
        Assert.Equal(StatusUnavailable, Assert.Single(raised).Code);
    }

    [Fact]
    public async Task CorruptState_IsReportedOnce_AndWatchingContinues()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Join(_folder, "state.json"), "{ not json");
        using var watchdog = Create();
        var changes = new List<FirewallChange>();
        watchdog.ChangeDetected += changes.Add;

        await watchdog.CheckOnceAsync(default);
        await watchdog.CheckOnceAsync(default);

        Assert.Equal(FirewallWatchdog.StateUnreadable, Assert.Single(changes).Code);
        Assert.Equal(FirewallHealth.Healthy, watchdog.LastReport?.Health);
    }

    [Fact]
    public async Task RunAsync_StopsCleanlyOnCancellation()
    {
        using var watchdog = Create();
        using var cts = new CancellationTokenSource();
        var checkedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watchdog.Checked += _ => checkedOnce.TrySetResult();

        var run = watchdog.RunAsync(cts.Token);
        await checkedOnce.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(run.IsCompletedSuccessfully);
    }

    public void Dispose()
    {
        // Every watchdog is disposed by the end of its test, so nothing holds these files open.
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private sealed class FakeProvider : IFirewallStatusProvider
    {
        public FirewallStatus Status { get; set; } = TestData.Healthy();
        public Exception? Throw { get; set; }
        public List<bool> Requests { get; } = new();

        public Task<FirewallStatus> GetStatusAsync(bool includeInboundAllowRules, CancellationToken cancellationToken)
        {
            Requests.Add(includeInboundAllowRules);
            if (Throw is not null)
            {
                return Task.FromException<FirewallStatus>(Throw);
            }
            // Like the real provider, rules are only returned when asked for.
            return Task.FromResult(includeInboundAllowRules ? Status : Status with { InboundAllowRules = null });
        }
    }
}

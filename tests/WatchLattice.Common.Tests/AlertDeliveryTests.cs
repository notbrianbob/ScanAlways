using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WatchLattice.Common.Alerts;
using WatchLattice.Common.Modules;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace WatchLattice.Common.Tests;

public sealed class AlertDeliveryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "watchlattice-alerts-" + Guid.NewGuid().ToString("N"));
    private readonly AlertDeliveryOptions _options;

    public AlertDeliveryTests()
    {
        Directory.CreateDirectory(_folder);
        _options = new() { FeedPath = Path.Combine(_folder, "alerts.json"), HistoryLimit = 3 };
    }

    private AlertHub Hub() => new(Options.Create(_options));
    private AlertDeliveryService Service(AlertHub hub) => new(hub, Options.Create(_options), NullLogger<AlertDeliveryService>.Instance);

    [Fact]
    public void History_IsBounded_DeduplicatesBursts_AndKeepsRecovery()
    {
        var hub = Hub();
        hub.Publish("Module", "Problem", AlertSeverity.Warning, "Problem", "details");
        hub.Publish("Module", "Problem", AlertSeverity.Warning, "Problem", "details");
        Assert.Single(hub.Snapshot().Alerts);
        hub.Publish("Module", "Recovered", AlertSeverity.Info, "Recovered", "details");
        hub.Publish("Module", "Other", AlertSeverity.Critical, "Other", "other");
        hub.Publish("Module", "New", AlertSeverity.Warning, "New", "new");
        Assert.Equal(new[] { "Recovered", "Other", "New" }, hub.Snapshot().Alerts.Select(a => a.Code));
    }

    [Fact]
    public async Task Feed_PreservesHistoryAcrossRestart_AndMarksCleanShutdownOffline()
    {
        var hub = Hub();
        hub.Publish("Canary", "TRIPPED", AlertSeverity.Critical, "Canary", "changed");
        hub.SetModuleState("Canary", ModuleRunState.Running);
        using (var service = Service(hub))
        {
            await service.StartAsync(default);
            var snapshot = AlertSnapshot.Read(_options.FeedPath!);
            Assert.True(snapshot.IsFresh(DateTimeOffset.UtcNow));
            Assert.Equal(ModuleRunState.Running, Assert.Single(snapshot.Modules).State);
            await service.StopAsync(default);
        }
        var stopped = AlertSnapshot.Read(_options.FeedPath!);
        Assert.False(stopped.IsFresh(DateTimeOffset.UtcNow));
        var nextHub = Hub();
        using var nextService = Service(nextHub);
        await nextService.StartAsync(default);
        var restarted = AlertSnapshot.Read(_options.FeedPath!);
        Assert.NotEqual(stopped.SessionId, restarted.SessionId);
        Assert.Equal(Assert.Single(stopped.Alerts).Id, Assert.Single(restarted.Alerts).Id);
        Assert.Empty(restarted.Modules);
        await nextService.StopAsync(default);
    }

    [Fact]
    public async Task CorruptFeed_ProducesVisibleHistoryWarning_WithoutStoppingDelivery()
    {
        File.WriteAllText(_options.FeedPath!, "{}");
        var hub = Hub();
        using var service = Service(hub);
        await service.StartAsync(default);
        Assert.Contains(AlertSnapshot.Read(_options.FeedPath!).Alerts, a => a.Code == "ALERT_HISTORY_UNAVAILABLE");
        await service.StopAsync(default);
    }

    [Fact]
    public async Task UnwritableFeed_DoesNotStopMonitoring_AndDeliveryRecovers()
    {
        Directory.CreateDirectory(_options.FeedPath!);
        var hub = Hub();
        using var service = Service(hub);
        await service.StartAsync(default);
        hub.Publish("Canary", "TRIPPED", AlertSeverity.Critical, "Canary", "changed");
        Assert.Single(hub.Snapshot().Alerts, a => a.Module == "Canary");
        Directory.Delete(_options.FeedPath!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!File.Exists(_options.FeedPath!)) await Task.Delay(50, timeout.Token);
        Assert.Contains(AlertSnapshot.Read(_options.FeedPath!).Alerts, a => a.Module == "Canary");
        await service.StopAsync(default);
    }

    [Fact]
    public async Task IdleDelivery_RefreshesSmallHeartbeat_WithoutRewritingHistory()
    {
        using var service = Service(Hub());
        await service.StartAsync(default);
        var path = _options.FeedPath!;
        var initial = AtomicJsonFile.Read<AlertSnapshot>(path);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (AlertSnapshot.Read(path).HeartbeatAt <= initial.HeartbeatAt)
            await Task.Delay(50, timeout.Token);
        Assert.Equal(initial.HeartbeatAt, AtomicJsonFile.Read<AlertSnapshot>(path).HeartbeatAt);
        Assert.True(AlertSnapshot.Read(path).IsFresh(DateTimeOffset.UtcNow));
        await service.StopAsync(default);
    }

    [Fact]
    public async Task DisabledDelivery_DoesNotCreateFeed()
    {
        _options.Enabled = false;
        using var service = Service(Hub());
        await service.StartAsync(default);
        await service.StopAsync(default);
        Assert.False(File.Exists(_options.FeedPath!));
    }

    [Fact]
    public void NotificationCursor_SuppressesInitialHistory_AndNotifiesNewWarningsOnceAcrossSessions()
    {
        var hub = Hub();
        hub.Publish("Old", "Old", AlertSeverity.Warning, "Old", "old");
        var cursor = new AlertNotificationCursor();
        Assert.Empty(cursor.Observe(hub.Snapshot()));
        hub.Publish("New", "New", AlertSeverity.Critical, "New", "new");
        hub.Publish("Recovered", "Recovered", AlertSeverity.Info, "Recovered", "recovered");
        Assert.Equal("New", Assert.Single(cursor.Observe(hub.Snapshot())).Code);
        Assert.Empty(cursor.Observe(hub.Snapshot()));
        var restarted = Hub();
        restarted.RestoreHistory(hub.Snapshot().Alerts);
        restarted.Publish("AfterRestart", "AfterRestart", AlertSeverity.Warning, "New", "new after restart");
        Assert.Equal("AfterRestart", Assert.Single(cursor.Observe(restarted.Snapshot())).Code);
    }

    [Fact]
    public void Freshness_RejectsStaleAndFutureHeartbeats()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Hub().Snapshot();
        Assert.False((snapshot with { HeartbeatAt = now.AddMinutes(-1) }).IsFresh(now));
        Assert.False((snapshot with { HeartbeatAt = now.AddMinutes(1) }).IsFresh(now));
        Assert.False((snapshot with { Online = false }).IsFresh(now));
    }

    [Fact]
    public async Task Supervisor_ReportsFailedAndFinishedModules_ToSharedFeed()
    {
        var alerts = Hub();
        var failed = new FakeModule("Failed", run: _ => throw new InvalidOperationException("boom"));
        var finished = new FakeModule("Finished", run: _ => Task.CompletedTask);
        using var services = new ServiceCollection().BuildServiceProvider();
        var supervisor = new ModuleSupervisor(new EnabledModules(new[] { failed, finished }), services,
            Options.Create(new HubOptions { RestartFailedModules = false }), NullLogger<ModuleSupervisor>.Instance, alerts);
        await supervisor.RunAllAsync(default);
        var snapshot = alerts.Snapshot();
        Assert.Contains(snapshot.Alerts, a => a.Code == "MODULE_FAILED" && a.Module == "Failed");
        Assert.Contains(snapshot.Alerts, a => a.Code == "MODULE_FINISHED" && a.Module == "Finished");
        Assert.Contains(snapshot.Modules, m => m.Id == "Failed" && m.State == ModuleRunState.Failed);
        Assert.Contains(snapshot.Modules, m => m.Id == "Finished" && m.State == ModuleRunState.Finished);
    }

    [Fact]
    public async Task AtomicFeed_CanBeReadDuringReplacement()
    {
        var path = _options.FeedPath!;
        var snapshot = Hub().Snapshot();
        AtomicJsonFile.Write(path, snapshot);
        var writer = Task.Run(() => { for (var i = 0; i < 30; i++) AtomicJsonFile.Write(path, snapshot); });
        for (var i = 0; i < 30; i++) Assert.Equal(snapshot.SessionId, AlertSnapshot.Read(path).SessionId);
        await writer;
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}

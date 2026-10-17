using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WatchLattice.Common.Alerts;
using WatchLattice.Common.Modules;
using WatchLattice.Persistence.Verification;
using Xunit;
using static WatchLattice.Persistence.Tests.Verification.TaskScenarios;

namespace WatchLattice.Persistence.Tests.Verification;

public sealed class VerificationCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "watchlattice-verify-" + Guid.NewGuid().ToString("N"));
    private readonly TaskScenarios _machine = new();
    private readonly PersistenceOptions _options = new();

    public VerificationCoordinatorTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string StorePath => Path.Combine(_directory, "alerts.json" + VerificationStore.FileSuffix);

    private (PersistenceVerificationCoordinator Coordinator, AlertHub Hub) Create()
    {
        var store = new VerificationStore(StorePath);
        PersistenceVerificationCoordinator? coordinator = null;
        var hub = new AlertHub(Options.Create(new AlertDeliveryOptions()), new IAlertSink[] { new Forward(() => coordinator!) });
        coordinator = new PersistenceVerificationCoordinator(_machine.Engine(), store, Options.Create(_options),
            NullLogger<PersistenceVerificationCoordinator>.Instance) { Alerts = hub };
        return (coordinator, hub);
    }

    /// <summary>Raises an alert the way the auditor does: evidence staged first, then published.</summary>
    private static void RaiseAlert(PersistenceVerificationCoordinator coordinator, IAlertPublisher hub, PersistenceObservation observation, string code = "ENTRY_CHANGED")
    {
        coordinator.Stage(observation.Resource, observation);
        hub.Publish("Persistence", code, AlertSeverity.Warning, "Autostart entry changed", "excerpt only", observation.Resource);
    }

    // The hub hands alerts to the coordinator synchronously, so everything raised so far is already queued.
    private static Task DrainAsync(PersistenceVerificationCoordinator coordinator) =>
        coordinator.ProcessQueuedAsync(CancellationToken.None);

    [Fact]
    public async Task NewPersistenceAlert_IsVerifiedAutomatically_StoredWithTheAlert_AndLogged()
    {
        var (coordinator, hub) = Create();
        RaiseAlert(coordinator, hub, TriggerTimeChanged());
        await DrainAsync(coordinator);

        var alert = hub.Snapshot().Alerts.First(a => a.Code == "ENTRY_CHANGED");
        var record = Assert.Single(VerificationStore.Read(StorePath));
        Assert.Equal(alert.Id, record.AlertId);
        Assert.Equal(alert.Resource, record.Resource);
        // Historical evidence is the full logged definition, not the alert's excerpt.
        Assert.Equal(TriggerTimeChanged().Before, record.Original.Before);
        var run = Assert.Single(record.Runs);
        Assert.Equal(VerificationTrigger.Automatic, run.Trigger);
        Assert.Equal(VerificationOutcome.ChecksPassed, run.Outcome);

        var outcome = hub.Snapshot().Alerts.Single(a => a.Code == PersistenceVerificationCoordinator.PassedCode);
        Assert.Equal(AlertSeverity.Info, outcome.Severity);
        Assert.Equal(alert.Resource, outcome.Resource);
        Assert.Contains(alert.Id.ToString(), outcome.Message);
        Assert.Contains("not proof", outcome.Message);
    }

    [Fact]
    public async Task SuspiciousOutcome_RaisesACriticalAlert()
    {
        _machine.Files.Add(Dll, new SignatureEvidence(SignatureState.Invalid, SignatureKind.Catalog, Problem: "bad"));
        var (coordinator, hub) = Create();
        RaiseAlert(coordinator, hub, TriggerTimeChanged());
        await DrainAsync(coordinator);

        var outcome = hub.Snapshot().Alerts.Single(a => a.Code == PersistenceVerificationCoordinator.SuspiciousCode);
        Assert.Equal(AlertSeverity.Critical, outcome.Severity);
    }

    [Fact]
    public async Task WithAutomaticVerificationOff_TheEvidenceIsStillStored_ForAManualCheckLater()
    {
        _options.AutoVerifyAlerts = false;
        var (coordinator, hub) = Create();
        RaiseAlert(coordinator, hub, TriggerTimeChanged());
        await DrainAsync(coordinator);

        var record = Assert.Single(VerificationStore.Read(StorePath));
        Assert.Empty(record.Runs);
        Assert.DoesNotContain(hub.Snapshot().Alerts, a => a.Code.StartsWith("VERIFY_", StringComparison.Ordinal));
        Assert.Empty(_machine.Tasks.Reads);

        // A manual re-verification uses the stored evidence.
        var manual = await _machine.Engine().VerifyAsync(record.Original, VerificationTrigger.Manual, default);
        Assert.Equal(VerificationOutcome.ChecksPassed, manual.Outcome);
        Assert.Single(manual.LoggedChanges);
    }

    [Fact]
    public async Task OtherAlerts_AreNotRecorded()
    {
        var (coordinator, hub) = Create();
        var registry = new PersistenceObservation("Registry/HKLM/Registry64/Run", "App", PersistenceChangeKind.Added, null, "REG_SZ: app.exe", DateTimeOffset.UtcNow);
        RaiseAlert(coordinator, hub, registry, "ENTRY_ADDED");
        hub.Publish("Persistence", "ENTRY_REMOVED", AlertSeverity.Info, "removed", "x", TriggerTimeChanged().Resource);
        hub.Publish("Canary", "ENTRY_CHANGED", AlertSeverity.Warning, "spoof", "x", TriggerTimeChanged().Resource);
        await DrainAsync(coordinator);

        Assert.False(File.Exists(StorePath));
        Assert.Empty(coordinator.Store.Records);
    }

    [Fact]
    public async Task ReVerification_AppendsRuns_AndKeepsTheOriginalEvidence()
    {
        var (coordinator, hub) = Create();
        RaiseAlert(coordinator, hub, TriggerTimeChanged());
        await DrainAsync(coordinator);
        var record = coordinator.Store.Records.Single();

        for (var i = 0; i < VerificationStore.MaxRunsPerRecord + 2; i++)
            coordinator.Store.AddRun(record.AlertId, await _machine.Engine().VerifyAsync(record.Original, VerificationTrigger.Manual, default));
        coordinator.Store.Save();

        var saved = VerificationStore.Read(StorePath).Single();
        Assert.Equal(VerificationStore.MaxRunsPerRecord, saved.Runs.Count);
        Assert.All(saved.Runs, r => Assert.Equal(VerificationTrigger.Manual, r.Trigger));
        Assert.Equal(record.Original, saved.Original);
    }

    [Fact]
    public void UnreadableFindings_AreMovedAside_NotOverwritten()
    {
        File.WriteAllText(StorePath, "{ not json");
        var store = new VerificationStore(StorePath);

        var problem = store.Load();

        Assert.NotNull(problem);
        Assert.False(File.Exists(StorePath));
        Assert.Single(Directory.GetFiles(_directory, "*.unreadable-*"));
    }

    [Fact]
    public void Store_IsBounded()
    {
        var store = new VerificationStore(StorePath);
        for (var i = 0; i < VerificationStore.MaxRecords + 10; i++)
            store.Add(new VerificationRecord(Guid.NewGuid(), "ENTRY_ADDED", DateTimeOffset.UtcNow, "r", TriggerTimeChanged(), Array.Empty<VerificationRun>()));
        Assert.Equal(VerificationStore.MaxRecords, store.Records.Count);

        var huge = TriggerTimeChanged() with { After = new string('x', VerificationStore.MaxEvidenceChars * 2) };
        store.Add(new VerificationRecord(Guid.NewGuid(), "ENTRY_ADDED", DateTimeOffset.UtcNow, "r", huge, Array.Empty<VerificationRun>()));
        Assert.EndsWith("(truncated)", store.Records[^1].Original.After);
    }

    [Fact]
    public async Task ModuleWiring_HasNoDependencyCycle_AndTheAuditorFeedsTheVerifier()
    {
        var feed = Path.Combine(_directory, "alerts.json");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Alerts:FeedPath"] = feed,
            ["Persistence:BaselinePath"] = Path.Combine(_directory, "baseline.json"),
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IPersistenceProvider>(new StubProvider());
        services.AddSingleton(_machine.Engine());
        services.AddWatchLatticeHub(configuration, new ModuleCatalog(new IWatchLatticeModule[] { new PersistenceModule() }));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var hub = provider.GetRequiredService<AlertHub>();
        var coordinator = provider.GetRequiredService<PersistenceVerificationCoordinator>();
        Assert.Contains(provider.GetServices<IAlertSink>(), s => ReferenceEquals(s, coordinator));
        Assert.Equal(StorePath, coordinator.Store.FilePath);

        var auditor = provider.GetRequiredService<PersistenceAuditor>();
        StubProvider.After = ComHandlerProjection(Clsid, "2026-10-17T03:12:44Z");
        await auditor.CheckOnceAsync(default);
        StubProvider.After = ComHandlerProjection(Clsid, "2026-10-18T03:12:44Z");
        await auditor.CheckOnceAsync(default);
        await DrainAsync(coordinator);

        var record = Assert.Single(coordinator.Store.Records);
        Assert.Equal(hub.Snapshot().Alerts.Single(a => a.Code == "ENTRY_CHANGED").Id, record.AlertId);
        Assert.Equal(StubProvider.After, record.Original.After);
        Assert.Contains(hub.Snapshot().Alerts, a => a.Code == PersistenceVerificationCoordinator.PassedCode);
    }

    private sealed class StubProvider : IPersistenceProvider
    {
        public static string After { get; set; } = "{}";

        public Task<IReadOnlyList<PersistenceSource>> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PersistenceSource>>(new[] { new PersistenceSource(Source, new[] { new PersistenceEntry(TaskPath, After) }) });
    }

    private sealed class Forward : IAlertSink
    {
        private readonly Func<IAlertSink> _target;
        public Forward(Func<IAlertSink> target) => _target = target;
        public void Accept(ProtectionAlert alert) => _target().Accept(alert);
    }
}

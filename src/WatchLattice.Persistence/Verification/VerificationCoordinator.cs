using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WatchLattice.Common.Alerts;

namespace WatchLattice.Persistence.Verification;

/// <summary>Receives the full evidence for a change just before the auditor raises its alert.</summary>
public interface IChangeEvidenceSink
{
    void Stage(string resource, PersistenceObservation observation);
}

/// <summary>
/// Links persistence alerts to their evidence, stores a verification record
/// for every alert a verifier applies to, and, when automatic verification is
/// on, verifies the entry in the background and raises a VERIFY_* alert with
/// the outcome. Never remediates anything.
/// </summary>
public sealed class PersistenceVerificationCoordinator : IAlertSink, IChangeEvidenceSink
{
    public const string Module = "Persistence";
    public const string PassedCode = "VERIFY_PASSED";
    public const string NeedsReviewCode = "VERIFY_NEEDS_REVIEW";
    public const string SuspiciousCode = "VERIFY_SUSPICIOUS";
    public const string StoreFailedCode = "VERIFICATION_STORE_FAILED";
    public const int QueueCapacity = 256;
    private const int MaxStaged = 512;

    private readonly PersistenceVerificationEngine _engine;
    private readonly VerificationStore _store;
    private readonly PersistenceOptions _options;
    private readonly ILogger<PersistenceVerificationCoordinator> _logger;
    private readonly IServiceProvider? _services;
    private readonly ConcurrentDictionary<string, PersistenceObservation> _staged = new(StringComparer.Ordinal);
    private readonly Channel<VerificationRecord> _queue;
    private IAlertPublisher? _alerts;
    private bool _storeFailed;

    public PersistenceVerificationCoordinator(PersistenceVerificationEngine engine, VerificationStore store,
        IOptions<PersistenceOptions> options, ILogger<PersistenceVerificationCoordinator> logger, IServiceProvider? services = null)
    {
        _engine = engine;
        _store = store;
        _options = options.Value;
        _logger = logger;
        // Resolved lazily: the alert hub depends on this sink, so asking for it here would be a cycle.
        _services = services;
        _queue = Channel.CreateBounded<VerificationRecord>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        }, dropped => _logger.LogWarning("Verification queue full; alert {AlertId} was not recorded for verification.", dropped.AlertId));
    }

    /// <summary>For tests: publish outcomes here instead of resolving the hub's publisher.</summary>
    public IAlertPublisher? Alerts { get => _alerts; init => _alerts = value; }

    public VerificationStore Store => _store;

    public static bool IsEntryAlert(string code) => code is "ENTRY_ADDED" or "ENTRY_CHANGED";

    public void Stage(string resource, PersistenceObservation observation)
    {
        if (!_engine.CanVerify(observation)) return;
        if (_staged.Count >= MaxStaged) _staged.Clear();
        _staged[ClipResource(resource)] = observation;
    }

    /// <summary>Called by the alert hub for every new alert. Only queues work; never blocks or throws.</summary>
    public void Accept(ProtectionAlert alert)
    {
        if (alert.Module != Module || !IsEntryAlert(alert.Code) || alert.Resource is null) return;
        if (!_staged.TryRemove(alert.Resource, out var observation)) return;
        _queue.Writer.TryWrite(new VerificationRecord(alert.Id, alert.Code, alert.At, alert.Resource, observation,
            Array.Empty<VerificationRun>()));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        LoadStore();
        try
        {
            await foreach (var record in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                await ProcessAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>For tests: loads the store like <see cref="RunAsync"/>, processes every queued record to completion, then returns.</summary>
    public async Task ProcessQueuedAsync(CancellationToken cancellationToken)
    {
        LoadStore();
        while (_queue.Reader.TryRead(out var record))
            await ProcessAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private void LoadStore()
    {
        var problem = _store.Load();
        if (problem is not null) Publish(StoreFailedCode, AlertSeverity.Warning, "Verification findings need attention", problem, _store.FilePath);
    }

    /// <summary>Stores the alert's record and, if enabled, verifies it. Public for tests.</summary>
    public async Task ProcessAsync(VerificationRecord record, CancellationToken cancellationToken)
    {
        _store.Add(record);
        if (_options.AutoVerifyAlerts)
        {
            // Verify with the untrimmed evidence held in memory, not the stored copy.
            var run = await _engine.VerifyAsync(record.Original, VerificationTrigger.Automatic, cancellationToken).ConfigureAwait(false);
            _store.AddRun(record.AlertId, run);
            var (code, severity, title) = run.Outcome switch
            {
                VerificationOutcome.ChecksPassed => (PassedCode, AlertSeverity.Info, "Persistence entry verified: checks passed"),
                VerificationOutcome.NeedsReview => (NeedsReviewCode, AlertSeverity.Warning, "Persistence entry needs review"),
                _ => (SuspiciousCode, AlertSeverity.Critical, "Persistence entry looks suspicious"),
            };
            Publish(code, severity, title, $"{record.Original.EntryId}\n{run.Summary}\nVerifies alert {record.AlertId}.", record.Resource);
            _logger.Log(severity >= AlertSeverity.Warning ? LogLevel.Warning : LogLevel.Information,
                "Verified {Resource}: {Outcome}", record.Resource, run.Outcome);
        }
        Save();
    }

    private void Save()
    {
        try
        {
            _store.Save();
            if (_storeFailed) _logger.LogInformation("Verification findings can be saved again.");
            _storeFailed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (!_storeFailed)
                Publish(StoreFailedCode, AlertSeverity.Warning, "Verification findings need attention",
                    "Verification findings could not be saved and are kept in memory only: " + ex.Message, _store.FilePath);
            _storeFailed = true;
        }
    }

    private void Publish(string code, AlertSeverity severity, string title, string message, string resource)
    {
        _alerts ??= _services?.GetService<IAlertPublisher>();
        _alerts?.Publish(Module, code, severity, title, message, resource);
    }

    /// <summary>The alert hub clips resources to 512 characters; staged keys must match what it delivers.</summary>
    private static string ClipResource(string resource) => resource.Length <= 512 ? resource : resource[..512] + "…";
}

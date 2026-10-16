using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WatchLattice.Common;
using WatchLattice.Common.Alerts;

namespace WatchLattice.Canary;

/// <summary>
/// The canary tripwire engine.
///
/// It plants decoy files in the configured folders, then watches them with
/// <see cref="FileSystemWatcher"/> (filtered to the canary names only, so it
/// costs almost nothing) and re-hashes them on a timer as a backstop. Nothing
/// legitimate should ever modify, rename, or delete a canary, so any such
/// change raises an alert: a line in the rolling alert log, a critical log
/// entry (which lands in the Windows Event Log when running as a service), and
/// the <see cref="AlertRaised"/> event for a UI host.
///
/// It never touches real user files: it only writes files it created, and it
/// does not try to stop or quarantine anything. Detection only.
/// </summary>
public sealed class CanaryWatcher : IDisposable
{
    private readonly CanaryOptions _options;
    private readonly ILogger<CanaryWatcher> _logger;
    private readonly IAlertPublisher? _alerts;
    private readonly CanaryDeployer _deployer;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _tripped = new(CanaryPaths.Comparer);
    private readonly SemaphoreSlim _checkNow = new(0);
    private readonly object _gate = new();
    private CanaryTripwire? _tripwire;
    private ScanLogWriter? _alertLog;

    public CanaryWatcher(IOptions<CanaryOptions> options, ILogger<CanaryWatcher> logger, IAlertPublisher? alerts = null)
    {
        _options = options.Value;
        _logger = logger;
        _alerts = alerts;
        _deployer = new CanaryDeployer(_options, logger);
    }

    /// <summary>Raised once per tripped canary (debounced).</summary>
    public event Action<CanaryAlert>? AlertRaised;

    /// <summary>Raised on every status line, so a UI host can show live state.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Canaries currently armed.</summary>
    public IReadOnlyCollection<string> ActiveCanaries =>
        (IReadOnlyCollection<string>?)_tripwire?.Canaries ?? Array.Empty<string>();

    /// <summary>Plants, arms, and watches until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Arm();
        if (ActiveCanaries.Count == 0)
        {
            _logger.LogWarning("No canaries could be planted; the watcher has nothing to guard.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(10, _options.IntegrityCheckSeconds));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Wake on the timer, or early when a watcher reports an overflow.
                await _checkNow.WaitAsync(interval, cancellationToken);
                CheckIntegrityNow();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            Disarm();
        }
    }

    /// <summary>Plants the canaries and starts the file watchers.</summary>
    public void Arm()
    {
        _alertLog ??= TryOpenAlertLog();

        var deployment = _deployer.Deploy();
        _tripwire = new CanaryTripwire(
            deployment.Active, TimeSpan.FromSeconds(Math.Max(0, _options.AlertDebounceSeconds)));

        foreach (var alert in deployment.TamperedWhileOffline)
        {
            Raise(alert);
        }

        // Watch only after planting, so our own writes are never reported.
        foreach (var group in deployment.Active.GroupBy(p => Path.GetDirectoryName(p)!, CanaryPaths.Comparer))
        {
            StartWatcher(group.Key, group.Select(p => Path.GetFileName(p)!));
        }

        Emit($"Canary watcher armed: {deployment.Active.Count} canaries in {_watchers.Count} folders" +
             (deployment.SkippedForeignFiles.Count > 0
                 ? $", {deployment.SkippedForeignFiles.Count} skipped (name already taken by a real file)"
                 : ""));
    }

    /// <summary>
    /// Re-hashes every canary that has not already tripped. Catches anything
    /// the file watcher missed.
    /// </summary>
    public void CheckIntegrityNow()
    {
        var tripwire = _tripwire;
        if (tripwire is null)
        {
            return;
        }

        foreach (var path in tripwire.Canaries)
        {
            // Serialized with watcher callbacks, so a change is reported once,
            // by whichever path sees it first.
            lock (_gate)
            {
                if (_tripped.Contains(path))
                {
                    continue;
                }

                var state = _deployer.Verify(path);
                if (state is CanaryState.Intact or CanaryState.Unreadable)
                {
                    continue;
                }

                var alert = tripwire.Report(
                    path,
                    state == CanaryState.Missing ? CanaryChange.Deleted : CanaryChange.Modified,
                    "found by integrity check");
                if (alert is not null)
                {
                    Raise(alert);
                }
            }
        }
    }

    /// <summary>Stops watching and, if configured, removes intact canaries.</summary>
    public void Disarm()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();

        if (_options.RemoveCanariesOnStop)
        {
            var removed = _deployer.RemoveAll();
            _logger.LogInformation("Removed {Count} intact canaries on stop.", removed);
        }

        _tripwire = null;
        _logger.LogInformation("Canary watcher disarmed.");
    }

    private void StartWatcher(string folder, IEnumerable<string> names)
    {
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                // Attributes and security are left out on purpose: sync clients
                // such as OneDrive flip attributes on files they manage.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            foreach (var name in names)
            {
                watcher.Filters.Add(name);
            }

            watcher.Changed += (_, e) => OnEvent(e.ChangeType, e.FullPath, null);
            watcher.Created += (_, e) => OnEvent(e.ChangeType, e.FullPath, null);
            watcher.Deleted += (_, e) => OnEvent(e.ChangeType, e.FullPath, null);
            watcher.Renamed += (_, e) => OnEvent(e.ChangeType, e.FullPath, e.OldFullPath);
            watcher.Error += (_, e) =>
            {
                _logger.LogWarning("File watcher on {Folder} lost events ({Error}); running an integrity check.",
                    folder, e.GetException().Message);
                _checkNow.Release();
            };

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not watch {Folder}: {Error}. Integrity checks still cover it.", folder, ex.Message);
        }
    }

    private void OnEvent(WatcherChangeTypes type, string fullPath, string? oldFullPath)
    {
        lock (_gate)
        {
            var alert = _tripwire?.Evaluate(type, fullPath, oldFullPath);
            if (alert is not null)
            {
                Raise(alert);
            }
        }
    }

    /// <summary>Alerts are rare, so they are raised one at a time under the gate.</summary>
    private void Raise(CanaryAlert alert)
    {
        lock (_gate)
        {
            _tripped.Add(alert.Path);

            var line = alert.ToString();
            _alerts?.Publish("Canary", "CANARY_" + alert.Change.ToString().ToUpperInvariant(), AlertSeverity.Critical,
                "Canary file changed", line, alert.Path);
            _logger.LogCritical("{CanaryAlert}", line);
            _alertLog?.WriteLine(line);
            AlertRaised?.Invoke(alert);
            StatusChanged?.Invoke(line);
        }
    }

    private void Emit(string message)
    {
        _logger.LogInformation("{CanaryStatus}", message);
        _alertLog?.WriteLine(message);
        StatusChanged?.Invoke(message);
    }

    private ScanLogWriter? TryOpenAlertLog()
    {
        try
        {
            return new ScanLogWriter(_options.ResolveAlertLogPath());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not open canary alert log: {Error}", ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (_watchers.Count > 0)
        {
            Disarm();
        }
        _alertLog?.Dispose();
        _alertLog = null;
        _checkNow.Dispose();
    }
}

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScanAlways.Common;
using ScanAlways.Common.Alerts;

namespace ScanAlways.FirewallWatchdog;

/// <summary>
/// Periodically checks that Windows Firewall is actually filtering: every
/// profile on, and unsolicited inbound traffic blocked by default. It reports
/// a disabled profile or an "allow inbound" default the moment it appears and
/// again when it clears, reports every change to a profile's default inbound
/// action, and (opt-in) reports newly introduced inbound allow rules.
///
/// It is read-only by design. It never changes a firewall setting, never
/// re-enables a profile, never removes a rule; it observes and reports.
/// </summary>
public sealed class FirewallWatchdog : IDisposable
{
    public const string ModuleId = "FirewallWatchdog";
    public const string StateSaveFailed = "STATE_SAVE_FAILED";
    public const string StateUnreadable = "STATE_UNREADABLE";

    private readonly IFirewallStatusProvider _provider;
    private readonly FirewallWatchdogOptions _options;
    private readonly ILogger<FirewallWatchdog> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IAlertPublisher? _alerts;
    private Dictionary<string, FirewallFinding> _active = new();
    private FirewallState? _state;
    private string? _saveError;
    private ScanLogWriter? _alertLog;
    private bool _alertLogOpened;

    public FirewallWatchdog(
        IFirewallStatusProvider provider,
        IOptions<FirewallWatchdogOptions> options,
        ILogger<FirewallWatchdog> logger,
        Func<DateTimeOffset>? clock = null,
        IAlertPublisher? alerts = null)
    {
        _provider = provider;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _alerts = alerts;
    }

    /// <summary>Raised when a new standing problem appears (not on every check while it persists).</summary>
    public event Action<FirewallFinding>? AlertRaised;

    /// <summary>Raised when a previously reported standing problem clears.</summary>
    public event Action<FirewallFinding>? AlertCleared;

    /// <summary>Raised for each one-off change: a new default inbound action or a new inbound allow rule.</summary>
    public event Action<FirewallChange>? ChangeDetected;

    /// <summary>Raised after every check, with the full report.</summary>
    public event Action<FirewallHealthReport>? Checked;

    public FirewallHealthReport? LastReport { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(30, _options.CheckIntervalSeconds));
        _logger.LogInformation("Firewall watchdog starting: checking every {Interval}{Rules}.", interval,
            _options.MonitorInboundAllowRules ? ", including new inbound allow rules" : "");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckOnceAsync(cancellationToken);
                await Task.Delay(interval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Runs one check, raises alerts for new, cleared and changed state, and returns the report.</summary>
    public async Task<FirewallHealthReport> CheckOnceAsync(CancellationToken cancellationToken)
    {
        var state = LoadState();

        FirewallStatus status;
        try
        {
            status = await _provider.GetStatusAsync(_options.MonitorInboundAllowRules, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status = FirewallStatus.Unavailable(ex.Message);
        }

        var report = FirewallHealthEvaluator.Evaluate(status, _options, _clock());
        var isFirst = LastReport is null;
        LastReport = report;

        if (status.Available)
        {
            TrackChanges(state, status);
        }

        var current = report.Findings.ToDictionary(f => f.Code);
        if (!status.Available)
        {
            // Not being able to look is not evidence that a problem went away,
            // so keep earlier findings open instead of reporting a recovery.
            foreach (var finding in _active.Values)
            {
                current.TryAdd(finding.Code, finding);
            }
        }
        if (_saveError is not null)
        {
            current[StateSaveFailed] = new FirewallFinding(StateSaveFailed, AlertSeverity.Warning,
                $"Firewall changes are tracked in memory but will not survive a restart: {_saveError}");
        }

        foreach (var finding in current.Values.Where(f => !_active.ContainsKey(f.Code)))
        {
            RaiseAlert(finding);
        }
        foreach (var finding in _active.Values.Where(f => !current.ContainsKey(f.Code)))
        {
            ClearAlert(finding);
        }
        _active = current;

        if (isFirst)
        {
            _logger.LogInformation("{FirewallSummary}", report.Summary);
        }
        Checked?.Invoke(report);
        return report;
    }

    private void TrackChanges(FirewallState state, FirewallStatus status)
    {
        var before = JsonSerializer.Serialize(state, AtomicJsonFile.JsonOptions);

        foreach (var change in FirewallHealthEvaluator.CompareProfiles(state.Profiles, status.Profiles))
        {
            ReportChange(change);
        }
        foreach (var profile in status.Profiles)
        {
            // Keep the last known action when this check couldn't read it, so a
            // later real change is still compared against something.
            var previous = state.Profiles.GetValueOrDefault(profile.Name);
            state.Profiles[profile.Name] = new FirewallProfileSnapshot(
                profile.Enabled ?? previous?.Enabled,
                profile.DefaultInboundAction ?? previous?.DefaultInboundAction);
        }

        if (!_options.MonitorInboundAllowRules)
        {
            // Forget the baseline, so turning monitoring back on starts fresh
            // instead of reporting everything installed in between.
            state.InboundAllowRules = null;
        }
        else if (status.InboundAllowRules is { } rules)
        {
            if (state.InboundAllowRules is null)
            {
                ReportChange(new FirewallChange(FirewallHealthEvaluator.RuleBaselineCreated, AlertSeverity.Info,
                    "Inbound rule baseline recorded",
                    $"Recorded {rules.Count} existing enabled inbound allow rules. This is an inventory, not a safety verdict; only rules added from now on are reported."));
            }
            else
            {
                foreach (var change in FirewallHealthEvaluator.CompareRules(
                             state.InboundAllowRules.Select(r => r.Id).ToList(), rules, _options))
                {
                    ReportChange(change);
                }
            }
            state.InboundAllowRules = rules.ToList();
        }

        if (before != JsonSerializer.Serialize(state, AtomicJsonFile.JsonOptions) || _saveError is not null)
        {
            SaveState(state);
        }
    }

    private FirewallState LoadState()
    {
        if (_state is not null)
        {
            return _state;
        }

        try
        {
            var loaded = AtomicJsonFile.Read<FirewallState>(_options.ResolveStatePath());
            if (!loaded.IsValid())
            {
                throw new InvalidDataException("Invalid or unsupported firewall state file.");
            }
            loaded.Profiles = new(loaded.Profiles, StringComparer.OrdinalIgnoreCase);
            _state = loaded;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _state = new FirewallState();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            // The state only enables change reports, so start over rather than
            // stop watching; say so once, because changes made while the hub
            // was stopped can't be reported this time.
            _state = new FirewallState();
            ReportChange(new FirewallChange(StateUnreadable, AlertSeverity.Warning, "Firewall watchdog state reset",
                $"The saved firewall state could not be read, so changes made while the hub was stopped can't be reported: {ex.Message}"));
        }
        return _state;
    }

    private void SaveState(FirewallState state)
    {
        try
        {
            AtomicJsonFile.Write(_options.ResolveStatePath(), state);
            _saveError = null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _saveError = ex.Message;
        }
    }

    private void RaiseAlert(FirewallFinding finding)
    {
        _alerts?.Publish(ModuleId, finding.Code, finding.Severity, "Firewall needs attention", finding.Message, finding.Resource);
        var line = $"FIREWALL ALERT {finding}";
        if (finding.Severity == AlertSeverity.Critical)
        {
            _logger.LogCritical("{FirewallAlert}", line);
        }
        else
        {
            _logger.LogWarning("{FirewallAlert}", line);
        }
        AlertLog()?.WriteLine(line);
        AlertRaised?.Invoke(finding);
    }

    private void ClearAlert(FirewallFinding finding)
    {
        _alerts?.Publish(ModuleId, finding.Code + "_CLEARED", AlertSeverity.Info,
            "Firewall recovered", finding.Message + " is no longer reported.", finding.Resource);
        var line = $"Firewall recovered: {finding.Message} is no longer reported.";
        _logger.LogInformation("{FirewallRecovered}", line);
        AlertLog()?.WriteLine(line);
        AlertCleared?.Invoke(finding);
    }

    private void ReportChange(FirewallChange change)
    {
        _alerts?.Publish(ModuleId, change.Code, change.Severity, change.Title, change.Message, change.Resource);
        var line = $"FIREWALL CHANGE {change}";
        _logger.Log(change.Severity >= AlertSeverity.Warning ? LogLevel.Warning : LogLevel.Information,
            "{FirewallChange}", line);
        AlertLog()?.WriteLine(line);
        ChangeDetected?.Invoke(change);
    }

    private ScanLogWriter? AlertLog()
    {
        if (!_alertLogOpened)
        {
            _alertLogOpened = true;
            try
            {
                _alertLog = new ScanLogWriter(_options.ResolveAlertLogPath());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not open firewall watchdog log: {Error}", ex.Message);
            }
        }
        return _alertLog;
    }

    public void Dispose()
    {
        _alertLog?.Dispose();
        _alertLog = null;
    }
}

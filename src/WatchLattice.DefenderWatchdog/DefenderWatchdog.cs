using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WatchLattice.Common;
using WatchLattice.Common.Alerts;

namespace WatchLattice.DefenderWatchdog;

/// <summary>
/// Periodically checks that Microsoft Defender is actually protecting the
/// machine: real-time protection on, tamper protection on, signatures fresh.
/// Turning Defender off is often the first thing malware does, so the watchdog
/// alerts the moment a problem appears and again when it clears, without
/// repeating itself on every check. It also watches the quieter route of
/// leaving Defender on but blind: new exclusions, weakened attack surface
/// reduction rules, and cloud-delivered protection turned down.
///
/// It is read-only by design. It never changes a Defender setting, never
/// re-enables anything, and never hooks Defender's components; it observes and
/// reports.
/// </summary>
public sealed class DefenderWatchdog : IDisposable
{
    public const string ModuleId = "DefenderWatchdog";
    public const string StateSaveFailed = "STATE_SAVE_FAILED";
    public const string StateUnreadable = "STATE_UNREADABLE";

    private readonly IDefenderStatusProvider _provider;
    private readonly DefenderWatchdogOptions _options;
    private readonly ILogger<DefenderWatchdog> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IAlertPublisher? _alerts;
    private Dictionary<string, DefenderFinding> _active = new();
    private DefenderSettingsState? _state;
    private string? _saveError;
    private ScanLogWriter? _alertLog;
    private bool _alertLogOpened;

    public DefenderWatchdog(
        IDefenderStatusProvider provider,
        IOptions<DefenderWatchdogOptions> options,
        ILogger<DefenderWatchdog> logger,
        Func<DateTimeOffset>? clock = null,
        IAlertPublisher? alerts = null)
    {
        _provider = provider;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _alerts = alerts;
    }

    /// <summary>Raised when a new problem appears (not on every check while it persists).</summary>
    public event Action<DefenderFinding>? AlertRaised;

    /// <summary>Raised when a previously reported problem clears.</summary>
    public event Action<DefenderFinding>? AlertCleared;

    /// <summary>Raised for each one-off settings change, such as a new exclusion or a weakened ASR rule.</summary>
    public event Action<DefenderChange>? ChangeDetected;

    /// <summary>Raised after every check, with the full report.</summary>
    public event Action<DefenderHealthReport>? Checked;

    /// <summary>Raised on every status line, so a UI host can show live state.</summary>
    public event Action<string>? StatusChanged;

    public DefenderHealthReport? LastReport { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(30, _options.CheckIntervalSeconds));
        _logger.LogInformation("Defender watchdog starting: checking every {Interval}.", interval);

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

    /// <summary>Runs one check, raises alerts for new and cleared findings, and returns the report.</summary>
    public async Task<DefenderHealthReport> CheckOnceAsync(CancellationToken cancellationToken)
    {
        DefenderStatus status;
        try
        {
            status = await _provider.GetStatusAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status = DefenderStatus.Unavailable(ex.Message);
        }

        var report = DefenderHealthEvaluator.Evaluate(status, _options, _clock());
        var isFirst = LastReport is null;
        LastReport = report;

        if (_options.MonitorSettings && status.Settings is { } settings)
        {
            TrackChanges(settings);
        }

        var current = report.Findings
            .Where(f => f.Severity > FindingSeverity.Info)
            .ToDictionary(f => f.Code);

        // Not being able to look is not evidence that a problem went away, so
        // keep earlier findings open instead of reporting a recovery.
        foreach (var finding in _active.Values.Where(f => !CouldBeChecked(f.Code, status)))
        {
            current.TryAdd(finding.Code, finding);
        }
        if (_saveError is not null)
        {
            current[StateSaveFailed] = new DefenderFinding(StateSaveFailed, FindingSeverity.Warning,
                $"Defender setting changes are tracked in memory but will not survive a restart: {_saveError}");
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
            _logger.LogInformation("{DefenderSummary}", report.Summary);
        }
        StatusChanged?.Invoke(report.Summary);
        Checked?.Invoke(report);
        return report;
    }

    private static bool CouldBeChecked(string code, DefenderStatus status)
    {
        if (!status.Available)
        {
            return code == DefenderHealthEvaluator.StatusUnavailable;
        }
        if (code.StartsWith(DefenderHealthEvaluator.BroadExclusionPrefix, StringComparison.Ordinal))
        {
            return status.Settings?.Exclusions is not null;
        }
        if (code is DefenderHealthEvaluator.CloudProtectionOff or DefenderHealthEvaluator.BlockAtFirstSeenOff)
        {
            return status.Settings is not null;
        }
        return true;
    }

    private void TrackChanges(DefenderSettings settings)
    {
        var state = LoadState();
        var before = JsonSerializer.Serialize(state, AtomicJsonFile.JsonOptions);

        foreach (var change in DefenderHealthEvaluator.CompareSettings(state, settings, _options))
        {
            ReportChange(change);
        }

        var baselined = new List<string>();
        if (settings.Exclusions is { } exclusions)
        {
            if (state.Exclusions is null)
            {
                baselined.Add($"{exclusions.Count} exclusions");
                foreach (var exclusion in exclusions)
                {
                    AlertLog()?.WriteLine($"Defender baseline: {exclusion}");
                }
            }
            state.Exclusions = exclusions.ToList();
        }
        if (settings.AsrRules is { } rules)
        {
            if (state.AsrRules is null)
            {
                baselined.Add($"{rules.Count} attack surface reduction rules");
            }
            state.AsrRules = new Dictionary<string, int>(rules, StringComparer.OrdinalIgnoreCase);
        }
        state.CloudBlockLevel = settings.CloudBlockLevel ?? state.CloudBlockLevel;

        if (baselined.Count > 0)
        {
            ReportChange(new DefenderChange(DefenderHealthEvaluator.SettingsBaselineCreated, FindingSeverity.Info,
                "Defender settings baseline recorded",
                $"Recorded {string.Join(" and ", baselined)} already configured. This is an inventory, not a safety verdict; only changes from now on are reported."));
        }

        if (before != JsonSerializer.Serialize(state, AtomicJsonFile.JsonOptions) || _saveError is not null)
        {
            SaveState(state);
        }
    }

    private DefenderSettingsState LoadState()
    {
        if (_state is not null)
        {
            return _state;
        }

        try
        {
            var loaded = AtomicJsonFile.Read<DefenderSettingsState>(_options.ResolveStatePath());
            if (!loaded.IsValid())
            {
                throw new InvalidDataException("Invalid or unsupported Defender settings state file.");
            }
            if (loaded.AsrRules is not null)
            {
                loaded.AsrRules = new(loaded.AsrRules, StringComparer.OrdinalIgnoreCase);
            }
            _state = loaded;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _state = new DefenderSettingsState();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            // The state only enables change reports, so start over rather than
            // stop watching; say so once, because changes made while the hub
            // was stopped can't be reported this time.
            _state = new DefenderSettingsState();
            ReportChange(new DefenderChange(StateUnreadable, FindingSeverity.Warning, "Defender watchdog state reset",
                $"The saved Defender settings could not be read, so changes made while the hub was stopped can't be reported: {ex.Message}"));
        }
        return _state;
    }

    private void SaveState(DefenderSettingsState state)
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

    private void ReportChange(DefenderChange change)
    {
        _alerts?.Publish(ModuleId, change.Code, ToAlertSeverity(change.Severity), change.Title, change.Message, change.Resource);
        var line = $"DEFENDER CHANGE {change}";
        _logger.Log(change.Severity >= FindingSeverity.Warning ? LogLevel.Warning : LogLevel.Information,
            "{DefenderChange}", line);
        AlertLog()?.WriteLine(line);
        ChangeDetected?.Invoke(change);
    }

    private static AlertSeverity ToAlertSeverity(FindingSeverity severity) => severity switch
    {
        FindingSeverity.Critical => AlertSeverity.Critical,
        FindingSeverity.Warning => AlertSeverity.Warning,
        _ => AlertSeverity.Info,
    };

    private void RaiseAlert(DefenderFinding finding)
    {
        _alerts?.Publish(ModuleId, finding.Code, ToAlertSeverity(finding.Severity),
            "Defender needs attention", finding.Message);
        var line = $"DEFENDER ALERT {finding}";
        if (finding.Severity == FindingSeverity.Critical)
        {
            _logger.LogCritical("{DefenderAlert}", line);
        }
        else
        {
            _logger.LogWarning("{DefenderAlert}", line);
        }
        AlertLog()?.WriteLine(line);
        AlertRaised?.Invoke(finding);
    }

    private void ClearAlert(DefenderFinding finding)
    {
        _alerts?.Publish(ModuleId, finding.Code + "_CLEARED", AlertSeverity.Info,
            "Defender recovered", finding.Message + " is no longer reported.");
        var line = $"Defender recovered: {finding.Message} is no longer reported.";
        _logger.LogInformation("{DefenderRecovered}", line);
        AlertLog()?.WriteLine(line);
        AlertCleared?.Invoke(finding);
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
                _logger.LogWarning("Could not open Defender watchdog log: {Error}", ex.Message);
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

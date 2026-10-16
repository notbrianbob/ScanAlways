using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScanAlways.Common;

namespace ScanAlways.DefenderWatchdog;

/// <summary>
/// Periodically checks that Microsoft Defender is actually protecting the
/// machine: real-time protection on, tamper protection on, signatures fresh.
/// Turning Defender off is often the first thing malware does, so the watchdog
/// alerts the moment a problem appears and again when it clears, without
/// repeating itself on every check.
///
/// It is read-only by design. It never changes a Defender setting, never
/// re-enables anything, and never hooks Defender's components; it observes and
/// reports.
/// </summary>
public sealed class DefenderWatchdog : IDisposable
{
    private readonly IDefenderStatusProvider _provider;
    private readonly DefenderWatchdogOptions _options;
    private readonly ILogger<DefenderWatchdog> _logger;
    private readonly Func<DateTimeOffset> _clock;
    private Dictionary<string, DefenderFinding> _active = new();
    private ScanLogWriter? _alertLog;
    private bool _alertLogOpened;

    public DefenderWatchdog(
        IDefenderStatusProvider provider,
        IOptions<DefenderWatchdogOptions> options,
        ILogger<DefenderWatchdog> logger,
        Func<DateTimeOffset>? clock = null)
    {
        _provider = provider;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised when a new problem appears (not on every check while it persists).</summary>
    public event Action<DefenderFinding>? AlertRaised;

    /// <summary>Raised when a previously reported problem clears.</summary>
    public event Action<DefenderFinding>? AlertCleared;

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

        var current = report.Findings
            .Where(f => f.Severity > FindingSeverity.Info)
            .ToDictionary(f => f.Code);

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

    private void RaiseAlert(DefenderFinding finding)
    {
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

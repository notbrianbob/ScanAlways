using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ScanAlways.Common.Alerts;

/// <summary>Writes a local heartbeat and alert history every two seconds; failures never stop monitoring.</summary>
public sealed class AlertDeliveryService : BackgroundService
{
    private readonly AlertHub _alerts;
    private readonly AlertDeliveryOptions _options;
    private readonly ILogger<AlertDeliveryService> _logger;
    private bool _writeFailed;
    private long _lastWrittenRevision = -1;

    public AlertDeliveryService(AlertHub alerts, IOptions<AlertDeliveryOptions> options, ILogger<AlertDeliveryService> logger)
    {
        _alerts = alerts;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        string path;
        try { path = _options.ResolveFeedPath(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            _logger.LogError("Shared alert delivery has an invalid path: {Error}", ex.Message);
            return;
        }
        try
        {
            _alerts.RestoreHistory(AlertSnapshot.Read(path, includeHeartbeat: false).Alerts);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("Could not restore alert history: {Error}", ex.Message);
            _alerts.Publish("Hub", "ALERT_HISTORY_UNAVAILABLE", AlertSeverity.Warning,
                "Previous alert history unavailable", ex.Message);
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Write(path, online: true);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { Write(path, online: false); }
    }

    private void Write(string path, bool online)
    {
        try
        {
            var snapshot = _alerts.Snapshot(online);
            if (snapshot.Revision != _lastWrittenRevision || !online || !File.Exists(path))
            {
                AtomicJsonFile.Write(path, snapshot);
                _lastWrittenRevision = snapshot.Revision;
            }
            AtomicJsonFile.Write(path + ".heartbeat.json", new AlertHeartbeat(snapshot.SessionId, snapshot.HeartbeatAt, online));
            if (_writeFailed) _logger.LogInformation("Shared alert delivery recovered.");
            _writeFailed = false;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (!_writeFailed) _logger.LogWarning("Shared alerts cannot be written to {Path}: {Error}", path, ex.Message);
            _writeFailed = true;
            _lastWrittenRevision = -1;
        }
    }
}

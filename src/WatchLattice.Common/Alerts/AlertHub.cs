using Microsoft.Extensions.Options;

namespace WatchLattice.Common.Alerts;

/// <summary>Thread-safe, bounded alert history. Publishers never wait on disk or UI callbacks.</summary>
public sealed class AlertHub : IAlertPublisher, IUpdateStatusPublisher
{
    private readonly object _gate = new();
    private readonly List<ProtectionAlert> _history = new();
    private readonly Dictionary<string, ModuleStatus> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _limit;
    private readonly Guid _session = Guid.NewGuid();
    private long _revision;
    private readonly IAlertSink[] _sinks;
    private UpdateNotice? _update;

    public AlertHub(IOptions<AlertDeliveryOptions> options, IEnumerable<IAlertSink>? sinks = null)
    {
        _limit = Math.Clamp(options.Value.HistoryLimit, 1, 1000);
        _sinks = sinks?.ToArray() ?? Array.Empty<IAlertSink>();
    }

    public void Publish(string module, string code, AlertSeverity severity, string title, string message, string? resource = null)
    {
        var alert = new ProtectionAlert(Guid.NewGuid(), DateTimeOffset.UtcNow, Clip(module, 100), Clip(code, 100),
            severity, Clip(title, 160), Clip(message, 2048), resource is null ? null : Clip(resource, 512));
        lock (_gate)
        {
            // Collapse repeated identical events in a short burst, without suppressing recovery or changed details.
            if (_history.Any(a => alert.At >= a.At && alert.At - a.At < TimeSpan.FromSeconds(5) && a.Module == alert.Module
                && a.Code == alert.Code && a.Severity == alert.Severity && a.Message == alert.Message
                && a.Resource == alert.Resource)) return;
            _history.Add(alert);
            Trim();
            _revision++;
        }

        // Outside the lock: sinks only queue the alert, and a faulty one must not cost the others.
        foreach (var sink in _sinks)
        {
            try { sink.Accept(alert); }
            catch (Exception) { }
        }
    }

    public void SetModuleState(string id, ModuleRunState state)
    {
        lock (_gate)
        {
            _modules[id] = new(id, state, DateTimeOffset.UtcNow);
            _revision++;
        }
    }

    public void SetUpdate(UpdateNotice? notice)
    {
        lock (_gate)
        {
            if (_update == notice) return;
            _update = notice;
            _revision++;
        }
    }

    public void RestoreHistory(IEnumerable<ProtectionAlert> history)
    {
        lock (_gate)
        {
            var existing = _history.ToArray();
            _history.Clear();
            _history.AddRange(history.Concat(existing).DistinctBy(a => a.Id).OrderBy(a => a.At).TakeLast(_limit));
            _revision++;
        }
    }

    public AlertSnapshot Snapshot(bool online = true)
    {
        lock (_gate) return new(AlertSnapshot.CurrentVersion, _session, DateTimeOffset.UtcNow, online,
            _modules.Values.OrderBy(m => m.Id).ToArray(), _history.ToArray(), _revision, _update);
    }

    private void Trim()
    {
        if (_history.Count > _limit) _history.RemoveRange(0, _history.Count - _limit);
    }

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length] + "…";
}

namespace WatchLattice.Common.Alerts;

/// <summary>Finds new warnings for the tray; opening the companion never replays old notifications.</summary>
public sealed class AlertNotificationCursor
{
    private HashSet<Guid>? _seen;

    public IReadOnlyList<ProtectionAlert> Observe(AlertSnapshot snapshot)
    {
        var fresh = _seen is null ? Array.Empty<ProtectionAlert>() : snapshot.Alerts
            .Where(a => !_seen.Contains(a.Id) && a.Severity >= AlertSeverity.Warning).ToArray();
        _seen = snapshot.Alerts.Select(a => a.Id).ToHashSet();
        return fresh;
    }
}

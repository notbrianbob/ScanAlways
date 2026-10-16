namespace ScanAlways.Common.Alerts;

public enum AlertSeverity { Info, Warning, Critical }
public enum ModuleRunState { Disabled, Starting, Running, Restarting, Finished, Failed, Stopped }

/// <summary>A display-only alert. Resource and Message must never be executed as commands.</summary>
public sealed record ProtectionAlert(Guid Id, DateTimeOffset At, string Module, string Code,
    AlertSeverity Severity, string Title, string Message, string? Resource = null);

public sealed record ModuleStatus(string Id, ModuleRunState State, DateTimeOffset UpdatedAt);

/// <summary>Versioned, bounded snapshot consumed by the tray without a control channel.</summary>
public sealed record AlertSnapshot(int Version, Guid SessionId, DateTimeOffset HeartbeatAt,
    bool Online, IReadOnlyList<ModuleStatus> Modules, IReadOnlyList<ProtectionAlert> Alerts, long Revision = 0)
{
    public const int CurrentVersion = 1;

    public bool IsFresh(DateTimeOffset now) => Online && HeartbeatAt <= now.AddSeconds(10)
        && now - HeartbeatAt <= TimeSpan.FromSeconds(20);

    public static AlertSnapshot Read(string path, bool includeHeartbeat = true)
    {
        var snapshot = AtomicJsonFile.Read<AlertSnapshot>(path);
        if (snapshot.Version != CurrentVersion || snapshot.SessionId == Guid.Empty
            || snapshot.Modules is null || snapshot.Alerts is null
            || snapshot.Modules.Count > 100 || snapshot.Alerts.Count > 1000
            || snapshot.Modules.Any(m => m is null || string.IsNullOrEmpty(m.Id) || !Enum.IsDefined(m.State))
            || snapshot.Alerts.Any(a => a is null || a.Id == Guid.Empty || a.Module is null
                || a.Code is null || a.Title is null || a.Message is null || !Enum.IsDefined(a.Severity)))
            throw new InvalidDataException("Unsupported or invalid alert feed.");
        if (includeHeartbeat)
        {
            try
            {
                var heartbeat = AtomicJsonFile.Read<AlertHeartbeat>(path + ".heartbeat.json");
                if (heartbeat.SessionId == snapshot.SessionId)
                    snapshot = snapshot with { HeartbeatAt = heartbeat.At, Online = snapshot.Online && heartbeat.Online };
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
        }
        return snapshot;
    }
}

/// <summary>A small sidecar refreshed while an unchanged alert snapshot remains on disk.</summary>
public sealed record AlertHeartbeat(Guid SessionId, DateTimeOffset At, bool Online);

public interface IAlertPublisher
{
    void Publish(string module, string code, AlertSeverity severity, string title, string message, string? resource = null);
}

public sealed class AlertDeliveryOptions
{
    public bool Enabled { get; set; } = true;
    public string? FeedPath { get; set; }
    public int HistoryLimit { get; set; } = 200;

    public string ResolveFeedPath() => Path.GetFullPath(Environment.ExpandEnvironmentVariables(
        string.IsNullOrWhiteSpace(FeedPath) ? DefaultFeedPath : FeedPath));

    public static string DefaultFeedPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanAlways", "alerts.json");
}

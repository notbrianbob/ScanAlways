using Microsoft.Extensions.Logging;

namespace WatchLattice.Common.Activity;

/// <summary>One line of the hub's own log, as the dashboard's Activity tab shows it.</summary>
public sealed record ActivityEntry(DateTimeOffset At, LogLevel Level, string Source, string Message);

/// <summary>
/// Recent hub log lines, written next to the alert feed as
/// <c>alerts.json.activity.json</c> so the tray can show what the hub has been
/// doing without reading the service's own logs. Display-only, like the feed.
/// </summary>
public sealed record ActivitySnapshot(int Version, Guid SessionId, IReadOnlyList<ActivityEntry> Entries)
{
    public const int CurrentVersion = 1;
    public const string FileSuffix = ".activity.json";

    public static string PathFor(string feedPath) => feedPath + FileSuffix;

    public static ActivitySnapshot Read(string path)
    {
        var snapshot = AtomicJsonFile.Read<ActivitySnapshot>(path);
        if (snapshot.Version != CurrentVersion || snapshot.Entries is null || snapshot.Entries.Count > ActivityLog.MaxLimit
            || snapshot.Entries.Any(e => e is null || e.Source is null || e.Message is null || !Enum.IsDefined(e.Level)))
            throw new InvalidDataException("Unsupported or invalid activity log.");
        return snapshot;
    }
}

/// <summary>Thread-safe, bounded history of the hub's Information-and-above log lines.</summary>
public sealed class ActivityLog
{
    public const int DefaultLimit = 500;
    public const int MaxLimit = 2000;
    private readonly object _gate = new();
    private readonly Queue<ActivityEntry> _entries = new();
    private readonly int _limit;
    private readonly Guid _session = Guid.NewGuid();
    private long _revision;

    public ActivityLog(int limit = DefaultLimit) => _limit = Math.Clamp(limit, 1, MaxLimit);

    public long Revision
    {
        get { lock (_gate) return _revision; }
    }

    public void Add(DateTimeOffset at, LogLevel level, string source, string message)
    {
        var entry = new ActivityEntry(at, level, Clip(source, 100), Clip(message, 2048));
        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _limit) _entries.Dequeue();
            _revision++;
        }
    }

    /// <summary>Puts the previous session's lines before this session's, after a restart.</summary>
    public void Restore(IEnumerable<ActivityEntry> previous)
    {
        lock (_gate)
        {
            var merged = previous.Concat(_entries).OrderBy(e => e.At).TakeLast(_limit).ToArray();
            _entries.Clear();
            foreach (var entry in merged) _entries.Enqueue(entry);
            _revision++;
        }
    }

    public ActivitySnapshot Snapshot()
    {
        lock (_gate) return new(ActivitySnapshot.CurrentVersion, _session, _entries.ToArray());
    }

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length] + "…";
}

/// <summary>
/// Copies the hub's log lines into <see cref="ActivityLog"/>. The usual
/// Logging:LogLevel filters apply first; HTTP client chatter is left out.
/// </summary>
public sealed class ActivityLoggerProvider : ILoggerProvider
{
    private readonly ActivityLog _log;

    public ActivityLoggerProvider(ActivityLog log) => _log = log;

    public ILogger CreateLogger(string categoryName) => new ActivityLogger(_log, categoryName);

    public void Dispose()
    {
    }

    /// <summary>"WatchLattice.Canary.CanaryWatcher" becomes "CanaryWatcher".</summary>
    public static string ShortSource(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot < 0 || dot == category.Length - 1 ? category : category[(dot + 1)..];
    }

    private sealed class ActivityLogger : ILogger
    {
        private readonly ActivityLog _log;
        private readonly string _source;
        private readonly bool _ignored;

        public ActivityLogger(ActivityLog log, string category)
        {
            _log = log;
            _source = ShortSource(category);
            _ignored = category.StartsWith("System.Net.Http", StringComparison.Ordinal);
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => !_ignored && logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            // The exception's message, not its stack trace: this is for people, not debugging.
            if (exception is not null) message += $" ({exception.GetType().Name}: {exception.Message})";
            _log.Add(DateTimeOffset.UtcNow, logLevel, _source, message);
        }
    }
}

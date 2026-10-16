namespace ScanAlways.Canary;

/// <summary>
/// Pure decision logic: given a file-system event, decide whether it touched a
/// canary and, if so, produce an alert. Duplicate alerts for the same canary
/// and change are suppressed for a short window, because a single save or
/// encryption pass usually fires several events. Thread-safe, since file
/// watcher callbacks arrive on pool threads.
/// </summary>
public sealed class CanaryTripwire
{
    private readonly HashSet<string> _canaries;
    private readonly Dictionary<(string Path, CanaryChange Change), DateTimeOffset> _lastAlert;
    private readonly TimeSpan _debounce;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    public CanaryTripwire(IEnumerable<string> canaryPaths, TimeSpan debounce, Func<DateTimeOffset>? clock = null)
    {
        _canaries = new HashSet<string>(canaryPaths.Select(CanaryPaths.Normalize), CanaryPaths.Comparer);
        _lastAlert = new Dictionary<(string, CanaryChange), DateTimeOffset>(new KeyComparer());
        _debounce = debounce;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyCollection<string> Canaries => _canaries;

    public bool IsCanary(string path) => _canaries.Contains(CanaryPaths.Normalize(path));

    /// <summary>Maps a raw watcher event to an alert, or null if no canary was involved.</summary>
    public CanaryAlert? Evaluate(WatcherChangeTypes type, string fullPath, string? oldFullPath = null)
    {
        switch (type)
        {
            case WatcherChangeTypes.Changed:
                return IsCanary(fullPath) ? Report(fullPath, CanaryChange.Modified, "content written") : null;

            case WatcherChangeTypes.Created:
                // A canary path re-appearing means something deleted it and
                // wrote a replacement (a common "encrypt to copy" pattern).
                return IsCanary(fullPath) ? Report(fullPath, CanaryChange.Modified, "file replaced") : null;

            case WatcherChangeTypes.Deleted:
                return IsCanary(fullPath) ? Report(fullPath, CanaryChange.Deleted, "file deleted") : null;

            case WatcherChangeTypes.Renamed:
                if (oldFullPath is not null && IsCanary(oldFullPath))
                {
                    return Report(oldFullPath, CanaryChange.Renamed, $"renamed to {Path.GetFileName(fullPath)}");
                }
                if (IsCanary(fullPath))
                {
                    var from = oldFullPath is null ? "another file" : Path.GetFileName(oldFullPath);
                    return Report(fullPath, CanaryChange.Modified, $"replaced by rename from {from}");
                }
                return null;

            default:
                return null;
        }
    }

    /// <summary>Raises an alert for a known canary, subject to debouncing.</summary>
    public CanaryAlert? Report(string path, CanaryChange change, string detail)
    {
        var normalized = CanaryPaths.Normalize(path);
        if (!_canaries.Contains(normalized))
        {
            return null;
        }

        var now = _clock();
        lock (_gate)
        {
            var key = (normalized, change);
            if (_lastAlert.TryGetValue(key, out var last) && now - last < _debounce)
            {
                return null;
            }
            _lastAlert[key] = now;
        }

        return new CanaryAlert(now, normalized, change, detail);
    }

    private sealed class KeyComparer : IEqualityComparer<(string Path, CanaryChange Change)>
    {
        public bool Equals((string Path, CanaryChange Change) x, (string Path, CanaryChange Change) y) =>
            x.Change == y.Change && CanaryPaths.Comparer.Equals(x.Path, y.Path);

        public int GetHashCode((string Path, CanaryChange Change) obj) =>
            HashCode.Combine(CanaryPaths.Comparer.GetHashCode(obj.Path), obj.Change);
    }
}

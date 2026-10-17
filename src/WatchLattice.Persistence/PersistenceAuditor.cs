using System.IO.Enumeration;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WatchLattice.Common;
using WatchLattice.Common.Alerts;
using WatchLattice.Persistence.Verification;

namespace WatchLattice.Persistence;

/// <summary>Read-only inventory, durable baseline, and change-only alerts. Never repairs autostarts.</summary>
public sealed class PersistenceAuditor
{
    private readonly IPersistenceProvider _provider;
    private readonly PersistenceOptions _options;
    private readonly ILogger<PersistenceAuditor> _logger;
    private readonly IAlertPublisher? _alerts;
    private readonly IChangeEvidenceSink? _evidence;
    private readonly Dictionary<string, string> _problems = new(StringComparer.OrdinalIgnoreCase);
    private PersistenceBaseline? _baseline;

    public PersistenceAuditor(IPersistenceProvider provider, IOptions<PersistenceOptions> options,
        ILogger<PersistenceAuditor> logger, IAlertPublisher? alerts = null, IChangeEvidenceSink? evidence = null)
    {
        _provider = provider;
        _options = options.Value;
        _logger = logger;
        _alerts = alerts;
        _evidence = evidence;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckOnceAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(30, _options.CheckIntervalSeconds)), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        if (!LoadBaseline()) return;
        IReadOnlyList<PersistenceSource> sources;
        try { sources = await _provider.ReadAsync(cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Problem("inventory", ex.Message);
            return;
        }
        Recovered("inventory");
        if (sources.Count == 0) Problem("coverage", "No persistence sources are configured or available.");
        else Recovered("coverage");

        var changed = false;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Error is not null)
            {
                Problem(source.Id, source.Error);
                continue;
            }
            if (source.Entries.Any(e => e is null || string.IsNullOrEmpty(e.Id) || e.Command is null)
                || source.Entries.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != source.Entries.Count)
            {
                Problem(source.Id, "Inventory contains invalid or duplicate entries.");
                continue;
            }
            Recovered(source.Id);
            if (!_baseline!.Sources.TryGetValue(source.Id, out var previous))
            {
                Emit("BASELINE_CREATED", AlertSeverity.Info, "Autostart baseline recorded",
                    $"Recorded {source.Entries.Count} existing entries in {source.Id}. This is an inventory, not a safety verdict.", source.Id);
                changed = true;
            }
            else
            {
                foreach (var difference in PersistenceDiff.Compare(previous, source.Entries))
                {
                    changed = true;
                    var resource = source.Id + "/" + difference.Id;
                    if (_options.IgnoredEntries.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, resource, ignoreCase: true))) continue;
                    // Hand over the complete before/after first; the alert message only carries excerpts.
                    if (difference.Kind != PersistenceChangeKind.Removed)
                        _evidence?.Stage(resource, new PersistenceObservation(source.Id, difference.Id, difference.Kind,
                            difference.Before, difference.After, DateTimeOffset.UtcNow));
                    Emit("ENTRY_" + difference.Kind.ToString().ToUpperInvariant(),
                        difference.Kind == PersistenceChangeKind.Removed ? AlertSeverity.Info : AlertSeverity.Warning,
                        $"Autostart entry {difference.Kind.ToString().ToLowerInvariant()}",
                        $"{Excerpt(resource, 350)}\nPrevious: {Excerpt(difference.Before, 700)}\nCurrent: {Excerpt(difference.After, 700)}", resource);
                }
            }
            _baseline.Sources[source.Id] = source.Entries.ToList();
        }

        if (changed || _problems.ContainsKey("baseline-save"))
        {
            try
            {
                AtomicJsonFile.Write(_options.ResolveBaselinePath(), _baseline);
                Recovered("baseline-save");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                Problem("baseline-save", "Changes are tracked in memory, but cannot survive a restart: " + ex.Message);
            }
        }
    }

    private bool LoadBaseline()
    {
        if (_baseline is not null) return true;
        try
        {
            var loaded = AtomicJsonFile.Read<PersistenceBaseline>(_options.ResolveBaselinePath());
            if (loaded.Version != 1 || loaded.Sources is null || loaded.Sources.Any(s => s.Value is null
                || s.Value.Any(e => e is null || string.IsNullOrEmpty(e.Id) || e.Command is null)
                || s.Value.Select(e => e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != s.Value.Count)
                || loaded.Sources.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != loaded.Sources.Count)
                throw new InvalidDataException("Invalid or unsupported persistence baseline.");
            loaded.Sources = new(loaded.Sources, StringComparer.OrdinalIgnoreCase);
            _baseline = loaded;
            Recovered("baseline-load");
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            _baseline = new();
            Recovered("baseline-load");
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            Problem("baseline-load", "Auditing paused; the existing baseline will not be replaced: " + ex.Message);
            return false;
        }
    }

    private void Problem(string source, string message)
    {
        if (_problems.TryGetValue(source, out var previous) && previous == message) return;
        _problems[source] = message;
        Emit("SOURCE_UNAVAILABLE", AlertSeverity.Warning, "Persistence coverage needs attention", message, source);
    }

    private void Recovered(string source)
    {
        if (_problems.Remove(source)) Emit("SOURCE_RECOVERED", AlertSeverity.Info, "Persistence coverage recovered", source + " is readable again.", source);
    }

    private void Emit(string code, AlertSeverity severity, string title, string message, string resource)
    {
        _alerts?.Publish("Persistence", code, severity, title, message, resource);
        _logger.Log(severity >= AlertSeverity.Warning ? LogLevel.Warning : LogLevel.Information,
            "Persistence {Code}: {Message}", code, message);
    }

    private static string Excerpt(string? value, int limit) => value is null ? "(absent)"
        : value.Length <= limit ? value : value[..limit] + "… (truncated)";
}

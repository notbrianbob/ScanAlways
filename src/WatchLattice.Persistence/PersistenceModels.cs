namespace WatchLattice.Persistence;

/// <summary>An observed autostart entry. Command is evidence, never something to execute.</summary>
public sealed record PersistenceEntry(string Id, string Command);

/// <summary>An error means the entire source is incomplete and must not replace its baseline.</summary>
public sealed record PersistenceSource(string Id, IReadOnlyList<PersistenceEntry> Entries, string? Error = null);

public interface IPersistenceProvider
{
    Task<IReadOnlyList<PersistenceSource>> ReadAsync(CancellationToken cancellationToken);
}

public sealed class PersistenceBaseline
{
    [System.Text.Json.Serialization.JsonRequired]
    public int Version { get; set; } = 1;
    [System.Text.Json.Serialization.JsonRequired]
    public Dictionary<string, List<PersistenceEntry>> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum PersistenceChangeKind { Added, Changed, Removed }
public sealed record PersistenceChange(PersistenceChangeKind Kind, string Id, string? Before, string? After);

/// <summary>Compares identities case-insensitively and command text exactly.</summary>
public static class PersistenceDiff
{
    public static IReadOnlyList<PersistenceChange> Compare(IEnumerable<PersistenceEntry> previous, IEnumerable<PersistenceEntry> current)
    {
        var before = previous.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var after = current.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var changes = new List<PersistenceChange>();
        foreach (var entry in after.Values)
        {
            if (!before.TryGetValue(entry.Id, out var old)) changes.Add(new(PersistenceChangeKind.Added, entry.Id, null, entry.Command));
            else if (old.Command != entry.Command) changes.Add(new(PersistenceChangeKind.Changed, entry.Id, old.Command, entry.Command));
        }
        foreach (var entry in before.Values.Where(e => !after.ContainsKey(e.Id)))
            changes.Add(new(PersistenceChangeKind.Removed, entry.Id, entry.Command, null));
        return changes;
    }
}

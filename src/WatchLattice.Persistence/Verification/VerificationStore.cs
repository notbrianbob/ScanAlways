using System.Text.Json;
using System.Text.Json.Serialization;
using WatchLattice.Common;

namespace WatchLattice.Persistence.Verification;

public sealed class VerificationStoreFile
{
    public const int CurrentVersion = 1;

    [JsonRequired]
    public int Version { get; set; } = CurrentVersion;

    [JsonRequired]
    public List<VerificationRecord> Records { get; set; } = new();
}

/// <summary>
/// Verification findings, kept next to the alert feed (<c>alerts.json.verifications.json</c>)
/// so they share its folder and permissions: the hub writes, the tray user reads.
/// Bounded: the newest <see cref="MaxRecords"/> alerts, each with its newest
/// <see cref="MaxRunsPerRecord"/> runs. The original evidence in a record is
/// never replaced, only trimmed to a size limit when it is first stored.
/// </summary>
public sealed class VerificationStore
{
    public const int MaxRecords = 100;
    public const int MaxRunsPerRecord = 5;
    public const int MaxEvidenceChars = 32768;
    public const string FileSuffix = ".verifications.json";

    private readonly object _gate = new();
    private readonly string _path;
    private List<VerificationRecord> _records = new();

    public VerificationStore(string path) => _path = Path.GetFullPath(path);

    public string FilePath => _path;

    public static string PathForFeed(string feedPath) => feedPath + FileSuffix;

    public IReadOnlyList<VerificationRecord> Records
    {
        get { lock (_gate) return _records.ToArray(); }
    }

    public VerificationRecord? Find(Guid alertId)
    {
        lock (_gate) return _records.FirstOrDefault(r => r.AlertId == alertId);
    }

    /// <summary>
    /// Loads existing findings. A file that cannot be understood is moved
    /// aside (never overwritten) and the store starts empty. Returns a
    /// problem description, or null.
    /// </summary>
    public string? Load()
    {
        try
        {
            var loaded = Read(_path);
            lock (_gate) _records = loaded.ToList();
            return null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            var aside = _path + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            try { File.Move(_path, aside); }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException)
            {
                return $"Earlier verification findings could not be read ({ex.Message}) or moved aside ({move.Message}).";
            }
            return $"Earlier verification findings could not be read ({ex.Message}); kept as {Path.GetFileName(aside)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Earlier verification findings could not be read: " + ex.Message;
        }
    }

    /// <summary>Reads and validates a findings file, for the hub and the tray.</summary>
    public static IReadOnlyList<VerificationRecord> Read(string path)
    {
        var file = AtomicJsonFile.Read<VerificationStoreFile>(path);
        if (file.Version != VerificationStoreFile.CurrentVersion || file.Records is null || file.Records.Count > MaxRecords * 2
            || file.Records.Any(r => r is null || r.AlertId == Guid.Empty || r.Original is null || r.Runs is null || r.Resource is null
                || r.Original.SourceId is null || r.Original.EntryId is null || r.Runs.Any(run => run is null || run.Checks is null)))
            throw new InvalidDataException("Unsupported or invalid verification findings.");
        return file.Records;
    }

    /// <summary>Adds a record for a new alert. Returns false if the alert already has one.</summary>
    public bool Add(VerificationRecord record)
    {
        record = record with
        {
            Original = record.Original with { Before = Trim(record.Original.Before), After = Trim(record.Original.After) },
        };
        lock (_gate)
        {
            if (_records.Any(r => r.AlertId == record.AlertId)) return false;
            _records.Add(record);
            if (_records.Count > MaxRecords) _records.RemoveRange(0, _records.Count - MaxRecords);
            return true;
        }
    }

    /// <summary>Appends a run to an alert's record. Returns the updated record, or null if the record is gone.</summary>
    public VerificationRecord? AddRun(Guid alertId, VerificationRun run)
    {
        lock (_gate)
        {
            var index = _records.FindIndex(r => r.AlertId == alertId);
            if (index < 0) return null;
            var runs = _records[index].Runs.Append(run).TakeLast(MaxRunsPerRecord).ToArray();
            return _records[index] = _records[index] with { Runs = runs };
        }
    }

    public void Save()
    {
        VerificationStoreFile file;
        lock (_gate) file = new() { Records = _records.ToList() };
        // Drop the oldest records until the file fits the shared size limit.
        while (true)
        {
            try
            {
                AtomicJsonFile.Write(_path, file);
                return;
            }
            catch (InvalidDataException) when (file.Records.Count > 1)
            {
                file.Records.RemoveRange(0, Math.Max(1, file.Records.Count / 4));
                lock (_gate) _records = file.Records.ToList();
            }
        }
    }

    private static string? Trim(string? value) =>
        value is null || value.Length <= MaxEvidenceChars ? value : value[..MaxEvidenceChars] + "… (truncated)";
}

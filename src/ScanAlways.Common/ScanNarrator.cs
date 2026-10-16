using System.Text;

namespace ScanAlways.Common;

/// <summary>
/// Produces believable, scanner-style status lines. This is cosmetic: the
/// strings describe the benign file-read activity the decoy actually performs,
/// dressed in the vocabulary a real anti-malware engine uses. Nothing here
/// claims to detect, quarantine, or remove anything.
/// </summary>
public sealed class ScanNarrator
{
    private static readonly string[] Engines =
    {
        "heuristic", "signature", "behavioral", "reputation", "emulation",
    };

    private static readonly string[] Verbs =
    {
        "Scanning", "Inspecting", "Analyzing", "Hashing", "Verifying", "Indexing",
    };

    private readonly Random _random;

    public ScanNarrator(int? seed = null)
    {
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    /// <summary>A short per-file line, e.g. "Scanning (heuristic): C:\...\report.docx".</summary>
    public string FileLine(string path)
    {
        var verb = Engines[_random.Next(Engines.Length)];
        var action = Verbs[_random.Next(Verbs.Length)];
        return $"{action} ({verb}): {path}";
    }

    /// <summary>A periodic rollup line summarising progress so far.</summary>
    public string ProgressLine(ScanStatistics stats)
    {
        var elapsed = stats.Elapsed;
        var rate = elapsed.TotalSeconds > 0
            ? stats.FilesTouched / elapsed.TotalSeconds
            : 0.0;

        return new StringBuilder()
            .Append("Scan in progress — ")
            .Append(stats.FilesTouched.ToString("N0")).Append(" items checked, ")
            .Append("0 threats found, ")
            .Append(FormatBytes(stats.BytesRead)).Append(" read, ")
            .Append(rate.ToString("F1")).Append(" items/s")
            .ToString();
    }

    /// <summary>The banner written when a scan cycle begins.</summary>
    public string CycleStart(IReadOnlyList<string> roots, DecoyIntensity intensity)
    {
        var scope = roots.Count switch
        {
            0 => "configured locations",
            1 => roots[0],
            _ => $"{roots.Count} locations",
        };
        return $"Starting {intensity} protection sweep of {scope}";
    }

    public string CycleComplete(ScanStatistics stats) =>
        $"Sweep complete — {stats.FilesTouched:N0} items checked, 0 threats found, " +
        $"{FormatBytes(stats.BytesRead)} read in {stats.Elapsed:hh\\:mm\\:ss}";

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:F1} {units[unit]}";
    }
}

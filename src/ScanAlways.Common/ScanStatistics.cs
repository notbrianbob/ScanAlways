using System.Diagnostics;

namespace ScanAlways.Common;

/// <summary>
/// Running totals for a decoy scan session. Thread-affine to the engine loop;
/// the engine is the only writer.
/// </summary>
public sealed class ScanStatistics
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    /// <summary>Number of files the decoy has opened and read a prefix from.</summary>
    public long FilesTouched { get; private set; }

    /// <summary>Total bytes read across all touched files.</summary>
    public long BytesRead { get; private set; }

    /// <summary>Files skipped (too large, locked, or access denied).</summary>
    public long FilesSkipped { get; private set; }

    /// <summary>Number of completed full sweeps over the scan roots.</summary>
    public long CyclesCompleted { get; private set; }

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public void RecordRead(long bytes)
    {
        FilesTouched++;
        BytesRead += bytes;
    }

    public void RecordSkip() => FilesSkipped++;

    public void RecordCycle() => CyclesCompleted++;
}

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WatchLattice.Common;

/// <summary>
/// The heart of the always-scanning decoy.
///
/// It continuously walks a set of directories, opens each file read-only, reads
/// a small prefix and hashes it, then narrates the activity in anti-malware
/// vocabulary. The point is deception defense: make the machine look like a
/// live scan is always running so that malware which lies dormant under
/// observation stays dormant. It is honest about its limits — it detects
/// nothing, changes nothing, and removes nothing. Every file is opened with
/// read-only, shared access and the bytes are discarded after hashing.
/// </summary>
public sealed class DecoyEngine
{
    private readonly DecoyOptions _options;
    private readonly ILogger<DecoyEngine> _logger;
    private readonly ScanNarrator _narrator;
    private readonly TimeProvider _time;

    /// <summary>Shortest wait after a sweep that found nothing, whatever the settings say.</summary>
    private static readonly TimeSpan EmptySweepFloor = TimeSpan.FromSeconds(1);

    public DecoyEngine(IOptions<DecoyOptions> options, ILogger<DecoyEngine> logger, TimeProvider? time = null)
    {
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _narrator = new ScanNarrator(_options.RandomSeed);
    }

    /// <summary>Raised on every narration line, so a UI host can show live status.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Live totals for the current session.</summary>
    public ScanStatistics Statistics { get; } = new();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var roots = _options.ResolveScanRoots();
        if (roots.Count == 0)
        {
            _logger.LogWarning("No existing scan roots resolved; the decoy has nothing to sweep.");
            return;
        }

        using var log = _options.WriteScanLog ? new ScanLogWriter(ResolveLogPath()) : null;
        var (filesPerSecond, narrateEvery) = _options.Pace();
        var minInterval = TimeSpan.FromSeconds(1.0 / Math.Max(1, filesPerSecond));
        var narrateInterval = TimeSpan.FromSeconds(narrateEvery);
        var minSweepInterval = TimeSpan.FromSeconds(Math.Max(0, _options.MinSweepIntervalSeconds));
        var emptySweepInterval = minSweepInterval > EmptySweepFloor ? minSweepInterval : EmptySweepFloor;

        _logger.LogInformation(
            "Decoy starting: intensity={Intensity}, roots={RootCount}, cap={Cap} files/s, log={Log}",
            _options.Intensity, roots.Count, filesPerSecond, log?.Path ?? "(disabled)");

        try
        {
            var warnedEmpty = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                var sweepStart = _time.GetTimestamp();
                var filesFound = await RunCycleAsync(roots, log, minInterval, narrateInterval, cancellationToken);
                Statistics.RecordCycle();
                Emit(log, _narrator.CycleComplete(Statistics));

                // Warn once per empty spell rather than once per sweep.
                if (filesFound == 0 && !warnedEmpty)
                {
                    _logger.LogWarning(
                        "The scan roots contain no files; the decoy will look again every {Interval}.",
                        emptySweepInterval);
                }
                warnedEmpty = filesFound == 0;

                // A sweep over few or no files ends almost at once. Without this
                // floor the next sweep would start immediately and spin.
                var floor = filesFound == 0 ? emptySweepInterval : minSweepInterval;
                var rest = floor - _time.GetElapsedTime(sweepStart);
                if (rest > TimeSpan.Zero)
                {
                    await Task.Delay(rest, _time, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _logger.LogInformation(
            "Decoy stopped after {Files:N0} items across {Cycles} sweeps.",
            Statistics.FilesTouched, Statistics.CyclesCompleted);
    }

    /// <summary>Runs one sweep and returns how many files it found.</summary>
    private async Task<long> RunCycleAsync(
        IReadOnlyList<string> roots,
        ScanLogWriter? log,
        TimeSpan minInterval,
        TimeSpan narrateInterval,
        CancellationToken cancellationToken)
    {
        Emit(log, _narrator.CycleStart(roots, _options.Intensity));
        var lastNarrate = _time.GetUtcNow();
        var buffer = new byte[Math.Clamp(_options.BytesReadPerFile, 4096, 1024 * 1024)];
        long filesFound = 0;

        foreach (var file in FileWalker.Enumerate(roots, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            filesFound++;

            var start = _time.GetUtcNow();
            TouchFile(file, buffer);

            if (_time.GetUtcNow() - lastNarrate >= narrateInterval)
            {
                Emit(log, _narrator.FileLine(file));
                Emit(log, _narrator.ProgressLine(Statistics), toLogger: false);
                lastNarrate = _time.GetUtcNow();
            }

            var spent = _time.GetUtcNow() - start;
            var remaining = minInterval - spent;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, _time, cancellationToken);
            }
        }

        return filesFound;
    }

    /// <summary>Opens the file read-only, reads a prefix, hashes it, discards it.</summary>
    private void TouchFile(string path, byte[] buffer)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > _options.MaxFileSizeBytes)
            {
                Statistics.RecordSkip();
                return;
            }

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var toRead = (int)Math.Min(buffer.Length, info.Length);
            var read = toRead > 0 ? stream.Read(buffer, 0, toRead) : 0;

            // Hashing the prefix is what a signature scanner would do; we throw
            // the result away. It exists only to make the read look purposeful
            // to anything watching process I/O.
            if (read > 0)
            {
                _ = SHA256.HashData(buffer.AsSpan(0, read));
            }

            Statistics.RecordRead(read);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException)
        {
            Statistics.RecordSkip();
        }
    }

    private string ResolveLogPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.ScanLogPath))
        {
            return Environment.ExpandEnvironmentVariables(_options.ScanLogPath);
        }
        return Path.Combine(Path.GetTempPath(), "ScanAlways", "scan-activity.log");
    }

    private void Emit(ScanLogWriter? log, string message, bool toLogger = true)
    {
        log?.WriteLine(message);
        if (toLogger)
        {
            _logger.LogInformation("{ScanLine}", message);
        }
        StatusChanged?.Invoke(message);
    }
}

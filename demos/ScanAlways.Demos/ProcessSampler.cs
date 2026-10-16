using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace ScanAlways.Demos;

/// <summary>One reading of this process's resource counters.</summary>
public sealed record ProcessSample(
    TimeSpan Elapsed,
    TimeSpan CpuTime,
    long? LogicalReadBytes,
    long? LogicalReadOps,
    long WorkingSetBytes,
    long ManagedHeapBytes);

/// <summary>
/// Totals over a measurement window, computed from the first and last sample.
/// Rates are per wall-clock second.
/// </summary>
public sealed record WindowResult(
    string Name,
    TimeSpan Wall,
    TimeSpan Cpu,
    long? ReadBytes,
    long? ReadOps,
    long PeakWorkingSetBytes,
    long PeakManagedHeapBytes,
    double CpuPercentStdDev,
    int Intervals)
{
    /// <summary>Standard error of the window's mean CPU %, from the per-interval spread.</summary>
    public double CpuPercentStdError => Intervals > 1 ? CpuPercentStdDev / Math.Sqrt(Intervals) : 0;

    /// <summary>CPU time as a percentage of one logical core.</summary>
    public double CpuPercentOfOneCore => Wall.TotalSeconds > 0 ? 100.0 * Cpu.TotalSeconds / Wall.TotalSeconds : 0;

    /// <summary>CPU time as a percentage of the whole machine (all logical cores).</summary>
    public double CpuPercentOfMachine => CpuPercentOfOneCore / Environment.ProcessorCount;

    public double? ReadBytesPerSecond => ReadBytes is { } b && Wall.TotalSeconds > 0 ? b / Wall.TotalSeconds : null;

    public double? ReadOpsPerSecond => ReadOps is { } o && Wall.TotalSeconds > 0 ? o / Wall.TotalSeconds : null;
}

/// <summary>
/// Samples this process's CPU time, logical read I/O and memory once per
/// interval. The I/O counters are the operating system's own:
/// <c>GetProcessIoCounters</c> (ReadTransferCount / ReadOperationCount) on
/// Windows and <c>/proc/self/io</c> (rchar / syscr) on Linux. Both count bytes
/// the process asked to read, whether or not they came from the disk or the
/// file cache, which is what a file-activity monitor sees. Elsewhere the I/O
/// columns are left empty rather than guessed.
/// </summary>
public sealed class ProcessSampler
{
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public ProcessSample Sample()
    {
        _self.Refresh();
        var (bytes, ops) = ReadIo();
        return new ProcessSample(
            _clock.Elapsed,
            _self.TotalProcessorTime,
            bytes,
            ops,
            _self.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false));
    }

    /// <summary>
    /// Takes a few throwaway samples so JIT compilation of the sampler itself
    /// does not land in the first measured window.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 5; i++)
        {
            _ = Sample();
            _ = Summarize("warm-up", new[] { Sample(), Sample() });
            _ = ToCsv(new[] { Sample() });
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Samples every <paramref name="interval"/> until the window ends, then summarizes.</summary>
    public async Task<(WindowResult Result, IReadOnlyList<ProcessSample> Samples)> MeasureAsync(
        string name, TimeSpan window, TimeSpan interval, CancellationToken cancellationToken)
    {
        var samples = new List<ProcessSample> { Sample() };
        var end = samples[0].Elapsed + window;
        while (_clock.Elapsed < end && !cancellationToken.IsCancellationRequested)
        {
            var wait = end - _clock.Elapsed;
            await Task.Delay(wait < interval ? wait : interval, cancellationToken).ConfigureAwait(false);
            samples.Add(Sample());
        }
        return (Summarize(name, samples), samples);
    }

    public static WindowResult Summarize(string name, IReadOnlyList<ProcessSample> samples)
    {
        var first = samples[0];
        var last = samples[^1];
        return new WindowResult(
            name,
            last.Elapsed - first.Elapsed,
            last.CpuTime - first.CpuTime,
            last.LogicalReadBytes - first.LogicalReadBytes,
            last.LogicalReadOps - first.LogicalReadOps,
            samples.Max(s => s.WorkingSetBytes),
            samples.Max(s => s.ManagedHeapBytes),
            IntervalCpuStdDev(samples),
            samples.Count - 1);
    }

    /// <summary>
    /// Standard deviation of CPU use (% of one core) across sampling
    /// intervals. On the idle baseline it is this method's noise floor.
    /// </summary>
    private static double IntervalCpuStdDev(IReadOnlyList<ProcessSample> samples)
    {
        var rates = new List<double>();
        for (var i = 1; i < samples.Count; i++)
        {
            var wall = (samples[i].Elapsed - samples[i - 1].Elapsed).TotalSeconds;
            if (wall > 0)
            {
                rates.Add(100.0 * (samples[i].CpuTime - samples[i - 1].CpuTime).TotalSeconds / wall);
            }
        }
        if (rates.Count < 2)
        {
            return 0;
        }
        var mean = rates.Average();
        return Math.Sqrt(rates.Sum(r => (r - mean) * (r - mean)) / (rates.Count - 1));
    }

    public static string ToCsv(IReadOnlyList<ProcessSample> samples)
    {
        var lines = new List<string> { "elapsed_s,cpu_ms,read_bytes,read_ops,working_set_bytes,managed_heap_bytes" };
        lines.AddRange(samples.Select(s => string.Join(',',
            s.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture),
            s.CpuTime.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture),
            s.LogicalReadBytes?.ToString(CultureInfo.InvariantCulture) ?? "",
            s.LogicalReadOps?.ToString(CultureInfo.InvariantCulture) ?? "",
            s.WorkingSetBytes.ToString(CultureInfo.InvariantCulture),
            s.ManagedHeapBytes.ToString(CultureInfo.InvariantCulture))));
        return string.Join('\n', lines) + "\n";
    }

    /// <summary>Which OS counter the I/O columns come from, for the report.</summary>
    public static string IoCounterSource =>
        OperatingSystem.IsWindows() ? "GetProcessIoCounters (ReadTransferCount, ReadOperationCount)"
        : OperatingSystem.IsLinux() ? "/proc/self/io (rchar, syscr)"
        : "not available on this OS";

    private (long? Bytes, long? Ops) ReadIo()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetProcessIoCounters(_self.Handle, out var counters)
                ? ((long)counters.ReadTransferCount, (long)counters.ReadOperationCount)
                : (null, null);
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                long? rchar = null, syscr = null;
                foreach (var line in File.ReadLines("/proc/self/io"))
                {
                    var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    {
                        continue;
                    }
                    if (parts[0] == "rchar") rchar = value;
                    else if (parts[0] == "syscr") syscr = value;
                }
                return (rchar, syscr);
            }
            catch (IOException)
            {
                return (null, null);
            }
        }

        return (null, null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IoCounters counters);
}

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WatchLattice.Common;

namespace WatchLattice.Demos;

/// <summary>
/// Runs the decoy engine against a seeded synthetic corpus and measures what it
/// costs: CPU time, logical bytes read, and memory, first with nothing running
/// (the harness's own baseline) and then once per intensity.
/// </summary>
public static class DecoyDemo
{
    public static async Task<Report> RunAsync(DemoArgs args, CancellationToken cancellationToken)
    {
        var report = new Report(Path.Combine(args.OutputDir, "decoy"), "Always-scanning decoy");
        var corpusRoot = args.CorpusDir ?? Path.Combine(args.WorkDir, "decoy-corpus");
        if (Directory.Exists(corpusRoot) && args.CorpusDir is null)
        {
            Directory.Delete(corpusRoot, recursive: true);
        }

        var corpus = args.CorpusDir is not null && Directory.Exists(corpusRoot)
            ? new Corpus.Summary(corpusRoot, Directory.EnumerateFiles(corpusRoot, "*", SearchOption.AllDirectories).Count(), 0, 0)
            : Corpus.Create(corpusRoot, args.CorpusFiles, args.Seed);

        report.Line("### Setup");
        report.Line();
        report.Line(corpus.TotalBytes > 0
            ? Report.Inv($"- Corpus: {corpus.Files:N0} files, {Report.Bytes(corpus.TotalBytes)}, seed {args.Seed}, in {corpus.Folders} folders")
            : Report.Inv($"- Corpus: {corpus.Files:N0} existing files in `{corpusRoot}`"));
        report.Line(Report.Inv($"- Window: {args.Seconds} s per intensity, sampled every {args.SampleMs} ms, after a {args.BaselineSeconds} s baseline"));
        report.Line($"- I/O counter: {ProcessSampler.IoCounterSource}");
        report.Line("- Engine settings: the hub defaults (`MaxFilesPerSecond` 8, `BytesReadPerFile` 64 KiB, `MaxFileSizeBytes` 256 MiB)");
        report.Line();

        var sampler = new ProcessSampler();
        await sampler.WarmUpAsync(cancellationToken);
        var (baseline, baselineSamples) = await sampler.MeasureAsync(
            "Baseline (harness idle)", TimeSpan.FromSeconds(args.BaselineSeconds), TimeSpan.FromMilliseconds(args.SampleMs), cancellationToken);
        report.WriteFile("samples-baseline.csv", ProcessSampler.ToCsv(baselineSamples));

        var results = new List<(WindowResult Window, ScanStatistics Stats)>();
        var logExcerpt = new List<string>();
        foreach (var intensity in args.Intensities)
        {
            var logPath = Path.Combine(report.OutputDir, $"scan-activity-{intensity}.log");
            File.Delete(logPath);
            var options = new DecoyOptions
            {
                Intensity = intensity,
                ScanRoots = new List<string> { corpusRoot },
                ScanLogPath = logPath,
                RandomSeed = args.Seed,
            };
            var engine = new DecoyEngine(Options.Create(options), NullLogger<DecoyEngine>.Instance);

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var run = Task.Run(() => engine.RunAsync(stop.Token), CancellationToken.None);
            var (window, samples) = await sampler.MeasureAsync(
                $"Decoy {intensity}", TimeSpan.FromSeconds(args.Seconds), TimeSpan.FromMilliseconds(args.SampleMs), cancellationToken);
            stop.Cancel();
            await run;

            results.Add((window, engine.Statistics));
            report.WriteFile($"samples-{intensity}.csv", ProcessSampler.ToCsv(samples));
            if (logExcerpt.Count == 0 && File.Exists(logPath))
            {
                logExcerpt.AddRange(ReadShared(logPath).Take(8));
            }
        }

        // Cost per file: the same engine with the pacing cap lifted, so enough
        // files are opened in a short window for the CPU time to rise well
        // above the noise floor. Only the ratio of CPU time to files is used.
        var fast = await MeasureFastAsync(sampler, corpusRoot, args, report, cancellationToken);

        report.Line("### Measured overhead");
        report.Line();
        report.Line("| Window | CPU (% of one core) | CPU (% of machine) | Logical reads | Read calls/s | Files opened | Peak working set | Peak managed heap |");
        report.Line("|---|---:|---:|---:|---:|---:|---:|---:|");
        report.Line(Row(baseline, null));
        foreach (var (window, stats) in results)
        {
            report.Line(Row(window, stats));
        }
        report.Line();
        report.Line(Report.Inv(
            $"Noise: CPU use per {args.SampleMs} ms interval varies by ±{baseline.CpuPercentStdDev:F2}% of one core on the idle baseline (one standard deviation), from the .NET runtime and the sampler itself, so each window's mean is only known to about ±{baseline.CpuPercentStdError:F2}% (standard error). A decoy row within that of the baseline means the decoy's CPU cost is too small for this method to resolve; the cost-per-file run below measures it directly."));
        report.Line();
        report.Line("Working set is the whole harness process (the .NET runtime included), so read it as an upper bound for the module.");
        report.Line();

        report.Line("### Cost per file");
        report.Line();
        var (fastWindow, fastStats) = fast;
        var cpuPerFile = fastStats.FilesTouched > 0 ? (fastWindow.Cpu - baseline.Cpu / baseline.Wall.TotalSeconds * fastWindow.Wall.TotalSeconds).TotalMilliseconds / fastStats.FilesTouched : 0;
        report.Line(Report.Inv(
            $"With the pacing cap lifted for {args.FastSeconds} s, the engine opened {fastStats.FilesTouched:N0} files ({fastStats.FilesTouched / fastWindow.Wall.TotalSeconds:F0}/s) and read {Report.Bytes(fastStats.BytesRead)}, using {fastWindow.Cpu.TotalMilliseconds:F0} ms of CPU."));
        report.Line(Report.Inv(
            $"After subtracting the baseline rate, that is **{cpuPerFile:F3} ms of CPU per file**. At the paced rates this predicts:"));
        report.Line();
        report.Line("| Intensity | Files/s | Predicted CPU (% of one core) |");
        report.Line("|---|---:|---:|");
        foreach (var intensity in new[] { DecoyIntensity.Idle, DecoyIntensity.Balanced, DecoyIntensity.FullScan })
        {
            var rate = new DecoyOptions { Intensity = intensity }.Pace().FilesPerSecond;
            report.Line(Report.Inv($"| {intensity} | {rate} | {cpuPerFile * rate / 10.0:F3} |"));
        }
        report.Line();
        report.Line("The corpus was written just before the run, so its files are in the OS file cache: these are CPU costs with warm-cache reads. On a cold cache each file read also waits on the disk, which adds latency but little CPU.");
        report.Line();

        report.Line("### What it emits");
        report.Line();
        report.Line("First lines of the scanner-style log (`scan-activity.log`), copied verbatim:");
        report.Line();
        report.Code(logExcerpt.Select(l => l.Replace(corpusRoot, "<corpus>", StringComparison.Ordinal)));

        report.Save();
        return report;
    }

    private static async Task<(WindowResult Window, ScanStatistics Stats)> MeasureFastAsync(
        ProcessSampler sampler, string corpusRoot, DemoArgs args, Report report, CancellationToken cancellationToken)
    {
        var options = new DecoyOptions
        {
            Intensity = DecoyIntensity.FullScan,
            // Above 1,000 files/s the per-file delay rounds to zero, so the
            // engine runs flat out (on its own thread, see Task.Run below).
            MaxFilesPerSecond = 100_000,
            ScanRoots = new List<string> { corpusRoot },
            WriteScanLog = false,
            RandomSeed = args.Seed,
        };
        var engine = new DecoyEngine(Options.Create(options), NullLogger<DecoyEngine>.Instance);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var run = Task.Run(() => engine.RunAsync(stop.Token), CancellationToken.None);
        var (window, samples) = await sampler.MeasureAsync(
            "Decoy, cap lifted", TimeSpan.FromSeconds(args.FastSeconds), TimeSpan.FromMilliseconds(args.SampleMs), cancellationToken);
        stop.Cancel();
        await run;
        report.WriteFile("samples-cap-lifted.csv", ProcessSampler.ToCsv(samples));
        return (window, engine.Statistics);
    }

    private static string Row(WindowResult w, ScanStatistics? stats)
    {
        var reads = w.ReadBytesPerSecond is { } bps ? $"{Report.Bytes(bps)}/s" : "n/a";
        var ops = w.ReadOpsPerSecond is { } o ? Report.Inv($"{o:F1}") : "n/a";
        var files = stats is null ? "0" : Report.Inv($"{stats.FilesTouched:N0} ({stats.FilesTouched / w.Wall.TotalSeconds:F1}/s)");
        return Report.Inv(
            $"| {w.Name} | {w.CpuPercentOfOneCore:F2} | {w.CpuPercentOfMachine:F3} | {reads} | {ops} | {files} | {Report.Bytes(w.PeakWorkingSetBytes)} | {Report.Bytes(w.PeakManagedHeapBytes)} |");
    }

    private static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}

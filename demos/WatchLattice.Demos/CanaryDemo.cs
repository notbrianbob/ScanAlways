using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WatchLattice.Canary;

namespace WatchLattice.Demos;

/// <summary>
/// Plants canaries in a throwaway folder, measures the watcher's idle cost,
/// then does what ransomware does to them (overwrite, rename, delete) and
/// times how long each alert takes. A normal file edited in the same folder is
/// the control: it must not raise anything.
/// </summary>
public static class CanaryDemo
{
    private static readonly TimeSpan AlertTimeout = TimeSpan.FromSeconds(20);

    public static async Task<Report> RunAsync(DemoArgs args, CancellationToken cancellationToken)
    {
        var report = new Report(Path.Combine(args.OutputDir, "canary"), "Canary file watcher");
        var home = Path.Combine(args.WorkDir, "canary-home");
        if (Directory.Exists(home))
        {
            Directory.Delete(home, recursive: true);
        }
        var documents = Path.Combine(home, "Documents");
        Directory.CreateDirectory(documents);
        var alertLog = Path.Combine(report.OutputDir, "canary-alerts.log");
        File.Delete(alertLog);

        var options = new CanaryOptions
        {
            Folders = new List<string> { documents },
            AlertLogPath = alertLog,
            ManifestPath = Path.Combine(home, "canary-manifest.json"),
            // The integrity check is the backstop; the hub default is 60 s.
            IntegrityCheckSeconds = 60,
        };

        var alerts = new List<(CanaryAlert Alert, long Ticks)>();
        var alertArrived = new SemaphoreSlim(0);
        var clock = Stopwatch.StartNew();

        using var watcher = new CanaryWatcher(Options.Create(options), NullLogger<CanaryWatcher>.Instance);
        watcher.AlertRaised += alert =>
        {
            lock (alerts)
            {
                alerts.Add((alert, clock.ElapsedTicks));
            }
            alertArrived.Release();
        };

        var sampler = new ProcessSampler();
        await sampler.WarmUpAsync(cancellationToken);
        var (baseline, baselineSamples) = await sampler.MeasureAsync(
            "Baseline (harness idle, nothing armed)", TimeSpan.FromSeconds(args.CanaryIdleSeconds), TimeSpan.FromMilliseconds(args.SampleMs), cancellationToken);
        report.WriteFile("samples-baseline.csv", ProcessSampler.ToCsv(baselineSamples));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var run = Task.Run(() => watcher.RunAsync(stop.Token), CancellationToken.None);
        var armed = Stopwatch.StartNew();
        while (watcher.ActiveCanaries.Count == 0 && armed.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50, cancellationToken);
        }
        var canaries = watcher.ActiveCanaries.OrderBy(p => p, StringComparer.Ordinal).ToList();

        report.Line("### Setup");
        report.Line();
        report.Line($"- Planted {canaries.Count} canaries in a throwaway `Documents` folder:");
        foreach (var path in canaries)
        {
            report.Line($"  - `{Path.GetFileName(path)}` ({Report.Bytes(new FileInfo(path).Length)})");
        }
        report.Line(Report.Inv($"- Windows: {args.CanaryIdleSeconds} s before arming (baseline) and {args.CanaryIdleSeconds} s armed with nothing happening, sampled every {args.SampleMs} ms"));
        report.Line($"- I/O counter: {ProcessSampler.IoCounterSource}");
        report.Line();

        var (idle, idleSamples) = await sampler.MeasureAsync(
            "Watcher armed, nothing happening", TimeSpan.FromSeconds(args.CanaryIdleSeconds), TimeSpan.FromMilliseconds(args.SampleMs), cancellationToken);
        report.WriteFile("samples-idle.csv", ProcessSampler.ToCsv(idleSamples));

        report.Line("### Idle overhead");
        report.Line();
        report.Line("| Window | CPU (% of one core) | Logical reads | Peak working set |");
        report.Line("|---|---:|---:|---:|");
        foreach (var w in new[] { baseline, idle })
        {
            report.Line(Report.Inv(
                $"| {w.Name} | {w.CpuPercentOfOneCore:F3} | {(w.ReadBytesPerSecond is { } r ? Report.Bytes(r) + "/s" : "n/a")} | {Report.Bytes(w.PeakWorkingSetBytes)} |"));
        }
        report.Line();
        report.Line("The difference between the two rows is the armed watcher's cost. Its integrity re-hash runs every 60 s by default, so a window shorter than that may not include one. Working set is the whole harness process, including anything an earlier demo in the same run allocated.");
        report.Line();

        // Control first: an ordinary file next to the canaries.
        var control = Path.Combine(documents, "meeting-notes.txt");
        var steps = new List<(string Action, Action Do, bool ExpectAlert)>
        {
            ("Create and edit an ordinary file next to the canaries (control)", () =>
            {
                File.WriteAllText(control, "agenda\n");
                File.AppendAllText(control, "more notes\n");
            }, false),
            ("Overwrite the first 4 KiB of a canary with random bytes (in-place encryption)", () =>
            {
                using var stream = new FileStream(canaries[0], FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                var junk = new byte[4096];
                new Random(args.Seed).NextBytes(junk);
                stream.Write(junk);
            }, true),
            ("Rename a canary to `.locked` (encrypt-and-rename)", () =>
                File.Move(canaries[1], canaries[1] + ".locked"), true),
            ("Delete a canary", () => File.Delete(canaries[2]), true),
        };

        report.Line("### Tripping the canaries");
        report.Line();
        report.Line("| Action | Expected | Result | Time to alert |");
        report.Line("|---|---|---|---:|");
        var allAsExpected = true;
        foreach (var (action, act, expectAlert) in steps)
        {
            int before;
            lock (alerts)
            {
                before = alerts.Count;
            }
            while (alertArrived.CurrentCount > 0)
            {
                await alertArrived.WaitAsync(cancellationToken);
            }

            var started = clock.ElapsedTicks;
            act();
            var got = await alertArrived.WaitAsync(expectAlert ? AlertTimeout : TimeSpan.FromSeconds(3), cancellationToken);

            string result, latency;
            if (got)
            {
                CanaryAlert alert;
                long at;
                lock (alerts)
                {
                    (alert, at) = alerts[before];
                }
                result = $"alert: {alert.Change}" + (string.IsNullOrEmpty(alert.Detail) ? "" : $" ({alert.Detail})");
                latency = Report.Inv($"{(at - started) * 1000.0 / Stopwatch.Frequency:F1} ms");
            }
            else
            {
                result = "no alert";
                latency = "n/a";
            }
            allAsExpected &= got == expectAlert;
            report.Line($"| {action} | {(expectAlert ? "alert" : "no alert")} | {result} | {latency} |");

            // Let the debounce window and any trailing events from this step pass.
            await Task.Delay(500, cancellationToken);
        }
        report.Line();
        report.Line(allAsExpected
            ? "Every step behaved as expected."
            : "**At least one step did not behave as expected; see the table.**");
        report.Line();

        stop.Cancel();
        await run;

        report.Line("### What it emits");
        report.Line();
        report.Line("The canary alert log (`canary-alerts.log`), copied verbatim:");
        report.Line();
        report.Code(File.Exists(alertLog)
            ? File.ReadAllLines(alertLog).Select(l => l.Replace(documents, "<Documents>", StringComparison.Ordinal))
            : new[] { "(no log written)" });

        report.Save();
        if (!allAsExpected)
        {
            Environment.ExitCode = 1;
        }
        return report;
    }
}

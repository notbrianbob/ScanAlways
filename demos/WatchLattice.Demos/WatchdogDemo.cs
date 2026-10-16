using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WatchLattice.DefenderWatchdog;

namespace WatchLattice.Demos;

/// <summary>
/// Shows the Defender watchdog's readout for a healthy machine and for the
/// ways Defender is commonly degraded, then walks one problem appearing and
/// clearing to show that alerts fire on change only. The scenarios are
/// recorded <c>Get-MpComputerStatus</c> shapes fed through the real evaluator;
/// the demo never changes a Defender setting. With <c>--live</c> on Windows it
/// also reads this machine's real status (read-only) and times the query.
/// </summary>
public static class WatchdogDemo
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly DefenderStatus Healthy = new()
    {
        AMServiceEnabled = true,
        AntivirusEnabled = true,
        RealTimeProtectionEnabled = true,
        BehaviorMonitorEnabled = true,
        IoavProtectionEnabled = true,
        OnAccessProtectionEnabled = true,
        IsTamperProtected = true,
        RunningMode = "Normal",
        SignatureVersion = "1.419.120.0",
        SignatureLastUpdated = Now.AddHours(-6),
    };

    private static readonly (string Name, DefenderStatus Status)[] Scenarios =
    {
        ("Healthy", Healthy),
        ("Real-time protection switched off", Healthy with { RealTimeProtectionEnabled = false, OnAccessProtectionEnabled = false }),
        ("Tamper protection off, signatures 3 days old", Healthy with { IsTamperProtected = false, SignatureLastUpdated = Now.AddDays(-3) }),
        ("Signatures 9 days old", Healthy with { SignatureLastUpdated = Now.AddDays(-9) }),
        ("Another antivirus is primary (passive mode)", Healthy with
        {
            RunningMode = "Passive Mode", RealTimeProtectionEnabled = false, BehaviorMonitorEnabled = false,
            IoavProtectionEnabled = false, OnAccessProtectionEnabled = false,
        }),
        ("Status cannot be read", DefenderStatus.Unavailable("Get-MpComputerStatus failed (exit 1): module not found")),
    };

    public static async Task<Report> RunAsync(DemoArgs args, CancellationToken cancellationToken)
    {
        var report = new Report(Path.Combine(args.OutputDir, "watchdog"), "Defender health watchdog");
        var options = new DefenderWatchdogOptions();

        report.Line("### Readouts for recorded states");
        report.Line();
        report.Line("Each state is a `Get-MpComputerStatus` result run through the real evaluator with the default thresholds.");
        report.Line();
        report.Line("| State | Health | Findings |");
        report.Line("|---|---|---|");
        foreach (var (name, status) in Scenarios)
        {
            var result = DefenderHealthEvaluator.Evaluate(status, options, Now);
            var findings = result.Findings.Count == 0 ? "none" : string.Join("<br>", result.Findings.Select(f => f.ToString()));
            report.Line($"| {name} | **{result.Health}** | {findings} |");
        }
        report.Line();

        report.Line("### Alerting on change");
        report.Line();
        report.Line("Five checks in a row: healthy, real-time protection turned off, still off, back on, healthy.");
        report.Line("The watchdog log shows one alert when the problem appears and one line when it clears:");
        report.Line();
        var alertLog = Path.Combine(report.OutputDir, "defender-watchdog.log");
        File.Delete(alertLog);
        var off = Healthy with { RealTimeProtectionEnabled = false };
        var provider = new SequenceProvider(Healthy, off, off, Healthy, Healthy);
        using (var watchdog = new WatchLattice.DefenderWatchdog.DefenderWatchdog(
            provider,
            Options.Create(new DefenderWatchdogOptions { AlertLogPath = alertLog }),
            NullLogger<WatchLattice.DefenderWatchdog.DefenderWatchdog>.Instance,
            () => Now))
        {
            var readouts = new List<string>();
            for (var i = 1; i <= 5; i++)
            {
                var result = await watchdog.CheckOnceAsync(cancellationToken);
                readouts.Add($"check {i}: {result.Health,-8} {result.Summary}");
            }
            report.Code(readouts);
        }
        report.Line("`defender-watchdog.log`, copied verbatim:");
        report.Line();
        report.Code(File.Exists(alertLog) ? File.ReadAllLines(alertLog) : new[] { "(no log written)" });

        if (args.Live)
        {
            await LiveAsync(report, options, cancellationToken);
        }

        report.Save();
        return report;
    }

    private static async Task LiveAsync(Report report, DefenderWatchdogOptions options, CancellationToken cancellationToken)
    {
        report.Line("### Live readout of this machine");
        report.Line();
        if (!OperatingSystem.IsWindows())
        {
            report.Line("Skipped: Microsoft Defender status can only be read on Windows.");
            report.Line();
            return;
        }

        var provider = new PowerShellDefenderStatusProvider();
        var timings = new List<double>();
        DefenderStatus? status = null;
        for (var i = 0; i < 3; i++)
        {
            var watch = Stopwatch.StartNew();
            status = await provider.GetStatusAsync(cancellationToken);
            timings.Add(watch.Elapsed.TotalMilliseconds);
        }

        var result = DefenderHealthEvaluator.Evaluate(status!, options, DateTimeOffset.UtcNow);
        report.Line($"Health: **{result.Health}**. {result.Summary}");
        report.Line();
        report.Code(new[]
        {
            $"AMServiceEnabled          {status!.AMServiceEnabled}",
            $"AntivirusEnabled          {status.AntivirusEnabled}",
            $"RealTimeProtectionEnabled {status.RealTimeProtectionEnabled}",
            $"BehaviorMonitorEnabled    {status.BehaviorMonitorEnabled}",
            $"IoavProtectionEnabled     {status.IoavProtectionEnabled}",
            $"OnAccessProtectionEnabled {status.OnAccessProtectionEnabled}",
            $"IsTamperProtected         {status.IsTamperProtected}",
            $"AMRunningMode             {status.RunningMode}",
            $"SignatureLastUpdated      {status.SignatureLastUpdated:u}",
            $"Error                     {status.Error}",
        });
        timings.Sort();
        report.Line(Report.Inv(
            $"One check (starting Windows PowerShell and running `Get-MpComputerStatus`) took {timings[1]:F0} ms (median of 3; min {timings[0]:F0}, max {timings[2]:F0}). At the default 300 s interval that is one such call every five minutes."));
        report.Line();
    }

    private sealed class SequenceProvider : IDefenderStatusProvider
    {
        private readonly Queue<DefenderStatus> _states;

        public SequenceProvider(params DefenderStatus[] states) => _states = new Queue<DefenderStatus>(states);

        public Task<DefenderStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_states.Count > 1 ? _states.Dequeue() : _states.Peek());
    }
}

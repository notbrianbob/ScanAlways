using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static ScanAlways.DefenderWatchdog.DefenderHealthEvaluator;

namespace ScanAlways.DefenderWatchdog.Tests;

public class DefenderWatchdogTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"scanalways-watchdog-{Guid.NewGuid():N}.log");
    private readonly FakeProvider _provider = new();

    private DefenderWatchdog Create() => new(
        _provider,
        Options.Create(new DefenderWatchdogOptions { AlertLogPath = _logPath }),
        NullLogger<DefenderWatchdog>.Instance,
        () => DefenderHealthEvaluatorTests.Now);

    [Fact]
    public async Task HealthyChecks_RaiseNoAlerts()
    {
        using var watchdog = Create();
        var alerts = new List<DefenderFinding>();
        watchdog.AlertRaised += alerts.Add;

        await watchdog.CheckOnceAsync(CancellationToken.None);
        await watchdog.CheckOnceAsync(CancellationToken.None);

        Assert.Empty(alerts);
        Assert.Equal(DefenderHealth.Healthy, watchdog.LastReport?.Health);
    }

    [Fact]
    public async Task NewProblem_AlertsOnce_ThenRecoveryIsReported()
    {
        using var watchdog = Create();
        var raised = new List<DefenderFinding>();
        var cleared = new List<DefenderFinding>();
        watchdog.AlertRaised += raised.Add;
        watchdog.AlertCleared += cleared.Add;

        await watchdog.CheckOnceAsync(CancellationToken.None);

        _provider.Status = DefenderHealthEvaluatorTests.Healthy() with { RealTimeProtectionEnabled = false };
        await watchdog.CheckOnceAsync(CancellationToken.None);
        await watchdog.CheckOnceAsync(CancellationToken.None);

        var alert = Assert.Single(raised);
        Assert.Equal(RealTimeOff, alert.Code);
        Assert.Empty(cleared);

        _provider.Status = DefenderHealthEvaluatorTests.Healthy();
        await watchdog.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(RealTimeOff, Assert.Single(cleared).Code);
        Assert.Single(raised);

        // The watchdog still holds the log open for writing, so read it the way
        // a log viewer would: sharing write access. (Windows refuses a plain
        // File.ReadAllText here.)
        using var reader = new StreamReader(
            new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var log = reader.ReadToEnd();
        Assert.Contains("DEFENDER ALERT", log);
        Assert.Contains("Defender recovered", log);
    }

    [Fact]
    public async Task ProblemPresentAtStartup_IsAlerted()
    {
        _provider.Status = DefenderHealthEvaluatorTests.Healthy() with { IsTamperProtected = false };
        using var watchdog = Create();
        var raised = new List<DefenderFinding>();
        watchdog.AlertRaised += raised.Add;

        await watchdog.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(TamperProtectionOff, Assert.Single(raised).Code);
    }

    [Fact]
    public async Task ProviderException_IsReportedAsUnknown()
    {
        _provider.Throw = new InvalidOperationException("WMI exploded");
        using var watchdog = Create();
        var raised = new List<DefenderFinding>();
        watchdog.AlertRaised += raised.Add;

        var report = await watchdog.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(DefenderHealth.Unknown, report.Health);
        Assert.Equal(StatusUnavailable, Assert.Single(raised).Code);
    }

    [Fact]
    public async Task RunAsync_StopsCleanlyOnCancellation()
    {
        using var watchdog = Create();
        using var cts = new CancellationTokenSource();
        var checkedOnce = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watchdog.Checked += _ => checkedOnce.TrySetResult();

        var run = watchdog.RunAsync(cts.Token);
        await checkedOnce.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(run.IsCompletedSuccessfully);
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_logPath);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeProvider : IDefenderStatusProvider
    {
        public DefenderStatus Status { get; set; } = DefenderHealthEvaluatorTests.Healthy();
        public Exception? Throw { get; set; }

        public Task<DefenderStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Throw is not null ? Task.FromException<DefenderStatus>(Throw) : Task.FromResult(Status);
    }
}

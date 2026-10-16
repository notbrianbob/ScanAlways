using Xunit;
using static ScanAlways.DefenderWatchdog.DefenderHealthEvaluator;

namespace ScanAlways.DefenderWatchdog.Tests;

public class DefenderHealthEvaluatorTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);

    internal static DefenderStatus Healthy() => new()
    {
        AMServiceEnabled = true,
        AntivirusEnabled = true,
        RealTimeProtectionEnabled = true,
        BehaviorMonitorEnabled = true,
        IoavProtectionEnabled = true,
        OnAccessProtectionEnabled = true,
        IsTamperProtected = true,
        RunningMode = "Normal",
        SignatureLastUpdated = Now.AddHours(-3),
    };

    private static DefenderHealthReport Evaluate(DefenderStatus status, DefenderWatchdogOptions? options = null) =>
        DefenderHealthEvaluator.Evaluate(status, options ?? new DefenderWatchdogOptions(), Now);

    private static IEnumerable<string> Codes(DefenderHealthReport report) => report.Findings.Select(f => f.Code);

    [Fact]
    public void HealthyMachine_HasNoFindings()
    {
        var report = Evaluate(Healthy());

        Assert.Equal(DefenderHealth.Healthy, report.Health);
        Assert.Empty(report.Findings);
        Assert.StartsWith("Defender healthy", report.Summary);
    }

    [Fact]
    public void RealTimeProtectionOff_IsCritical()
    {
        var report = Evaluate(Healthy() with { RealTimeProtectionEnabled = false });

        Assert.Equal(DefenderHealth.Critical, report.Health);
        Assert.Contains(RealTimeOff, Codes(report));
        Assert.Contains("Real-time protection is OFF", report.Summary);
    }

    [Fact]
    public void AntivirusOrServiceDisabled_IsCritical()
    {
        var report = Evaluate(Healthy() with { AntivirusEnabled = false, AMServiceEnabled = false });

        Assert.Equal(DefenderHealth.Critical, report.Health);
        Assert.Contains(AntivirusDisabled, Codes(report));
        Assert.Contains(ServiceDisabled, Codes(report));
    }

    [Fact]
    public void TamperProtectionOff_IsWarningByDefault()
    {
        var report = Evaluate(Healthy() with { IsTamperProtected = false });

        Assert.Equal(DefenderHealth.Degraded, report.Health);
        Assert.Contains(TamperProtectionOff, Codes(report));
    }

    [Fact]
    public void TamperProtectionOff_CanBeMadeCritical()
    {
        var report = Evaluate(
            Healthy() with { IsTamperProtected = false },
            new DefenderWatchdogOptions { TamperProtectionOffIsCritical = true });

        Assert.Equal(DefenderHealth.Critical, report.Health);
    }

    [Fact]
    public void SubEnginesOff_AreWarnings()
    {
        var report = Evaluate(Healthy() with
        {
            BehaviorMonitorEnabled = false,
            IoavProtectionEnabled = false,
            OnAccessProtectionEnabled = false,
        });

        Assert.Equal(DefenderHealth.Degraded, report.Health);
        Assert.Contains(BehaviorMonitorOff, Codes(report));
        Assert.Contains(DownloadScanOff, Codes(report));
        Assert.Contains(OnAccessOff, Codes(report));
    }

    [Theory]
    [InlineData(47, DefenderHealth.Healthy, null)]
    [InlineData(49, DefenderHealth.Degraded, SignaturesStale)]
    [InlineData(24 * 8, DefenderHealth.Critical, SignaturesVeryStale)]
    public void SignatureAge_IsGraded(int hoursOld, DefenderHealth expected, string? code)
    {
        var report = Evaluate(Healthy() with { SignatureLastUpdated = Now.AddHours(-hoursOld) });

        Assert.Equal(expected, report.Health);
        if (code is not null)
        {
            Assert.Contains(code, Codes(report));
        }
    }

    [Fact]
    public void UnknownSignatureDate_IsInfoOnly()
    {
        var report = Evaluate(Healthy() with { SignatureLastUpdated = null });

        Assert.Equal(DefenderHealth.Healthy, report.Health);
        Assert.Contains(SignatureDateUnknown, Codes(report));
    }

    [Fact]
    public void UnknownFields_DoNotAlert()
    {
        var report = Evaluate(new DefenderStatus { RunningMode = "Normal", SignatureLastUpdated = Now });

        Assert.Equal(DefenderHealth.Healthy, report.Health);
    }

    [Fact]
    public void PassiveMode_IsOneWarningInsteadOfACascade()
    {
        var report = Evaluate(Healthy() with
        {
            RunningMode = "Passive Mode",
            RealTimeProtectionEnabled = false,
            BehaviorMonitorEnabled = false,
            SignatureLastUpdated = Now.AddDays(-30),
        });

        Assert.Equal(DefenderHealth.Degraded, report.Health);
        Assert.Equal(new[] { PassiveMode }, Codes(report));
    }

    [Fact]
    public void NotRunning_IsCritical()
    {
        var report = Evaluate(Healthy() with { RunningMode = "Not running" });

        Assert.Equal(DefenderHealth.Critical, report.Health);
        Assert.Contains(NotRunning, Codes(report));
    }

    [Fact]
    public void Unavailable_IsUnknownWithAWarning()
    {
        var report = Evaluate(DefenderStatus.Unavailable("boom"));

        Assert.Equal(DefenderHealth.Unknown, report.Health);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(StatusUnavailable, finding.Code);
        Assert.Contains("boom", finding.Message);
    }
}

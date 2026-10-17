using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScanAlways.Common.Alerts;
using Xunit;
using static ScanAlways.DefenderWatchdog.DefenderHealthEvaluator;

namespace ScanAlways.DefenderWatchdog.Tests;

public class DefenderSettingsTests : IDisposable
{
    private const string AsrRule = "56a863a9-875e-4185-98a7-b882c64b5ce5";
    private readonly string _dir = Path.Join(Path.GetTempPath(), $"scanalways-defender-{Guid.NewGuid():N}");
    private readonly FakeProvider _provider = new();

    private static DefenderExclusion PathExclusion(string value) => new(DefenderExclusionKinds.Path, value);

    private static DefenderSettings Settings(params DefenderExclusion[] exclusions) => new()
    {
        Exclusions = exclusions,
        AsrRules = new Dictionary<string, int> { [AsrRule] = 1 },
        MapsReporting = 2,
        DisableBlockAtFirstSeen = false,
        CloudBlockLevel = 2,
    };

    private static DefenderStatus WithSettings(DefenderSettings settings) =>
        DefenderHealthEvaluatorTests.Healthy() with { Settings = settings };

    private static IEnumerable<string> Codes(DefenderHealthReport report) => report.Findings.Select(f => f.Code);

    private static DefenderHealthReport Evaluate(DefenderStatus status, DefenderWatchdogOptions? options = null) =>
        DefenderHealthEvaluator.Evaluate(status, options ?? new DefenderWatchdogOptions(), DefenderHealthEvaluatorTests.Now);

    private DefenderWatchdogOptions WatchdogOptions() => new()
    {
        AlertLogPath = Path.Join(_dir, "watchdog.log"),
        StatePath = Path.Join(_dir, "state.json"),
    };

    private DefenderWatchdog Create(IAlertPublisher? alerts = null) => new(
        _provider, Options.Create(WatchdogOptions()), NullLogger<DefenderWatchdog>.Instance,
        () => DefenderHealthEvaluatorTests.Now, alerts);

    // Parsing

    [Fact]
    public void Parse_ReadsExclusionsAsrAndCloudSettings()
    {
        const string json = """
            {"AMRunningMode":"Normal","Preferences":{
              "ExclusionPath":["C:\\Tools"],"ExclusionExtension":[".log"],"ExclusionProcess":["build.exe"],"ExclusionIpAddress":[],
              "AsrRuleIds":["56A863A9-875E-4185-98A7-B882C64B5CE5"],"AsrRuleActions":[2],
              "MAPSReporting":2,"DisableBlockAtFirstSeen":false,"CloudBlockLevel":4}}
            """;

        var settings = DefenderStatusParser.Parse(json).Settings;

        Assert.NotNull(settings);
        Assert.False(settings.ExclusionsHidden);
        Assert.Equal(new[] { "Path exclusion \"C:\\Tools\"", "Extension exclusion \".log\"", "Process exclusion \"build.exe\"" },
            settings.Exclusions!.Select(e => e.ToString()));
        Assert.Equal(2, settings.AsrRules![AsrRule]);
        Assert.Equal(2, settings.MapsReporting);
        Assert.False(settings.DisableBlockAtFirstSeen);
        Assert.Equal(4, settings.CloudBlockLevel);
    }

    [Fact]
    public void Parse_AdministratorPlaceholderMeansExclusionsHidden()
    {
        const string json = """
            {"Preferences":{"ExclusionPath":["N/A: Must be and administrator to view exclusions"],
              "ExclusionExtension":[],"ExclusionProcess":[],"ExclusionIpAddress":[],"AsrRuleIds":[],"AsrRuleActions":[]}}
            """;

        var settings = DefenderStatusParser.Parse(json).Settings!;

        Assert.True(settings.ExclusionsHidden);
        Assert.Null(settings.Exclusions);
        Assert.Empty(settings.AsrRules!);
    }

    [Fact]
    public void Parse_MismatchedAsrListsAreUnknown()
    {
        var settings = DefenderStatusParser.Parse("""{"Preferences":{"AsrRuleIds":["a","b"],"AsrRuleActions":[1]}}""").Settings!;

        Assert.Null(settings.AsrRules);
    }

    [Fact]
    public void Parse_PreferencesErrorIsKept()
    {
        var status = DefenderStatusParser.Parse("""{"AMRunningMode":"Normal","Preferences":null,"PreferencesError":"denied"}""");

        Assert.True(status.Available);
        Assert.Null(status.Settings);
        Assert.Equal("denied", status.SettingsError);
    }

    [Fact]
    public void Script_ReadsPreferencesWithoutChangingThem()
    {
        Assert.Contains("Get-MpPreference", PowerShellDefenderStatusProvider.Script);
        Assert.DoesNotContain("Set-Mp", PowerShellDefenderStatusProvider.Script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Add-Mp", PowerShellDefenderStatusProvider.Script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Mp", PowerShellDefenderStatusProvider.Script, StringComparison.OrdinalIgnoreCase);
    }

    // Standing findings

    [Fact]
    public void NormalSettings_AreHealthy()
    {
        var report = Evaluate(WithSettings(Settings(PathExclusion(@"C:\Tools\Build"))));

        Assert.Equal(DefenderHealth.Healthy, report.Health);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void CloudProtectionOff_IsWarning()
    {
        var report = Evaluate(WithSettings(Settings() with { MapsReporting = 0 }));

        Assert.Equal(DefenderHealth.Degraded, report.Health);
        Assert.Contains(CloudProtectionOff, Codes(report));
    }

    [Fact]
    public void BlockAtFirstSeenOff_IsWarning()
    {
        var report = Evaluate(WithSettings(Settings() with { DisableBlockAtFirstSeen = true }));

        Assert.Contains(BlockAtFirstSeenOff, Codes(report));
    }

    [Fact]
    public void HiddenExclusions_AreWarning()
    {
        var report = Evaluate(WithSettings(new DefenderSettings { ExclusionsHidden = true }));

        Assert.Equal(new[] { ExclusionsHidden }, Codes(report));
    }

    [Fact]
    public void SettingsReadError_IsWarning_ButMissingSettingsAreNot()
    {
        Assert.Contains(SettingsUnavailable,
            Codes(Evaluate(DefenderHealthEvaluatorTests.Healthy() with { SettingsError = "denied" })));
        Assert.Empty(Evaluate(DefenderHealthEvaluatorTests.Healthy()).Findings);
    }

    [Fact]
    public void MonitorSettingsOff_IgnoresSettings()
    {
        var report = Evaluate(WithSettings(Settings(PathExclusion(@"C:\")) with { MapsReporting = 0 }),
            new DefenderWatchdogOptions { MonitorSettings = false });

        Assert.Empty(report.Findings);
    }

    [Theory]
    [InlineData(DefenderExclusionKinds.Path, @"C:\", true)]
    [InlineData(DefenderExclusionKinds.Path, @"D:", true)]
    [InlineData(DefenderExclusionKinds.Path, @"C:\*", true)]
    [InlineData(DefenderExclusionKinds.Path, @"C:\Windows\Temp\", true)]
    [InlineData(DefenderExclusionKinds.Path, @"c:\users\alice\downloads", true)]
    [InlineData(DefenderExclusionKinds.Path, @"C:\Users\alice\AppData\Local\Temp\*", true)]
    [InlineData(DefenderExclusionKinds.Path, @"%TEMP%", true)]
    [InlineData(DefenderExclusionKinds.Path, @"%USERPROFILE%\Downloads", true)]
    [InlineData(DefenderExclusionKinds.Path, @"C:\ProgramData", true)]
    [InlineData(DefenderExclusionKinds.Path, @"C:\ProgramData\Vendor\Cache", false)]
    [InlineData(DefenderExclusionKinds.Path, @"C:\Users\alice\source\repos", false)]
    [InlineData(DefenderExclusionKinds.Path, @"\\server\share", false)]
    [InlineData(DefenderExclusionKinds.Extension, ".exe", true)]
    [InlineData(DefenderExclusionKinds.Extension, "*.PS1", true)]
    [InlineData(DefenderExclusionKinds.Extension, "log", false)]
    [InlineData(DefenderExclusionKinds.Process, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", true)]
    [InlineData(DefenderExclusionKinds.Process, "*", true)]
    [InlineData(DefenderExclusionKinds.Process, @"C:\Tools\build.exe", false)]
    [InlineData(DefenderExclusionKinds.IpAddress, "10.0.0.1", false)]
    public void IsBroad_FlagsExclusionsThatCoverALot(string kind, string value, bool broad)
    {
        Assert.Equal(broad, IsBroad(new DefenderExclusion(kind, value)));
    }

    [Fact]
    public void BroadExclusions_AreOneWarningEach_UnlessIgnored()
    {
        var status = WithSettings(Settings(PathExclusion(@"C:\"), new(DefenderExclusionKinds.Extension, ".exe")));

        var report = Evaluate(status);
        Assert.Equal(2, report.Findings.Count(f => f.Code.StartsWith(BroadExclusionPrefix, StringComparison.Ordinal)));

        var ignored = Evaluate(status, new DefenderWatchdogOptions { IgnoredExclusions = new[] { ".exe" } });
        Assert.Single(ignored.Findings);
    }

    [Fact]
    public void PassiveMode_SkipsCloudAndBroadExclusionFindings()
    {
        var report = Evaluate(WithSettings(Settings(PathExclusion(@"C:\")) with { MapsReporting = 0 }) with
        {
            RunningMode = "Passive Mode",
        });

        Assert.Equal(new[] { PassiveMode }, Codes(report));
    }

    // Changes

    [Fact]
    public void CompareSettings_ReportsAddedAndRemovedExclusions()
    {
        var before = new DefenderSettingsState { Exclusions = new() { PathExclusion(@"C:\Old") } };

        var changes = CompareSettings(before, Settings(PathExclusion(@"c:\NEW")), new DefenderWatchdogOptions());

        var added = Assert.Single(changes, c => c.Code == ExclusionAdded);
        Assert.Equal(FindingSeverity.Warning, added.Severity);
        Assert.Contains(@"c:\NEW", added.Message);
        Assert.Equal(FindingSeverity.Info, Assert.Single(changes, c => c.Code == ExclusionRemoved).Severity);
    }

    [Fact]
    public void CompareSettings_ExclusionsDifferingOnlyInCase_AreUnchanged()
    {
        var before = new DefenderSettingsState { Exclusions = new() { PathExclusion(@"C:\Tools") } };

        Assert.Empty(CompareSettings(before, Settings(PathExclusion(@"c:\tools")), new DefenderWatchdogOptions()));
    }

    [Fact]
    public void CompareSettings_UnreadableExclusions_AreNotReportedAsRemoved()
    {
        var before = new DefenderSettingsState { Exclusions = new() { PathExclusion(@"C:\Tools") } };

        Assert.Empty(CompareSettings(before, new DefenderSettings { ExclusionsHidden = true }, new DefenderWatchdogOptions()));
    }

    [Fact]
    public void CompareSettings_CapsAddedExclusionAlerts()
    {
        var before = new DefenderSettingsState { Exclusions = new() };
        var many = Enumerable.Range(0, 5).Select(i => PathExclusion($@"C:\Dir{i}")).ToArray();

        var changes = CompareSettings(before, Settings(many), new DefenderWatchdogOptions { MaxChangeAlertsPerCheck = 2 });

        Assert.Equal(2, changes.Count(c => c.Code == ExclusionAdded));
        Assert.Contains("3 more", Assert.Single(changes, c => c.Code == ExclusionsAddedSummary).Message);
    }

    [Fact]
    public void CompareSettings_IgnoredExclusionsAreNotReported()
    {
        var before = new DefenderSettingsState { Exclusions = new() };

        var changes = CompareSettings(before, Settings(PathExclusion(@"C:\Tools\Build")),
            new DefenderWatchdogOptions { IgnoredExclusions = new[] { @"C:\Tools\*" } });

        Assert.Empty(changes);
    }

    [Theory]
    [InlineData(1, 2, AsrRuleWeakened)]
    [InlineData(1, 0, AsrRuleWeakened)]
    [InlineData(6, 2, AsrRuleWeakened)]
    [InlineData(2, 1, AsrRuleStrengthened)]
    public void CompareSettings_GradesAsrChanges(int before, int after, string code)
    {
        var state = new DefenderSettingsState { AsrRules = new() { [AsrRule] = before } };

        var change = Assert.Single(CompareSettings(state,
            new DefenderSettings { AsrRules = new Dictionary<string, int> { [AsrRule] = after } },
            new DefenderWatchdogOptions()));

        Assert.Equal(code, change.Code);
        Assert.Equal(AsrRule, change.Resource);
    }

    [Fact]
    public void CompareSettings_RemovedAsrRuleIsWeakened()
    {
        var state = new DefenderSettingsState { AsrRules = new() { [AsrRule] = 1 } };

        var change = Assert.Single(CompareSettings(state,
            new DefenderSettings { AsrRules = new Dictionary<string, int>() }, new DefenderWatchdogOptions()));

        Assert.Equal(AsrRuleWeakened, change.Code);
        Assert.Contains("from Block to not set", change.Message);
    }

    [Fact]
    public void CompareSettings_LoweredCloudBlockLevelIsWarning()
    {
        var state = new DefenderSettingsState { CloudBlockLevel = 4 };

        var change = Assert.Single(CompareSettings(state, new DefenderSettings { CloudBlockLevel = 0 }, new DefenderWatchdogOptions()));

        Assert.Equal(CloudBlockLevelLowered, change.Code);
        Assert.Equal(FindingSeverity.Warning, change.Severity);
    }

    // Watchdog

    [Fact]
    public async Task FirstCheck_RecordsABaseline_ThenANewExclusionIsAlerted()
    {
        var shared = new AlertHub(Options.Create(new AlertDeliveryOptions()));
        using var watchdog = Create(shared);
        var changes = new List<DefenderChange>();
        watchdog.ChangeDetected += changes.Add;

        _provider.Status = WithSettings(Settings(PathExclusion(@"C:\Tools")));
        await watchdog.CheckOnceAsync(default);
        Assert.Equal(SettingsBaselineCreated, Assert.Single(changes).Code);

        await watchdog.CheckOnceAsync(default);
        Assert.Single(changes);

        _provider.Status = WithSettings(Settings(PathExclusion(@"C:\Tools"), PathExclusion(@"C:\Users\Public\x")));
        await watchdog.CheckOnceAsync(default);

        var added = changes[^1];
        Assert.Equal(ExclusionAdded, added.Code);
        Assert.Contains(shared.Snapshot().Alerts,
            a => a.Code == ExclusionAdded && a.Severity == AlertSeverity.Warning && a.Module == DefenderWatchdog.ModuleId);
    }

    [Fact]
    public async Task ExclusionAddedWhileStopped_IsReportedAfterRestart()
    {
        _provider.Status = WithSettings(Settings(PathExclusion(@"C:\Tools")));
        using (var first = Create())
        {
            await first.CheckOnceAsync(default);
        }

        _provider.Status = WithSettings(Settings(PathExclusion(@"C:\Tools"), PathExclusion(@"C:\Evil")));
        using var second = Create();
        var changes = new List<DefenderChange>();
        second.ChangeDetected += changes.Add;
        await second.CheckOnceAsync(default);

        var change = Assert.Single(changes);
        Assert.Equal(ExclusionAdded, change.Code);
        Assert.Contains(@"C:\Evil", change.Message);
    }

    [Fact]
    public async Task CorruptState_IsReportedOnce_AndANewBaselineRecorded()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Join(_dir, "state.json"), "{not json");
        _provider.Status = WithSettings(Settings());
        using var watchdog = Create();
        var changes = new List<DefenderChange>();
        watchdog.ChangeDetected += changes.Add;

        await watchdog.CheckOnceAsync(default);

        Assert.Equal(new[] { DefenderWatchdog.StateUnreadable, SettingsBaselineCreated }, changes.Select(c => c.Code));
    }

    [Fact]
    public async Task BroadExclusion_IsNotReportedAsClearedWhenExclusionsBecomeHidden()
    {
        using var watchdog = Create();
        var raised = new List<DefenderFinding>();
        var cleared = new List<DefenderFinding>();
        watchdog.AlertRaised += raised.Add;
        watchdog.AlertCleared += cleared.Add;

        _provider.Status = WithSettings(Settings(PathExclusion(@"C:\")));
        await watchdog.CheckOnceAsync(default);
        Assert.StartsWith(BroadExclusionPrefix, Assert.Single(raised).Code);

        _provider.Status = WithSettings(new DefenderSettings { ExclusionsHidden = true });
        await watchdog.CheckOnceAsync(default);
        Assert.Empty(cleared);

        _provider.Status = WithSettings(Settings());
        await watchdog.CheckOnceAsync(default);
        Assert.Contains(cleared, f => f.Code.StartsWith(BroadExclusionPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnreadableStatus_DoesNotReportRecovery()
    {
        using var watchdog = Create();
        var cleared = new List<DefenderFinding>();
        watchdog.AlertCleared += cleared.Add;

        _provider.Status = DefenderHealthEvaluatorTests.Healthy() with { RealTimeProtectionEnabled = false };
        await watchdog.CheckOnceAsync(default);
        _provider.Status = DefenderStatus.Unavailable("timed out");
        await watchdog.CheckOnceAsync(default);

        Assert.Empty(cleared);
    }

    public void Dispose()
    {
        // Every watchdog is disposed by the end of its test, so nothing holds these files open.
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class FakeProvider : IDefenderStatusProvider
    {
        public DefenderStatus Status { get; set; } = DefenderHealthEvaluatorTests.Healthy();

        public Task<DefenderStatus> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Status);
    }
}

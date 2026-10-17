using WatchLattice.Common.Alerts;
using WatchLattice.Persistence.Verification;
using Xunit;
using static WatchLattice.Persistence.Tests.Verification.TaskScenarios;

namespace WatchLattice.Persistence.Tests.Verification;

public sealed class VerificationFindingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "watchlattice-findings-" + Guid.NewGuid().ToString("N"));
    private readonly TaskScenarios _machine = new();

    public VerificationFindingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string HubPath => Path.Combine(_directory, "alerts.json" + VerificationStore.FileSuffix);

    private string ManualPath => Path.Combine(_directory, "user", "manual-verifications.json");

    private static ProtectionAlert EntryAlert(Guid? id = null, string code = "ENTRY_CHANGED", string module = "Persistence") =>
        new(id ?? Guid.NewGuid(), DateTimeOffset.UtcNow, module, code, AlertSeverity.Warning, "Autostart entry changed", "excerpt", TriggerTimeChanged().Resource);

    private static ProtectionAlert OutcomeAlert(Guid verifies) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, "Persistence", PersistenceVerificationCoordinator.NeedsReviewCode, AlertSeverity.Warning,
            "Verification needs review", $"{TaskPath}\nSome summary.\nVerifies alert {verifies}.", TriggerTimeChanged().Resource);

    /// <summary>Writes the hub's findings file the way the hub does, with one automatic run.</summary>
    private async Task<VerificationRecord> WriteHubRecordAsync(ProtectionAlert alert)
    {
        var run = await _machine.Engine().VerifyAsync(TriggerTimeChanged(), VerificationTrigger.Automatic, default);
        var store = new VerificationStore(HubPath);
        var record = new VerificationRecord(alert.Id, alert.Code, alert.At, alert.Resource!, TriggerTimeChanged(), new[] { run });
        store.Add(record);
        store.Save();
        return record;
    }

    [Fact]
    public void LinkedAlert_IsTheEntryAlertItself_OrTheOneAnOutcomeAlertNames()
    {
        var entry = EntryAlert();
        Assert.Equal(entry.Id, VerificationFindings.LinkedAlertId(entry));
        Assert.Equal(entry.Id, VerificationFindings.LinkedAlertId(OutcomeAlert(entry.Id)));

        Assert.Null(VerificationFindings.LinkedAlertId(EntryAlert(code: "ENTRY_REMOVED")));
        Assert.Null(VerificationFindings.LinkedAlertId(EntryAlert(module: "Canary")));
        Assert.Null(VerificationFindings.LinkedAlertId(OutcomeAlert(entry.Id) with { Message = "Verifies alert not-a-guid." }));
    }

    [Fact]
    public async Task HubFindings_AreShownForBothTheEntryAlertAndItsOutcomeAlert()
    {
        var entry = EntryAlert();
        await WriteHubRecordAsync(entry);
        var findings = new VerificationFindings(HubPath, ManualPath);

        Assert.True(findings.Refresh());
        Assert.False(findings.Refresh());

        var record = Assert.IsType<VerificationRecord>(findings.For(entry));
        Assert.Equal(VerificationOutcome.ChecksPassed, record.Latest!.Outcome);
        Assert.Equal(record.Latest.Id, findings.For(OutcomeAlert(entry.Id))!.Latest!.Id);
        Assert.Null(findings.Problem);
    }

    [Fact]
    public async Task ManualVerification_IsKeptInTheUsersOwnFile_AndMergedAfterTheHubRuns()
    {
        var entry = EntryAlert();
        await WriteHubRecordAsync(entry);
        var hubBefore = await File.ReadAllTextAsync(HubPath);
        var findings = new VerificationFindings(HubPath, ManualPath);
        findings.Refresh();

        // The task changed again after the alert: the manual run must say so.
        _machine.Tasks.Tasks[TaskPath] = new(TaskReadStatus.Found, ComHandlerXml(Clsid, "2026-10-19T03:12:44Z"));
        var run = await findings.VerifyAsync(entry, _machine.Engine(), default);

        Assert.Equal(VerificationTrigger.Manual, run.Trigger);
        Assert.NotEmpty(run.ChangesSinceAlert);
        Assert.Equal(hubBefore, await File.ReadAllTextAsync(HubPath));
        Assert.True(File.Exists(ManualPath));

        var merged = findings.For(entry)!;
        Assert.Equal(new[] { VerificationTrigger.Automatic, VerificationTrigger.Manual }, merged.Runs.Select(r => r.Trigger));
        Assert.Equal(run.Id, merged.Latest!.Id);
        Assert.Equal(TriggerTimeChanged().Before, merged.Original.Before);

        // A fresh dashboard reads the same history back.
        var reopened = new VerificationFindings(HubPath, ManualPath);
        reopened.Refresh();
        Assert.Equal(2, reopened.For(entry)!.Runs.Count);
    }

    [Fact]
    public async Task ManualResults_StayReadable_AfterTheHubDropsItsRecord()
    {
        var entry = EntryAlert();
        await WriteHubRecordAsync(entry);
        var findings = new VerificationFindings(HubPath, ManualPath);
        findings.Refresh();
        await findings.VerifyAsync(entry, _machine.Engine(), default);

        File.Delete(HubPath);
        Assert.True(findings.Refresh());

        var record = findings.For(entry)!;
        Assert.Equal(VerificationTrigger.Manual, Assert.Single(record.Runs).Trigger);
        Assert.Equal(TriggerTimeChanged().After, record.Original.After);
    }

    [Fact]
    public async Task AlertsWithoutStoredEvidence_CannotBeVerified()
    {
        var findings = new VerificationFindings(HubPath, ManualPath);
        findings.Refresh();
        Assert.Null(findings.For(EntryAlert()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => findings.VerifyAsync(EntryAlert(), _machine.Engine(), default));
        Assert.Empty(_machine.Tasks.Reads);
    }

    [Fact]
    public void UnreadableHubFindings_AreReported_NotThrown()
    {
        File.WriteAllText(HubPath, "{ not json");
        var findings = new VerificationFindings(HubPath, ManualPath);

        findings.Refresh();

        Assert.NotNull(findings.Problem);
        Assert.Null(findings.For(EntryAlert()));
        // The dashboard never moves or rewrites the hub's file.
        Assert.True(File.Exists(HubPath));
    }

    [Fact]
    public async Task Report_KeepsTheLoggedEvidenceApartFromTheCurrentState()
    {
        var entry = EntryAlert();
        var record = await WriteHubRecordAsync(entry);

        var report = VerificationFindings.Report(record, record.Latest);

        var historical = report.IndexOf("AT ALERT TIME", StringComparison.Ordinal);
        var current = report.IndexOf("CURRENT STATE", StringComparison.Ordinal);
        Assert.True(historical >= 0 && current > historical);
        Assert.Contains("not proof", report[current..]);
        Assert.Contains("sppcext.dll", report[current..], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SHA-256", report[current..]);

        var unverified = VerificationFindings.Report(record with { Runs = Array.Empty<VerificationRun>() }, null);
        Assert.Contains("NOT VERIFIED YET", unverified);
        Assert.DoesNotContain("CURRENT STATE", unverified);
    }
}

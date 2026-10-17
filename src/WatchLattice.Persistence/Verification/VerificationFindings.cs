using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WatchLattice.Common.Alerts;

namespace WatchLattice.Persistence.Verification;

/// <summary>
/// What the dashboard shows about verification: the hub's findings (read-only,
/// next to the alert feed) merged with the signed-in user's own manual
/// re-verifications, which are kept in that user's profile. Manual checks run
/// in the dashboard's process with the user's own rights, so no request ever
/// goes from the dashboard to the hub. Safe to use from several threads.
/// </summary>
public sealed partial class VerificationFindings
{
    private readonly object _gate = new();
    private readonly string _hubPath;
    private readonly VerificationStore _manual;
    private DateTime _hubStamp = DateTime.MinValue;
    private DateTime _manualStamp = DateTime.MinValue;
    private IReadOnlyList<VerificationRecord> _hub = Array.Empty<VerificationRecord>();

    public VerificationFindings(string hubStorePath, string? manualStorePath = null)
    {
        _hubPath = hubStorePath;
        _manual = new VerificationStore(manualStorePath ?? DefaultManualStorePath);
    }

    /// <summary>Manual results, in the same per-user folder as the other WatchLattice data.</summary>
    public static string DefaultManualStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanAlways", "manual-verifications.json");

    /// <summary>Why findings could not be read, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Re-reads whichever file changed. Returns true when anything new was read. Does file I/O; call off the UI thread.</summary>
    public bool Refresh()
    {
        lock (_gate) return RefreshLocked();
    }

    private bool RefreshLocked()
    {
        var changed = false;
        try
        {
            var stamp = File.Exists(_hubPath) ? File.GetLastWriteTimeUtc(_hubPath) : DateTime.MinValue;
            if (stamp != _hubStamp)
            {
                _hub = stamp == DateTime.MinValue ? Array.Empty<VerificationRecord>() : VerificationStore.Read(_hubPath);
                _hubStamp = stamp;
                changed = true;
            }
            Problem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            Problem = "The hub's verification findings could not be read: " + ex.Message;
        }
        try
        {
            var stamp = File.Exists(_manual.FilePath) ? File.GetLastWriteTimeUtc(_manual.FilePath) : DateTime.MinValue;
            if (stamp != _manualStamp)
            {
                var problem = _manual.Load();
                if (problem is not null) Problem = problem;
                _manualStamp = stamp;
                changed = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem = "Your earlier manual checks could not be read: " + ex.Message;
        }
        return changed;
    }

    /// <summary>
    /// The alert a verification belongs to: the alert itself for an entry
    /// alert, or the alert a VERIFY_* alert names. Null for other alerts.
    /// </summary>
    public static Guid? LinkedAlertId(ProtectionAlert alert)
    {
        if (alert.Module != PersistenceVerificationCoordinator.Module) return null;
        if (PersistenceVerificationCoordinator.IsEntryAlert(alert.Code)) return alert.Id;
        if (!alert.Code.StartsWith("VERIFY_", StringComparison.Ordinal)) return null;
        var match = VerifiesAlert().Match(alert.Message);
        return match.Success && Guid.TryParse(match.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>The record for an alert with every run, hub and manual, oldest first; null when the alert has none.</summary>
    public VerificationRecord? For(ProtectionAlert alert)
    {
        var id = LinkedAlertId(alert);
        if (id is null) return null;
        lock (_gate) return Merge(id.Value);
    }

    private VerificationRecord? Merge(Guid id)
    {
        var hub = _hub.FirstOrDefault(r => r.AlertId == id);
        var manual = _manual.Find(id);
        if (hub is null) return manual;
        if (manual is null) return hub;
        return hub with { Runs = hub.Runs.Concat(manual.Runs).DistinctBy(r => r.Id).OrderBy(r => r.StartedAt).ToArray() };
    }

    /// <summary>
    /// Verifies the alert's entry again, now, as the current user, and keeps
    /// the result in the user's own store. The original evidence is copied
    /// from the hub's record unchanged.
    /// </summary>
    public async Task<VerificationRun> VerifyAsync(ProtectionAlert alert, PersistenceVerificationEngine engine, CancellationToken cancellationToken)
    {
        var record = For(alert) ?? throw new InvalidOperationException("This alert has no stored evidence to verify.");
        var run = await engine.VerifyAsync(record.Original, VerificationTrigger.Manual, cancellationToken).ConfigureAwait(false);
        await Task.Run(() =>
        {
            lock (_gate)
            {
                // The original evidence goes with the run, so a manual result
                // stays readable after the hub has dropped its own record.
                if (_manual.Find(record.AlertId) is null) _manual.Add(record with { Runs = Array.Empty<VerificationRun>() });
                _manual.AddRun(record.AlertId, run);
                _manual.Save();
                _manualStamp = File.GetLastWriteTimeUtc(_manual.FilePath);
            }
        }, CancellationToken.None).ConfigureAwait(false);
        return run;
    }

    public static string OutcomeText(VerificationOutcome outcome) => outcome switch
    {
        VerificationOutcome.ChecksPassed => "Checks passed",
        VerificationOutcome.NeedsReview => "Needs review",
        _ => "Suspicious",
    };

    /// <summary>A full plain-text report of one run, with the historical evidence kept separate.</summary>
    public static string Report(VerificationRecord record, VerificationRun? run)
    {
        var text = new StringBuilder();
        void Line(string value = "") => text.AppendLine(value);
        var original = record.Original;

        Line($"Entry: {original.EntryId}");
        Line($"Source: {original.SourceId}");
        Line($"Alert: {record.AlertCode} at {record.AlertAt.ToLocalTime():F} ({record.AlertId})");
        Line();
        Line("AT ALERT TIME (historical evidence, logged when the change was detected)");
        Line($"  Observed: {original.ObservedAt.ToLocalTime():F}, change: {original.Kind}");
        if (run is not null && run.LoggedChanges.Count > 0)
            foreach (var change in run.LoggedChanges) Line($"  {change.Field} [{change.Area}]: {Show(change.Before)} → {Show(change.After)}");
        Line($"  Before: {Show(original.Before)}");
        Line($"  After:  {Show(original.After)}");
        Line();

        if (run is null)
        {
            Line("NOT VERIFIED YET. Choose Verify entry to check the current state.");
            return text.ToString();
        }
        Line($"CURRENT STATE (verified {run.CompletedAt.ToLocalTime():F}, {run.Trigger.ToString().ToLowerInvariant()}, as {run.CheckedAs})");
        Line($"  {OutcomeText(run.Outcome)}. {run.Summary}");
        Line();
        Line("Checks:");
        foreach (var check in run.Checks) Line($"  [{check.Status}] {check.Title}: {check.Detail}");
        if (run.ChangesSinceAlert.Count > 0)
        {
            Line();
            Line("Changed since the alert:");
            foreach (var change in run.ChangesSinceAlert) Line($"  {change.Field} [{change.Area}]: {Show(change.Before)} → {Show(change.After)}");
        }
        foreach (var action in run.Actions)
        {
            Line();
            Line($"Action {action.Index + 1}: {action.Summary}");
            if (action.ClassId is not null) Line($"  CLSID: {action.ClassId}");
            if (action.RegistrationLocation is not null) Line($"  Registered at: {action.RegistrationLocation}");
            if (action.RegisteredServer is not null) Line($"  Registered target: {action.RegisteredServer}");
            if (action.ResolvedPath is not null) Line($"  Resolved path: {action.ResolvedPath}");
            if (action.File is { } file)
            {
                Line($"  Exists: {(file.Exists ? "yes" : "no")}{(file.Error is null ? "" : " (" + file.Error + ")")}");
                if (file.Length is not null) Line($"  Size: {file.Length:N0} bytes");
                if (file.Sha256 is not null) Line($"  SHA-256: {file.Sha256}");
                if (file.FileVersion is not null) Line($"  File version: {file.FileVersion}");
                if (file.ProductName is not null || file.CompanyName is not null) Line($"  Product: {file.ProductName} ({file.CompanyName})");
                if (file.OriginalFilename is not null) Line($"  Original file name: {file.OriginalFilename}");
                if (file.LastWriteUtc is not null) Line($"  Last written: {file.LastWriteUtc.Value.ToLocalTime():F}");
                if (file.Signature is { } signature)
                {
                    Line($"  Signature: {signature.State} ({signature.Kind}){(signature.Problem is null ? "" : ", " + signature.Problem)}");
                    if (signature.Subject is not null) Line($"  Signer: {signature.Subject}");
                    if (signature.Issuer is not null) Line($"  Issuer: {signature.Issuer}");
                    if (signature.Thumbprint is not null) Line($"  Thumbprint: {signature.Thumbprint}");
                }
            }
            foreach (var note in action.Notes ?? Array.Empty<string>()) Line($"  Note: {note}");
        }
        if (run.Errors.Count > 0)
        {
            Line();
            Line("Errors:");
            foreach (var error in run.Errors) Line("  " + error);
        }
        if (run.CurrentDefinition is not null)
        {
            Line();
            Line("Current task definition:");
            Line(run.CurrentDefinition);
        }
        return text.ToString();
    }

    private static string Show(string? value) => value is null ? "(none)" : value.Length <= 4000 ? value : value[..4000] + "… (truncated)";

    [GeneratedRegex(@"Verifies alert ([0-9a-fA-F-]{36})")]
    private static partial Regex VerifiesAlert();
}

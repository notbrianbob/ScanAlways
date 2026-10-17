using System.Text.Json.Serialization;

namespace WatchLattice.Persistence.Verification;

[JsonConverter(typeof(JsonStringEnumConverter<CheckStatus>))]
public enum CheckStatus { Pass, Warning, Fail, Unknown }

/// <summary>
/// The overall assessment. None of these is a verdict that an entry is harmless:
/// <see cref="ChecksPassed"/> means only that every check found what it expected.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<VerificationOutcome>))]
public enum VerificationOutcome { ChecksPassed, NeedsReview, Suspicious }

[JsonConverter(typeof(JsonStringEnumConverter<VerificationTrigger>))]
public enum VerificationTrigger { Automatic, Manual }

[JsonConverter(typeof(JsonStringEnumConverter<ChangeArea>))]
public enum ChangeArea { Action, Principal, Enabled, Schedule, Other }

[JsonConverter(typeof(JsonStringEnumConverter<SignatureState>))]
public enum SignatureState { Valid, NotSigned, Invalid, Unknown }

[JsonConverter(typeof(JsonStringEnumConverter<SignatureKind>))]
public enum SignatureKind { None, Embedded, Catalog }

/// <summary>One check and its result. Detail is display text, never something to execute.</summary>
public sealed record VerificationCheck(string Id, string Title, CheckStatus Status, string Detail);

/// <summary>One field that differs, e.g. <c>Triggers[0].StartBoundary</c>.</summary>
public sealed record FieldChange(string Field, ChangeArea Area, string? Before, string? After);

/// <summary>A file's Authenticode state. Subject is the signer certificate's subject when one was read.</summary>
public sealed record SignatureEvidence(SignatureState State, SignatureKind Kind, string? Subject = null,
    string? Issuer = null, string? Thumbprint = null, bool RevocationChecked = false, string? Problem = null)
{
    public static SignatureEvidence NotSigned { get; } = new(SignatureState.NotSigned, SignatureKind.None);
}

/// <summary>What could be read about a target file without running or loading it.</summary>
public sealed record FileEvidence(string Path, bool Exists, long? Length = null, string? Sha256 = null,
    string? FileVersion = null, string? ProductVersion = null, string? CompanyName = null, string? ProductName = null,
    string? OriginalFilename = null, string? FileDescription = null, DateTimeOffset? CreatedUtc = null,
    DateTimeOffset? LastWriteUtc = null, bool IsReparsePoint = false, SignatureEvidence? Signature = null, string? Error = null);

/// <summary>What one task action points at, after resolving COM registrations and paths.</summary>
public sealed record ActionEvidence(int Index, string Kind, string Summary, string? ClassId = null,
    string? RegistrationLocation = null, string? RegisteredServer = null, string? ResolvedPath = null,
    FileEvidence? File = null, IReadOnlyList<string>? Notes = null);

/// <summary>
/// The evidence the auditor logged when it detected the change: the entry's
/// previous and current definitions exactly as recorded at that time. It is
/// copied into the verification record once and never changed afterwards.
/// </summary>
public sealed record PersistenceObservation(string SourceId, string EntryId,
    [property: JsonConverter(typeof(JsonStringEnumConverter<PersistenceChangeKind>))] PersistenceChangeKind Kind,
    string? Before, string? After, DateTimeOffset ObservedAt)
{
    [JsonIgnore]
    public string Resource => SourceId + "/" + EntryId;
}

/// <summary>One verification pass over the entry's current state.</summary>
public sealed record VerificationRun(Guid Id, VerificationTrigger Trigger, string CheckedAs, DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt, string Mechanism, VerificationOutcome Outcome, string Summary,
    IReadOnlyList<VerificationCheck> Checks, IReadOnlyList<FieldChange> LoggedChanges,
    IReadOnlyList<FieldChange> ChangesSinceAlert, IReadOnlyList<ActionEvidence> Actions,
    string? CurrentDefinition, IReadOnlyList<string> Errors);

/// <summary>
/// Verification findings kept alongside the alert they belong to.
/// <see cref="Original"/> is the historical state at alert time; each run in
/// <see cref="Runs"/> is a later look at the current state.
/// </summary>
public sealed record VerificationRecord(Guid AlertId, string AlertCode, DateTimeOffset AlertAt, string Resource,
    PersistenceObservation Original, IReadOnlyList<VerificationRun> Runs)
{
    [JsonIgnore]
    public VerificationRun? Latest => Runs.Count == 0 ? null : Runs[^1];
}

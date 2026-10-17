namespace WatchLattice.Persistence.Verification;

/// <summary>What one action of a known Windows task is expected to be.</summary>
/// <param name="Target">For Exec, the program; for ComHandler, the registered server. May use machine-wide %VARIABLES%.</param>
public sealed record TrustedAction(string Kind, string Target, string? ClassId = null);

/// <summary>A Windows component whose configuration is known.</summary>
/// <param name="Publisher">The signer certificate's common name the target files must have.</param>
/// <param name="Reference">Where the expected values come from, for whoever maintains this list.</param>
public sealed record TrustedTask(string TaskPath, IReadOnlyList<TrustedAction> Actions, string Publisher, string Reference);

/// <summary>
/// Known Windows scheduled tasks and what they should point at.
///
/// Maintenance rules: add an entry only after checking it on a clean, current
/// Windows 11 install (and ideally Windows Server, which CI runs on), record
/// where the values came from, and keep entries to things Windows itself
/// ships. Only stable facts belong here (task path, action kind, CLSID,
/// file location, signer), never hashes or versions, which change with every
/// cumulative update. A mismatch with this list is reported as suspicious,
/// so a wrong entry causes false alarms; when unsure, leave a task out and it
/// is treated as unknown (needs review) instead. The Windows CI job checks
/// every entry against the runner's own configuration.
/// </summary>
public static class TrustedBaseline
{
    public const string MicrosoftWindowsPublisher = "Microsoft Windows";

    public static IReadOnlyList<TrustedTask> Tasks { get; } = new[]
    {
        new TrustedTask(@"\Microsoft\Windows\SoftwareProtectionPlatform\SvcRestartTask",
            new[] { new TrustedAction(TaskActionKinds.ComHandler, @"%SystemRoot%\System32\sppcext.dll", "{B1AEBB5D-EAD9-4476-B375-9C3ED9F32AFC}") },
            MicrosoftWindowsPublisher,
            "Software Protection Platform restart task; COM handler served by sppcext.dll. Reference scenario from the feature request."),
    };

    public static TrustedTask? Find(string taskPath) =>
        Tasks.FirstOrDefault(t => string.Equals(t.TaskPath, taskPath, StringComparison.OrdinalIgnoreCase));
}

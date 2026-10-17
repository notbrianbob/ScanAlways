using System.Text.Json;
using WatchLattice.Persistence.Verification;

namespace WatchLattice.Persistence.Tests.Verification;

/// <summary>
/// The reference scenario from the feature request and variations of it:
/// \Microsoft\Windows\SoftwareProtectionPlatform\SvcRestartTask, a COM
/// handler {B1AEBB5D-…} served by C:\Windows\System32\sppcext.dll.
/// </summary>
internal sealed class TaskScenarios
{
    public const string TaskPath = @"\Microsoft\Windows\SoftwareProtectionPlatform\SvcRestartTask";
    public const string Clsid = "{B1AEBB5D-EAD9-4476-B375-9C3ED9F32AFC}";
    public const string Dll = @"C:\Windows\System32\sppcext.dll";
    public const string Source = "ScheduledTasks/S-1-5-18";
    public const string WindowsSubject = "CN=Microsoft Windows, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    public const string User = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    public static SignatureEvidence MicrosoftCatalog => new(SignatureState.Valid, SignatureKind.Catalog, WindowsSubject,
        "CN=Microsoft Windows Production PCA 2011, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", "ABCDEF", true, "catalog Microsoft-Windows-Client.cat");

    public FakeTaskSource Tasks { get; } = new();
    public FakeComRegistry Registry { get; } = new();
    public FakeAccounts Accounts { get; } = new();
    public FakeFiles Files { get; } = new();
    public List<string> FamiliarPublishers { get; } = new();

    public static SystemPaths Paths { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows", ["windir"] = @"C:\Windows", ["SystemDrive"] = "C:",
        ["ProgramFiles"] = @"C:\Program Files", ["ProgramFiles(x86)"] = @"C:\Program Files (x86)", ["ProgramData"] = @"C:\ProgramData",
    }, Is64BitOperatingSystem: true);

    /// <summary>A healthy machine: the task as Windows ships it, its class registered machine-wide, the DLL catalog-signed.</summary>
    public TaskScenarios()
    {
        Tasks.Tasks[TaskPath] = new(TaskReadStatus.Found, ComHandlerXml(Clsid, "2026-10-18T03:12:44Z"));
        Registry.Machine(Clsid, @"%SystemRoot%\system32\sppcext.dll");
        Files.Add(Dll, MicrosoftCatalog);
    }

    public ScheduledTaskVerifier Verifier() => new(Tasks, Registry, Accounts, Files, Paths, FamiliarPublishers);

    public PersistenceVerificationEngine Engine(TimeSpan? timeout = null) => new(new[] { Verifier() }, Accounts, timeout: timeout);

    public Task<VerificationRun> VerifyAsync(PersistenceObservation observation) =>
        Engine().VerifyAsync(observation, VerificationTrigger.Manual, CancellationToken.None);

    /// <summary>The auditor's logged projection of a COM-handler task, as WindowsPersistenceProvider.ScheduledTaskScript emits it.</summary>
    public static string ComHandlerProjection(string clsid, string start, string user = "NETWORK SERVICE", string runLevel = "Limited", bool enabled = true) =>
        JsonSerializer.Serialize(new
        {
            Enabled = enabled, UserId = user, GroupId = default(string), RunLevel = runLevel, LogonType = "ServiceAccount",
            Actions = new[] { new { Execute = default(string), Arguments = default(string), WorkingDirectory = default(string), ClassId = clsid, Data = "timer" } },
            Triggers = new[] { new { Type = "MSFT_TaskTimeTrigger", Enabled = true, StartBoundary = start, EndBoundary = default(string) } },
        });

    public static string ExecProjection(string execute, string? arguments, string start = "2026-10-17T09:00:00", string user = "SYSTEM") =>
        JsonSerializer.Serialize(new
        {
            Enabled = true, UserId = user, GroupId = default(string), RunLevel = "Highest", LogonType = "ServiceAccount",
            Actions = new[] { new { Execute = execute, Arguments = arguments, WorkingDirectory = default(string), ClassId = default(string), Data = default(string) } },
            Triggers = new[] { new { Type = "MSFT_TaskDailyTrigger", Enabled = true, StartBoundary = start } },
        });

    public static string ComHandlerXml(string clsid, string start, string userId = "S-1-5-20", string runLevel = "LeastPrivilege") => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.6" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Source>Microsoft Corporation</Source><URI>{TaskPath}</URI></RegistrationInfo>
          <Principals><Principal id="NetworkService"><UserId>{userId}</UserId><RunLevel>{runLevel}</RunLevel></Principal></Principals>
          <Settings><Enabled>true</Enabled><Hidden>true</Hidden></Settings>
          <Triggers><TimeTrigger><StartBoundary>{start}</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
          <Actions Context="NetworkService"><ComHandler><ClassId>{clsid}</ClassId><Data><![CDATA[timer]]></Data></ComHandler></Actions>
        </Task>
        """;

    public static string ExecXml(string command, string? arguments, string start = "2026-10-17T09:00:00", string userId = "S-1-5-18",
        string? workingDirectory = null) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Principals><Principal id="Author"><UserId>{userId}</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
          <Triggers><CalendarTrigger><StartBoundary>{start}</StartBoundary><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger></Triggers>
          <Actions Context="Author"><Exec><Command>{System.Security.SecurityElement.Escape(command)}</Command>{(arguments is null ? "" : $"<Arguments>{System.Security.SecurityElement.Escape(arguments)}</Arguments>")}{(workingDirectory is null ? "" : $"<WorkingDirectory>{workingDirectory}</WorkingDirectory>")}</Exec></Actions>
        </Task>
        """;

    /// <summary>The example event: only the trigger's start time changed.</summary>
    public static PersistenceObservation TriggerTimeChanged() => new(Source, TaskPath, PersistenceChangeKind.Changed,
        ComHandlerProjection(Clsid, "2026-10-17T03:12:44Z"), ComHandlerProjection(Clsid, "2026-10-18T03:12:44Z"), DateTimeOffset.UtcNow);

    public static PersistenceObservation Changed(string taskPath, string before, string after) =>
        new(Source, taskPath, PersistenceChangeKind.Changed, before, after, DateTimeOffset.UtcNow);

    public static PersistenceObservation Added(string taskPath, string after) =>
        new(Source, taskPath, PersistenceChangeKind.Added, null, after, DateTimeOffset.UtcNow);
}

internal static class RunAssertions
{
    public static VerificationCheck Check(this VerificationRun run, string id) =>
        run.Checks.SingleOrDefault(c => c.Id == id) ?? throw new Xunit.Sdk.XunitException($"No check {id}. Checks:\n{run.Describe()}");

    public static string Describe(this VerificationRun run) =>
        string.Join("\n", run.Checks.Select(c => $"{c.Id} [{c.Status}] {c.Detail}")) + "\n" + run.Summary;
}

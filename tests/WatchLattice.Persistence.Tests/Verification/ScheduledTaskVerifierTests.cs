using WatchLattice.Persistence.Verification;
using Xunit;
using static WatchLattice.Persistence.Tests.Verification.TaskScenarios;

namespace WatchLattice.Persistence.Tests.Verification;

public sealed class ScheduledTaskVerifierTests
{
    private readonly TaskScenarios _machine = new();

    [Fact]
    public async Task ReferenceScenario_SvcRestartTask_IdentifiesTheTriggerOnlyChange_AndValidatesComAndDll()
    {
        var run = await _machine.VerifyAsync(TriggerTimeChanged());

        Assert.True(run.Outcome == VerificationOutcome.ChecksPassed, run.Describe());
        var change = Assert.Single(run.LoggedChanges);
        Assert.Equal("Triggers[0].StartBoundary", change.Field);
        Assert.Equal(ChangeArea.Schedule, change.Area);
        Assert.Equal("2026-10-17T03:12:44Z", change.Before);
        Assert.Equal("2026-10-18T03:12:44Z", change.After);
        Assert.Contains("Only the schedule", run.Check("logged-change").Detail);
        Assert.Empty(run.ChangesSinceAlert);

        // Independently validated current state, not taken from the alert.
        Assert.Equal(new[] { TaskPath }, _machine.Tasks.Reads);
        var action = Assert.Single(run.Actions);
        Assert.Equal(Clsid, action.ClassId);
        Assert.Equal(@"%SystemRoot%\system32\sppcext.dll", action.RegisteredServer);
        Assert.Equal(Dll, action.ResolvedPath, ignoreCase: true);
        Assert.Equal("AB12", action.File!.Sha256);
        Assert.Equal(CheckStatus.Pass, run.Check("action1.registration").Status);
        Assert.Equal(CheckStatus.Pass, run.Check("action1.baseline").Status);
        Assert.Equal(CheckStatus.Pass, run.Check("action1.signature").Status);
        Assert.Contains("catalog", run.Check("action1.signature").Detail);
        Assert.Equal(CheckStatus.Pass, run.Check("action1.publisher").Status);
        Assert.Equal(CheckStatus.Pass, run.Check("known-configuration").Status);
        Assert.Contains("not proof", run.Summary);
        Assert.NotNull(run.CurrentDefinition);
    }

    [Fact]
    public async Task ScheduleDriftAfterTheAlert_IsReportedButNotSuspicious()
    {
        _machine.Tasks.Tasks[TaskPath] = new(TaskReadStatus.Found, ComHandlerXml(Clsid, "2026-10-19T08:00:00Z"));

        var run = await _machine.VerifyAsync(TriggerTimeChanged());

        Assert.Equal(VerificationOutcome.ChecksPassed, run.Outcome);
        var drift = Assert.Single(run.ChangesSinceAlert);
        Assert.Equal(ChangeArea.Schedule, drift.Area);
        Assert.Contains("apart from its schedule", run.Check("since-alert").Detail);
    }

    [Fact]
    public async Task PrincipalNamesAndSids_CompareAsTheSameAccount()
    {
        var run = await _machine.VerifyAsync(TriggerTimeChanged());

        // The log says "NETWORK SERVICE", the task XML says S-1-5-20.
        Assert.DoesNotContain(run.ChangesSinceAlert, c => c.Area == ChangeArea.Principal);
        Assert.Equal(CheckStatus.Pass, run.Check("since-alert").Status);
    }

    [Fact]
    public async Task UnknownComHandler_ThatIsNotRegistered_NeedsReview()
    {
        const string unknown = "{11111111-2222-3333-4444-555555555555}";
        const string task = @"\Vendor\Updater";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ComHandlerXml(unknown, "2026-10-18T00:00:00"));

        var run = await _machine.VerifyAsync(Added(task, ComHandlerProjection(unknown, "2026-10-18T00:00:00")));

        Assert.Equal(VerificationOutcome.NeedsReview, run.Outcome);
        Assert.Equal(CheckStatus.Warning, run.Check("action1.registration").Status);
        Assert.Contains("not registered", run.Check("action1.registration").Detail);
        Assert.Equal(CheckStatus.Warning, run.Check("known-configuration").Status);
        Assert.Empty(_machine.Files.Inspected);
    }

    [Fact]
    public async Task ModifiedTaskAction_InTheAlert_IsSuspicious()
    {
        const string task = @"\Vendor\Sync";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ExecXml(@"C:\Users\Public\sync.exe", null));
        _machine.Files.Add(@"C:\Users\Public\sync.exe", SignatureEvidence.NotSigned);

        var run = await _machine.VerifyAsync(Changed(task,
            ExecProjection(@"C:\Program Files\Vendor\sync.exe", null), ExecProjection(@"C:\Users\Public\sync.exe", null)));

        Assert.Equal(VerificationOutcome.Suspicious, run.Outcome);
        var check = run.Check("logged-change");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains("Actions[0].Execute", check.Detail);
        Assert.Equal(@"C:\Program Files\Vendor\sync.exe", Assert.Single(run.LoggedChanges).Before);
    }

    [Fact]
    public async Task ActionChangedAgainSinceTheAlert_IsSuspicious()
    {
        _machine.Tasks.Tasks[TaskPath] = new(TaskReadStatus.Found, ComHandlerXml("{22222222-2222-2222-2222-222222222222}", "2026-10-18T03:12:44Z"));

        var run = await _machine.VerifyAsync(TriggerTimeChanged());

        Assert.Equal(VerificationOutcome.Suspicious, run.Outcome);
        Assert.Equal(CheckStatus.Fail, run.Check("since-alert").Status);
        Assert.Contains(run.ChangesSinceAlert, c => c.Field == "Actions[0].ClassId");
        Assert.Equal(CheckStatus.Fail, run.Check("known-configuration").Status);
    }

    [Fact]
    public async Task UnsignedFile_NeedsReview()
    {
        _machine.Files.Add(Dll, SignatureEvidence.NotSigned);

        var run = await _machine.VerifyAsync(TriggerTimeChanged());

        Assert.Equal(VerificationOutcome.NeedsReview, run.Outcome);
        Assert.Equal(CheckStatus.Warning, run.Check("action1.signature").Status);
        Assert.DoesNotContain(run.Checks, c => c.Id == "action1.publisher");
    }

    [Fact]
    public async Task InvalidSignature_IsSuspicious()
    {
        _machine.Files.Add(Dll, new SignatureEvidence(SignatureState.Invalid, SignatureKind.Embedded, Problem: "The digital signature of the object did not verify."));

        var run = await _machine.VerifyAsync(TriggerTimeChanged());

        Assert.Equal(VerificationOutcome.Suspicious, run.Outcome);
        Assert.Equal(CheckStatus.Fail, run.Check("action1.signature").Status);
    }

    [Fact]
    public async Task MissingFile_IsInconclusive_NotSafe()
    {
        _machine.Files.Files.Clear();

        var run = await _machine.VerifyAsync(TriggerTimeChanged());

        Assert.Equal(VerificationOutcome.NeedsReview, run.Outcome);
        Assert.Equal(CheckStatus.Warning, run.Check("action1.target").Status);
        Assert.Contains("does not exist", run.Check("action1.target").Detail);
    }

    [Fact]
    public async Task FileAccessError_IsUnknown()
    {
        _machine.Files.Files[Dll] = new FileEvidence(Dll, true, 10, Error: "Access is denied.");

        var run = await _machine.VerifyAsync(TriggerTimeChanged());

        Assert.Equal(VerificationOutcome.NeedsReview, run.Outcome);
        Assert.Equal(CheckStatus.Unknown, run.Check("action1.target").Status);
    }

    [Fact]
    public async Task MismatchedPath_RegisteredServerIsNotTheBaselineDll_IsSuspicious()
    {
        const string impostor = @"C:\ProgramData\SoftwareProtection\sppcext.dll";
        _machine.Registry.Machine(Clsid, impostor);
        _machine.Files.Add(impostor, MicrosoftCatalog);

        var run = await _machine.VerifyAsync(TriggerTimeChanged());

        Assert.Equal(VerificationOutcome.Suspicious, run.Outcome);
        var check = run.Check("action1.baseline");
        Assert.Equal(CheckStatus.Fail, check.Status);
        Assert.Contains(Dll, check.Detail);
        Assert.Contains(impostor, check.Detail);
        // A Microsoft signature does not excuse the wrong file.
        Assert.Equal(CheckStatus.Pass, run.Check("action1.signature").Status);
    }

    [Fact]
    public async Task ComHijack_PerUserRegistrationOverridingTheMachineClass_IsSuspicious()
    {
        const string task = @"\Vendor\UserTask";
        const string hijack = @"C:\Users\victim\AppData\Roaming\x.dll";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ComHandlerXml(Clsid, "2026-10-18T00:00:00", userId: User));
        _machine.Registry.User(User, Clsid, hijack);
        _machine.Files.Add(hijack, SignatureEvidence.NotSigned);

        var run = await _machine.VerifyAsync(Added(task, ComHandlerProjection(Clsid, "2026-10-18T00:00:00", user: User)));

        Assert.Equal(VerificationOutcome.Suspicious, run.Outcome);
        var registration = run.Check("action1.registration");
        Assert.Equal(CheckStatus.Fail, registration.Status);
        Assert.Contains("hijacking", registration.Detail);
        // The evidence follows the file that would actually load.
        Assert.Equal(hijack, Assert.Single(run.Actions).ResolvedPath);
    }

    [Fact]
    public async Task ComHijack_ForTasksRunningAsTheSignedInUser_ChecksEveryLoadedProfile()
    {
        const string task = @"\Vendor\LogonTask";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ComHandlerXml(Clsid, "2026-10-18T00:00:00", userId: "")
            .Replace("<UserId></UserId>", "<GroupId>S-1-5-32-545</GroupId>"));
        _machine.Registry.LoadedUsers.Add(User);
        _machine.Registry.User(User, Clsid, @"C:\Users\victim\x.dll");

        var run = await _machine.VerifyAsync(Added(task, ComHandlerProjection(Clsid, "2026-10-18T00:00:00", user: "")));

        Assert.Equal(CheckStatus.Fail, run.Check("action1.registration").Status);
    }

    [Fact]
    public async Task UnloadedProfile_MakesTheRegistrationCheckInconclusive()
    {
        const string task = @"\Vendor\UserTask";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ComHandlerXml(Clsid, "2026-10-18T00:00:00", userId: User));
        _machine.Registry.UnloadedHives.Add(User);

        var run = await _machine.VerifyAsync(Added(task, ComHandlerProjection(Clsid, "2026-10-18T00:00:00", user: User)));

        Assert.Equal(CheckStatus.Unknown, run.Check("action1.registration").Status);
        Assert.NotEqual(VerificationOutcome.ChecksPassed, run.Outcome);
    }

    [Fact]
    public async Task TreatAsRedirection_IsFollowed_AndNeedsReview()
    {
        const string other = "{33333333-3333-3333-3333-333333333333}";
        _machine.Registry.Machine(Clsid, @"%SystemRoot%\system32\sppcext.dll", treatAs: other);
        _machine.Registry.Machine(other, @"C:\Windows\System32\other.dll");
        _machine.Files.Add(@"C:\Windows\System32\other.dll", MicrosoftCatalog);

        var run = await _machine.VerifyAsync(TriggerTimeChanged());

        Assert.Equal(CheckStatus.Warning, run.Check("action1.registration").Status);
        Assert.Equal(@"C:\Windows\System32\other.dll", Assert.Single(run.Actions).ResolvedPath);
        Assert.Equal(CheckStatus.Fail, run.Check("action1.baseline").Status);
    }

    [Fact]
    public async Task InvalidClsid_IsNotLookedUp_AndFails()
    {
        const string task = @"\Vendor\Bad";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ComHandlerXml(@"{not-a-guid}\..\..", "2026-10-18T00:00:00"));

        var run = await _machine.VerifyAsync(Added(task, ComHandlerProjection("{not-a-guid}", "2026-10-18T00:00:00")));

        Assert.Equal(VerificationOutcome.Suspicious, run.Outcome);
        Assert.Equal(CheckStatus.Fail, run.Check("action1.registration").Status);
        Assert.DoesNotContain(_machine.Registry.Keys.Keys, k => k.Clsid.Contains("not"));
    }

    [Fact]
    public async Task MicrosoftSignedHostProgram_StillNeedsReview()
    {
        const string task = @"\Vendor\Cleanup";
        const string powershell = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ExecXml(powershell, "-enc SQBFAFgA"));
        _machine.Files.Add(powershell, MicrosoftCatalog);

        var run = await _machine.VerifyAsync(Added(task, ExecProjection(powershell, "-enc SQBFAFgA")));

        Assert.Equal(VerificationOutcome.NeedsReview, run.Outcome);
        Assert.Equal(CheckStatus.Pass, run.Check("action1.signature").Status);
        Assert.Equal(CheckStatus.Warning, run.Check("action1.host").Status);
        Assert.Contains("-enc SQBFAFgA", run.Check("action1.host").Detail);
    }

    [Fact]
    public async Task UnfamiliarPublisher_NeedsReview_UntilListedAsFamiliar()
    {
        const string task = @"\Vendor\Update";
        const string exe = @"C:\Program Files\Vendor\update.exe";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ExecXml(exe, "/check", start: "2026-10-18T09:00:00"));
        _machine.Files.Add(exe, new SignatureEvidence(SignatureState.Valid, SignatureKind.Embedded, "CN=Vendor Ltd, O=Vendor Ltd, C=GB", RevocationChecked: true));
        var observation = Changed(task, ExecProjection(exe, "/check", start: "2026-10-17T09:00:00"), ExecProjection(exe, "/check", start: "2026-10-18T09:00:00"));

        var run = await _machine.VerifyAsync(observation);
        Assert.Equal(VerificationOutcome.NeedsReview, run.Outcome);
        Assert.Equal(CheckStatus.Warning, run.Check("action1.publisher").Status);

        _machine.FamiliarPublishers.Add("Vendor Ltd");
        run = await _machine.VerifyAsync(observation);
        Assert.True(run.Outcome == VerificationOutcome.ChecksPassed, run.Describe());
    }

    [Fact]
    public async Task NewTaskNotInTheBaseline_NeedsReview_EvenWhenEverythingIsSigned()
    {
        const string task = @"\Microsoft\Windows\Fake\Telemetry";
        const string exe = @"C:\Windows\System32\svchost.exe";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ExecXml(exe, "-k netsvcs"));
        _machine.Files.Add(exe, MicrosoftCatalog);

        var run = await _machine.VerifyAsync(Added(task, ExecProjection(exe, "-k netsvcs")));

        Assert.Equal(VerificationOutcome.NeedsReview, run.Outcome);
        Assert.Equal(CheckStatus.Warning, run.Check("known-configuration").Status);
    }

    [Fact]
    public async Task WindowsNamedTaskRunningFromAUserFolder_NeedsReview()
    {
        const string task = @"\Microsoft\Windows\Maintenance\Helper";
        const string exe = @"C:\Users\Public\helper.exe";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ExecXml(exe, null));
        _machine.Files.Add(exe, MicrosoftCatalog);

        var run = await _machine.VerifyAsync(Added(task, ExecProjection(exe, null)));

        Assert.Equal(CheckStatus.Warning, run.Check("action1.location").Status);
    }

    [Fact]
    public async Task NetworkPath_IsNeverOpened()
    {
        const string task = @"\Vendor\Remote";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ExecXml(@"\\attacker.example\share\run.exe", null));

        var run = await _machine.VerifyAsync(Added(task, ExecProjection(@"\\attacker.example\share\run.exe", null)));

        Assert.Equal(CheckStatus.Warning, run.Check("action1.target").Status);
        Assert.Empty(_machine.Files.Inspected);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\notepad.exe:hidden.exe")]
    [InlineData(@"\\.\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\x.exe")]
    public async Task AlternateStreamsAndDevicePaths_FailWithoutBeingOpened(string target)
    {
        const string task = @"\Vendor\Odd";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ExecXml(target, null));

        var run = await _machine.VerifyAsync(Added(task, ExecProjection(target, null)));

        Assert.Equal(CheckStatus.Fail, run.Check("action1.target").Status);
        Assert.Empty(_machine.Files.Inspected);
    }

    [Fact]
    public async Task PerAccountVariables_ResolveThroughTheTasksOwnProfile()
    {
        const string task = @"\Vendor\OneDrive";
        const string exe = @"C:\Users\brian\AppData\Local\Microsoft\OneDrive\OneDriveStandaloneUpdater.exe";
        _machine.Accounts.Profiles[User] = @"C:\Users\brian";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ExecXml(@"%localappdata%\Microsoft\OneDrive\OneDriveStandaloneUpdater.exe", null, userId: User));
        _machine.Files.Add(exe, MicrosoftCatalog);

        var run = await _machine.VerifyAsync(Added(task, ExecProjection(@"%localappdata%\Microsoft\OneDrive\OneDriveStandaloneUpdater.exe", null, user: User)));

        Assert.Equal(exe, Assert.Single(run.Actions).ResolvedPath);
        Assert.Equal(CheckStatus.Pass, run.Check("action1.target").Status);
    }

    [Fact]
    public async Task UnknownVariables_AreNotGuessed()
    {
        const string task = @"\Vendor\Temp";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ExecXml(@"%TEMP%\run.exe", null));

        var run = await _machine.VerifyAsync(Added(task, ExecProjection(@"%TEMP%\run.exe", null)));

        Assert.Equal(CheckStatus.Unknown, run.Check("action1.target").Status);
        Assert.Empty(_machine.Files.Inspected);
    }

    [Fact]
    public async Task RemovedTask_IsInconclusive()
    {
        _machine.Tasks.Tasks.Clear();

        var run = await _machine.VerifyAsync(TriggerTimeChanged());

        Assert.Equal(VerificationOutcome.NeedsReview, run.Outcome);
        Assert.Equal(CheckStatus.Warning, run.Check("task").Status);
        // The logged change is still analysed from the alert's own evidence.
        Assert.Single(run.LoggedChanges);
    }

    [Fact]
    public async Task AccessDenied_IsUnknown_NotSafe()
    {
        _machine.Tasks.Tasks[TaskPath] = new(TaskReadStatus.AccessDenied, Error: "Access denied.");

        var run = await _machine.VerifyAsync(TriggerTimeChanged());

        Assert.Equal(VerificationOutcome.NeedsReview, run.Outcome);
        Assert.Equal(CheckStatus.Unknown, run.Check("task").Status);
    }

    [Fact]
    public async Task UnreadableLoggedEvidence_IsUnknown()
    {
        var run = await _machine.VerifyAsync(Changed(TaskPath, "{not json", ComHandlerProjection(Clsid, "x")));

        Assert.Equal(CheckStatus.Unknown, run.Check("logged-change").Status);
        Assert.NotEmpty(run.Errors);
        Assert.Equal(VerificationOutcome.NeedsReview, run.Outcome);
    }

    [Fact]
    public async Task InvalidTaskPath_IsNeverLookedUp()
    {
        var run = await _machine.VerifyAsync(Added(@"\..\..\Windows\System32", ComHandlerProjection(Clsid, "x")));

        Assert.Equal(CheckStatus.Unknown, run.Check("task").Status);
        Assert.Empty(_machine.Tasks.Reads);
    }

    [Fact]
    public async Task VerificationFailure_IsRecorded_AsNeedsReview()
    {
        _machine.Tasks.Override = _ => throw new InvalidOperationException("Task Scheduler service is not running");

        var run = await _machine.VerifyAsync(TriggerTimeChanged());

        Assert.Equal(VerificationOutcome.NeedsReview, run.Outcome);
        Assert.Contains(run.Errors, e => e.Contains("Task Scheduler service is not running"));
        Assert.Equal(CheckStatus.Unknown, run.Check("verification").Status);
    }

    [Fact]
    public async Task VerificationTimeout_IsRecorded_AsNeedsReview()
    {
        _machine.Tasks.Override = _ =>
        {
            Thread.Sleep(2000);
            return new(TaskReadStatus.NotFound);
        };

        var run = await _machine.Engine(TimeSpan.FromMilliseconds(100)).VerifyAsync(TriggerTimeChanged(), VerificationTrigger.Automatic, default);

        Assert.Equal(VerificationOutcome.NeedsReview, run.Outcome);
        Assert.Contains(run.Errors, e => e.Contains("did not finish"));
    }

    [Fact]
    public async Task RemovedEntriesAndOtherSources_AreNotVerified()
    {
        var engine = _machine.Engine();
        Assert.False(engine.CanVerify(new PersistenceObservation(Source, TaskPath, PersistenceChangeKind.Removed, "{}", null, DateTimeOffset.UtcNow)));
        Assert.False(engine.CanVerify(new PersistenceObservation("Registry/HKLM/Registry64/Run", "App", PersistenceChangeKind.Added, null, "REG_SZ: x", DateTimeOffset.UtcNow)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.VerifyAsync(
            new PersistenceObservation("Startup/C:\\x", "a.lnk", PersistenceChangeKind.Added, null, "x", DateTimeOffset.UtcNow), VerificationTrigger.Manual, default));
    }

    [Fact]
    public async Task PrivilegeEscalationInTheAlert_NeedsReview()
    {
        const string task = @"\Vendor\Sync";
        const string exe = @"C:\Program Files\Vendor\sync.exe";
        _machine.Tasks.Tasks[task] = new(TaskReadStatus.Found, ExecXml(exe, null));
        _machine.Files.Add(exe, MicrosoftCatalog);

        var run = await _machine.VerifyAsync(Changed(task, ExecProjection(exe, null, user: TaskScenarios.User), ExecProjection(exe, null, user: "SYSTEM")));

        Assert.Equal(CheckStatus.Warning, run.Check("logged-change").Status);
        Assert.Contains("account or privileges", run.Check("logged-change").Detail);
    }
}

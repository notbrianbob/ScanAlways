# Persistence & Autostart Auditor

The `Persistence` module inventories Windows autostarts and reports changes.
It never executes, disables, edits, or removes an entry. It is enabled by
default and runs inside the hub alongside the other modules.

## Coverage

- **Registry:** `Run` and `RunOnce` under
  `Software\Microsoft\Windows\CurrentVersion` in the current user's hive and
  the local machine hive, including the machine's 32-bit and 64-bit views.
  Reports the value type and literal command, without expanding variables.
- **Startup folders:** the current user's and common Startup folders, or an
  explicit folder list. Inventories direct files and hashes their contents.
  A changed shortcut or script produces an alert. `desktop.ini` is ignored.
  Shortcuts are not executed or resolved, and their target binaries are not
  hashed. Large or unreadable files make that folder's inventory incomplete.
- **Scheduled tasks:** tasks visible to the hub's account, queried using the
  in-box Windows PowerShell at its full system path. Compares task actions,
  principal, enabled setting, and common trigger fields. Transient runtime
  state is excluded. This is not a full comparison of task XML or every task
  setting. Each query has a 60-second timeout and bounded output.

The first successful inventory of each source becomes its baseline, with an
informational entry. Existing entries are **not certified safe**. Subsequent
checks report additions and changes as warnings, and removals as information,
including previous and current values. The baseline advances automatically
after observing changes; it is a last-observed inventory, not an approved list.
It survives hub restarts. Polling defaults to five minutes.

Long values are explicitly truncated in alert messages so both previous and
current excerpts fit. The baseline retains the complete observed values.

Alerts appear in normal hub logs and the [shared alert feed](../../docs/alerts-and-tray.md).
The tray companion displays warnings and critical alerts as Windows balloons.

## Verifying changed scheduled tasks

An alert says *that* an entry changed. Verification collects evidence about
*what* changed and whether it looks expected. When a scheduled task is added
or changed, the hub verifies it automatically (on by default) and raises one
more alert with the outcome:

| Outcome | Alert | Meaning |
|---|---|---|
| **Checks passed** | `VERIFY_PASSED` (Info) | Known configuration, expected file, valid signature, no unexpected changes. |
| **Needs review** | `VERIFY_NEEDS_REVIEW` (Warning) | An unknown handler, an unfamiliar or missing signature, a missing file, or a check that could not be completed. |
| **Suspicious** | `VERIFY_SUSPICIOUS` (Critical) | The program or COM class was changed, a per-user COM registration overrides the machine one, a signature is invalid, or the task differs from the trusted baseline. |

None of these is a verdict that the task is harmless. A valid signature shows
who published a file, not what a task makes it do, and a Microsoft signature
never hides another warning: `powershell.exe -enc …` is Microsoft-signed and
still needs review. Anything that cannot be checked (a missing file, access
denied, a profile that is not loaded, a variable such as `%TEMP%` that depends
on the account) makes the result *needs review*, never *passed*.

### What is checked

| Check | How |
|---|---|
| Change in the alert | The definitions logged before and after the change, compared field by field (for example `Triggers[0].StartBoundary`). A changed program or COM class fails; changed arguments, account or privileges, or a re-enabled task need review; a schedule-only change passes. |
| Current task | Read from the Task Scheduler API (`ITaskService`, `IRegisteredTask.Xml`). A removed task needs review. |
| Current task vs. alert | Has it changed again since the alert? Accounts are compared by SID, so `NETWORK SERVICE` and `S-1-5-20` match. Schedule drift is reported but passes. |
| Known configuration | The trusted baseline of Windows components, or, for a changed task, what it runs and its account being unchanged from the earlier recording. A new task that is in neither needs review. |
| COM registration | For COM handler actions: the CLSID is validated, then looked up in `HKLM\SOFTWARE\Classes\CLSID` (64-bit view, with the 32-bit view noted when it differs) and in the per-user classes of the account the task runs as, or of every signed-in user for tasks that run as whoever signs in. `TreatAs` redirections are followed. |
| Expected file / location | The registered DLL or program against the trusted baseline; tasks under `\Microsoft\Windows\` that run files from outside Windows or program folders need review. |
| Program / handler file | Exists; size, SHA-256, file version, product and company, timestamps. Links (reparse points) need review. Network paths are never opened (that would contact the host as the hub's account); device paths and alternate data streams fail. |
| Signature, publisher | WinVerifyTrust on the file, including Windows catalog signatures (most system DLLs, such as `sppcext.dll`, are catalog-signed). Revocation uses cached data only, so a check never waits on the network. Publishers other than Microsoft need review unless listed in `FamiliarPublishers`. |
| Host program | `cmd`, `powershell`, `rundll32`, `mshta` and similar run what their arguments say; shown with the arguments and always needs review. |

The reference case is `\Microsoft\Windows\SoftwareProtectionPlatform\SvcRestartTask`,
whose COM handler `{B1AEBB5D-EAD9-4476-B375-9C3ED9F32AFC}` is served by
`C:\Windows\System32\sppcext.dll`. Windows reschedules it regularly, so it
often raises `ENTRY_CHANGED`; verification reports that only
`Triggers[0].StartBoundary` changed, and independently confirms the current
registration, the DLL and its Microsoft Windows catalog signature. The
Windows CI job runs this scenario against the real APIs.

### Findings and history

Findings are stored next to the alert feed, in `alerts.json.verifications.json`
(same folder and permissions as the feed: the hub writes, the tray user reads),
keyed by the alert's ID. Each record keeps the **original evidence** logged
with the alert (complete before/after definitions, stored once and never
replaced) separately from each **verification run** of the current state:
when and as which account it ran, every check with Pass, Warning, Fail or
Unknown, the field changes, each action's resolved path, hash, version and
signer, the current task XML, and any errors. The newest 100 alerts and 5 runs
per alert are kept. An unreadable findings file is moved aside, not
overwritten. Verification alerts also go to the Windows Event Log (IDs
5010–5013, see [event-log.md](../../docs/event-log.md)).

With `AutoVerifyAlerts` off, the original evidence is still stored, so an
entry can be verified later.

### What verification never does

It never runs the task, activates the COM class, or loads or executes the
target file; it reads task XML, registry keys and file bytes only. It never
disables tasks, deletes or quarantines files, or changes anything else;
there is no automatic remediation. Entry data is never put into a shell
command: task paths, CLSIDs, SIDs and file paths are validated and passed to
structured Windows APIs. Each run has a 90-second limit; a run that fails or
times out is recorded as needing review, with the error.

### Trusted baseline

`Verification/TrustedBaseline.cs` lists Windows tasks whose configuration is
known: task path, action type, CLSID, expected file and expected signer. It
deliberately holds only stable facts (no hashes or versions, which change
with every update) and starts with the reference task only. A mismatch is
reported as suspicious, so entries are added only after checking them on
clean installs; the Windows CI job fails if an entry does not match the
runner. Tasks that are not listed are treated as unknown, not as safe.

### Adding other persistence mechanisms

Verification is built around `IEntryVerifier` (in `Verification/VerificationEngine.cs`):
`AppliesTo` picks entries by source (for example `Registry/…`, `Startup/…`),
`VerifyAsync` returns checks, field changes and per-target evidence, and the
engine handles time limits, errors and the overall outcome. A new mechanism
(Run keys, services, Startup shortcuts, WMI event subscriptions) adds one
verifier to `VerificationFactory` and reuses `WindowsPath`, `FileInspector`
and the Authenticode inspector. Only scheduled tasks are verified today.

## Configuration

Settings live in the hub's `appsettings.json`, under `Persistence`:

| Setting | Default | Meaning |
|---|---|---|
| `CheckIntervalSeconds` | `300` | Poll interval; minimum 30 seconds. |
| `WatchRegistry` | `true` | Inventory Run and RunOnce keys. |
| `WatchStartupFolders` | `true` | Inventory Startup folder contents. |
| `WatchScheduledTasks` | `true` | Query accessible scheduled tasks. |
| `StartupFolders` | `[]` | Empty uses the account's Startup and common Startup folders; explicit paths replace those defaults. |
| `IgnoredEntries` | `[]` | Case-insensitive `*` / `?` patterns matching the full source/entry identity shown in alerts. Suppresses change alerts only; coverage failures remain visible. |
| `BaselinePath` | `null` | Defaults to `%LOCALAPPDATA%\ScanAlways\persistence-baseline.json`. |
| `MaxStartupFileBytes` | `4194304` | Per-file hashing limit; clamped to 4 KiB–16 MiB. |
| `AutoVerifyAlerts` | `true` | Verify a scheduled task when an alert reports it added or changed, and raise a `VERIFY_*` alert. Never remediates. |
| `FamiliarPublishers` | `[]` | Certificate common names or organizations (for example `Google LLC`) whose signatures pass the publisher check, besides Microsoft. Other checks still apply. |

Example suppression for a known updater in a Run key:

```json
"Persistence": {
  "IgnoredEntries": ["Registry/*/Run/TrustedUpdater"]
}
```

Suppressed entries still advance the baseline. Removing a suppression does not
retroactively alert on changes already observed. Keep patterns narrow: software
installers and updaters legitimately change autostarts, and a changed entry is
not by itself evidence of malware.

Disable the module with `Modules:Persistence:Enabled=false`.

## Incomplete coverage and baseline recovery

An unreadable source produces a change-only coverage warning. Its previous
baseline stays intact; missing permissions are never interpreted as removals.
When access returns, the module reports recovery and compares against the last
successful inventory. Tasks hidden from the account cannot be enumerated at
all; the module cannot claim complete machine coverage.

A malformed or unsupported baseline pauses auditing and raises a warning. It
is never silently overwritten. To reset it deliberately, stop the hub, preserve
the old file for inspection, then move it out of the configured baseline path
and restart. That creates a new initial inventory and loses comparison with
the old baseline. If saving fails, the module reports it, tracks changes in
memory, and retries the save; those changes may be reported again after a crash.

## Service accounts and limits

Run the hub under the account you want to monitor for current-user entries.
Under LocalSystem, HKCU and the default user Startup folder refer to the system
account. `StartupFolders` can explicitly include another user's Startup folder,
but it does not change which HKCU hive is read. Machine entries and visible
scheduled tasks are still checked. Setting up a shared tray feed does not change
the auditor's account or permissions.

This version does not inventory services, drivers, WMI subscriptions, browser
extensions, every registry autostart location, or all user profiles. Changes
made and undone between checks can be missed. The baseline and alert feed are
local files, not tamper-proof evidence; protect their directories with normal
Windows permissions. Command lines can contain sensitive data, so restrict
who can read the baseline and alerts. No inventory is available off Windows;
the module reports that limitation rather than claiming a healthy result.

### Verification limits

- Only scheduled tasks are verified. Run keys, Startup files, services and WMI
  subscriptions are not yet.
- Verification sees the task as it is when the check runs. A task changed and
  changed back between the alert and the check looks unchanged, though the
  alert's own before/after evidence is kept and compared.
- `rundll32`, script hosts and similar programs are flagged, but the DLL or
  script in their arguments is not resolved or inspected.
- Per-user COM registrations can only be checked for profiles whose registry
  hive is loaded (signed-in users and service accounts); otherwise the check is
  inconclusive.
- Revocation is checked only against cached revocation data.
- The trusted baseline is small. Most tasks are judged by their own history,
  signature and publisher, not against a known-good configuration.
- Signatures are evidence of origin. Signed malware and abused Microsoft
  programs exist; nothing here replaces Microsoft Defender.

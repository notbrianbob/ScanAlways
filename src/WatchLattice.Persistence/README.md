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

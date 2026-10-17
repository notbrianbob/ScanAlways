# WatchLattice.DefenderWatchdog — Defender health watchdog

Turning Microsoft Defender off is often the first thing malware does. The
quieter route is to leave Defender on but blind: add an exclusion for its own
folder, switch an attack surface reduction rule to audit, or turn cloud
protection down. This small service checks every few minutes that Defender is
actually protecting the machine, and raises an alert the moment it isn't.

## What it checks

It runs the documented, read-only `Get-MpComputerStatus` and
`Get-MpPreference` cmdlets and evaluates:

| Check | Alert if | Severity |
|---|---|---|
| Real-time protection | off | Critical |
| Antivirus / antimalware service | disabled | Critical |
| Defender running mode | "Not running" | Critical |
| Signature age | older than 48 h / 7 days | Warning / Critical |
| Tamper protection | off | Warning (configurable to Critical) |
| Behavior monitoring, download (IOAV) scanning, on-access protection | off | Warning |
| Passive mode | another antivirus is primary | Warning (once, instead of a cascade of "off" alerts) |
| Status can't be read | PowerShell or the Defender module fails | Warning ("Unknown" health) |
| Cloud-delivered protection | off (MAPS reporting disabled) | Warning |
| Block at first sight | off while cloud protection is on | Warning |
| Broad exclusions | an exclusion covering a drive root, `Windows`, `Program Files`, `ProgramData`, a user profile, Downloads, Desktop, Documents, AppData or a temp folder; an executable or script extension (`.exe`, `.dll`, `.ps1`, `.js`, …); or a script host / system process (`powershell.exe`, `cmd.exe`, `rundll32.exe`, …) | Warning, one per exclusion |
| Exclusions hidden | Defender won't show its exclusion list to this process (not elevated) | Warning |
| Settings can't be read | `Get-MpPreference` fails | Warning |

Cloud and broad-exclusion checks are skipped while Defender is passive or not
running, since that is already reported and those settings protect nothing
then.

## Settings changes

Besides the standing checks above, the watchdog remembers Defender's
exclusions, attack surface reduction (ASR) rules and cloud block level, and
reports each change as a one-off event:

| Change | Severity |
|---|---|
| New exclusion (path, extension, process or IP address) | Warning |
| Exclusion removed | Info |
| ASR rule weakened: Block → Warn → Audit → off, or removed | Warning |
| ASR rule strengthened or added | Info |
| Cloud block level lowered / raised | Warning / Info |

Malware often adds its own folder or process to Defender's exclusions so
Defender stops looking at it, which is why a new exclusion is a warning even
though installers and developer tools add them legitimately too. ASR rules are
named by GUID; Microsoft's
[ASR rules reference](https://learn.microsoft.com/defender-endpoint/attack-surface-reduction-rules-reference)
maps each GUID to its name.

The first check records a **baseline** of what is already configured and
reports it once as an Info event ("inventory, not a safety verdict"); each
baseline exclusion is listed in the alert log so you can review it. Only
changes after that are reported. The snapshot is saved to
`%LOCALAPPDATA%\ScanAlways\defender-settings.json`, so an exclusion added while
the hub was stopped is still reported at the next start. If the snapshot can't
be read it is rebuilt and that is reported once; if it can't be saved, a
warning stays open until saving works again.

**Reading exclusions needs elevation.** Defender only shows its exclusion list
to administrators (and, with the "hide exclusions from local admins" policy, to
SYSTEM only). The hub installed as a Windows service runs as LocalSystem and
can read them. Run it unelevated and you get one "exclusions hidden" warning
instead; the rest of the checks still work. A check that can't read a setting
never reports it as removed or recovered.
## How it alerts

Only on **change**: when a problem first appears it writes a `DEFENDER ALERT`
line to `%LOCALAPPDATA%\ScanAlways\defender-watchdog.log`, logs a warning or
critical entry (Windows Event Log when running as a service), and raises
`AlertRaised`. When the problem clears it logs a "recovered" line and raises
`AlertCleared`. A problem that persists is not repeated every check. Every
check also raises `Checked` with the full report, for a UI host.

When hosted by the hub, findings and recovery events also go to the shared
feed. The [Windows tray companion](../../docs/alerts-and-tray.md) displays new
warnings/critical alerts and provides recent history.

## Read-only by design

The watchdog never changes a Defender setting, never re-enables anything, and
never hooks Defender's components. The exact PowerShell it runs is the
`PowerShellDefenderStatusProvider.Script` constant, and a unit test asserts it
contains no `Set-Mp*`, `Add-Mp*` or `Remove-Mp*` calls. Exclusion values are
only stored in the local snapshot, logs and alert feed; they never leave the
machine. It launches the in-box
Windows PowerShell by full path so a lookalike `powershell.exe` on `PATH` can't
answer for Defender.

## Configuration (hub `appsettings.json`, section `DefenderWatchdog`)

| Setting | Default | Meaning |
|---|---|---|
| `CheckIntervalSeconds` | `300` | Seconds between checks (minimum 30). |
| `MaxSignatureAgeHours` | `48` | Signatures older than this are a warning. |
| `CriticalSignatureAgeHours` | `168` | Signatures older than this are critical. |
| `TamperProtectionOffIsCritical` | `false` | Escalate tamper protection off to critical. Off by default because some managed (Intune/GPO) machines disable it on purpose. |
| `MonitorSettings` | `true` | Watch exclusions, ASR rules and cloud-delivered protection. |
| `ReportBroadExclusions` | `true` | Raise a standing warning for each broad exclusion (see above). |
| `IgnoredExclusions` | `[]` | Exclusion values you added on purpose, e.g. `"C:\\Dev\\*"` in JSON. Wildcards `*` and `?`; backslashes are literal. Matching exclusions never raise a new-exclusion or broad-exclusion alert. |
| `MaxChangeAlertsPerCheck` | `10` | Individual change alerts per check before the rest are summarized in one alert. |
| `StatePath` | `%LOCALAPPDATA%\ScanAlways\defender-settings.json` | Saved settings snapshot. |
| `AlertLogPath` | `%LOCALAPPDATA%\ScanAlways\defender-watchdog.log` | Rolling alert log. |

## Run

This tool runs as the `DefenderWatchdog` module inside the [WatchLattice hub](../WatchLattice.Hub/),
alongside the other tools. It is enabled by default; turn it off with
`Modules:DefenderWatchdog:Enabled = false`.

```powershell
dotnet run -c Release --project src/WatchLattice.Hub
```

To install the hub as a Windows service, see
[Install as a Windows service](../../README.md#install-as-a-windows-service)
in the top-level README.

## Honest limits

- **It reports, it doesn't repair.** Re-enabling protection is your call (or
  your admin's); the watchdog only tells you something changed.
- **Polling, not real-time.** A problem is noticed within one check interval.
  Malware that disables Defender, acts, and re-enables it between checks can
  slip past. Lower the interval if you want tighter coverage.
- **It trusts what Defender reports.** Malware with enough privilege to tamper
  with Defender's own status reporting can also fool this check.
- **It can be killed.** Like any user-mode process it can be stopped by
  malware running with the same or higher rights; when that happens you stop
  getting reports, so pair it with the other tools in the suite.
- **Exclusions need elevation.** Unelevated, the watchdog can't see
  exclusions and says so; it does not guess.
- **The baseline trusts what is already there.** Exclusions present at the
  first check are recorded, not judged, apart from the broad-exclusion check.
  Review the baseline lines in the alert log once.
- **Managed machines.** Exclusions and ASR rules pushed by Intune or Group
  Policy show up like any other change. Add expected ones to
  `IgnoredExclusions`.
- **Not checked:** controlled folder access, network protection, PUA
  protection, and Defender's own scan schedules.

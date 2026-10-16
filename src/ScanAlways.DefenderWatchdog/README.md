# ScanAlways.DefenderWatchdog — Defender health watchdog

Turning Microsoft Defender off is often the first thing malware does. This
small service checks every few minutes that Defender is actually protecting the
machine, and raises an alert the moment it isn't.

## What it checks

It runs the documented, read-only `Get-MpComputerStatus` cmdlet (works for a
standard user, no admin needed) and evaluates:

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

## How it alerts

Only on **change**: when a problem first appears it writes a `DEFENDER ALERT`
line to `%LOCALAPPDATA%\ScanAlways\defender-watchdog.log`, logs a warning or
critical entry (Windows Event Log when running as a service), and raises
`AlertRaised`. When the problem clears it logs a "recovered" line and raises
`AlertCleared`. A problem that persists is not repeated every check. Every
check also raises `Checked` with the full report, for a UI host.

## Read-only by design

The watchdog never changes a Defender setting, never re-enables anything, and
never hooks Defender's components. The exact PowerShell it runs is the
`PowerShellDefenderStatusProvider.Script` constant, and a unit test asserts it
contains no `Set-Mp*`, `Add-Mp*` or `Remove-Mp*` calls. It launches the in-box
Windows PowerShell by full path so a lookalike `powershell.exe` on `PATH` can't
answer for Defender.

## Configuration (`appsettings.json`, section `DefenderWatchdog`)

| Setting | Default | Meaning |
|---|---|---|
| `CheckIntervalSeconds` | `300` | Seconds between checks (minimum 30). |
| `MaxSignatureAgeHours` | `48` | Signatures older than this are a warning. |
| `CriticalSignatureAgeHours` | `168` | Signatures older than this are critical. |
| `TamperProtectionOffIsCritical` | `false` | Escalate tamper protection off to critical. Off by default because some managed (Intune/GPO) machines disable it on purpose. |
| `AlertLogPath` | `%LOCALAPPDATA%\ScanAlways\defender-watchdog.log` | Rolling alert log. |

## Run

```powershell
dotnet run --project src/ScanAlways.DefenderWatchdog
# or as a Windows service
sc.exe create "ScanAlways Defender Watchdog" binPath= "C:\path\to\ScanAlways.DefenderWatchdog.exe"
```

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
- **Not yet checked:** exclusion lists (reading them needs admin on current
  Windows), ASR rules, and cloud-delivered protection. Good candidates for a
  later version.

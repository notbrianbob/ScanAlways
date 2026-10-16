# ScanAlways.FirewallWatchdog — Windows Firewall health watchdog

Malware that wants to be reached from the network (a remote shell, a botnet
listener, lateral movement tooling) often turns the firewall off, flips a
profile to "allow inbound", or quietly adds an inbound allow rule for itself.
This module checks every few minutes that Windows Firewall is still filtering,
and alerts when that changes.

It is a separate layer from the [Defender health watchdog](../ScanAlways.DefenderWatchdog/):
a machine can have Defender fully healthy and its firewall wide open, or the
reverse. Neither watchdog replaces the product it watches.

## What it checks

It runs the documented, read-only NetSecurity cmdlets against the **effective**
policy (`-PolicyStore ActiveStore`, which includes Group Policy and Intune
settings) and evaluates:

| Check | Alert if | Severity |
|---|---|---|
| Profile enabled (Domain, Private, Public) | a profile is off | Critical if a connected network uses that profile, Warning if not |
| Default inbound action | a profile allows unsolicited inbound connections by default | Critical / Warning, as above |
| Default inbound action changes | the action differs from the last check (including changes made while the hub was stopped) | Warning when it changed to Allow, Info otherwise |
| New inbound allow rules (**opt-in**, `MonitorInboundAllowRules`) | an enabled inbound allow rule appears that was not there at the last check | Warning, up to 10 per check, then one summary alert |
| Status can't be read | PowerShell or the NetSecurity module fails | Warning ("Unknown" health) |

"In use" comes from `Get-NetConnectionProfile`. When that can't be read, every
profile is treated as possibly in use, so a problem is never downplayed.

A disabled profile is reported once as "profile off", not also as an inbound
default problem.

## New inbound allow rules (opt-in)

With `MonitorInboundAllowRules` on, the first check records the enabled inbound
allow rules that already exist as a baseline (an inventory, not a safety
verdict) and reports nothing about them. From then on, any rule that wasn't in
the previous check is reported: a brand-new rule, an existing rule that was
enabled, or a block rule switched to allow. Removed rules are not reported.

It is **off by default** because installers, games and Windows updates add
rules routinely, and listing every rule costs a few seconds of PowerShell per
check. Turn it on when you want to know about each one, and silence known
noisy apps with `IgnoredInboundRules` (matched against the rule's name or
display name, `*` and `?` wildcards). Turning monitoring off forgets the
baseline, so turning it back on starts fresh instead of reporting everything
installed in between.

## How it alerts

Standing problems (a profile off, inbound allowed by default) are reported on
**change**: when one first appears it writes a `FIREWALL ALERT` line to
`%LOCALAPPDATA%\ScanAlways\firewall-watchdog.log`, logs a warning or critical
entry (Windows Event Log when running as a service), and raises `AlertRaised`.
When it clears it logs a "recovered" line and raises `AlertCleared`. A problem
that persists is not repeated every check, and a check that can't read the
firewall at all never counts as a recovery: earlier problems stay open until a
readable check shows them gone. One-off changes (a new default
inbound action, a new rule) are logged as `FIREWALL CHANGE` lines and raise
`ChangeDetected`.

When hosted by the hub, all of these also go to the shared alert feed under the
`FirewallWatchdog` module, and the [Windows tray companion](../../docs/alerts-and-tray.md)
shows new warnings and critical alerts.

The last observed policy (and the rule baseline, when monitoring is on) is kept
in `%LOCALAPPDATA%\ScanAlways\firewall-state.json`, so a change made while the
hub was stopped is reported at the next start. If that file can't be read the
watchdog says so once and starts fresh; if it can't be written, a warning
explains that changes won't survive a restart.

## Read-only by design

The watchdog never changes a firewall setting, never re-enables a profile, and
never adds or removes a rule. The exact PowerShell it runs is the
`PowerShellFirewallStatusProvider.Script` constant (prefixed with one line
setting `$IncludeRules`), and a unit test asserts it contains no `Set-`,
`New-`, `Remove-`, `Enable-`, `Disable-` or `netsh` calls. It launches the
in-box Windows PowerShell by full path so a lookalike `powershell.exe` on
`PATH` can't answer for the firewall. Reading firewall policy does not need
administrator rights.

## Configuration (hub `appsettings.json`, section `FirewallWatchdog`)

| Setting | Default | Meaning |
|---|---|---|
| `CheckIntervalSeconds` | `300` | Seconds between checks (minimum 30). |
| `MonitorInboundAllowRules` | `false` | Also report newly introduced enabled inbound allow rules. |
| `IgnoredInboundRules` | `[]` | Rule names or display names (wildcards allowed) that never raise a new-rule alert. |
| `MaxRuleAlertsPerCheck` | `10` | New-rule alerts listed individually per check; the rest are summarized in one alert. |
| `StatePath` | `%LOCALAPPDATA%\ScanAlways\firewall-state.json` | Last observed policy and rule baseline. |
| `AlertLogPath` | `%LOCALAPPDATA%\ScanAlways\firewall-watchdog.log` | Rolling alert log. |

## Run

This tool runs as the `FirewallWatchdog` module inside the [ScanAlways hub](../ScanAlways.Hub/),
alongside the other tools. It is enabled by default; turn it off with
`Modules:FirewallWatchdog:Enabled = false`.

```powershell
dotnet run -c Release --project src/ScanAlways.Hub
```

To install the hub as a Windows service, see
[Install as a Windows service](../../README.md#install-as-a-windows-service)
in the top-level README.

To see a new-rule alert without weakening anything, turn on
`MonitorInboundAllowRules`, let one check run, then add a narrowly scoped
test rule (for example an inbound allow rule for a program path that doesn't
exist, limited to `LocalSubnet`) in Windows Defender Firewall with
Advanced Security, and delete it afterwards. Don't turn a profile off to test
notifications.

## Honest limits

- **It reports, it doesn't repair.** Turning a profile back on or removing a
  rule is your call (or your admin's); the watchdog only tells you something
  changed.
- **Polling, not real-time.** A change is noticed within one check interval.
  Something that opens the firewall, acts, and restores it between checks can
  slip past. Lower the interval if you want tighter coverage.
- **Only Windows Firewall.** If a third-party firewall has taken over, Windows
  Firewall profiles may legitimately report off; this module will still warn,
  and it cannot judge the other product's policy.
- **It trusts what Windows reports.** An attacker with administrator rights can
  tamper with the firewall service or its reporting, and can stop the hub.
- **New rules, not every change.** With monitoring on it reports rules that
  newly appear; it does not report edits to an existing allow rule's ports,
  addresses or program, outbound rules, or rules that were removed. Those are
  candidates for a later version.
- **Outbound policy is not checked.** Windows allows outbound traffic by
  default, so an "allow outbound" default would only produce noise.

# Always-Scanning Decoy

Makes a Windows 11 machine look like it is **continuously running an
anti-malware scan**, as a deception-based defense layer.

Some malware watches for signs that it is being analyzed or scanned and stays
dormant — or changes behavior — when it thinks a scanner or sandbox is active.
This tool manufactures those signs on an ordinary machine around the clock, so
that a sample relying on "the coast is clear" may keep its head down. That is
the entire idea.

It runs as a background **Windows service** (or a plain console process for
testing) under the name **ScanAlways Protection**, alongside Microsoft Defender.

## What it actually does

Under the hood it does one honest, benign thing on a loop:

1. Walks a configured set of directories (read-only).
2. Opens each file, reads a small prefix, and computes a hash of it — exactly
   the kind of I/O a signature scanner produces — then **discards** the result.
3. Narrates the activity in anti-malware vocabulary to a rolling log and to the
   service log.

Pacing is throttled hard (a configurable files-per-second cap, a per-file byte
cap, and a max file size) so CPU and disk overhead stay modest.

## Signals it emulates

- **A scanner-named process always resident.** The service binary is
  `ScanAlways.Protection.Service.exe`, and it registers as the
  *ScanAlways Protection* Windows service — a plausible always-on security
  service in the process/service list.
- **Steady scanner-style file-read I/O.** Continuous read + hash activity across
  user-profile locations, the way real-time protection or an on-demand scan
  looks to anything watching file access.
- **A live scan log.** A rolling `scan-activity.log` full of
  `Scanning (heuristic): …`, `N items checked, 0 threats found, … items/s`
  style lines, so the machine appears to keep an active scan journal.
- **Selectable intensity.** `Idle`, `Balanced` (default, a real-time-protection
  trickle), or `FullScan` (looks like an on-demand full-system scan in
  progress).

## Honest limits — read this

This tool is a **decoy**. It does not provide real protection:

- It **detects nothing.** The hash is thrown away; there is no signature or
  reputation check. "0 threats found" is literally always true because it never
  looks for threats.
- It **removes and changes nothing.** Every file is opened read-only; it does
  not quarantine, delete, modify, or even update access/modified timestamps.
- It **does not touch Defender.** It neither impersonates nor hooks Defender's
  components, and does nothing that could disable or confuse your real
  anti-malware.
- Its value depends entirely on whether a given piece of malware actually checks
  for scanning activity and backs off. Many do not. Plenty of real threats run
  happily in plain sight, and some evasive samples look for *specific* analysis
  tools rather than generic file-read activity — this will not fool those.
- A determined attacker who inspects the machine can tell a decoy scanner from a
  real engine. The deception is shallow by design (so it stays harmless and
  cheap); it is not a sandbox and not an analysis environment.

Treat it as a cheap, always-on bluff layered on top of a real, up-to-date
anti-malware product — **never** as a replacement for one.

## Configuration

Settings live in [`appsettings.json`](appsettings.json) under the `Decoy`
section (overridable via environment variables or command-line switches):

| Key | Default | Meaning |
|-----|---------|---------|
| `Intensity` | `Balanced` | `Idle`, `Balanced`, or `FullScan`. |
| `ScanRoots` | `[]` | Directories to sweep. Empty = sensible user-profile fallbacks. |
| `MaxFilesPerSecond` | `8` | Hard cap on files opened per second. |
| `BytesReadPerFile` | `65536` | Bytes read from the front of each file. |
| `MaxFileSizeBytes` | `268435456` | Files larger than this are named but not read. |
| `WriteScanLog` | `true` | Write the cosmetic rolling scan log. |
| `ScanLogPath` | `null` | Log location; `null` = `%TEMP%\ScanAlways\scan-activity.log`. |

## Run it

As a console app (for testing — Ctrl+C to stop):

```bash
dotnet run -c Release --project src/ScanAlways.Decoy
```

Install as a Windows service (run from an elevated PowerShell on the target
machine):

```powershell
dotnet publish src/ScanAlways.Decoy -c Release -r win-x64 --self-contained false -o C:\ScanAlways
sc.exe create "ScanAlways Protection" binPath= "C:\ScanAlways\ScanAlways.Protection.Service.exe" start= auto
sc.exe start "ScanAlways Protection"
```

To remove it:

```powershell
sc.exe stop "ScanAlways Protection"
sc.exe delete "ScanAlways Protection"
```

# ScanAlways

[![CI](https://github.com/notbrianbob/ScanAlways/actions/workflows/ci.yml/badge.svg)](https://github.com/notbrianbob/ScanAlways/actions/workflows/ci.yml)
[![CodeQL](https://github.com/notbrianbob/ScanAlways/actions/workflows/codeql.yml/badge.svg)](https://github.com/notbrianbob/ScanAlways/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![Platform: Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D6)

A suite of small, complementary defense tools for Windows 11, designed to run
**alongside** Microsoft Defender, never instead of it. All tools run together
inside one lightweight background service, the **ScanAlways hub**, and each
one can be switched on or off in a single config file.

## Why complement Defender?

Defender is good, and it is everywhere. That ubiquity is also its weak spot:
commodity malware is routinely tested against Defender before it is released,
and many samples check for it specifically. Far fewer are built to anticipate
small, unfamiliar tools.

ScanAlways leans on that gap. Each module adds a cheap, slightly unusual layer
of deception or early warning that a given sample was probably not built to
expect. It is defense in depth: Defender stays your primary engine for
detection and removal, and ScanAlways adds friction and tripwires around it.

## Threat model

**What ScanAlways is meant to help with**

- Commodity malware and ransomware on a single-user Windows 11 machine, running
  with the logged-in user's privileges.
- Samples that change behavior when they think they are being watched (for
  example, staying dormant while a scan appears to be running).
- Early, noisy steps of an attack that a tripwire can catch: touching decoy
  files, or trying to switch protection off.

**Assumptions**

- Microsoft Defender (or another real anti-malware product) is installed, up
  to date, and running. ScanAlways never replaces it.
- The attacker does not already have administrator or SYSTEM rights. An
  attacker with those can stop any user-mode tool, including this one.

**Out of scope**

- Targeted attacks by someone who studies the machine before acting.
- Kernel-mode threats, bootkits, and firmware implants.
- Detecting or removing malware. ScanAlways modules observe, deceive, and
  alert; they do not quarantine or delete anything.

## Tools

| Module | Status | What it does |
|--------|--------|--------------|
| **Always-Scanning Decoy** ([`src/ScanAlways.Decoy`](src/ScanAlways.Decoy/)) | Available | Makes the machine look like an anti-malware scan is permanently running, so malware that hides from scanners stays hidden. Read-only. See its [README](src/ScanAlways.Decoy/README.md). |
| **Canary File Watcher** ([`src/ScanAlways.Canary`](src/ScanAlways.Canary/)) | Available | Plants decoy files in Desktop, Documents and Pictures and alerts the moment anything modifies, renames, or deletes one: an early ransomware tripwire. See its [README](src/ScanAlways.Canary/README.md). |
| **Defender Health Watchdog** ([`src/ScanAlways.DefenderWatchdog`](src/ScanAlways.DefenderWatchdog/)) | Available | Checks every few minutes that Defender's real-time protection, tamper protection and signatures are actually on and current, and alerts when something turns them off. Read-only. See its [README](src/ScanAlways.DefenderWatchdog/README.md). |
| **Firewall Health Watchdog** ([`src/ScanAlways.FirewallWatchdog`](src/ScanAlways.FirewallWatchdog/)) | Available | Reports disabled Windows Firewall profiles, profiles that allow unsolicited inbound traffic by default, and every change to a profile's default inbound action. Optionally (off by default) reports newly introduced inbound allow rules. A separate layer from the Defender watchdog. Read-only. See its [README](src/ScanAlways.FirewallWatchdog/README.md). |
| **Persistence & Autostart Auditor** ([`src/ScanAlways.Persistence`](src/ScanAlways.Persistence/)) | Available | Records a baseline of Run/RunOnce keys, Startup files, and visible scheduled tasks, then reports additions, changes, removals, and gaps in coverage. Read-only. See its [README](src/ScanAlways.Persistence/README.md). |

The optional **Windows tray companion** shows shared alerts, recent history,
module run states, and hub connection status. See [alerts and tray setup](docs/alerts-and-tray.md).

The **Update Checker** ([`src/ScanAlways.Updater`](src/ScanAlways.Updater/)) runs
in the hub too. It checks this repository's GitHub Releases, tells you in the
tray when a newer version is out, and installs it only when you choose
**Install update**, after verifying the download's SHA-256 checksum. See its
[README](src/ScanAlways.Updater/README.md) and [cutting a release](docs/releasing.md).

## How it fits together

```
ScanAlways.Protection.Service.exe  (the hub: one process, console or Windows service)
├── reads appsettings.json
├── discovers the modules it was built with
└── runs every enabled module side by side
    ├── Decoy             ── Modules:Decoy:Enabled
    ├── Canary            ── Modules:Canary:Enabled
    ├── DefenderWatchdog  ── Modules:DefenderWatchdog:Enabled
    ├── FirewallWatchdog  ── Modules:FirewallWatchdog:Enabled
    ├── Persistence       ── Modules:Persistence:Enabled
    └── Updater           ── Modules:Updater:Enabled (checks only)

ScanAlways.Tray.exe (runs in the signed-in user's session)
├── reads the hub's local alert feed and shows notifications/status
└── Install update: starts the hub's --apply-update, elevated, on request
```

The hub isolates modules from each other: if one fails it is logged and
restarted with back-off, and the rest keep running. Modules are compiled into
the hub; nothing is loaded from a plugins folder at runtime, so there is no
way to drop a DLL next to the service and have it executed.

## Quick start

You need Windows 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
git clone https://github.com/notbrianbob/ScanAlways.git
cd ScanAlways
dotnet build -c Release
dotnet test  -c Release
```

Run the hub in a console window (Ctrl+C to stop):

```bash
dotnet run -c Release --project src/ScanAlways.Hub
```

See which modules are available and whether they are enabled:

```bash
dotnet run -c Release --project src/ScanAlways.Hub -- --list-modules
```

### Install as a Windows service

From an elevated PowerShell:

```powershell
dotnet publish src/ScanAlways.Hub -c Release -r win-x64 --self-contained false -o C:\ScanAlways
sc.exe create "ScanAlways Protection" binPath= "C:\ScanAlways\ScanAlways.Protection.Service.exe" start= auto
sc.exe start "ScanAlways Protection"
```

A service runs as LocalSystem by default, so folders that default to "your"
profile (the decoy's scan roots, the canary's Desktop/Documents/Pictures)
resolve to the system profile instead. Either run the service under your own
account (`obj= ".\you" password= ...`) or list your folders explicitly in
`Decoy:ScanRoots` and `Canary:Folders`.

Persistence's HKCU inventory also follows the service account. Run the hub under
your account to monitor your current-user Run keys. The tray always runs in your
interactive session; a LocalSystem service needs an explicit shared feed path
with read permission for the tray user. See [service and tray setup](docs/alerts-and-tray.md#localsystem-service-with-a-user-tray).

To remove it:

```powershell
sc.exe stop "ScanAlways Protection"
sc.exe delete "ScanAlways Protection"
```

## Configuration

Everything lives in `appsettings.json` next to the executable
([source](src/ScanAlways.Hub/appsettings.json)). Any value can also be
overridden with an environment variable or a command-line switch, for example
`--Modules:Canary:Enabled=false`.

```jsonc
{
  "Hub": {
    "RestartFailedModules": true,   // restart a module that crashes
    "RestartDelaySeconds": 30,      // first back-off; doubles each time
    "MaxRestartDelaySeconds": 600
  },
  "Modules": {                      // one switch per module
    "Decoy":            { "Enabled": true },
    "Canary":           { "Enabled": true },
    "DefenderWatchdog": { "Enabled": true },
    "FirewallWatchdog": { "Enabled": true },
    "Persistence":      { "Enabled": true },
    "Updater":          { "Enabled": true }
  },
  // Each module's own settings live in a section named after it; see its README.
  "Decoy": { },
  "Canary": { },
  "DefenderWatchdog": { },
  "FirewallWatchdog": { },
  "Persistence": { },
  "Updater": { },
  "Alerts": { "Enabled": true, "FeedPath": null, "HistoryLimit": 200 }
}
```

## Honest limits

- ScanAlways **does not detect or remove malware**. The canary watcher and
  the Defender and firewall watchdogs raise alarms; they do not stop anything. If something is
  already running on your machine, use Defender or another real scanner.
- Deception only works on malware that checks for the signals we emulate. Many
  threats do not check, and those will not be slowed down.
- Every module runs in user mode. An attacker with administrator rights can
  stop the hub.
- A determined human attacker can tell a decoy from a real security product.
  The modules are deliberately shallow so they stay harmless and cheap.

## Repository layout

```
ScanAlways.sln
├── src/
│   ├── ScanAlways.Common/            # Shared library: module contract, hub supervisor, decoy engine, rolling log
│   ├── ScanAlways.Hub/               # The hub executable (console app / Windows service)
│   ├── ScanAlways.Decoy/             # Module: always-scanning decoy
│   ├── ScanAlways.Canary/            # Module: canary file watcher (ransomware tripwire)
│   ├── ScanAlways.DefenderWatchdog/  # Module: Defender health watchdog
│   ├── ScanAlways.FirewallWatchdog/  # Module: Windows Firewall health watchdog
│   ├── ScanAlways.Persistence/       # Module: persistence and autostart auditor
│   ├── ScanAlways.Updater/           # Module + installer: update checks and verified installs
│   └── ScanAlways.Tray/              # Windows tray notification companion
├── tests/
│   ├── ScanAlways.Common.Tests/
│   ├── ScanAlways.Decoy.Tests/
│   ├── ScanAlways.Canary.Tests/
│   ├── ScanAlways.DefenderWatchdog.Tests/
│   ├── ScanAlways.FirewallWatchdog.Tests/
│   ├── ScanAlways.Persistence.Tests/
│   └── ScanAlways.Updater.Tests/
└── docs/
    ├── adding-a-tool.md              # How to write a new module
    ├── alerts-and-tray.md            # Shared alerts and tray deployment
    └── releasing.md                  # Cutting a release the updater can install
```

## Design principles

- **User mode, no kernel drivers.** Nothing here installs a driver or needs
  tricks that only work as SYSTEM.
- **Harmless by construction.** Modules read and observe; they do not modify,
  quarantine, or delete your files (the canary watcher only writes the decoy
  files it creates itself), and they never touch, hook, or impersonate
  Defender's own components.
- **Honest about limits.** Every module's README says plainly what it does and
  what it cannot do.
- **One process, many small tools.** Each tool is its own project and module,
  shared code lives in `ScanAlways.Common`, and the hub runs them all.

## Roadmap

- [x] Always-scanning decoy
- [x] Hub that runs every tool as a module
- [x] Canary (honeypot) file watcher
- [x] Defender health watchdog
- [x] Firewall health watchdog (profiles and inbound default policy)
- [x] Opt-in monitoring of new inbound firewall allow rules
- [x] Persistence and autostart auditor
- [x] Shared alert delivery and Windows tray companion
- [x] Update checks and verified, user-approved installs from GitHub Releases
- [ ] Code-signed releases
- [ ] Measured overhead benchmarks

## Contributing and security

Contributions are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md) and the
[Code of Conduct](CODE_OF_CONDUCT.md). To add a tool, start with
[docs/adding-a-tool.md](docs/adding-a-tool.md).

Please report vulnerabilities privately as described in
[SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE). ScanAlways is early and experimental; use it as an extra layer
on top of a real, up-to-date anti-malware product.

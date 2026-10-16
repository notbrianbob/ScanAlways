# ScanAlways

A suite of small, complementary anti-virus tools for Windows 11. They are meant
to run **alongside** Microsoft Defender, not replace it.

## Why

Mainstream malware has had years to tune itself against the defenses almost
every Windows machine ships with — above all Defender. It has had far less
exposure to unfamiliar, home-grown tooling. ScanAlways leans on that gap: each
tool here adds an extra, slightly unusual layer of friction or deception that a
given sample may not have been built to anticipate.

This is defense-in-depth, and we are honest about where it sits in the stack.
Isolation, detection, and removal by a real engine are far more reliable than
trying to keep an attacker dormant. Everything here is a *supplement* that might
buy a sliver of extra protection — not a primary defense.

## Tools

| Tool | Project | What it does |
|------|---------|--------------|
| **Always-Scanning Decoy** | [`src/ScanAlways.Decoy`](src/ScanAlways.Decoy/) | Makes the machine look like an anti-malware scan is permanently running, as a deception layer. See its [README](src/ScanAlways.Decoy/README.md). |
| **Canary File Watcher** | [`src/ScanAlways.Canary`](src/ScanAlways.Canary/) | Plants decoy files in Desktop, Documents and Pictures and alerts the moment anything modifies, renames, or deletes one: an early ransomware tripwire. See its [README](src/ScanAlways.Canary/README.md). |
| **Defender Health Watchdog** | [`src/ScanAlways.DefenderWatchdog`](src/ScanAlways.DefenderWatchdog/) | Checks every few minutes that Defender's real-time protection, tamper protection and signatures are actually on and current, and alerts when something turns them off. Read-only. See its [README](src/ScanAlways.DefenderWatchdog/README.md). |

Shared, tool-agnostic code lives in [`src/ScanAlways.Common`](src/ScanAlways.Common/).
More tools will be added as sibling projects under `src/`.

## Layout

```
ScanAlways.sln
├── src/
│   ├── ScanAlways.Common/            # Shared library (engine, config, narration, rolling log)
│   ├── ScanAlways.Decoy/             # Tool 1: always-scanning decoy host
│   ├── ScanAlways.Canary/            # Tool 2: canary file watcher (ransomware tripwire)
│   └── ScanAlways.DefenderWatchdog/  # Tool 3: Defender health watchdog
└── tests/
    ├── ScanAlways.Common.Tests/
    ├── ScanAlways.Canary.Tests/
    └── ScanAlways.DefenderWatchdog.Tests/
```

Each tool is its own project with its own README. Anything reusable across tools
belongs in `ScanAlways.Common`.

## Requirements

- Windows 11 (the tools are written for it and run in user mode; several build
  and test cross-platform too)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Build & test

```bash
dotnet build -c Release
dotnet test  -c Release
```

## Design principles

- **User mode, no kernel drivers.** Nothing here installs a driver or needs
  tricks that only work as SYSTEM.
- **Harmless by construction.** Tools read and observe; they do not modify,
  quarantine, or delete your files (the canary watcher only writes the decoy
  files it creates itself), and they never touch or impersonate Defender's
  own components in a way that could break it.
- **Honest about limits.** Every tool's README says plainly what signal it
  emulates and what it cannot do.
- **Clean and additive.** One project per tool, shared code factored out, so the
  repo stays readable as the suite grows.

## Status

Early and experimental. Treat ScanAlways as an extra layer on top of a real,
up-to-date anti-malware product — never as a substitute for one.

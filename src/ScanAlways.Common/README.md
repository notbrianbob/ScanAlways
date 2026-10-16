# ScanAlways.Common

Shared, tool-agnostic library for the ScanAlways suite. Code that more than one
tool would want lives here so the individual tools stay thin.

## What's here today

The first tool (the always-scanning decoy) drove the initial contents:

- **`DecoyOptions`** — configuration for the decoy, including intensity profiles
  and scan-root resolution with sensible fallbacks.
- **`DecoyEngine`** — the read-only sweep loop: walk files, read a prefix, hash
  it, discard it, narrate, repeat. Exposes live `ScanStatistics` and a
  `StatusChanged` event so any host (service, console, or a future tray UI) can
  show status.
- **`FileWalker`** — resilient, read-only recursive file enumeration that
  swallows the access errors routine on a live system.
- **`ScanNarrator`** — turns benign file reads into believable, scanner-style
  status lines (seedable for deterministic tests).
- **`ScanLogWriter`** — size-capped, rolling cosmetic scan log.
- **`ServiceCollectionExtensions.AddScanAlwaysDecoy`** — DI wiring that binds
  options and registers the engine.

## Conventions

- `net8.0`, nullable enabled, no Windows-only dependencies in this library so it
  builds and unit-tests cross-platform.
- Keep anything specific to a single tool in that tool's project; promote code
  here only once a second tool needs it.

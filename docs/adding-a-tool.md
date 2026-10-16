# Adding a new tool to the suite

ScanAlways is a multi-tool repo. Keep it clean as it grows:

1. **One project per tool** under `src/`, named `ScanAlways.<Tool>` (e.g.
   `ScanAlways.Decoy`). Add it to `ScanAlways.sln`.
2. **Shared code goes in `ScanAlways.Common`.** Promote something into the
   shared library only when a *second* tool actually needs it — don't
   pre-generalize.
3. **Each tool ships its own `README.md`** that states, plainly:
   - what it does,
   - which concrete signal or behavior it adds, and
   - its honest limits (what it does *not* protect against).
4. **Tests** live under `tests/ScanAlways.<Tool>.Tests` (or
   `ScanAlways.Common.Tests` for shared code).
5. **Add a row** to the Tools table in the top-level [README](../README.md).

## Guardrails for every tool

These are non-negotiable for the suite's "harmless, complementary" promise:

- User mode only — no kernel drivers.
- Don't modify, quarantine, or delete user files unless that is explicitly the
  tool's stated job and the README says so loudly.
- Never disable, impersonate, or hook Microsoft Defender's components in a way
  that could break real protection. ScanAlways runs *alongside* Defender.
- Keep overhead modest and configurable (CPU, disk, memory).

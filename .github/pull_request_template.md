## Summary

<!-- What does this change, and why? Link the issue if there is one. -->

## Modules affected

<!-- e.g. Hub, Decoy, Common -->

## How it was tested

<!-- Commands run, manual checks on Windows 11, new tests added. -->

## Checklist

- [ ] `dotnet build -c Release` and `dotnet test -c Release` pass locally
- [ ] New behavior has tests
- [ ] User mode only; nothing disables, hooks, or impersonates Microsoft Defender
- [ ] Overhead stays modest and configurable
- [ ] READMEs updated (module README, top-level Tools table, honest limits)

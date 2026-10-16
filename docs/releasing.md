# Cutting a release

Releases are what the in-hub updater installs, so each one must come from
the release workflow ([`.github/workflows/release.yml`](../.github/workflows/release.yml)),
not from files uploaded by hand.

1. Make sure `main` is green.
2. Tag the commit and push the tag:

   ```bash
   git tag v0.2.0
   git push origin v0.2.0
   ```

   Use `v<major>.<minor>.<patch>`, optionally with a prerelease suffix such as
   `v0.3.0-beta.1`. Prereleases are marked as such on GitHub and are not
   offered to stable installs.
3. The workflow runs the tests, publishes the hub and tray for `win-x64`
   with that version stamped in, and creates the GitHub release with:
   - `WatchLattice-<version>-win-x64.zip`, containing `Hub/`, `Tray/`, `LICENSE` and `README.md`
   - `SHA256SUMS.txt`, which the updater checks before installing anything
   - a build provenance attestation for the zip
4. Edit the generated release notes if needed. Do not replace or re-upload
   assets: the checksum file would no longer match, and installs of that
   release would be refused (which is the point).

Installed hubs see the release at their next check (within 12 hours, or at
the next hub start), and the tray shows that an update is available.

## First install from a release

From an elevated PowerShell, after downloading and checking the zip:

```powershell
$expected = (Get-Content .\SHA256SUMS.txt).Split(' ')[0]
(Get-FileHash .\WatchLattice-0.2.0-win-x64.zip -Algorithm SHA256).Hash.ToLower() -eq $expected
Expand-Archive .\WatchLattice-0.2.0-win-x64.zip 'C:\Program Files\WatchLattice'
sc.exe create "ScanAlways Protection" binPath= "C:\Program Files\WatchLattice\Hub\ScanAlways.Protection.Service.exe" start= auto
sc.exe start "ScanAlways Protection"
& 'C:\Program Files\WatchLattice\Tray\ScanAlways.Tray.exe'
```

Keeping `Hub` and `Tray` side by side is what lets **Install update** in the
tray update both. The hub needs the .NET 8 Runtime and the tray the .NET 8
Desktop Runtime. See [alerts and tray setup](alerts-and-tray.md) for running
the service as LocalSystem with a user tray.

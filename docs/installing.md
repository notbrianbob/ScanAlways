# Installing WatchLattice

Each [release](https://github.com/notbrianbob/WatchLattice/releases) zip
contains the hub, the tray companion, and a one-step installer.

## Before you start

WatchLattice needs the **.NET 8 Desktop Runtime (x64)**, which also includes
the .NET 8 Runtime the hub uses. The installer checks for it and stops with
instructions if it is missing. To install it:

```powershell
winget install Microsoft.DotNet.DesktopRuntime.8
```

or download it from <https://dotnet.microsoft.com/download/dotnet/8.0>.

Optionally, check the download before extracting it:

```powershell
$expected = (Get-Content .\SHA256SUMS.txt).Split(' ')[0]
(Get-FileHash .\WatchLattice-0.2.0-win-x64.zip -Algorithm SHA256).Hash.ToLower() -eq $expected
gh attestation verify .\WatchLattice-0.2.0-win-x64.zip --repo notbrianbob/WatchLattice
```

## Install

1. Extract the zip anywhere (your Downloads folder is fine).
2. Double-click **Install.cmd** and accept the administrator prompt.
   From a terminal, `.\install.ps1` does the same.
3. Answer the first-run questions, or press Enter for the defaults:
   - **Canary folders.** Defaults to your Desktop, Documents and Pictures
     (wherever they really are, including OneDrive). Type other folders
     separated by `;` to choose your own.
   - **Run the decoy?** Yes by default.
   - **Check GitHub for new releases?** Yes by default. Updates are only
     installed when you choose **Install update** in the tray.
   - **Start the tray when you sign in?** Yes by default.

The installer then:

- copies `Hub\` and `Tray\` to `C:\Program Files\WatchLattice` and makes the
  folder writable only by administrators,
- writes your choices to `Hub\appsettings.json`, spelling out your own folders,
  because the service runs as LocalSystem and its default folders would
  otherwise be the system profile's,
- creates the alert feed folder `C:\ProgramData\WatchLattice\Alerts`, which
  only the hub can write and only you can read,
- installs the `ScanAlways Protection` service (shown as *WatchLattice
  Protection*), set to start automatically and to restart if it crashes,
  and starts it,
- starts the tray at your sign-in (your `Run` key), adds **WatchLattice** to
  the Start menu, and lists WatchLattice under **Settings > Apps**,
- starts the tray now.

Settings for an unattended install:

| Parameter | Meaning |
|---|---|
| `-Quiet` | Accept the defaults (or the values below) without asking. |
| `-CanaryFolders 'C:\a;D:\b'` | Canary folders. |
| `-NoDecoy` | Do not run the decoy. |
| `-NoUpdateCheck` | Do not check for new releases. |
| `-NoTrayAutostart` / `-NoTrayStart` | Do not start the tray at sign-in / at the end of the install. |
| `-TrayUser 'PC\name'` | Whose tray and folders to set up. Defaults to the signed-in user. |
| `-InstallDir` | Install somewhere else. Must be in a folder only administrators can change; the installer refuses anything else. |

If Group Policy enforces an `AllSigned` execution policy, `-ExecutionPolicy
Bypass` in Install.cmd has no effect and the script will not run.

## Updating

Use **Install update** in the tray (see the [updater](../src/WatchLattice.Updater/README.md)).
Running a newer release's installer over an existing install also works: it
replaces the program files, keeps your `appsettings.json` (writing the
release's copy next to it as `appsettings.json.default`) and keeps your
canaries and baselines. Add `-Reconfigure` to answer the questions again.

The installer uses the names that installed hubs and the updater depend on
(see [names that must not change](releasing.md#names-that-must-not-change)),
so an installed copy updates from the tray like any other.

## Uninstall

Open **Settings > Apps > WatchLattice > Uninstall**, or run
`C:\Program Files\WatchLattice\uninstall.ps1`. It:

- stops the tray and the service,
- deletes the canary files the hub planted that are still unchanged. A
  canary that was changed is kept as evidence and its path is printed; check
  it, then delete it yourself,
- deletes the service, the tray's sign-in entry, the Start menu shortcut and
  the Settings > Apps entry,
- removes the `WatchLattice` Application event log source, if the hub
  registered one (past entries stay in the log),
- deletes the alert feed, the hub's baselines and logs (under the system
  profile's `AppData\Local\ScanAlways` and the system `Temp\ScanAlways`),
  and the install folder.

`-KeepData` keeps the alert feed, baselines and canary manifest, so a later
install carries on from them. `-Quiet` skips the confirmation. The uninstaller
never follows a link or junction out of the folders it deletes.

## Known limits

- The service runs as LocalSystem. The Persistence auditor's current-user
  registry inventory is therefore LocalSystem's, not yours; your Startup
  folder is still watched because the installer lists it explicitly. Run the
  service under your own account (see the [README](../README.md#install-as-a-windows-service-from-source))
  if you need your own `Run` keys watched.
- One tray user per install. Other accounts on the PC can open the tray but
  cannot read the feed; rerun the installer with `-Reconfigure -TrayUser` to
  switch users.
- There is no MSI or winget package yet.

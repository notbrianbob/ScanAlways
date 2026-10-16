# ScanAlways.Tray

A Windows Forms notification-area companion for the ScanAlways hub. It reads
the local shared alert feed and provides balloons, recent alert details, module
run states, and a connected/offline indicator.

Run in your interactive Windows session, separately from the hub service:

```powershell
dotnet run -c Release --project src/ScanAlways.Tray
```

For a custom or service-account feed:

```powershell
dotnet run -c Release --project src/ScanAlways.Tray -- --feed 'C:\ProgramData\ScanAlways\Alerts\alerts.json'
```

See [shared alerts and tray setup](../../docs/alerts-and-tray.md) for publishing,
permissions, optional login startup, delivery limits, and Windows smoke checks.
This project targets `net8.0-windows`; `EnableWindowsTargeting` permits
cross-compilation, but running the companion requires Windows and the .NET 8
Desktop Runtime.

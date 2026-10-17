# The dashboard

The tray companion's window is the WatchLattice dashboard. Open it by
double-clicking the tray icon or choosing **Open dashboard** from its menu.
It has four tabs:

| Tab | What it shows |
|---|---|
| **Overview** | Hub connection, and every module with its running state, whether it is turned on in the settings, and what it does. **Turn on…/Turn off…** and **Change settings…** open the settings editor. |
| **Alerts** | Recent alerts from every module, newest first, with full details for the selected one. |
| **Activity** | The hub's own recent log: modules starting, stopping, failing and recovering, checks that could not run, feed and Event Log problems. Filter by level or text. |
| **Settings** | Every setting the hub reads, its current value, and whether it comes from appsettings.json or is the built-in default. |

The dashboard runs as you, with your normal rights, and cannot change
anything by itself.

## Changing settings needs an administrator

The hub's settings live in `Hub\appsettings.json` in the install folder
(`C:\Program Files\WatchLattice` by default), which only administrators can
change. That is on purpose: the hub runs as LocalSystem, so whoever can edit
its settings decides what it does.

So the first time you choose **Change settings…**, **Turn on…/Turn off…**, or
double-click a setting, Windows asks for administrator permission. If you
approve, the **settings editor** opens as its own elevated window, and you can
make as many changes as you like there without being asked again. If you
decline, nothing changes and the dashboard stays as it was. On a standard
account, Windows asks for an administrator's password instead, so only an
administrator can change settings.

In the editor:

- Each module and hub area has its own page. Pages with unsaved changes are
  marked with a dot.
- Values are checked as you type: numbers must be whole numbers (or numbers)
  that are 0 or more, choices come from a list, and lists take one entry per
  line. A value that is not set shows the hub's built-in default.
- **Save and restart hub** lists your changes and asks you to confirm. The hub
  then:
  1. checks the new settings again, the same way, and refuses anything that
     does not fit,
  2. saves a copy of the current file in `Hub\settings-backups\` (the newest
     10 are kept),
  3. replaces `appsettings.json` in one step, so it is never half-written,
  4. restarts the `ScanAlways Protection` service, and
  5. if the hub does not keep running with the new settings, puts the backup
     back and starts it again.
- **Undo all changes** goes back to what is saved. Closing with unsaved
  changes asks first.

The editor keeps the rest of the file as it is, including settings it does not
know about. It warns about keys in a WatchLattice section that this version
does not use, such as a misspelt name.

### Settings the dashboard does not change

Two settings are shown but locked, and saving refuses any change to them:

| Setting | Why |
|---|---|
| `Updater:Repository` | Where updates come from. A settings change must never be able to point the updater at another repository. |
| `Alerts:FeedPath` | The installer gave only your account read access to the feed folder. Moving the feed would disconnect the tray. |

An administrator can still change them by editing `appsettings.json` and
restarting the service.

### From the command line

The editor uses two hub commands, which also work by hand:

```powershell
cd 'C:\Program Files\WatchLattice\Hub'
.\ScanAlways.Protection.Service.exe --describe-settings   # the schema and current file, as JSON; no rights needed
.\ScanAlways.Protection.Service.exe --apply-settings      # from an administrator prompt
```

`--apply-settings` reads the new settings from `appsettings.json.pending` in
the hub folder, never from a path you pass it, because only administrators
can write that folder. Its exit codes are `0` saved, `20` nothing changed,
`21` refused (the reasons are printed), `22` the hub did not keep running so
the previous settings were restored, and `1` any other failure.

## How the Activity tab works

The hub copies its own log lines (Information and above, following the usual
`Logging:LogLevel` settings) into `alerts.json.activity.json`, next to the
alert feed, keeping the newest 500 across restarts. Like the feed, it is
display-only: only the hub can write it and only the tray user can read it,
and the dashboard never runs anything it contains. Lines can contain file
paths. It is a convenience, not an audit log; the Windows Event Log keeps
every alert (see [Event Log alerts](event-log.md)).

## Without the installer

The dashboard finds the hub through the release layout: `..\Hub\` next to the
tray's own folder, as the installer and the release zip set it up. A tray
started from somewhere else still shows status, alerts and activity, and its
Settings tab explains that it cannot reach the hub's settings. Edit
`appsettings.json` next to the hub by hand in that case.

## What needs checking on Windows

CI installs a release-shaped package on a Windows runner and runs
`--describe-settings` and `--apply-settings` against the real service,
including a refused change to a locked setting. The windows themselves
(the administrator prompt, the editor, and the dashboard tabs) can only be
checked by hand:

1. Open the dashboard. Overview lists every module; Activity shows the hub's
   startup lines; Settings lists every section.
2. Choose **Turn off…** on the decoy. Approve the prompt. The editor opens on
   the Modules page with the decoy switched off and one unsaved change. Save.
   The hub restarts, Overview shows the decoy as Disabled and Off, and
   `Hub\settings-backups` has one file.
3. Choose **Change settings…** again and decline the prompt: nothing opens and
   nothing changes.
4. In the editor, type `abc` into a number: the field explains the problem and
   Save stays disabled.
5. On a standard (non-administrator) account, the prompt asks for an
   administrator's password.

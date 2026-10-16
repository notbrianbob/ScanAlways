using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ScanAlways.Common.Alerts;
using ScanAlways.Updater;

namespace ScanAlways.Tray;

/// <summary>
/// A per-user reader: the tray cannot change the hub's configuration. The one
/// thing it can start is the hub's own updater, from the hub folder installed
/// next to it (never a path from the feed), elevated through Windows' consent
/// prompt and only when the user chooses Install update.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private const int ErrorCancelled = 1223;
    private readonly string _path;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _status = new("Waiting for hub…") { Enabled = false };
    private readonly ToolStripMenuItem _update = new() { Visible = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly AlertNotificationCursor _cursor = new();
    private AlertSnapshot? _snapshot;
    private AlertWindow? _window;
    private string _statusText = "Waiting for the hub's alert feed.";
    private string? _announcedUpdate;
    private bool _reading;
    private bool _installing;
    private bool _exiting;

    public TrayContext(string path)
    {
        _path = path;
        _update.Click += (_, _) => OpenWindow();
        _menu.Items.Add(_status);
        _menu.Items.Add(_update);
        _menu.Items.Add("Open alerts and status", null, (_, _) => OpenWindow());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit tray companion", null, (_, _) => ExitThread());
        _icon = new NotifyIcon { Icon = SystemIcons.Shield, Text = "ScanAlways: waiting for hub", ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => OpenWindow();
        _icon.BalloonTipClicked += (_, _) => OpenWindow();
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
    }

    private async Task PollAsync()
    {
        if (_reading || _exiting) return;
        _reading = true;
        try
        {
            var snapshot = await Task.Run(() => AlertSnapshot.Read(_path));
            if (_exiting) return;
            _snapshot = snapshot;
            var fresh = snapshot.IsFresh(DateTimeOffset.UtcNow);
            var running = snapshot.Modules.Count(m => m.State == ModuleRunState.Running);
            var attention = snapshot.Modules.Any(m => m.State is ModuleRunState.Failed or ModuleRunState.Restarting or ModuleRunState.Finished);
            _statusText = fresh ? $"Hub connected • {running} module(s) running" + (attention ? " • module needs attention" : "")
                : $"Hub offline or feed stale • last update {snapshot.HeartbeatAt.ToLocalTime():g}";
            _status.Text = fresh ? "Hub connected" : "Hub offline or feed stale";
            _icon.Text = (fresh ? "ScanAlways: hub connected" : "ScanAlways: hub offline")
                + (snapshot.Update is null ? "" : " • update available");
            _icon.Icon = fresh && !attention ? SystemIcons.Shield : SystemIcons.Warning;

            var alerts = _cursor.Observe(snapshot);
            if (alerts.Count > 0)
            {
                var mostSevere = alerts.OrderByDescending(a => a.Severity).ThenByDescending(a => a.At).First();
                var title = alerts.Count == 1 ? mostSevere.Title : $"{alerts.Count} new ScanAlways alerts";
                var message = alerts.Count == 1 ? mostSevere.Message : mostSevere.Title + ". Open the tray to review all new alerts.";
                _icon.ShowBalloonTip(8000, Clip(title, 63), Clip(message, 240),
                    mostSevere.Severity == AlertSeverity.Critical ? ToolTipIcon.Error : ToolTipIcon.Warning);
            }
            // Protection alerts take the balloon first; the update notice waits for the next poll.
            ShowUpdate(snapshot.Update, balloonFree: alerts.Count == 0);
            UpdateWindow();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            if (_exiting) return;
            _statusText = "Alert feed unavailable: " + ex.Message;
            _status.Text = "Alert feed unavailable";
            _icon.Text = "ScanAlways: alert feed unavailable";
            _icon.Icon = SystemIcons.Warning;
            UpdateWindow();
        }
        finally { _reading = false; }
    }

    private void ShowUpdate(UpdateNotice? notice, bool balloonFree)
    {
        _update.Visible = notice is not null;
        if (notice is null) return;
        _update.Text = $"Update available: {notice.LatestVersion}…";
        if (!balloonFree || _announcedUpdate == notice.LatestVersion) return;
        _announcedUpdate = notice.LatestVersion;
        _icon.ShowBalloonTip(8000, Clip($"WatchLattice {notice.LatestVersion} is available", 63),
            Clip(notice.Installable
                ? $"You have {notice.CurrentVersion}. Open the tray to install it; nothing changes until you choose to."
                : $"You have {notice.CurrentVersion}. Open the tray for details.", 240),
            ToolTipIcon.Info);
    }

    private async Task InstallUpdateAsync()
    {
        if (_installing || _snapshot?.Update is not { Installable: true } notice) return;
        var hub = ReleaseLayout.FindHubExecutable(AppContext.BaseDirectory);
        if (hub is null)
        {
            MessageBox.Show(_window, "This tray is not installed next to the hub (…\\Hub and …\\Tray), so it cannot start the updater.\n\n"
                + "From an administrator prompt in the hub's folder, run:\nScanAlways.Protection.Service.exe --apply-update",
                "Install update", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(_window, $"Install WatchLattice {notice.LatestVersion}? You have {notice.CurrentVersion}.\n\n"
            + "Windows will ask for administrator permission. The updater downloads the release from GitHub, checks its SHA-256 "
            + "checksum, and stops and restarts the hub service. Your appsettings.json is kept. If anything fails to verify, "
            + "nothing is changed.\n\nThe tray restarts when the update finishes.",
            "Install update", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        _installing = true;
        _window?.SetInstalling(true);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(hub)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--apply-update --pause-on-failure",
                WorkingDirectory = Path.GetDirectoryName(hub)!,
            }) ?? throw new InvalidOperationException("The updater did not start.");
            await process.WaitForExitAsync();
            switch ((UpdateOutcome)process.ExitCode)
            {
                case UpdateOutcome.Installed:
                    Program.RestartRequested = true;
                    ExitThread();
                    return;
                case UpdateOutcome.UpToDate:
                    MessageBox.Show(_window, "WatchLattice is already up to date.", "Install update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    break;
                case UpdateOutcome.VerificationFailed:
                    MessageBox.Show(_window, "The download did not pass verification, so nothing was changed.", "Update not installed",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
                default:
                    MessageBox.Show(_window, $"The update did not complete (code {process.ExitCode}). The updater window explained why.",
                        "Update not installed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // The user declined the administrator prompt.
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(_window, "The updater could not start: " + ex.Message, "Update not installed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _installing = false;
            if (!_exiting) _window?.SetInstalling(false);
        }
    }

    private void OpenWindow()
    {
        if (_window is null || _window.IsDisposed)
        {
            _window = new AlertWindow(_path);
            _window.InstallRequested += async () => await InstallUpdateAsync();
            _window.SetInstalling(_installing);
        }
        UpdateWindow();
        _window.Show();
        if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
        _window.Activate();
    }

    private void UpdateWindow()
    {
        if (_window is { IsDisposed: false }) _window.UpdateSnapshot(_snapshot, _statusText);
    }

    protected override void ExitThreadCore()
    {
        _exiting = true;
        _timer.Stop();
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _window?.Dispose();
        base.ExitThreadCore();
    }

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";
}

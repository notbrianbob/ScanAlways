using System.Text.Json;
using ScanAlways.Common.Alerts;

namespace ScanAlways.Tray;

/// <summary>A per-user reader only: the tray cannot launch commands or change the hub's configuration.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly string _path;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _status = new("Waiting for hub…") { Enabled = false };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly AlertNotificationCursor _cursor = new();
    private AlertSnapshot? _snapshot;
    private AlertWindow? _window;
    private string _statusText = "Waiting for the hub's alert feed.";
    private bool _reading;
    private bool _exiting;

    public TrayContext(string path)
    {
        _path = path;
        _menu.Items.Add(_status);
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
            _icon.Text = fresh ? "ScanAlways: hub connected" : "ScanAlways: hub offline";
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

    private void OpenWindow()
    {
        if (_window is null || _window.IsDisposed) _window = new AlertWindow(_path);
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

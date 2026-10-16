using ScanAlways.Common.Alerts;

namespace ScanAlways.Tray;

internal sealed class AlertWindow : Form
{
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(10), AutoEllipsis = true };
    private readonly Label _modules = new() { Dock = DockStyle.Top, Height = 60, Padding = new Padding(10, 0, 10, 0) };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
        HideSelection = false, AccessibleName = "Recent alerts",
    };
    private readonly TextBox _details = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        AccessibleName = "Selected alert details", Text = "Select an alert to see its details.",
    };
    private Guid[] _displayed = Array.Empty<Guid>();

    public AlertWindow(string path)
    {
        Text = "ScanAlways — Alerts and status";
        Icon = SystemIcons.Shield;
        Size = new Size(960, 640);
        MinimumSize = new Size(640, 440);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        _list.Columns.Add("Time", 150);
        _list.Columns.Add("Severity", 80);
        _list.Columns.Add("Module", 130);
        _list.Columns.Add("Alert", 500);
        _list.SelectedIndexChanged += (_, _) => ShowDetails();

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(900, 420), SplitterDistance = 260 };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_details);
        Controls.Add(split);
        Controls.Add(_modules);
        Controls.Add(_status);
        Controls.Add(new TextBox
        {
            Dock = DockStyle.Bottom, ReadOnly = true, Text = "Feed: " + path,
            AccessibleName = "Alert feed path",
        });
    }

    public void UpdateSnapshot(AlertSnapshot? snapshot, string status)
    {
        _status.Text = status;
        _modules.Text = snapshot is null ? "Start the ScanAlways hub to receive alerts."
            : string.Join("   •   ", snapshot.Modules.Select(m => $"{m.Id}: {m.State}"))
                + "\nRunning describes the module's process state, not a guarantee that protection is healthy.";
        if (snapshot is null) return;
        var alerts = snapshot.Alerts.Reverse().ToArray();
        var ids = alerts.Select(a => a.Id).ToArray();
        if (_displayed.SequenceEqual(ids)) return;
        var selected = _list.SelectedItems.Count == 0 ? (Guid?)null : ((ProtectionAlert)_list.SelectedItems[0].Tag!).Id;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var alert in alerts)
        {
            var item = new ListViewItem(new[] { alert.At.ToLocalTime().ToString("g"), alert.Severity.ToString(), alert.Module, alert.Title }) { Tag = alert };
            _list.Items.Add(item);
            if (alert.Id == selected) item.Selected = true;
        }
        _list.EndUpdate();
        _displayed = ids;
        if (alerts.Length == 0) _details.Text = "No alerts have been recorded yet.";
        else if (_list.SelectedItems.Count == 0) _details.Text = "Select an alert to see its details.";
    }

    private void ShowDetails()
    {
        if (_list.SelectedItems.Count == 0) return;
        var alert = (ProtectionAlert)_list.SelectedItems[0].Tag!;
        _details.Text = $"{alert.Title}\r\n{alert.At.ToLocalTime():F} • {alert.Module} • {alert.Severity}\r\nCode: {alert.Code}\r\n"
            + (alert.Resource is null ? "" : $"Source: {alert.Resource}\r\n") + "\r\n" + alert.Message.Replace("\n", "\r\n");
    }
}

using WatchLattice.Common.Alerts;
using WatchLattice.Persistence.Verification;

namespace WatchLattice.Tray;

/// <summary>
/// Verification details for one persistence alert: each run's per-check
/// results and the full report, with the evidence logged at alert time kept
/// apart from what was found later. It reads and copies findings only;
/// Verify again asks the dashboard to run a new read-only check.
/// </summary>
internal sealed class VerificationWindow : Form
{
    private readonly Label _header = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 10, 10, 4), MaximumSize = new Size(1000, 0) };
    private readonly Label _caveat = new()
    {
        Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 0, 10, 6), MaximumSize = new Size(1000, 0),
        Text = "These results are evidence to review, not a verdict. A valid signature or a familiar publisher says who signed a file, "
            + "not that the entry is harmless. Verification only reads: it never runs, disables or removes anything.",
    };
    private readonly Label _runLabel = new() { Text = "Run:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) };
    private readonly ComboBox _runs = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420, AccessibleName = "Verification run" };
    private readonly Button _verify = new() { Text = "Verify again", AutoSize = true };
    private readonly Button _copy = new() { Text = "Copy report", AutoSize = true };
    private readonly Label _state = new() { AutoSize = true, Margin = new Padding(12, 7, 0, 0) };
    private readonly ListView _checks = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        AccessibleName = "Checks", ShowItemToolTips = true,
    };
    private readonly TextBox _report = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        AccessibleName = "Verification report", Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    private readonly Func<Task> _verifyAgain;
    private VerificationRecord _record;
    private bool _verifying;

    public VerificationWindow(ProtectionAlert alert, VerificationRecord record, Func<Task> verifyAgain)
    {
        _record = record;
        _verifyAgain = verifyAgain;
        Text = "Verification details - " + record.Original.EntryId;
        Icon = SystemIcons.Shield;
        Size = new Size(1000, 720);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;

        _checks.Columns.Add("Status", 90);
        _checks.Columns.Add("Check", 260);
        _checks.Columns.Add("Detail", 600);
        _checks.SelectedIndexChanged += (_, _) => ShowCheck();
        _runs.SelectedIndexChanged += (_, _) => ShowRun();
        _verify.Click += async (_, _) => await _verifyAgain();
        _copy.Click += (_, _) => CopyReport();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = true };
        bar.Controls.AddRange(new Control[] { _runLabel, _runs, _verify, _copy, _state });
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(960, 520), SplitterDistance = 230 };
        split.Panel1.Controls.Add(_checks);
        split.Panel2.Controls.Add(_report);

        Controls.Add(split);
        Controls.Add(bar);
        Controls.Add(_caveat);
        Controls.Add(_header);
        _header.Font = new Font(Font, FontStyle.Bold);
        _header.Text = $"{record.Original.EntryId}\r\n{alert.Title} • {record.AlertCode} • {record.AlertAt.ToLocalTime():F}";
        ShowRecord(record);
    }

    public Guid AlertId => _record.AlertId;

    /// <summary>Shows the latest findings, keeping the selected run unless a new one arrived.</summary>
    public void ShowRecord(VerificationRecord record)
    {
        var newRun = record.Runs.Count != _record.Runs.Count || record.Latest?.Id != _record.Latest?.Id;
        var selected = _runs.SelectedItem is RunItem item && !newRun ? item.Run.Id : (Guid?)null;
        _record = record;
        _runs.BeginUpdate();
        _runs.Items.Clear();
        foreach (var run in record.Runs.Reverse()) _runs.Items.Add(new RunItem(run));
        if (_runs.Items.Count == 0) _runs.Items.Add("Not verified yet");
        _runs.EndUpdate();
        var index = selected is null ? -1 : _runs.Items.Cast<object>().ToList().FindIndex(i => i is RunItem r && r.Run.Id == selected);
        _runs.SelectedIndex = Math.Max(index, 0);
        ShowRun();
    }

    public void SetVerifying(bool verifying, string? problem = null)
    {
        _verifying = verifying;
        _verify.Enabled = !verifying;
        _verify.Text = verifying ? "Verifying…" : "Verify again";
        _state.Text = verifying ? "Checking the current state. This only reads; nothing is run or changed." : problem ?? "";
    }

    private VerificationRun? SelectedRun => (_runs.SelectedItem as RunItem)?.Run;

    private void ShowRun()
    {
        var run = SelectedRun;
        _checks.BeginUpdate();
        _checks.Items.Clear();
        if (run is null)
        {
            _checks.Items.Add(new ListViewItem(new[] { "", "Not verified yet", "Choose Verify again to check the entry's current state." }));
        }
        else
        {
            _checks.Items.Add(new ListViewItem(new[]
            {
                VerificationFindings.OutcomeText(run.Outcome), "Overall assessment", run.Summary,
            }) { ForeColor = OutcomeColor(run.Outcome), Font = new Font(_checks.Font, FontStyle.Bold), ToolTipText = run.Summary });
            foreach (var check in run.Checks)
            {
                _checks.Items.Add(new ListViewItem(new[] { check.Status.ToString(), check.Title, check.Detail })
                {
                    Tag = check, ForeColor = StatusColor(check.Status), ToolTipText = check.Detail,
                });
            }
        }
        _checks.EndUpdate();
        _report.Text = VerificationFindings.Report(_record, run).Replace("\r\n", "\n").Replace("\n", "\r\n");
        if (!_verifying) _state.Text = run is null ? "" : $"{run.Checks.Count} check(s), {run.Trigger.ToString().ToLowerInvariant()} run as {run.CheckedAs}.";
    }

    private void ShowCheck()
    {
        if (_checks.SelectedItems.Count == 0 || _checks.SelectedItems[0].Tag is not VerificationCheck check) return;
        _state.Text = $"[{check.Status}] {check.Title}: {check.Detail}";
    }

    private void CopyReport()
    {
        try { Clipboard.SetText(_report.Text); }
        catch (System.Runtime.InteropServices.ExternalException ex) { _state.Text = "Could not copy: " + ex.Message; }
    }

    internal static Color StatusColor(CheckStatus status) => status switch
    {
        CheckStatus.Fail => Color.Firebrick,
        CheckStatus.Warning => Color.DarkGoldenrod,
        CheckStatus.Unknown => Color.DarkSlateGray,
        _ => SystemColors.WindowText,
    };

    internal static Color OutcomeColor(VerificationOutcome outcome) => outcome switch
    {
        VerificationOutcome.Suspicious => Color.Firebrick,
        VerificationOutcome.NeedsReview => Color.DarkGoldenrod,
        _ => SystemColors.WindowText,
    };

    private sealed record RunItem(VerificationRun Run)
    {
        public override string ToString() =>
            $"{Run.CompletedAt.ToLocalTime():g} • {(Run.Trigger == VerificationTrigger.Manual ? "Manual" : "Automatic")} • {VerificationFindings.OutcomeText(Run.Outcome)}";
    }
}

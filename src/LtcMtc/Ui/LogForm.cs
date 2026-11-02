using System.Diagnostics;

namespace LtcMtc;

/// <summary>The event log: what happened when, newest at the bottom. Every entry also goes to a daily CSV file.</summary>
sealed class LogForm : Form
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LtcMtc", "logs");

    readonly ListView list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Foreground, Font = Theme.Body,
    };
    readonly List<EventLog.Entry> entries;

    public LogForm(List<EventLog.Entry> entries)
    {
        this.entries = entries;
        Text = "LTC ⇄ MTC · Log";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(820, 480);
        MinimumSize = new Size(480, 260);
        BackColor = Theme.Background;
        ForeColor = Theme.Foreground;
        Font = Theme.Body;
        Padding = new Padding(16);

        list.Columns.Add("Time", 110);
        list.Columns.Add("Timecode", 110);
        list.Columns.Add("Event", 170);
        list.Columns.Add("Detail", 380);
        var card = new Card("Events") { Dock = DockStyle.Fill, Margin = Padding.Empty };
        card.Controls.Add(list);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([
            Button("Export CSV…", Export),
            Button("Open log folder", () => { Directory.CreateDirectory(Folder); Process.Start("explorer.exe", Folder); }),
            Button("Clear", () => { entries.Clear(); list.Items.Clear(); }),
        ]);

        Controls.Add(card);
        Controls.Add(buttons);
        list.BeginUpdate();
        foreach (var e in entries) list.Items.Add(Item(e));
        list.EndUpdate();
        ScrollToEnd();
    }

    public void Append(IReadOnlyList<EventLog.Entry> added)
    {
        if (added.Count == 0) return;
        list.BeginUpdate();
        foreach (var e in added) list.Items.Add(Item(e));
        while (list.Items.Count > entries.Count) list.Items.RemoveAt(0);   // the owner trims old entries
        list.EndUpdate();
        ScrollToEnd();
    }

    void ScrollToEnd()
    {
        if (list.Items.Count > 0) list.EnsureVisible(list.Items.Count - 1);
    }

    static ListViewItem Item(EventLog.Entry e) => new([e.Time.ToString("HH:mm:ss.fff"), e.Timecode, e.Event, e.Detail]);

    void Export()
    {
        using var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"ltc-mtc-log-{DateTime.Now:yyyy-MM-dd-HHmm}.csv" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllLines(dlg.FileName, entries.Select(EventLog.Csv).Prepend(EventLog.CsvHeader));
    }

    static Button Button(string text, Action onClick)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Theme.Accent, ForeColor = Theme.Foreground,
            Cursor = Cursors.Hand, Margin = new Padding(0, 0, 10, 0), Padding = new Padding(10, 0, 10, 0),
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Theme.Border;
        b.Click += (_, _) => onClick();
        return b;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }
}

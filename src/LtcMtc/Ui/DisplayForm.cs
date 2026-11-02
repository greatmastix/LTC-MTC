namespace LtcMtc;

/// <summary>
/// A separate timecode display: the number as large as the window allows, with the state and a few stats underneath.
/// Double-click or F11 for full screen (Esc leaves it); right-click for always-on-top.
/// </summary>
sealed class DisplayForm : Form
{
    readonly Label tc = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "--:--:--:--" };
    readonly Label status = new() { Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleCenter, Height = 34 };
    readonly Label detail = new() { Dock = DockStyle.Bottom, TextAlign = ContentAlignment.TopCenter, Height = 30, ForeColor = Theme.MutedForeground };
    readonly ToolStripMenuItem onTop = new("Always on top") { CheckOnClick = true };
    readonly ToolStripMenuItem full = new("Full screen  (F11)");
    FormWindowState before;

    public DisplayForm()
    {
        Text = "LTC ⇄ MTC · Display";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(960, 360);
        MinimumSize = new Size(320, 160);
        BackColor = Theme.Background;
        ForeColor = Theme.Foreground;
        Font = Theme.Body;
        DoubleBuffered = true;
        KeyPreview = true;
        Padding = new Padding(16);

        status.Font = new Font("Segoe UI Semibold", 12f);
        detail.Font = new Font("Segoe UI", 10.5f);
        Controls.Add(tc);
        Controls.Add(status);
        Controls.Add(detail);

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([full, onTop, new ToolStripSeparator(), new ToolStripMenuItem("Close", null, (_, _) => Close())]);
        full.Click += (_, _) => FullScreen(FormBorderStyle != FormBorderStyle.None);
        onTop.CheckedChanged += (_, _) => TopMost = onTop.Checked;
        ContextMenuStrip = menu;
        foreach (Control c in Controls)
        {
            c.ContextMenuStrip = menu;
            c.DoubleClick += (_, _) => FullScreen(FormBorderStyle != FormBorderStyle.None);
        }
        DoubleClick += (_, _) => FullScreen(FormBorderStyle != FormBorderStyle.None);
        Resize += (_, _) => FitFont();
        FitFont();
    }

    /// <summary>Shows the engine's view; <paramref name="problem"/> overrides the state (e.g. "Reconnecting").</summary>
    public void Show(EngineView? v, string? problem)
    {
        string text = v?.Timecode ?? "--:--:--:--";
        if (tc.Text != text) tc.Text = text;
        var tone = problem != null ? Tone.Warning : v?.Tone ?? Tone.Neutral;
        tc.ForeColor = tone switch { Tone.Good => Theme.Foreground, Tone.Warning => Theme.Warning, _ => Theme.MutedForeground };
        string s = "●  " + (problem ?? v?.Status ?? "Stopped");
        if (status.Text != s) status.Text = s;
        status.ForeColor = tone switch { Tone.Good => Theme.Good, Tone.Warning => Theme.Warning, _ => Theme.MutedForeground };
        string d = v == null ? "" : string.Join("   ·   ", v.Summary);
        if (detail.Text != d) detail.Text = d;
    }

    /// <summary>The largest font at which "00:00:00:00" fits the space left for it.</summary>
    void FitFont()
    {
        var area = tc.ClientSize;
        if (area.Width < 10 || area.Height < 10) return;
        using var probe = new Font(Theme.Big.FontFamily, 100f, FontStyle.Bold);
        var size = TextRenderer.MeasureText("00:00:00:00", probe, Size.Empty, TextFormatFlags.NoPadding);
        float pt = Math.Max(8f, 100f * Math.Min(area.Width * 0.94f / size.Width, area.Height * 0.9f / size.Height));
        var old = tc.Font;
        tc.Font = new Font(Theme.Big.FontFamily, pt, FontStyle.Bold);
        if (old != Font) old.Dispose();
    }

    void FullScreen(bool on)
    {
        if (on)
        {
            before = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;   // re-maximise so the border-less window covers the taskbar
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = before;
        }
        full.Checked = on;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11) FullScreen(FormBorderStyle != FormBorderStyle.None);
        else if (e.KeyCode == Keys.Escape && FormBorderStyle == FormBorderStyle.None) FullScreen(false);
        base.OnKeyDown(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }
}

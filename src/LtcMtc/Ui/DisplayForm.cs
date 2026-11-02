namespace LtcMtc;

/// <summary>
/// A separate timecode display: the number as large as the window allows, with the state and a few stats underneath.
/// Double-click or F11 for full screen (Esc leaves it); right-click for always-on-top.
/// The number has its own ticker: a thread that checks every 2 ms and repaints only when the frame changes, so frames
/// land evenly instead of on a 33 ms UI timer's beat.
/// </summary>
sealed class DisplayForm : Form
{
    readonly TimecodeView tc = new() { Dock = DockStyle.Fill };
    readonly Func<(string Tc, Tone Tone)> timecode;
    Thread? ticker;
    volatile bool closing;
    readonly Label status = new() { Dock = DockStyle.Bottom, TextAlign = ContentAlignment.MiddleCenter, Height = 34 };
    readonly Label detail = new() { Dock = DockStyle.Bottom, TextAlign = ContentAlignment.TopCenter, Height = 30, ForeColor = Theme.MutedForeground };
    readonly ToolStripMenuItem onTop = new("Always on top") { CheckOnClick = true };
    readonly ToolStripMenuItem full = new("Full screen  (F11)");
    FormWindowState before;

    public DisplayForm(Func<(string Tc, Tone Tone)> timecode)
    {
        this.timecode = timecode;
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
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ticker = new Thread(Tick) { IsBackground = true, Name = "Display ticker" };
        ticker.Start();
    }

    void Tick()
    {
        using var timer = new HiResTimer();
        string? last = null;
        var lastTone = Tone.Neutral;
        while (!closing)
        {
            var (text, tone) = timecode();
            if (text != last || tone != lastTone)
            {
                (last, lastTone) = (text, tone);
                try { BeginInvoke(() => tc.Set(text, Colour(tone))); }
                catch (InvalidOperationException) { break; }   // the window is closing
            }
            timer.Wait(0.002);
        }
    }

    static Color Colour(Tone t) => t switch { Tone.Good => Theme.Foreground, Tone.Warning => Theme.Warning, _ => Theme.MutedForeground };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        closing = true;
        base.OnFormClosing(e);
    }

    /// <summary>The state and summary lines (the number updates itself); <paramref name="problem"/> overrides the state.</summary>
    public void Show(EngineView? v, string? problem)
    {
        var tone = problem != null ? Tone.Warning : v?.Tone ?? Tone.Neutral;
        string s = "●  " + (problem ?? v?.Status ?? "Stopped");
        if (status.Text != s) status.Text = s;
        status.ForeColor = tone switch { Tone.Good => Theme.Good, Tone.Warning => Theme.Warning, _ => Theme.MutedForeground };
        string d = v == null ? "" : string.Join("   ·   ", v.Summary);
        if (detail.Text != d) detail.Text = d;
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

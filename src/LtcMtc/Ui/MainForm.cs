namespace LtcMtc;

sealed class MainForm : Form
{
    sealed record Item(string Key, string Name, bool Missing = false)
    {
        public override string ToString() => Missing ? Name + "  (not connected)" : Name;
    }

    readonly Settings settings = Settings.Load();
    readonly Pill pill = new();
    readonly Label timecode = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = Theme.Big };
    readonly Label subline = new() { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleCenter };
    readonly ComboBox mode = Combo(), midi = Combo(), audio = Combo(), channel = Combo(), rate = Combo();
    readonly NumericUpDown level = Number(-60, 0, 1), offset = Number(-1000, 1000, 1), freewheel = Number(0, 10000, 0), buffer = Number(3, 200, 0);
    readonly CheckBox autoStart = new() { Text = "Start on launch", AutoSize = true, ForeColor = Theme.Foreground, Margin = new Padding(0, 8, 0, 4) };
    readonly Button start = new() { Dock = DockStyle.Bottom, Height = 40, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 10.5f), Cursor = Cursors.Hand };
    readonly TableLayoutPanel form = Grid(), stats = Grid();
    readonly Label midiLabel, audioLabel, channelLabel;
    readonly List<Control> mtcOnly = [], ltcOnly = [];
    readonly Label[] statLabels = new Label[10], statValues = new Label[10];
    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 }, watchdog = new() { Interval = 1000 };
    readonly Panel spacer = new() { Dock = DockStyle.Bottom, Height = 12 };
    IEngine? engine;
    string? error;
    bool armed;      // Start was pressed: keep an engine running, reconnecting when a device goes away
    int reconnects;

    public MainForm()
    {
        Text = "LTC ⇄ MTC";
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(920, 680);
        MinimumSize = new Size(780, 640);
        BackColor = Theme.Background;
        ForeColor = Theme.Foreground;
        Font = Theme.Body;
        Padding = new Padding(20, 12, 20, 4);

        // Header: name on the left, state on the right.
        var header = new Panel { Dock = DockStyle.Top, Height = 52 };
        header.Controls.Add(new Label { Text = "LTC ⇄ MTC", Font = Theme.Title, AutoSize = true, Location = new Point(0, 12) });
        pill.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        header.Controls.Add(pill);
        header.Resize += (_, _) => pill.Location = new Point(header.Width - pill.Width, 14);
        pill.Resize += (_, _) => pill.Location = new Point(header.Width - pill.Width, 14);

        var hero = new Card("Timecode") { Dock = DockStyle.Top, Height = 170 };
        hero.Controls.Add(timecode);
        hero.Controls.Add(subline);

        // Settings: label / control rows; rows belonging to the other mode are hidden.
        mode.Items.AddRange(["MTC → LTC", "LTC → MTC"]);
        rate.Items.AddRange(["Follow MTC", .. Enum.GetValues<FrameRate>().Select(r => r.Label())]);
        Row(form, "Mode", mode);
        midiLabel = Row(form, "MIDI input", midi);
        audioLabel = Row(form, "Audio output", audio);
        channelLabel = Row(form, "Channel", channel);
        mtcOnly.AddRange([Row(form, "LTC frame rate", rate), rate, Row(form, "LTC level (dBFS)", level), level]);
        Row(form, "Offset (ms)", offset);
        Row(form, "Freewheel (ms)", freewheel);
        Row(form, "WASAPI buffer (ms)", buffer);
        form.Controls.Add(autoStart, 1, form.RowCount++);

        var settingsCard = new Card("Settings") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 16) };
        settingsCard.Controls.Add(form);
        settingsCard.Controls.Add(spacer);
        settingsCard.Controls.Add(start);
        start.FlatAppearance.BorderSize = 0;
        start.Click += (_, _) => Toggle();

        for (int i = 0; i < statLabels.Length; i++)
        {
            statLabels[i] = Row(stats, "", statValues[i] = new Label { AutoSize = true, Font = Theme.Mono, Margin = new Padding(0, 7, 0, 7) });
            statLabels[i].Margin = new Padding(0, 7, 12, 7);
        }
        var statsCard = new Card("Stats") { Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 16) };
        statsCard.Controls.Add(stats);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 16, 0, 0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.Controls.Add(settingsCard, 0, 0);
        body.Controls.Add(statsCard, 1, 0);

        Controls.Add(body);
        Controls.Add(hero);
        Controls.Add(header);

        // Settings → controls, then keep the lists fresh whenever one is opened (devices come and go).
        mode.SelectedIndex = (int)settings.Mode;
        rate.SelectedIndex = settings.OutRate is { } r ? (int)r + 1 : 0;
        Set(level, settings.LevelDb);
        Set(offset, settings.OffsetMs);
        Set(freewheel, settings.FreewheelMs);
        Set(buffer, settings.BufferMs);
        autoStart.Checked = settings.AutoStart;
        mode.SelectedIndexChanged += (_, _) => { Collect(); ApplyMode(); };
        midi.DropDown += (_, _) => FillMidi();
        audio.DropDown += (_, _) => FillAudio();
        audio.SelectionChangeCommitted += (_, _) => { Collect(); FillChannels(); };
        ApplyMode();

        timer.Tick += (_, _) => UpdateView(engine?.View());
        timer.Start();
        watchdog.Tick += (_, _) => Watch();
        watchdog.Start();
        UpdateView(null);
        if (settings.AutoStart) Shown += (_, _) => Toggle();
    }

    Mode Mode => (Mode)mode.SelectedIndex;

    void ApplyMode()
    {
        bool m2l = Mode == Mode.MtcToLtc;
        midiLabel.Text = m2l ? "MIDI input" : "MIDI output";
        audioLabel.Text = m2l ? "Audio output" : "Audio input";
        channelLabel.Text = m2l ? "Output channel" : "Input channel";
        foreach (var c in mtcOnly) c.Visible = m2l;
        FillMidi();
        FillAudio();
    }

    void FillMidi()
    {
        bool input = Mode == Mode.MtcToLtc;
        string saved = input ? settings.MidiIn : settings.MidiOut;
        Fill(midi, WinMM.Names(input).Select(n => new Item(n, n)), saved, saved);
    }

    void FillAudio()
    {
        bool output = Mode == Mode.MtcToLtc;
        var (key, name) = output ? (settings.AudioOut, settings.AudioOutName) : (settings.AudioIn, settings.AudioInName);
        Fill(audio, Audio.Devices(output).Select(d => new Item(d.Key, d.Name)), key, name == "" ? key : name);
        Collect();
        FillChannels();
    }

    void FillChannels()
    {
        bool output = Mode == Mode.MtcToLtc;
        string key = output ? settings.AudioOut : settings.AudioIn;
        int n = key == "" ? 2 : Audio.Channels(key, output);
        var items = Enumerable.Range(0, n).Select(i => new Item(i.ToString(), $"{i + 1}"));
        if (output) items = items.Prepend(new Item("-1", "All"));
        int saved = output ? settings.OutChannel : settings.InChannel;
        Fill(channel, items, saved.ToString(), $"{saved + 1}");
    }

    /// <summary>Lists the items and reselects the saved one. A saved device that isn't plugged in stays selected, marked
    /// "not connected", so it is neither forgotten nor silently swapped for another one.</summary>
    static void Fill(ComboBox box, IEnumerable<Item> items, string selected, string missingName)
    {
        box.BeginUpdate();
        box.Items.Clear();
        foreach (var i in items) box.Items.Add(i);
        var hit = box.Items.Cast<Item>().FirstOrDefault(i => i.Key == selected);
        if (hit == null && selected != "") box.Items.Add(hit = new Item(selected, missingName, Missing: true));
        box.SelectedItem = hit ?? (box.Items.Count > 0 ? box.Items[0] : null);
        box.EndUpdate();
    }

    void Collect()
    {
        string Key(ComboBox b) => (b.SelectedItem as Item)?.Key ?? "";
        bool m2l = settings.Mode == Mode.MtcToLtc;   // the mode the device lists currently describe
        string audioName = (audio.SelectedItem as Item)?.Name ?? "";
        if (m2l) { (settings.MidiIn, settings.AudioOut, settings.AudioOutName) = (Key(midi), Key(audio), audioName); if (channel.SelectedItem != null) settings.OutChannel = int.Parse(Key(channel)); }
        else { (settings.MidiOut, settings.AudioIn, settings.AudioInName) = (Key(midi), Key(audio), audioName); if (channel.SelectedItem != null) settings.InChannel = int.Parse(Key(channel)); }
        settings.Mode = Mode;
        settings.OutRate = rate.SelectedIndex > 0 ? (FrameRate)(rate.SelectedIndex - 1) : null;
        (settings.LevelDb, settings.OffsetMs) = ((double)level.Value, (double)offset.Value);
        (settings.FreewheelMs, settings.BufferMs, settings.AutoStart) = ((int)freewheel.Value, (int)buffer.Value, autoStart.Checked);
    }

    void Toggle()
    {
        if (armed) { (armed, error) = (false, null); engine?.Dispose(); engine = null; }
        else
        {
            Collect();
            settings.Save();
            (armed, reconnects) = (true, 0);
            TryStart();
        }
        foreach (Control c in form.Controls) c.Enabled = !armed;
        UpdateView(engine?.View());
    }

    bool TryStart()
    {
        try
        {
            engine = Mode == Mode.MtcToLtc ? new MtcToLtc(settings) : new LtcToMtc(settings);
            error = null;
            return true;
        }
        catch (Exception ex) { (engine, error) = (null, ex.Message); return false; }
    }

    /// <summary>Once a second while armed: drop an engine whose device went away, and retry until the devices are back.</summary>
    void Watch()
    {
        if (!armed) return;
        if (engine?.Fault is { } fault) { engine.Dispose(); (engine, error) = (null, fault); }
        if (engine == null && TryStart()) reconnects++;
    }

    void UpdateView(EngineView? v)
    {
        bool running = armed, waiting = armed && engine == null;
        start.Text = running ? "Stop" : "Start";
        start.BackColor = running ? Theme.Accent : Theme.Primary;
        start.ForeColor = Color.White;
        start.FlatAppearance.MouseOverBackColor = running ? Theme.Border : Theme.Signal;

        if (waiting) pill.Set("Reconnecting", Tone.Warning);
        else if (error != null) pill.Set("Error", Tone.Warning);
        else pill.Set(v?.Status ?? "Stopped", v?.Tone ?? Tone.Neutral);

        string tc = v?.Timecode ?? "--:--:--:--";
        if (timecode.Text != tc) timecode.Text = tc;
        timecode.ForeColor = v?.Tone switch { Tone.Good => Theme.Foreground, Tone.Warning => Theme.Warning, _ => Theme.MutedForeground };
        subline.Text = waiting ? $"Waiting for device: {error}" : error ?? (Mode == Mode.MtcToLtc ? "MIDI timecode in  →  linear timecode out" : "Linear timecode in  →  MIDI timecode out");
        subline.ForeColor = waiting ? Theme.Warning : error != null ? Theme.Destructive : Theme.MutedForeground;

        var rows = v?.Rows ?? [];
        if (armed) rows = [.. rows, ("Reconnects", reconnects.ToString("N0"))];
        for (int i = 0; i < statLabels.Length; i++)
        {
            var (label, value) = i < rows.Length ? rows[i] : ("", "");
            if (statLabels[i].Text != label) statLabels[i].Text = label;
            if (statValues[i].Text != value) statValues[i].Text = value;
        }
    }

    // Grow the window until every settings row fits above the Start button. This runs after DPI scaling, so it holds at
    // any display scale, and it also becomes the minimum height so the window can't be shrunk over the button.
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        PerformLayout();
        var screen = Screen.FromControl(this).WorkingArea;
        int extra = form.Bottom + spacer.Height - spacer.Top;
        if (extra > 0) Height = Math.Min(Height + extra, screen.Height);
        if (Bottom > screen.Bottom) Top = Math.Max(screen.Top, screen.Bottom - Height);
        MinimumSize = new Size(MinimumSize.Width, Height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkTitleBar(Handle);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        timer.Stop();
        watchdog.Stop();
        engine?.Dispose();
        if (!armed) Collect();
        settings.Save();
        base.OnFormClosing(e);
    }

    static TableLayoutPanel Grid()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Theme.Card };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    static Label Row(TableLayoutPanel t, string text, Control c)
    {
        var l = new Label { Text = text, AutoSize = true, ForeColor = Theme.MutedForeground, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 16, 6) };
        if (c is not Label) c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        c.Margin = c.Margin with { Top = 4, Bottom = 4 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(l, 0, t.RowCount);
        t.Controls.Add(c, 1, t.RowCount++);
        return l;
    }

    static void Set(NumericUpDown n, double v) => n.Value = Math.Clamp((decimal)v, n.Minimum, n.Maximum);

    static ComboBox Combo() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.Muted, ForeColor = Theme.Foreground,
    };

    static NumericUpDown Number(int min, int max, int decimals) => new()
    {
        Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals > 0 ? 0.5m : 10,
        BackColor = Theme.Muted, ForeColor = Theme.Foreground, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Right,
    };
}

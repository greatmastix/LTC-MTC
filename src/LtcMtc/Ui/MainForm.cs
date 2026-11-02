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
    readonly Card hero = new("Timecode") { Dock = DockStyle.Top, Height = 170 };
    readonly FlowLayoutPanel transport = new() { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
    readonly Button play = Flat("▶  Play", Theme.Primary), stopGen = Flat("■  Stop", Theme.Accent);

    // Settings controls. Each device role has its own list, so every one maps to one saved setting.
    readonly ComboBox source = Combo(), midiIn = Combo(), audioIn = Combo(), chIn = Combo(), genRate = Combo(),
        audioOut = Combo(), chOut = Combo(), rate = Combo(), midiOut = Combo(), mmcPlay = Combo(), artTarget = Combo(editable: true);
    readonly NumericUpDown level = Number(-60, 0, 1), offset = Number(-1000, 1000, 1), freewheel = Number(0, 10000, 0),
        buffer = Number(3, 200, 0), mmcDevice = Number(0, 127, 0);
    readonly TextBox tcOffset = TcBox(), genStart = TcBox();
    readonly CheckBox outLtc = Check("LTC out (audio)"), outMidi = Check("MIDI out (MTC / MMC)"), outArt = Check("Art-Net out (network)"),
        jamSync = Check("Jam-sync: keep running if the input stops"), tray = Check("Minimize to tray"),
        sendMtc = Check("Send MTC (timecode)"), sendMmc = Check("Send MMC (transport: locate, play, stop)"),
        mmcLocate = Check("Locate before play"), mmcRateBits = Check("Frame-rate bits in locate hours (MMC spec)"),
        autoStart = Check("Connect on launch"), lowLatency = Check("WASAPI low-latency mode");

    readonly List<(Func<bool> When, Control[] Controls)> rules = [];   // which rows show, for the current choices
    readonly List<Control> live = [];                                   // stays editable while running
    readonly Button start = new() { Dock = DockStyle.Bottom, Height = 40, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 10.5f), Cursor = Cursors.Hand };
    readonly TableLayoutPanel form = Grid(), stats = Grid();
    readonly Card settingsCard = new("Settings") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 16) };
    readonly Card statsCard = new("Stats") { Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 16) };
    readonly Label[] statLabels = new Label[20], statValues = new Label[20];
    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 }, watchdog = new() { Interval = 1000 };
    readonly Panel spacer = new() { Dock = DockStyle.Bottom, Height = 12 };
    DisplayForm? display;
    LogForm? logForm;
    readonly List<EventLog.Entry> log = [];
    readonly NotifyIcon trayIcon = new() { Text = "LTC ⇄ MTC" };
    string? lastStatus;
    volatile Engine? engine;          // volatile: the display's ticker thread reads these two
    volatile GeneratorClock? generator;   // exists while the generator is the source; outlives engines and connections
    string? error;
    bool armed;                  // Start was pressed: keep an engine running, reconnecting when a device goes away
    int reconnects, shownRows = -1;

    SourceKind Src => (SourceKind)Math.Max(0, source.SelectedIndex);

    public MainForm()
    {
        Text = "LTC ⇄ MTC";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);   // the exe's own icon (app.ico)
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(920, 680);
        MinimumSize = new Size(780, 640);
        BackColor = Theme.Background;
        ForeColor = Theme.Foreground;
        Font = Theme.Body;
        Padding = new Padding(20, 12, 20, 4);
        KeyPreview = true;

        // Header: name on the left; Display, About and the state on the right.
        var header = new Panel { Dock = DockStyle.Top, Height = 52 };
        header.Controls.Add(new Label { Text = "LTC ⇄ MTC", Font = Theme.Title, AutoSize = true, Location = new Point(0, 12) });
        var displayLink = Link("Display", OpenDisplay);
        var logLink = Link("Log", OpenLog);
        var aboutLink = Link("About", () => { using var f = new AboutForm(); f.ShowDialog(this); });
        header.Controls.AddRange([pill, aboutLink, logLink, displayLink]);
        void Place()
        {
            pill.Location = new Point(header.Width - pill.Width, 14);
            int y = 14 + (pill.Height - aboutLink.Height) / 2;
            aboutLink.Location = new Point(pill.Left - aboutLink.Width - 16, y);
            logLink.Location = new Point(aboutLink.Left - logLink.Width - 14, y);
            displayLink.Location = new Point(logLink.Left - displayLink.Width - 14, y);
        }
        header.Resize += (_, _) => Place();
        pill.Resize += (_, _) => Place();

        // Hero: the big number, plus transport buttons while the generator is the source.
        transport.Controls.AddRange([play, stopGen]);
        play.Click += (_, _) => PlayPause();
        stopGen.Click += (_, _) => StopGenerator();
        hero.Controls.Add(timecode);
        hero.Controls.Add(subline);
        hero.Controls.Add(transport);

        // Settings, in sections; rows that don't apply to the current choices are hidden.
        source.Items.AddRange(["MTC (MIDI in)", "LTC (audio in)", "Generator", "Art-Net (network)"]);
        var rates = Enum.GetValues<FrameRate>().Select(r => r.Label()).ToArray();
        rate.Items.AddRange(["Follow source", .. rates]);
        genRate.Items.AddRange(rates);
        mmcPlay.Items.AddRange(["Play", "Deferred Play"]);
        bool Mtc() => Src == SourceKind.Mtc;
        bool Ltc() => Src == SourceKind.Ltc;
        bool Gen() => Src == SourceKind.Generator;
        bool Art() => Src == SourceKind.ArtNet;
        bool ArtOut() => outArt.Checked && !Art();
        bool AnyOut() => outLtc.Checked || outMidi.Checked || ArtOut();
        bool MmcOn() => outMidi.Checked && sendMmc.Checked;

        Section(form, "Source");
        Row("Source", source);
        Row("MIDI input", midiIn, Mtc);
        Row("Audio input", audioIn, Ltc);
        Row("Input channel", chIn, Ltc);
        live.AddRange(Row("Start time", genStart, Gen));
        Row("Frame rate", genRate, Gen);
        Option(jamSync, () => !Gen());

        Section(form, "Outputs");
        Option(outLtc);
        Row("Audio output", audioOut, () => outLtc.Checked);
        Row("Output channel", chOut, () => outLtc.Checked);
        Row("LTC frame rate", rate, () => outLtc.Checked && !Gen());
        Row("LTC level (dBFS)", level, () => outLtc.Checked);
        Option(outMidi);
        Row("MIDI output", midiOut, () => outMidi.Checked);
        Option(sendMtc, () => outMidi.Checked);
        Option(sendMmc, () => outMidi.Checked);
        Row("MMC device ID", mmcDevice, MmcOn);
        Row("MMC play", mmcPlay, MmcOn);
        Option(mmcLocate, MmcOn);
        Option(mmcRateBits, MmcOn);
        Option(outArt, () => !Art());
        Row("Art-Net target", artTarget, ArtOut);

        Section(form, "Timing");
        live.AddRange(Row("Timecode offset", tcOffset, AnyOut));
        live.AddRange(Row("Fine offset (ms)", offset, AnyOut));
        Row("Freewheel (ms)", freewheel, () => !Gen());

        Section(form, "Options");
        Row("WASAPI buffer (ms)", buffer);
        Option(lowLatency);
        Option(autoStart);
        Option(tray);

        settingsCard.Controls.Add(form);
        settingsCard.Controls.Add(spacer);
        settingsCard.Controls.Add(start);
        start.FlatAppearance.BorderSize = 0;
        start.Click += (_, _) => Toggle();

        for (int i = 0; i < statLabels.Length; i++)
        {
            statLabels[i] = AddRow(stats, "", statValues[i] = new Label { AutoSize = true, Font = Theme.Mono, Margin = new Padding(0, 7, 0, 7) });
            statLabels[i].Margin = new Padding(0, 7, 12, 7);
        }
        statsCard.Controls.Add(stats);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 16, 0, 0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        body.Controls.Add(settingsCard, 0, 0);
        body.Controls.Add(statsCard, 1, 0);

        Controls.Add(body);
        Controls.Add(hero);
        Controls.Add(header);

        // Settings → controls.
        source.SelectedIndex = (int)(settings.Source ?? SourceKind.Mtc);
        genStart.Text = Timecode.TryParse(settings.GenStart, out var gs) ? gs.ToString() : "00:00:00:00";
        genRate.SelectedIndex = (int)settings.GenRate;
        (outLtc.Checked, outMidi.Checked, outArt.Checked) = (settings.OutLtc, settings.OutMidi, settings.OutArtNet);
        (jamSync.Checked, tray.Checked) = (settings.JamSync, settings.Tray);
        FillArtTargets();
        artTarget.DropDown += (_, _) => FillArtTargets();
        rate.SelectedIndex = settings.OutRate is { } r ? (int)r + 1 : 0;
        Set(level, settings.LevelDb);
        Set(offset, settings.OffsetMs);
        tcOffset.Text = TimecodeOffset.TryParse(settings.TcOffset, out var tco) ? tco.ToString() : TimecodeOffset.Zero.ToString();
        Set(freewheel, settings.FreewheelMs);
        Set(buffer, settings.BufferMs);
        (autoStart.Checked, lowLatency.Checked) = (settings.AutoStart, settings.LowLatency);
        (sendMtc.Checked, sendMmc.Checked) = (settings.SendMtc, settings.SendMmc);
        Set(mmcDevice, settings.MmcDevice);
        mmcDevice.Increment = 1;
        mmcPlay.SelectedIndex = settings.MmcDeferredPlay ? 1 : 0;
        (mmcLocate.Checked, mmcRateBits.Checked) = (settings.MmcLocate, settings.MmcRateBits);

        // Device lists: filled now, and refreshed whenever one is opened (devices come and go).
        FillMidi(midiIn, true, settings.MidiIn);
        FillMidi(midiOut, false, settings.MidiOut);
        FillAudio(audioIn, false);
        FillAudio(audioOut, true);
        midiIn.DropDown += (_, _) => FillMidi(midiIn, true, Key(midiIn));
        midiOut.DropDown += (_, _) => FillMidi(midiOut, false, Key(midiOut));
        audioIn.DropDown += (_, _) => { Collect(); FillAudio(audioIn, false); };
        audioOut.DropDown += (_, _) => { Collect(); FillAudio(audioOut, true); };
        audioIn.SelectionChangeCommitted += (_, _) => { Collect(); FillChannels(chIn, false); };
        audioOut.SelectionChangeCommitted += (_, _) => { Collect(); FillChannels(chOut, true); };

        source.SelectedIndexChanged += (_, _) => UpdateRows();
        genRate.SelectedIndexChanged += (_, _) => EnsureGenerator();
        foreach (var c in new[] { outLtc, outMidi, outArt, sendMtc, sendMmc }) c.CheckedChanged += (_, _) => UpdateRows();
        tray.CheckedChanged += (_, _) => trayIcon.Visible = tray.Checked;
        offset.ValueChanged += (_, _) => PushLive();
        foreach (var t in new[] { tcOffset, genStart })
        {
            t.Leave += (_, _) => PushLive();
            t.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { PushLive(); e.SuppressKeyPress = true; } };
        }
        UpdateRows();

        timer.Tick += (_, _) => { UpdateView(engine?.View()); DrainLog(); };
        timer.Start();
        watchdog.Tick += (_, _) => { Watch(); UpdateTray(); };
        watchdog.Start();
        SetUpTray();
        UpdateView(null);
        if (settings.AutoStart) Shown += (_, _) =>
        {
            Toggle();
            if (tray.Checked) WindowState = FormWindowState.Minimized;   // unattended: start straight in the tray
        };
    }

    // ── Rows and visibility ──────────────────────────────────────────────────────────────────────

    Control[] Row(string label, Control c, Func<bool>? when = null)
    {
        Control[] pair = [AddRow(form, label, c), c];
        if (when != null) rules.Add((when, pair));
        return pair;
    }

    void Option(CheckBox c, Func<bool>? when = null)
    {
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.Controls.Add(c, 1, form.RowCount++);
        if (when != null) rules.Add((when, [c]));
    }

    void UpdateRows()
    {
        EnsureGenerator();
        form.SuspendLayout();
        foreach (var (when, controls) in rules)
            foreach (var c in controls) c.Visible = when();
        form.ResumeLayout();
        transport.Visible = Src == SourceKind.Generator;
        // Before the handle exists, WinForms' DPI auto-scaling still applies to this; afterwards we scale ourselves.
        hero.Height = (int)((transport.Visible ? 214 : 170) * (IsHandleCreated ? DeviceDpi / 96f : 1));
        Fit();
    }

    // ── Devices ──────────────────────────────────────────────────────────────────────────────────

    static string Key(ComboBox b) => (b.SelectedItem as Item)?.Key ?? "";

    static void FillMidi(ComboBox box, bool input, string saved) =>
        Fill(box, WinMM.Names(input).Select(n => new Item(n, n)), saved, saved);

    void FillAudio(ComboBox box, bool output)
    {
        var (key, name) = output ? (settings.AudioOut, settings.AudioOutName) : (settings.AudioIn, settings.AudioInName);
        Fill(box, Audio.Devices(output).Select(d => new Item(d.Key, d.Name)), key, name == "" ? key : name);
        FillChannels(output ? chOut : chIn, output);
    }

    void FillChannels(ComboBox box, bool output)
    {
        string key = Key(output ? audioOut : audioIn);
        int n = key == "" ? 2 : Audio.Channels(key, output);
        var items = Enumerable.Range(0, n).Select(i => new Item(i.ToString(), $"{i + 1}"));
        if (output) items = items.Prepend(new Item("-1", "All"));
        int saved = output ? settings.OutChannel : settings.InChannel;
        Fill(box, items, saved.ToString(), $"{saved + 1}");
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
        static string Name(ComboBox b) => (b.SelectedItem as Item)?.Name ?? "";
        settings.Source = Src;
        (settings.MidiIn, settings.MidiOut) = (Key(midiIn), Key(midiOut));
        (settings.AudioIn, settings.AudioInName, settings.AudioOut, settings.AudioOutName) = (Key(audioIn), Name(audioIn), Key(audioOut), Name(audioOut));
        if (chIn.SelectedItem != null) settings.InChannel = int.Parse(Key(chIn));
        if (chOut.SelectedItem != null) settings.OutChannel = int.Parse(Key(chOut));
        if (Timecode.TryParse(genStart.Text, out var gs)) settings.GenStart = gs.ToString();
        settings.GenRate = (FrameRate)Math.Max(0, genRate.SelectedIndex);
        (settings.OutLtc, settings.OutMidi, settings.OutArtNet) = (outLtc.Checked, outMidi.Checked, outArt.Checked);
        settings.ArtNetTarget = artTarget.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        (settings.JamSync, settings.Tray) = (jamSync.Checked, tray.Checked);
        settings.OutRate = rate.SelectedIndex > 0 ? (FrameRate)(rate.SelectedIndex - 1) : null;
        (settings.LevelDb, settings.OffsetMs) = ((double)level.Value, (double)offset.Value);
        if (TimecodeOffset.TryParse(tcOffset.Text, out var tco)) settings.TcOffset = tco.ToString();
        (settings.FreewheelMs, settings.BufferMs, settings.AutoStart) = ((int)freewheel.Value, (int)buffer.Value, autoStart.Checked);
        settings.LowLatency = lowLatency.Checked;
        (settings.SendMtc, settings.SendMmc, settings.MmcDevice) = (sendMtc.Checked, sendMmc.Checked, (int)mmcDevice.Value);
        (settings.MmcDeferredPlay, settings.MmcLocate, settings.MmcRateBits) = (mmcPlay.SelectedIndex == 1, mmcLocate.Checked, mmcRateBits.Checked);
    }

    // ── Running ──────────────────────────────────────────────────────────────────────────────────

    void Toggle()
    {
        if (armed)
        {
            (armed, error) = (false, null);   // the generator keeps its position
            engine?.Dispose();
            engine = null;
            EventLog.Add("Disconnected", "devices released");
        }
        else
        {
            Collect();
            settings.Save();
            EventLog.Add("Connected", Route());
            (armed, reconnects) = (true, 0);
            EnsureGenerator();   // connecting only opens devices: a paused generator stays paused (receivers get cued)
            TryStart();
        }
        foreach (Control c in form.Controls) c.Enabled = !armed || live.Contains(c);
        UpdateView(engine?.View());
    }

    bool TryStart()
    {
        try
        {
            engine = new Engine(settings, generator);
            error = null;
            return true;
        }
        catch (Exception ex) { (engine, error) = (null, ex.Message); return false; }
    }

    /// <summary>Once a second while armed: drop an engine whose device went away, and retry until the devices are back.</summary>
    void Watch()
    {
        if (!armed) return;
        if (engine?.Fault is { } fault)
        {
            engine.Dispose();
            (engine, error) = (null, fault);
            EventLog.Add("Device lost", fault);
        }
        if (engine == null && TryStart())
        {
            reconnects++;
            EventLog.Add("Reconnected", Route());
        }
    }

    /// <summary>The fields that stay editable while running: offsets, and the generator's start time.</summary>
    void PushLive()
    {
        if (TimecodeOffset.TryParse(tcOffset.Text, out var o)) settings.TcOffset = o.ToString();
        tcOffset.Text = settings.TcOffset;
        settings.OffsetMs = (double)offset.Value;
        if (engine != null) engine.Offsets = Offsets.From(settings);

        if (Timecode.TryParse(genStart.Text, out var gs)) settings.GenStart = gs.ToString();
        genStart.Text = settings.GenStart;
        if (generator is { } g && Timecode.TryParse(settings.GenStart, out gs))
        {
            g.Start = gs.ToFrames(g.Rate);
            if (!g.Current.Playing) g.Stop(Clock.Now);   // paused: cue to the new start right away
        }
    }

    /// <summary>The generator's clock, paused at the start time, whenever the generator is the source. Its transport
    /// works with or without a connection; Connect only opens the outputs.</summary>
    void EnsureGenerator()
    {
        if (Src != SourceKind.Generator) { if (!armed) generator = null; return; }
        var r = (FrameRate)Math.Max(0, genRate.SelectedIndex);
        if (generator?.Rate == r) return;
        Timecode.TryParse(genStart.Text, out var gs);
        generator = new GeneratorClock(r, gs.ToFrames(r));
    }

    void PlayPause()
    {
        if (generator is not { } g) return;
        bool playing = g.Current.Playing;
        if (playing) g.Pause(Clock.Now); else g.Play(Clock.Now);
        EventLog.Add(playing ? "Generator pause" : "Generator play", "", Fmt.Tc(g.FrameAt(Clock.Now), g.Rate));
    }

    void StopGenerator()
    {
        if (generator is not { } g) return;
        g.Stop(Clock.Now);
        EventLog.Add("Generator stop", "back to the start time", Fmt.Tc(g.Start, g.Rate));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Space plays / pauses the generator, unless you're typing in a field.
        if (e.KeyCode == Keys.Space && generator != null && ActiveControl is not (TextBoxBase or NumericUpDown or ComboBox))
        {
            PlayPause();
            e.Handled = e.SuppressKeyPress = true;
        }
        base.OnKeyDown(e);
    }

    // ── View ─────────────────────────────────────────────────────────────────────────────────────

    void UpdateView(EngineView? v)
    {
        bool waiting = armed && engine == null;
        start.Text = armed ? "Disconnect" : "Connect";
        start.BackColor = armed ? Theme.Accent : Theme.Primary;
        start.ForeColor = Color.White;
        start.FlatAppearance.MouseOverBackColor = armed ? Theme.Border : Theme.Signal;

        play.Text = generator?.Current.Playing == true ? "❚❚  Pause" : "▶  Play";
        play.Enabled = stopGen.Enabled = generator != null;
        if (engine == null && generator is { } g)   // not connected (yet): the generator still runs on screen
        {
            bool on = g.Current.Playing;
            v = new(on ? "Playing" : "Paused", on ? Tone.Good : Tone.Neutral, Fmt.Tc(g.FrameAt(Clock.Now), g.Rate),
                [$"Generator · {g.Rate.Label()}", "not connected"], []);
        }

        if (waiting) pill.Set("Reconnecting", Tone.Warning);
        else if (error != null) pill.Set("Error", Tone.Warning);
        else pill.Set(v?.Status ?? "Stopped", v?.Tone ?? Tone.Neutral);
        if (armed && engine != null && v != null && v.Status != lastStatus)   // Locked → Freewheel → Stopped, Jam-sync, Playing, …
            EventLog.Add(v.Status, string.Join(" · ", v.Summary), v.Timecode);
        lastStatus = armed ? v?.Status : null;

        string tc = v?.Timecode ?? "--:--:--:--";
        if (timecode.Text != tc) timecode.Text = tc;
        timecode.ForeColor = v?.Tone switch { Tone.Good => Theme.Foreground, Tone.Warning => Theme.Warning, _ => Theme.MutedForeground };
        subline.Text = waiting ? $"Waiting for device: {error}" : error ?? Route();
        subline.ForeColor = waiting ? Theme.Warning : error != null ? Theme.Destructive : Theme.MutedForeground;

        var rows = v?.Rows ?? [];
        if (armed) rows = [.. rows, ("Reconnects", reconnects.ToString("N0"))];
        for (int i = 0; i < statLabels.Length; i++)
        {
            var (label, value) = i < rows.Length ? rows[i] : ("", "");
            if (statLabels[i].Text != label) statLabels[i].Text = label;
            if (statValues[i].Text != value) statValues[i].Text = value;
        }
        if (rows.Length != shownRows)   // show only the rows in use, and make room for them
        {
            shownRows = rows.Length;
            stats.SuspendLayout();
            for (int i = 0; i < statLabels.Length; i++) statLabels[i].Visible = statValues[i].Visible = i < rows.Length;
            stats.ResumeLayout();
            Fit();
        }

        if (display is { IsDisposed: false }) display.Show(v, waiting ? "Reconnecting" : error != null ? "Error" : null);
    }

    /// <summary>The route in words, e.g. "LTC in  →  MTC out", or "LTC in  ·  display only".</summary>
    string Route()
    {
        string from = Src switch { SourceKind.Mtc => "MTC in", SourceKind.Ltc => "LTC in", SourceKind.ArtNet => "Art-Net in", _ => "Generator" };
        if (jamSync.Checked && Src != SourceKind.Generator) from += " (jam-sync)";
        var outs = new List<string>();
        if (outLtc.Checked) outs.Add("LTC out");
        if (outMidi.Checked) outs.Add(sendMtc.Checked && sendMmc.Checked ? "MTC + MMC out" : sendMmc.Checked ? "MMC out" : "MTC out");
        if (outArt.Checked && Src != SourceKind.ArtNet) outs.Add("Art-Net out");
        return outs.Count == 0 ? $"{from}  ·  display only" : $"{from}  →  {string.Join("  +  ", outs)}";
    }

    void OpenLog()
    {
        if (logForm is { IsDisposed: false }) { logForm.Activate(); return; }
        logForm = new LogForm(log);
        logForm.Show();
    }

    /// <summary>Moves new events into the window's list (last 5,000) and appends them to today's CSV file.</summary>
    void DrainLog()
    {
        var added = EventLog.Drain().ToList();
        if (added.Count == 0) return;
        log.AddRange(added);
        if (log.Count > 5000) log.RemoveRange(0, log.Count - 5000);
        if (logForm is { IsDisposed: false }) logForm.Append(added);
        try
        {
            Directory.CreateDirectory(LogForm.Folder);
            string file = Path.Combine(LogForm.Folder, $"{DateTime.Now:yyyy-MM-dd}.csv");
            if (!File.Exists(file)) File.WriteAllText(file, EventLog.CsvHeader + Environment.NewLine);
            File.AppendAllLines(file, added.Select(EventLog.Csv));
        }
        catch { /* logging must never stop the show */ }
    }

    /// <summary>Directed broadcasts of this PC's networks, all networks, and this PC; any IP can be typed in too.</summary>
    void FillArtTargets()
    {
        string current = artTarget.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? settings.ArtNetTarget;
        var items = new List<string>();
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || System.Net.IPAddress.IsLoopback(ua.Address)) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bc = new System.Net.IPAddress(ip.Zip(mask, (a, m) => (byte)(a | ~m)).ToArray());
                items.Add($"{bc}   ({ni.Name}: {ua.Address})");
            }
        }
        items.Add("255.255.255.255   (all networks)");
        items.Add("127.0.0.1   (this PC)");
        artTarget.BeginUpdate();
        artTarget.Items.Clear();
        artTarget.Items.AddRange([.. items]);
        artTarget.EndUpdate();
        artTarget.Text = items.FirstOrDefault(i => i.StartsWith(current + " ")) ?? current;
    }

    void SetUpTray()
    {
        trayIcon.Icon = Icon;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Connect / Disconnect", null, (_, _) => Toggle());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        trayIcon.ContextMenuStrip = menu;
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        trayIcon.Visible = tray.Checked;
        Resize += (_, _) => { if (tray.Checked && WindowState == FormWindowState.Minimized) Hide(); };
    }

    void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>Once a second: the tray tooltip shows the state and timecode (Windows caps it at 127 characters).</summary>
    void UpdateTray()
    {
        if (!trayIcon.Visible) return;
        string text = $"LTC ⇄ MTC · {pill.Text} · {timecode.Text}";
        trayIcon.Text = text.Length > 127 ? text[..127] : text;
    }

    /// <summary>The number for the Display window, called from its ticker thread every 2 ms: no UI state, no allocation
    /// beyond the string.</summary>
    (string, Tone) DisplayTimecode()
    {
        if (engine is { } e) return e.Timecode();
        if (generator is { } g) return (Fmt.Tc(g.FrameAt(Clock.Now), g.Rate), g.Current.Playing ? Tone.Good : Tone.Neutral);
        return ("--:--:--:--", armed ? Tone.Warning : Tone.Neutral);
    }

    void OpenDisplay()
    {
        if (display is { IsDisposed: false }) { display.Activate(); return; }
        display = new DisplayForm(DisplayTimecode);
        display.Show();
    }

    // ── Window ───────────────────────────────────────────────────────────────────────────────────

    // Grow the window until every settings row fits above the Start button and every stats row fits in its card. Runs
    // after DPI scaling and whenever rows appear, so it holds at any display scale; it also becomes the minimum height.
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Fit();
    }

    void Fit()
    {
        if (!IsHandleCreated) return;
        PerformLayout();
        var screen = Screen.FromControl(this).WorkingArea;
        int extra = Math.Max(form.Bottom + spacer.Height - spacer.Top, stats.Bottom - (statsCard.Height - statsCard.Padding.Bottom));
        if (extra > 0) Height = Math.Min(Height + extra, screen.Height);
        if (Bottom > screen.Bottom) Top = Math.Max(screen.Top, screen.Bottom - Height);
        MinimumSize = new Size(MinimumSize.Width, Math.Max(MinimumSize.Height, Height));
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
        display?.Close();
        logForm?.Close();
        DrainLog();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        base.OnFormClosing(e);
    }

    // ── Control factories ────────────────────────────────────────────────────────────────────────

    static TableLayoutPanel Grid()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Theme.Card };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    static Label AddRow(TableLayoutPanel t, string text, Control c)
    {
        var l = new Label { Text = text, AutoSize = true, ForeColor = Theme.MutedForeground, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 16, 6) };
        if (c is not Label) c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        c.Margin = c.Margin with { Top = 4, Bottom = 4 };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(l, 0, t.RowCount);
        t.Controls.Add(c, 1, t.RowCount++);
        return l;
    }

    static void Section(TableLayoutPanel t, string text)
    {
        var s = new SectionTitle(text);
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(s, 0, t.RowCount++);
        t.SetColumnSpan(s, 2);
    }

    static void Set(NumericUpDown n, double v) => n.Value = Math.Clamp((decimal)v, n.Minimum, n.Maximum);

    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, ForeColor = Theme.Foreground, Margin = new Padding(0, 6, 0, 2) };

    static TextBox TcBox() => new()
    {
        BackColor = Theme.Muted, ForeColor = Theme.Foreground, BorderStyle = BorderStyle.FixedSingle, Font = Theme.Mono, TextAlign = HorizontalAlignment.Right,
    };

    static LinkLabel Link(string text, Action onClick)
    {
        var l = new LinkLabel
        {
            Text = text, AutoSize = true, LinkColor = Theme.MutedForeground, ActiveLinkColor = Theme.Signal,
            VisitedLinkColor = Theme.MutedForeground, LinkBehavior = LinkBehavior.HoverUnderline,
        };
        l.LinkClicked += (_, _) => onClick();
        return l;
    }

    static Button Flat(string text, Color back)
    {
        var b = new Button
        {
            Text = text, Width = 120, Height = 34, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 10f), Cursor = Cursors.Hand, Margin = new Padding(0, 4, 10, 4),
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = back == Theme.Primary ? Theme.Signal : Theme.Border;
        return b;
    }

    static ComboBox Combo(bool editable = false) => new()
    {
        DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
        BackColor = Theme.Muted, ForeColor = Theme.Foreground,
    };

    static NumericUpDown Number(int min, int max, int decimals) => new()
    {
        Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = decimals > 0 ? 0.5m : 10,
        BackColor = Theme.Muted, ForeColor = Theme.Foreground, BorderStyle = BorderStyle.FixedSingle, TextAlign = HorizontalAlignment.Right,
    };
}

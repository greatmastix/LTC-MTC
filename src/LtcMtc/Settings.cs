using System.Text.Json;

namespace LtcMtc;

/// <summary>The 0.1 modes; only read, to migrate old settings files.</summary>
public enum Mode { MtcToLtc, LtcToMtc }

public enum SourceKind { Mtc, Ltc, Generator }

/// <summary>Saved as JSON in %APPDATA%\LtcMtc\settings.json. Devices are stored by name / endpoint ID, not index.</summary>
public sealed class Settings
{
    public Mode Mode { get; set; }                // legacy, see Load
    public SourceKind? Source { get; set; }
    public bool OutLtc { get; set; }
    public bool OutMidi { get; set; }
    public string GenStart { get; set; } = "00:00:00:00";
    public FrameRate GenRate { get; set; } = FrameRate.Fps25;
    public string MidiIn { get; set; } = "";
    public string MidiOut { get; set; } = "";
    public string AudioOut { get; set; } = "";
    public string AudioIn { get; set; } = "";
    public string AudioOutName { get; set; } = "";   // display names, to show a device that is unplugged
    public string AudioInName { get; set; } = "";
    public int OutChannel { get; set; } = -1;   // -1 = all channels
    public int InChannel { get; set; }
    public FrameRate? OutRate { get; set; }      // null = follow MTC
    public double LevelDb { get; set; } = -12;
    public double OffsetMs { get; set; }
    public string TcOffset { get; set; } = "+00:00:00:00";
    public int FreewheelMs { get; set; } = 500;
    public int BufferMs { get; set; } = 10;
    public bool LowLatency { get; set; }          // WASAPI IAudioClient3 mode: some drivers (e.g. Bluetooth) play silence
    public bool AutoStart { get; set; }
    public bool SendMtc { get; set; } = true;     // LTC → MTC: quarter-frames + full-frame
    public bool SendMmc { get; set; }             // LTC → MTC: MMC locate / deferred play / stop
    public int MmcDevice { get; set; } = 127;     // 127 = all devices
    public bool MmcDeferredPlay { get; set; }     // false: Play (0x02), more widely supported; true: Deferred Play (0x03)
    public bool MmcLocate { get; set; } = true;   // send Locate before Play
    public bool MmcRateBits { get; set; } = true; // frame-rate code in the Locate hours byte, per the spec

    static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LtcMtc", "settings.json");

    public static Settings Load()
    {
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new(); }
        catch { s = new(); }
        if (s.Source == null)   // a new or pre-0.2 file: the old mode becomes a source plus one output
            (s.Source, s.OutLtc, s.OutMidi) = s.Mode == Mode.LtcToMtc ? (SourceKind.Ltc, false, true) : (SourceKind.Mtc, true, false);
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not fatal */ }
    }
}

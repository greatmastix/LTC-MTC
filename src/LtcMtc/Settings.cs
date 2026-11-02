using System.Text.Json;

namespace LtcMtc;

public enum Mode { MtcToLtc, LtcToMtc }

/// <summary>Saved as JSON in %APPDATA%\LtcMtc\settings.json. Devices are stored by name / endpoint ID, not index.</summary>
public sealed class Settings
{
    public Mode Mode { get; set; }
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
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new(); }
        catch { return new(); }
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

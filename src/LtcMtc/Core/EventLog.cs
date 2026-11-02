using System.Collections.Concurrent;

namespace LtcMtc;

/// <summary>
/// Things worth knowing after a show: locks, losses, jumps, relocates, transport commands. Real-time threads only
/// enqueue (lock-free; events are rare); the UI drains the queue, shows it, and appends it to a daily CSV file.
/// </summary>
public static class EventLog
{
    public sealed record Entry(DateTime Time, string Timecode, string Event, string Detail);

    static readonly ConcurrentQueue<Entry> pending = new();
    static readonly ConcurrentDictionary<string, double> lastByKey = new();

    public static void Add(string ev, string detail = "", string timecode = "") =>
        pending.Enqueue(new(DateTime.Now, timecode, ev, detail));

    /// <summary>Like <see cref="Add"/>, but at most once a second per event, for things that can repeat quickly.</summary>
    public static void AddThrottled(string ev, string detail = "", string timecode = "")
    {
        double now = Clock.Now;
        if (lastByKey.TryGetValue(ev, out var last) && now - last < 1) return;
        lastByKey[ev] = now;
        Add(ev, detail, timecode);
    }

    public static IEnumerable<Entry> Drain()
    {
        while (pending.TryDequeue(out var e)) yield return e;
    }

    public static string Csv(Entry e) =>
        string.Join(",", e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"), Quote(e.Timecode), Quote(e.Event), Quote(e.Detail));

    public const string CsvHeader = "time,timecode,event,detail";

    static string Quote(string s) => s.IndexOfAny([',', '"', '\n']) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
}

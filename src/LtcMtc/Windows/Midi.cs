using System.Runtime.InteropServices;

namespace LtcMtc;

public delegate void SysExHandler(ReadOnlySpan<byte> data, double time);

/// <summary>
/// winmm MIDI, called directly: the callback runs on the driver's thread, stamps the message with QPC time and hands it
/// over without allocating (NAudio's MidiIn throws and catches an exception for every MTC quarter-frame).
/// </summary>
static unsafe partial class WinMM
{
    const string Dll = "winmm.dll";
    [LibraryImport(Dll)] public static partial uint midiInGetNumDevs();
    [LibraryImport(Dll)] public static partial uint midiOutGetNumDevs();
    [LibraryImport(Dll)] public static partial int midiInGetDevCapsW(nuint id, byte* caps, uint size);
    [LibraryImport(Dll)] public static partial int midiOutGetDevCapsW(nuint id, byte* caps, uint size);
    [LibraryImport(Dll)] public static partial int midiInOpen(out nint h, uint id, nint callback, nint instance, uint flags);
    [LibraryImport(Dll)] public static partial int midiInStart(nint h);
    [LibraryImport(Dll)] public static partial int midiInReset(nint h);
    [LibraryImport(Dll)] public static partial int midiInClose(nint h);
    [LibraryImport(Dll)] public static partial int midiInPrepareHeader(nint h, MidiHdr* hdr, uint size);
    [LibraryImport(Dll)] public static partial int midiInUnprepareHeader(nint h, MidiHdr* hdr, uint size);
    [LibraryImport(Dll)] public static partial int midiInAddBuffer(nint h, MidiHdr* hdr, uint size);
    [LibraryImport(Dll)] public static partial int midiOutOpen(out nint h, uint id, nint callback, nint instance, uint flags);
    [LibraryImport(Dll)] public static partial int midiOutShortMsg(nint h, uint msg);
    [LibraryImport(Dll)] public static partial int midiOutPrepareHeader(nint h, MidiHdr* hdr, uint size);
    [LibraryImport(Dll)] public static partial int midiOutUnprepareHeader(nint h, MidiHdr* hdr, uint size);
    [LibraryImport(Dll)] public static partial int midiOutLongMsg(nint h, MidiHdr* hdr, uint size);
    [LibraryImport(Dll)] public static partial int midiOutReset(nint h);
    [LibraryImport(Dll)] public static partial int midiOutClose(nint h);

    [StructLayout(LayoutKind.Sequential)]
    public struct MidiHdr
    {
        public byte* Data;
        public uint Length, Recorded;
        public nint User;
        public uint Flags;
        public MidiHdr* Next;
        public nint Reserved;
        public uint Offset;
        public fixed long Reserved2[8];
    }

    public static void Check(int r)
    {
        if (r != 0) throw new IOException(r == 4 ? "MIDI device is in use by another application." : $"MIDI error {r}.");
    }

    public static string[] Names(bool input)
    {
        var names = new string[input ? midiInGetNumDevs() : midiOutGetNumDevs()];
        byte* caps = stackalloc byte[84];
        for (int i = 0; i < names.Length; i++)
        {
            new Span<byte>(caps, 84).Clear();
            _ = input ? midiInGetDevCapsW((nuint)i, caps, 76) : midiOutGetDevCapsW((nuint)i, caps, 84);
            names[i] = new string((char*)(caps + 8));   // szPname follows wMid, wPid, vDriverVersion
        }
        return names;
    }

    public static int Open(string name, bool input)
    {
        int i = Array.IndexOf(Names(input), name);
        return i >= 0 ? i : throw new IOException($"MIDI {(input ? "input" : "output")} \"{name}\" is not connected.");
    }

    public static string? Gone(string name, bool input) =>
        Array.IndexOf(Names(input), name) < 0 ? $"MIDI {(input ? "input" : "output")} \"{name}\" disconnected." : null;
}

public sealed unsafe class MidiInput : IDisposable
{
    const int Buffers = 4, Size = 256;
    readonly Action<int, double> onShort;
    readonly SysExHandler onSysEx;
    readonly string name;
    GCHandle self;
    nint h;
    WinMM.MidiHdr* hdrs;
    volatile bool closing;
    volatile string? error;

    /// <summary>Why the port is dead, or null. Polls the device list, so call it about once a second, not per frame.</summary>
    public string? Fault => error ?? WinMM.Gone(name, input: true);

    public MidiInput(string name, Action<int, double> onShort, SysExHandler onSysEx)
    {
        int id = WinMM.Open(name, input: true);
        this.name = name;
        this.onShort = onShort;
        this.onSysEx = onSysEx;
        self = GCHandle.Alloc(this);
        WinMM.Check(WinMM.midiInOpen(out h, (uint)id, (nint)(delegate* unmanaged<nint, uint, nint, nint, nint, void>)&Callback,
            GCHandle.ToIntPtr(self), 0x30000 /* CALLBACK_FUNCTION */));
        hdrs = (WinMM.MidiHdr*)NativeMemory.AllocZeroed((nuint)(Buffers * sizeof(WinMM.MidiHdr)));
        for (int i = 0; i < Buffers; i++)
        {
            hdrs[i].Data = (byte*)NativeMemory.Alloc(Size);
            hdrs[i].Length = Size;
            WinMM.midiInPrepareHeader(h, &hdrs[i], (uint)sizeof(WinMM.MidiHdr));
            WinMM.midiInAddBuffer(h, &hdrs[i], (uint)sizeof(WinMM.MidiHdr));
        }
        WinMM.Check(WinMM.midiInStart(h));
    }

    [UnmanagedCallersOnly]
    static void Callback(nint h, uint msg, nint instance, nint p1, nint p2)
    {
        double t = Clock.Now;
        try
        {
            if (GCHandle.FromIntPtr(instance).Target is not MidiInput m) return;
            if (msg == 0x3C3) m.onShort((int)p1, t);                       // MIM_DATA
            else if (msg == 0x3C2 && !m.closing) m.error ??= "MIDI input closed by the driver.";   // MIM_CLOSE
            else if (msg == 0x3C4 && !m.closing)                            // MIM_LONGDATA
            {
                var hdr = (WinMM.MidiHdr*)p1;
                if (hdr->Recorded > 0) m.onSysEx(new ReadOnlySpan<byte>(hdr->Data, (int)hdr->Recorded), t);
                WinMM.midiInAddBuffer(h, hdr, (uint)sizeof(WinMM.MidiHdr));
            }
        }
        catch { /* never let an exception unwind into the driver */ }
    }

    public void Dispose()
    {
        if (h == 0) return;
        closing = true;
        WinMM.midiInReset(h);
        for (int i = 0; i < Buffers; i++)
        {
            WinMM.midiInUnprepareHeader(h, &hdrs[i], (uint)sizeof(WinMM.MidiHdr));
            NativeMemory.Free(hdrs[i].Data);
        }
        WinMM.midiInClose(h);
        NativeMemory.Free(hdrs);
        self.Free();
        h = 0;
    }
}

public sealed unsafe class MidiOutput : IDisposable
{
    nint h;
    readonly string name;
    readonly WinMM.MidiHdr* hdr = (WinMM.MidiHdr*)NativeMemory.AllocZeroed((nuint)sizeof(WinMM.MidiHdr));
    readonly byte* data = (byte*)NativeMemory.Alloc(64);
    volatile string? error;

    /// <summary>Why the port is dead, or null. Polls the device list, so call it about once a second, not per frame.</summary>
    public string? Fault => error ?? WinMM.Gone(name, input: false);

    public MidiOutput(string name)
    {
        this.name = name;
        WinMM.Check(WinMM.midiOutOpen(out h, (uint)WinMM.Open(name, input: false), 0, 0, 0));
    }

    public void Send(int msg)
    {
        if (WinMM.midiOutShortMsg(h, (uint)msg) != 0) error ??= "MIDI output stopped accepting messages.";
    }

    public void SendSysEx(ReadOnlySpan<byte> msg)
    {
        msg.CopyTo(new Span<byte>(data, 64));
        *hdr = default;
        hdr->Data = data;
        hdr->Length = (uint)msg.Length;
        WinMM.midiOutPrepareHeader(h, hdr, (uint)sizeof(WinMM.MidiHdr));
        if (WinMM.midiOutLongMsg(h, hdr, (uint)sizeof(WinMM.MidiHdr)) != 0) error ??= "MIDI output stopped accepting messages.";
        for (var until = Clock.Now + 0.1; (Volatile.Read(ref hdr->Flags) & 1) == 0 && Clock.Now < until;) Thread.Yield(); // MHDR_DONE
        WinMM.midiOutUnprepareHeader(h, hdr, (uint)sizeof(WinMM.MidiHdr));
    }

    public void Dispose()
    {
        if (h == 0) return;
        WinMM.midiOutReset(h);
        WinMM.midiOutClose(h);
        NativeMemory.Free(hdr);
        NativeMemory.Free(data);
        h = 0;
    }
}

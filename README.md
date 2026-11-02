# LTC ⇄ MTC

A small Windows app that converts **MIDI timecode (MTC) to linear timecode (LTC)** on an audio output, or **LTC from an
audio input to MTC** on a MIDI output. WinForms on .NET 10, with WASAPI and ASIO audio.

## Download

Every push builds two Windows x64 executables. Find them under **Actions → build → Artifacts**, or on the Releases page
for tagged versions:

| File | Size | Needs |
|---|---|---|
| `LtcMtc-win-x64` | ~50 MB | nothing |
| `LtcMtc-win-x64-framework-dependent` | ~1.5 MB | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |

To publish a release, push a tag: `git tag v0.1.0 && git push origin v0.1.0`.

## Using it

Pick a mode, the MIDI device and the audio device (WASAPI or ASIO), then press **Start**. For LTC → MTC the audio input list also has a
**Loopback** entry for every output device: it decodes whatever that output is playing, e.g. LTC from a DAW or video
player on the same PC, without a cable. Settings are saved to
`%APPDATA%\LtcMtc\settings.json`. Turn on **Start on launch** for unattended machines.

| Setting | Meaning |
|---|---|
| Channel | Which audio channel carries LTC. *All* duplicates it to every output channel. |
| LTC frame rate | *Follow MTC*, or force an output rate (the timeline is then converted by time). |
| LTC level | Output peak level. −12 dBFS to −18 dBFS suits most line inputs. |
| Send MTC / MMC | LTC → MTC only. MTC sends quarter-frames, plus a full-frame message on start and jumps. MMC sends transport commands: Locate + Play when LTC starts or jumps, Stop when it stops (after the freewheel time) or when you press Stop. Use both for gear that wants MMC to roll and MTC to stay in sync. |
| MMC device ID | 127 addresses all devices. |
| MMC play / locate / rate bits | Compatibility switches. *Play* (default) or *Deferred Play*. *Locate before play* (default on). *Frame-rate bits in locate hours* (default on, per the MMC spec); turn it off if a receiver shows hours such as 32 or 55. |
| Timecode offset | Added to the converted timecode, e.g. `+01:00:00:00` or `-00:00:00:05`. Can be changed while running. |
| Fine offset | Shifts the output in time against the input. Positive means the output runs ahead. Use it to cancel device latency. Can be changed while running: small changes are slewed in without a jump. |
| Freewheel | How long output continues after input stops or drops out. |
| WASAPI buffer | Requested buffer size. ASIO uses the buffer size set in the driver's own control panel. |
| WASAPI low-latency mode | Uses IAudioClient3 for very short buffers. Off by default: LTC doesn't need it (each frame is scheduled by timestamp, so latency is compensated rather than minimised), and some drivers, Bluetooth in particular, accept it but play silence. |

**Device loss.** Devices are saved by name (MIDI), endpoint ID (WASAPI) or driver name (ASIO). A saved device that
isn't plugged in stays selected, marked *not connected*, rather than being replaced by another one. While running, the
app checks once a second for a device that has gone away: the driver reports an error, the audio callbacks stop for a
second, or the MIDI port leaves the system list. If one has, it shows *Reconnecting* and retries every second with the
same devices until they are back. With *Start on launch* it also waits for devices that aren't connected yet.

Stats show the lock state, incoming timecode and rate, input jitter, the clock correction the PLL is applying, signal
level, counters for errors, jumps and relocates, and the actual audio format and latency.

Bluetooth outputs aren't suitable for LTC: some drop it entirely, and the codec's variable 100–200 ms delay would
make it useless for sync anyway. Use a wired output.

Windows has no built-in virtual MIDI ports. To exchange MTC with a DAW on the same PC, use
[loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html) or Windows MIDI Services (Windows 11).

## How it works

* **MTC → LTC.** The MIDI callback rebuilds the timecode from each set of eight quarter-frames and records when that
  frame started. At every LTC frame boundary the audio thread works out where the timeline will be by the time that
  sample is heard. Small differences are steered out by adjusting the encoder speed (a PI loop, at most ±0.5 %). A
  difference of more than a frame jumps straight to the new position. Full-frame SysEx messages relocate.
* **LTC → MTC.** The decoder finds zero crossings with hysteresis and sub-sample interpolation, tracks the bit length
  adaptively, and stamps each frame with the exact sample where it ends. WASAPI stamps every buffer with the QPC
  clock, so that sample maps straight to wall-clock time. A high-priority thread on a high-resolution waitable timer
  sends each quarter-frame when it falls due. A full-frame message goes out on lock and on every jump.
* MIDI uses `winmm` directly, so nothing is allocated per message. Audio uses NAudio's `WasapiPlayer` /
  `WasapiRecorder` (zero-copy, MMCSS "Pro Audio" priority) and `AsioDevice`.
* Supported rates: 24, 25, 29.97 drop-frame and 30 fps, the four rates MTC can express.

## Layout

```
src/LtcMtc/Core/       Timecode, LTC encoder/decoder, MTC reader/writer. Platform-independent and unit-tested.
src/LtcMtc/Windows/    winmm MIDI, WASAPI/ASIO audio, the two engines
src/LtcMtc/Ui/         the window: dark theme with a red accent
tests/LtcMtc.Tests/    xunit tests for Core (runs on any OS)
```

Build locally with the .NET 10 SDK: `dotnet run --project src/LtcMtc` (Windows), or `dotnet test` (anywhere).

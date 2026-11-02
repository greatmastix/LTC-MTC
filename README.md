# LTC ⇄ MTC

A small Windows timecode tool: convert between **MIDI timecode (MTC/MMC)**, **linear timecode (LTC)** and **Art-Net
timecode**, generate them, jam-sync to them, or just read and display one. WinForms on .NET 10, with WASAPI and ASIO audio.

## Download

Every push builds two Windows x64 executables. Find them under **Actions → build → Artifacts**, or on the Releases page
for tagged versions:

| File | Size | Needs |
|---|---|---|
| `LtcMtc-win-x64` | ~45 MB zipped (113 MB unpacked) | nothing |
| `LtcMtc-win-x64-framework-dependent` | ~1.5 MB | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |

Prefer the **Releases** page: a release is the same file for everyone, so browsers and SmartScreen learn to trust it,
whereas every Actions build is a brand-new unknown file. The exe isn't compressed or self-extracting, which antivirus
heuristics tend to flag.

To publish a release, either draft one on GitHub (**Releases → Draft a new release**, new tag such as `v0.3.0`, target
`main`), or push a tag: `git tag v0.3.0 && git push origin v0.3.0`. Either way the build takes its version from the tag
and attaches both zips to the release.

## Using it

Pick a **source** and any **outputs**, then press **Connect**. Connect only opens the devices and sockets (a MIDI port, for one, can only be open in one program at a time); conversion starts when the input runs:

| Source | Outputs | Result |
|---|---|---|
| MTC (MIDI in) | LTC | MTC → LTC |
| LTC (audio in) | MIDI | LTC → MTC and/or MMC |
| Generator | LTC and/or MIDI | a timecode generator, with Play / Pause and Stop (back to the start time); Space plays and pauses. It starts paused, and its transport works with or without a connection; connecting while paused cues MIDI and Art-Net receivers to its position |
| MTC or LTC | none | a reader: display only (with MTC, MMC commands and locates are shown too) |
| LTC | LTC | regenerated, clean LTC |
| Art-Net (network) | any | Art-Net timecode in (UDP 6454, shared with other Art-Net software on the PC) |
| any | Art-Net | Art-Net timecode out, one packet per frame, to a network's broadcast address or one IP |

**Jam-sync** (MTC, LTC or Art-Net source): follow the input while it runs, and keep running on our own clock from the
last position if it stops, until it comes back (output follows it again) or you disconnect.

**Log** in the header lists what happened and when: start/stop, lock, freewheel, jam-sync, input lost, jumps,
relocates, MMC in/out, device loss and reconnects. Each entry is also appended to `%APPDATA%\LtcMtc\logs\<date>.csv`, and
the window can export the list as CSV.

**Minimize to tray** puts the app in the notification area when minimized: the tooltip shows the state and timecode, the
menu has Show, Connect / Disconnect and Exit. With **Connect on launch** as well, it starts straight in the tray.

**Display** in the header opens a separate window with the timecode as large as the window allows and the state and a
few stats underneath: double-click or F11 for full screen, Esc to leave it, right-click for always-on-top.

The LTC audio input list also has a **Loopback** entry for every output device: it decodes whatever that output is
playing, e.g. LTC from a DAW or video player on the same PC, without a cable. Settings are saved to
`%APPDATA%\LtcMtc\settings.json`; settings from earlier versions carry over. Turn on **Connect on launch** for unattended
machines.

| Setting | Meaning |
|---|---|
| Channel | Which audio channel carries LTC. *All* duplicates it to every output channel. |
| Start time / Frame rate | Generator only. The start time can be changed while running; Stop returns to it. |
| LTC frame rate | *Follow source*, or force an output rate (the timeline is then converted by time). |
| LTC level | Output peak level. −12 dBFS to −18 dBFS suits most line inputs. |
| Send MTC / MMC | MIDI out. MTC sends quarter-frames, plus a full-frame message on start and jumps. MMC sends transport commands: Locate + Play when the source starts or jumps, Stop when it stops (after the freewheel time) or when you disconnect. With the generator, its Stop and a locate while paused also cue receivers with a full-frame / MMC locate. Use both for gear that wants MMC to roll and MTC to stay in sync. |
| MMC device ID | 127 addresses all devices. |
| MMC play / locate / rate bits | Compatibility switches. *Play* (default) or *Deferred Play*. *Locate before play* (default on). *Frame-rate bits in locate hours* (default on, per the MMC spec); turn it off if a receiver shows hours such as 32 or 55. |
| Timecode offset | Added to the converted timecode, e.g. `+01:00:00:00` or `-00:00:00:05`. Can be changed while running. |
| Fine offset | Shifts the output in time against the input. Positive means the output runs ahead. Use it to cancel device latency. Can be changed while running: small changes are slewed in without a jump. |
| Freewheel | How long output continues after input stops or drops out, on top of the normal gap between frames. 0 stops as soon as the input stops. |
| WASAPI buffer | Requested buffer size. ASIO uses the buffer size set in the driver's own control panel. |
| WASAPI low-latency mode | Uses IAudioClient3 for very short buffers. Off by default: LTC doesn't need it (each frame is scheduled by timestamp, so latency is compensated rather than minimised), and some drivers, Bluetooth in particular, accept it but play silence. |

**Device loss.** Devices are saved by name (MIDI), endpoint ID (WASAPI) or driver name (ASIO). A saved device that
isn't plugged in stays selected, marked *not connected*, rather than being replaced by another one. While running, the
app checks once a second for a device that has gone away: the driver reports an error, the audio callbacks stop for a
second, or the MIDI port leaves the system list. If one has, it shows *Reconnecting* and retries every second with the
same devices until they are back. With *Connect on launch* it also waits for devices that aren't connected yet.

Stats show the lock state, incoming timecode and rate, input jitter, the clock correction the PLL is applying, signal
level, counters for errors, jumps and relocates, and the actual audio format and latency.

Bluetooth outputs aren't suitable for LTC: some drop it entirely, and the codec's variable 100–200 ms delay would
make it useless for sync anyway. Use a wired output.

Windows has no built-in virtual MIDI ports. To exchange MTC with a DAW on the same PC, use
[loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html) or Windows MIDI Services (Windows 11).

## How it works

* **Sources and outputs.** A source (MTC in, LTC in, or the generator) publishes an anchor: "frame *n* started at
  time *t*", in one clock (QPC) for everything. Each output reads it from its own real-time thread.
* **MTC in → LTC out.** The MIDI callback rebuilds the timecode from each set of eight quarter-frames and records when that
  frame started. At every LTC frame boundary the audio thread works out where the timeline will be by the time that
  sample is heard. Small differences are steered out by adjusting the encoder speed (a PI loop, at most ±0.5 %). A
  difference of more than a frame jumps straight to the new position. Full-frame SysEx messages relocate.
* **LTC in → MTC out.** The decoder finds zero crossings with hysteresis and sub-sample interpolation, tracks the bit length
  adaptively, and stamps each frame with the exact sample where it ends. WASAPI stamps every buffer with the QPC
  clock, so that sample maps straight to wall-clock time. A high-priority thread on a high-resolution waitable timer
  sends each quarter-frame when it falls due. A full-frame message goes out on lock and on every jump.
* MIDI uses `winmm` directly, so nothing is allocated per message. Audio uses NAudio's `WasapiPlayer` /
  `WasapiRecorder` (zero-copy, MMCSS "Pro Audio" priority) and `AsioDevice`.
* Supported rates: 24, 25, 29.97 drop-frame and 30 fps, the four rates MTC can express.

## Layout

```
src/LtcMtc/Core/       Timecode, LTC encoder/decoder, MTC/MMC reader/writer, generator clock. Platform-independent, unit-tested.
src/LtcMtc/Windows/    winmm MIDI, WASAPI/ASIO audio, the sources and outputs
src/LtcMtc/Ui/         the window: dark theme with a red accent
tests/LtcMtc.Tests/    xunit tests for Core (runs on any OS)
```

Build locally with the .NET 10 SDK: `dotnet run --project src/LtcMtc` (Windows), or `dotnet test` (anywhere).

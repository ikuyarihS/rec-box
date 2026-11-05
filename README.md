# RecBox

Record your microphone and your PC's sound with one button. Stop, and you get a folder of clean WAV files plus a
`session.json` that explains them, ready to hand to an LLM or a transcription tool later. Optional: a free, offline
Whisper transcript after each recording.

## Use it

1. Install: run `RecBox-win-Setup.exe`. It installs for the current user only (no admin prompt) and opens RecBox.
2. Press the big red button, or **Space**. Press it again to stop.
3. Click **Open** to see the files, or the copy button to copy the folder path.

Pick what to record under **Record from**:

| Source | What it records | Device list |
|---|---|---|
| Microphone | your voice | any input; "Default" follows Windows and switches automatically when Windows switches |
| System audio | everything the PC plays: the other side of a Teams/Meet/Zoom call, videos | any output; same "Default" option |

Turn either one off with its switch. The level bars show what is coming in while you record. If the mic stays near
silence for about 5 seconds, RecBox warns "Mic is very quiet".

While recording, RecBox unmutes the microphone it captures and raises its Windows volume to 100% and its hardware
"Microphone Boost" (when the device has one) to the maximum. Everything goes back when the recording stops, when it
moves to another device, and when RecBox closes; after a crash, at the next start, or for a mic that is unplugged or not ready then, at the first start it can be reached. Each change is in the log. Turn it
off with **Boost mic to maximum while recording** in Settings.

If you plug in a headset or Windows changes the default device mid-recording, RecBox keeps recording on the new
device (when the device is set to "Default"). If a device is unplugged, it reopens it or falls back to the
default. Each switch is listed in `session.json`.

## What you get

One folder per recording, named by start time, under `Documents\RecBox` (change it in Settings):

```
2026-11-03_14-05-22/
  mic.wav            your microphone, raw, full quality (device rate, 16-bit)
  mic_16k.wav        same, cleaned up, 16 kHz mono: what speech models expect, about 2 MB per minute
  speaker.wav        system audio, raw, full quality
  speaker_16k.wav    same, 16 kHz mono
  mixed.wav          both together, raw, 48 kHz stereo (when both sources are on)
  mixed_16k.wav      both together, cleaned up and balanced, 16 kHz mono
  session.json       start and end times, length, devices, device switches, what each file is, what was processed
  transcript.md      only with transcription on: "[00:42] You: ..." / "[00:45] Others: ..."
  transcript.srt     same transcript as subtitles
```

All tracks start at the same moment and have the same length, so time 00:42 is the same instant in every file.
Silence is kept as silence (system audio sends nothing while nothing plays; RecBox fills those gaps).
The 16 kHz copies and the mixed track can be turned off in Settings.

**Clean up speech copies** (Settings, on by default) applies to `mic_16k.wav` and `mixed_16k.wav`: an 80 Hz high-pass
for hum, a level of about -20 dBFS measured on what stands out over the room noise and not the pauses (at most +30 dB), and a limiter at -1 dBFS
so nothing clips. In `mixed_16k.wav` the mic and the system audio each get their own level, with the system audio 3 dB
under the mic, so a quiet voice is not buried. `speechProcessing` in `session.json` lists what each file got. A silent or room-noise-only
track, or one that could not be processed, keeps the plain copy and says so there.

## Transcription (optional, off by default)

Settings, Transcription, turn on "Transcribe after each recording", pick a model, press **Download** once.
After every recording RecBox writes `transcript.md` and `transcript.srt`. Your voice is labelled **You**,
system audio **Others**. "Translate to English" writes the transcript in English whatever was spoken.

| Model | Download | Notes |
|---|---|---|
| Base | 60 MB | default; fine for clear speech |
| Small | 190 MB | more accurate |
| Large v3 Turbo | 574 MB | most accurate; best with a graphics card |

It runs on this PC with [Whisper.net](https://github.com/sandrohanea/whisper.net) (whisper.cpp): free, offline,
nothing is uploaded. It uses the graphics card through Vulkan when it can and the processor otherwise; turn off
"Use the graphics card" if a graphics driver misbehaves.

Measured on the build PC with Base (Vulkan): a 9 s clip took 15.2 s including loading the model, peak memory
489 MB for the whole process. Small and Large v3 Turbo were not measured; their model files (190 MB and 574 MB)
are well under a 2 GB graphics card or 8 GB of RAM, but check on the target machine.

Recording never depends on transcription: with no model, no graphics driver or a failed load, the recording is
saved as usual and `session.json` says why there is no transcript (`model-missing` or `failed` with the reason).
Transcription starts only after the files are saved.

Whisper runs in a separate worker process (`RecBox.exe --transcribe-worker ...`). If the speech engine crashes
(a processor without AVX/AVX2, a broken graphics driver, out of memory), only the worker ends: RecBox keeps
running and that transcript is `failed` in `session.json` with the worker's `exitCode` and a readable reason, for
example `the processor lacks an instruction the speech engine needs (AVX/AVX2) (worker exit code 0xC000001D)`.

- RecBox checks the processor first: with AVX, AVX2 and FMA it tries Vulkan (graphics card), then the normal CPU
  build; without them it uses only the no-AVX CPU build.
- If the graphics card attempt fails or crashes, RecBox retries once on the processor and turns "Use the graphics
  card" off in Settings (with the reason), so the next transcripts go straight to the processor. Turn it back on
  to try again.
- Before loading a model RecBox checks free memory (about twice the model file plus 300 MB) and refuses with a
  clear message instead of crashing.

## Updates

RecBox checks GitHub Releases of [ikuyarihS/rec-box](https://github.com/ikuyarihS/rec-box) when it starts (switch
in Settings), downloads a newer version in the background and shows "Update ready". **Restart to update** finishes
it. Updates are deltas, usually far smaller than the installer. This works for copies installed with
`RecBox-win-Setup.exe` and for the unzipped `RecBox-win-Portable.zip` (Velopack updates the portable folder in
place). A copy Velopack does not manage (for example a bare `dotnet publish` folder) shows "New version X available"
with a link to the release page instead.

## Cut a release (for whoever ships it)

Releases are built and published by GitHub Actions (`.github/workflows/release.yml`) when a version tag is pushed:

```powershell
git checkout main
git pull
git tag v1.2.0            # the next version: v<major>.<minor>.<patch>
git push origin v1.2.0
```

The workflow builds on `windows-latest` with .NET 8, downloads the previous release (so Velopack makes a delta),
runs the tests, runs `build.ps1 -Version 1.2.0`, and uploads `RecBox-win-Setup.exe`, `RecBox-win-Portable.zip` and
the update feed (`releases.win.json`, `*.nupkg`) as release `v1.2.0` with the repo's `GITHUB_TOKEN`. The version
comes from the tag only, so there is nothing else to bump. Every running copy sees the update on its next start or
on **Check now**.

### Build locally

Needs the .NET 8 SDK on Windows.

```powershell
./build.ps1 -Version 1.2.0
```

- `Releases/RecBox-win-Setup.exe`: the one file people install.
- `Releases/RecBox-win-Portable.zip`: runs without installing; updates itself too.
- `Releases/releases.win.json` and `*.nupkg`: the update feed.

The update feed is baked into the build by `-UpdateUrl` (default `https://github.com/ikuyarihS/rec-box`);
`UpdateUrl` in `settings.json` overrides it. To host the feed elsewhere, copy the `Releases` folder to any static
web folder and build with `-UpdateUrl https://your.host/recbox/`. Uploading by hand:
`dotnet vpk upload github "--repoUrl=https://github.com/ikuyarihS/rec-box" "--token=<token>" "--outputDir=Releases" --publish "--tag=v1.2.0"`
(the `--opt=value` form avoids an argument-splitting problem in Windows PowerShell).

Installer and updates are made with [Velopack](https://velopack.io). Builds are not code-signed, so Windows
SmartScreen may ask "Run anyway" on the first install.

Set `RECBOX_ARTIFACTS` to put build output on another drive.

## Why a desktop app and not UWP

RecBox records system audio with WASAPI loopback capture of the whole playback mix (NAudio's
`WasapiLoopbackCapture`), a Win32 desktop audio API. It is therefore a regular .NET 8 WPF desktop app, styled with
[WPF-UI](https://github.com/lepoco/wpfui) to look like a Windows 11 app (Mica background, Fluent controls, follows the
light or dark system theme). A desktop app also installs from a plain Setup.exe with no Store or certificate setup.

## Where things live

| What | Where |
|---|---|
| Recordings | `Documents\RecBox` (Settings, Files) |
| Settings | `%AppData%\RecBox\settings.json` |
| Whisper models | `%AppData%\RecBox\models` |
| Log | `%AppData%\RecBox\logs\recbox.log`: start (version, Windows, CPU with AVX/AVX2, RAM, graphics cards), device choices and switches, record start/stop, saved files, transcription start/end with the runtime and the worker exit code, update checks, errors. Rolls at 1 MB, 5 files kept |
| Worker log | `%AppData%\RecBox\logs\worker.log`: everything the transcription worker and whisper.cpp print (rolls the same way) |
| Crash files | `%AppData%\RecBox\logs\crash-<time>.txt`: stack trace and PC details of any unhandled error (newest 10 kept) |
| Diagnostics | Settings, Help and diagnostics: **Open log folder**, or **Copy diagnostics** (one zip of logs, crash files, settings and PC details, never audio, copied to the clipboard to paste into a chat or email) |
| Boost restore file | `%AppData%\RecBox\mic-boost-<pid>.json`: the original mic values while recording, so a crash can be undone at the next start; gone after each recording, except for a value that could not be restored yet, which stays (also as `mic-boost-left-<id>.json`) until a start can restore it |
| App | `%LocalAppData%\RecBox` (installed by Setup.exe) |

Set the environment variable `RECBOX_HOME` to keep settings, models, logs and recordings in one other folder.

## Known limits

- Transcription runs after a recording, not live.
- A device switch leaves a gap that is filled with silence. When Windows changes the default device the gap was
  about 0.3 s in tests. When a chosen device (not "Default") is lost, RecBox retries it first, and the gap was
  1.8 to 2 s in tests; if it does not come back, RecBox moves to the Windows default about 3 s after the loss.
- Closing the window while it is saving waits for the save to finish, then closes. A transcript that was still
  running is marked `interrupted` in `session.json`.
- While recording, each source is written to temporary part files whose WAV header is refreshed every 5 s, so a
  crash or power loss leaves playable parts in the folder; the final merge happens when you press Stop or close
  the window.
- Records the whole system mix, not a single app.
- A mic volume you change by hand during a recording is put back to its value from before the recording.
- A hardware boost is found by the word "boost" in the driver's control name; a driver that names it otherwise only
  gets the volume raise.
- Windows 10 or 11, x64.

## Changelog

- 1.0.1: Boosts the mic to maximum while recording and restores it afterwards, cleans up and balances the 16 kHz
  copies for speech models, reads the level meters in dB for every capture format, and warns "Mic is very quiet".

## Project layout

```
src/RecBox.Core   capture (NAudio WASAPI), device watching, segment merge, 16 kHz copies, mix, session.json, Whisper
src/RecBox        the WPF window (WPF-UI), settings, Velopack updates
build.ps1         publish + installer + update feed
.github/workflows/release.yml   on a v* tag: build, pack, publish the GitHub release
```

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

Turn either one off with its switch. The level bars show what is coming in before and while you record.

If you plug in a headset or Windows changes the default device mid-recording, RecBox keeps recording on the new
device (when the device is set to "Default"). If a device is unplugged, it reopens it or falls back to the
default. Each switch is listed in `session.json`.

## What you get

One folder per recording, named by start time, under `Documents\RecBox` (change it in Settings):

```
2026-11-03_14-05-22/
  mic.wav            your microphone, full quality (device rate, 16-bit)
  mic_16k.wav        same, 16 kHz mono: what speech models expect, about 2 MB per minute
  speaker.wav        system audio, full quality
  speaker_16k.wav    same, 16 kHz mono
  mixed.wav          both together, 48 kHz stereo (when both sources are on)
  mixed_16k.wav      both together, 16 kHz mono
  session.json       start and end times, length, devices, device switches, and what each file is
  transcript.md      only with transcription on: "[00:42] You: ..." / "[00:45] Others: ..."
  transcript.srt     same transcript as subtitles
```

All tracks start at the same moment and have the same length, so time 00:42 is the same instant in every file.
Silence is kept as silence (system audio sends nothing while nothing plays; RecBox fills those gaps).
The 16 kHz copies and the mixed track can be turned off in Settings.

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

## Updates

Installed copies check for updates when they start (switch in Settings), download in the background and show
"Update ready". **Restart to update** finishes it. Updates are deltas, usually far smaller than the installer.

## Build and publish (for whoever ships it)

Needs the .NET 8 SDK on Windows.

```powershell
./build.ps1 -Version 1.0.0 -UpdateUrl https://github.com/<you>/recbox
```

- `Releases/RecBox-win-Setup.exe`: the one file people install.
- `Releases/RecBox-win-Portable.zip`: runs without installing (no auto-update).
- `Releases/releases.win.json` and `*.nupkg`: the update feed.

To ship an update, raise the version, run `build.ps1` again with the same `Releases` folder (so it can make a
delta), then upload:

- GitHub: `dotnet vpk upload github "--repoUrl=https://github.com/<you>/recbox" "--token=<token>" --publish "--releaseName=1.1.0" "--tag=v1.1.0"`
  (the `--opt=value` form avoids an argument-splitting problem in Windows PowerShell)
- or copy the `Releases` folder to any static web folder and build with `-UpdateUrl https://your.host/recbox/`.

The update feed is baked into the build by `-UpdateUrl`; `UpdateUrl` in `settings.json` overrides it.
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
| Log | `%AppData%\RecBox\logs\recbox.log` (device switches, saved files, removed temporary files, errors) |
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
- Windows 10 or 11, x64.

## Project layout

```
src/RecBox.Core   capture (NAudio WASAPI), device watching, segment merge, 16 kHz copies, mix, session.json, Whisper
src/RecBox        the WPF window (WPF-UI), settings, Velopack updates
build.ps1         publish + installer + update feed
```

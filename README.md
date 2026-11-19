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
  mixed_16k.wav      both together, cleaned up, 16 kHz mono
  session.json       start and end times, length, devices, device switches, what each file is, what was processed
  transcript.md      only with transcription on: "[00:42] You: ..." / "[00:45] Others: ..."
  transcript.srt     same transcript as subtitles
```

All tracks start at the same moment and have the same length, so time 00:42 is the same instant in every file.
Silence is kept as silence (system audio sends nothing while nothing plays; RecBox fills those gaps).
The 16 kHz copies and the mixed track can be turned off in Settings.

**Clean up speech copies** (Settings, on by default) applies to `mic_16k.wav` and `mixed_16k.wav`: an 80 Hz high-pass
for hum, then one fixed gain that brings the peak to -1 dBFS. The gain is between 0 and +12 dB, so a file is never turned
down and never raised by more than 12 dB, and it never clips. `mixed_16k.wav` sums the mic and the system audio, each
filtered and raised this way, then runs a limiter at -1 dBFS. `speechProcessing` in `session.json` lists the gain each
file got. A completely silent file, or one that could not be processed, keeps the plain copy and says so there.

## Transcription (optional, off by default)

Settings, Transcription, turn on "Transcribe after each recording", pick a model, press **Download** once.
After every recording RecBox writes `transcript.md` and `transcript.srt`. Your voice is labelled **You**,
system audio **Others**. "Translate to English" writes the transcript in English whatever was spoken.

| Model | Download | Notes |
|---|---|---|
| Auto | - | default; picks the model below for this PC (see the table after this one) |
| Tiny | 31 MB | English only in practice; Auto uses it only when the language is set to English |
| Base | 60 MB | clear English speech; Auto uses it only for English |
| Small | 190 MB | the smallest model good enough for Vietnamese and other languages |
| Medium | 539 MB | more accurate than Small; live text on strong processors |
| Large v3 Turbo | 574 MB | most accurate; best with a graphics card |

### What to expect on your PC

**Auto** measures the PC each time: free and total RAM, the graphics card's own memory (DXGI), physical processor
cores and AVX2. It picks for the full transcript and for live text separately, and logs the pick and why
(`model choice: Auto picked Large v3 Turbo for the transcript: ...` in `recbox.log`).

| PC | Auto, full transcript | Auto, live text | Runs on | Speed |
|---|---|---|---|---|
| Separate graphics card with 1.8 GB or more of its own memory (RTX 3060 6 GB, GTX 860M 2 GB), 8 GB RAM | Large v3 Turbo | Large v3 Turbo | that card (Vulkan); processor if Vulkan fails | measured on an RTX 3060 Laptop (replay below): live text 3.4 s behind (median), 10 s (95 %), never skipped; transcript of an 18 min meeting in 6.9 min. A GTX 860M is not measured: it catches up by decoding queued speech together |
| No usable card, 8 or more cores and 16 GB RAM | Large v3 Turbo | Medium | processor | estimate: transcript slower than real time (after Stop); live text catches up when behind |
| No usable card (integrated graphics only), 4 or more cores, 8 GB RAM | Large v3 Turbo | Small | processor | measured as 3 threads of an i7-12800H (replay below): live text 52 s behind (median), 114 s (95 %), skipped 151 s once; transcript (Large v3 Turbo, 30 s windows) of an 18 min meeting in 34.6 min, 1.9 times its length, and a real 4-core laptop is slower |
| 4 GB RAM, or less than 1.5 GB free | Small | Small | processor | slow; Base when the language is English |
| Processor without AVX2 | Small | Small | processor (no-AVX build) | slow; Base when the language is English |

- The graphics card is the separate (discrete) card with the most memory of its own, picked from the Vulkan
  device list and logged (`Vulkan1 = NVIDIA GeForce RTX 3060 ...`): Vulkan lists a laptop's Intel graphics first,
  and RecBox 1.1 ran there by mistake. It is used only when it has room for the model plus 512 MB for Windows
  (Small 1.4 GB, Large v3 Turbo 1.8 GB, Medium 1.9 GB). Integrated graphics share system memory and are no faster
  than the processor, so they are never used. No Vulkan driver means the processor.
- Tiny and Base are never picked for Vietnamese or Auto language: they turned a Vietnamese meeting into noise.
- A graphics card attempt that fails, or that turns out slower than the processor with the same model, turns
  "Use the graphics card" off in Settings with the reason. To compare, the transcript after the first one on the
  card runs once on the processor; after that the faster of the two is kept.
- Auto never uses a larger model than it picked for the PC. If its pick is not downloaded, a smaller downloaded
  model fills in down to Small (Settings says which); otherwise the pick is downloaded.
- Whisper runs below normal priority with one physical core left free, so the recording never waits for it.
- On the graphics card the transcript after Stop cuts speech like live text (Silero, pauses of 0.45 s, pieces of
  at most 8 s); on the processor it decodes windows of up to 30 s (4 times fewer passes).
- Rows marked estimate are not measured; Auto adapts to what it measures, and you can always pick a model yourself.

#### Measured: replay of a real meeting (2026-11-19, 18 min, Vietnamese, both sides)

`RecBox.exe --replay <copy of a recording folder> [--profile pc|cpu] [--no-post | --post-only]` plays `mic_16k.wav` and `speaker_16k.wav` into
live text at recording pace (no window, no audio device) and then runs the transcript, the same code as a
recording. Scored with the evaluation's `score.py` against a faster-whisper large-v3-turbo reference, per 60 s
window: syllable error rate (lower is better), next to the owner's live meeting kit (faster-whisper large-v3-turbo)
on the same meeting.

| Window (s) | Kit live | RecBox live (RTX 3060) | RecBox transcript (RTX 3060) | RecBox live (CPU, Small) | RecBox transcript (CPU, Turbo) | RecBox 1.1 live |
|---|---|---|---|---|---|---|
| 0-60 | 0.224 | 0.265 | 0.220 | 0.448 | 0.386 | 0.641 |
| 180-240 | 0.298 | 0.287 | 0.309 | no line (skipped) | 0.567 | 0.725 |
| 420-480 | 0.403 | 0.199 | 0.299 | 0.312 | 0.307 | 0.762 |
| 700-760 | 0.556 | 0.401 | 0.415 | 0.585 | 0.517 | 0.870 |
| 1000-1060 | 0.270 | 0.213 | 0.242 | no line (after Stop) | 0.332 | 0.720 |

CPU = the profile Auto sees on a 4-core laptop with 8 GB and no usable card, run as 3 threads of an i7-12800H.
The CPU transcript decodes 30 s windows; it is within 0.10 of the kit in 3 of 5 windows and above that in 0-60 and
180-240 (the same 30 s windows failed 2 windows on the RTX 3060 too, which is why the card decodes short pieces;
on the processor short pieces would take about 4 times longer). It took 2075.5 s for the 1084 s meeting.

Whole meeting: Vietnamese lines 97.1 % live and 97.9 % in the transcript on the RTX 3060, 98.6 % live and 99.0 %
in the transcript on the CPU (kit 96.9 %); stock phrases ("See you next time" and the like) 0 everywhere (1.1 live
text had 56, "See you next time!" 25 times); no line repeated more than 3 times (live at most 2, transcript 3, a
short line in 5 s of noise at 14:13); no repeated-word loops. Sources: `summary-pc.json` and `summary-cpu.json`
(fields `live_all`, `transcript_all`, `kit_all`) written by the replay's scoring script beside each replay copy;
the 1.1 counts are the same stock-phrase pattern applied to the evaluation's `live_lines.json` of the original
recording.

### Live text

Press **Live text** at the top of the main window (one click, also while recording). It opens a panel on the right
side of the same window (no extra window in Alt-Tab or the taskbar; RecBox remembers whether it was open and its
width). It shows what is said while you record: **You** (your mic) and **Others** (system audio), each line with
its time. It scrolls along with the newest line; scroll up to read back and it stops following until you go back
down (or press **Jump to latest**). **Copy all** copies the lines; **Translate to English** applies to the next
lines. The top line says how far behind the live text is (`Live`, `~3 s behind`).

- Live text is on when "Transcribe after each recording" is on, unless you turn it off (main screen or the panel).
  Turning it on downloads the models it needs once (and Silero, the speech detector, 0.9 MB).
- Spoken language: **Auto** listens to about the first minute of speech (the other side counts more than your
  mic), picks the language by a vote and keeps it for the recording; or pick one (Vietnamese, English, ...), or
  **Vietnamese + English** (English only where Whisper is sure, p 0.8 or more). The screen says what was picked and
  why; a change applies from the next piece of speech, and the transcript after Stop uses the language at Stop.
  Both are saved in `session.json` and `live-transcript.json`.
- How it works: the captured audio is turned into 16 kHz mono and cut where people pause (Silero voice detection,
  pieces of 2 to 8 s; loudness if Silero cannot run), and each piece goes to one live worker process
  (`RecBox.exe --live-worker`) that keeps the model loaded. If that process crashes it is started again (after 3
  crashes in a minute live text stops and says so).
- Every line goes through the same filters as the transcript: Whisper's tags (`[Music]`) and stock phrases
  ("subscribe to the channel") are dropped, unsure words are marked `[?]`, text in another script is dropped,
  repeats collapse, and a mic line that is the speakers heard by the mic is marked **You (echo?)** (kept).
- Falling behind never switches to a smaller model: 8 s or more behind, the queued speech of one source is decoded
  together (up to 28 s at once, about the cost of one piece); only more than 3 minutes behind does it skip to the
  last minute and write a note. The recording itself is never slowed: live text only gets a copy of the audio.
- When you press Stop, `live-transcript.txt` and `live-transcript.json` (start and end seconds, source `mic`,
  `speaker` or `note`) are saved in the recording's folder. The full `transcript.md` still runs after Stop as
  before; it is the more accurate one.

It runs on this PC with [Whisper.net](https://github.com/sandrohanea/whisper.net) (whisper.cpp): free, offline,
nothing is uploaded. It uses the graphics card through Vulkan when it can and the processor otherwise; turn off
"Use the graphics card" if a graphics driver misbehaves.

Large v3 Turbo on the RTX 3060 (whisper.cpp's own log): 573 MB of weights plus about 450 MB of buffers per
decoder state. Small and Medium were not measured on a graphics card; check on the target machine.

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

- Live text is less accurate than the transcript after Stop (short pieces, no context between them), and a word cut
  at an 8 s piece boundary can be lost. Its times follow the recording clock and can be off by a fraction of a second.
- After a Windows logoff or restart during a recording, the recording is saved first; the live text keeps only
  what was transcribed by then.
- A settings file from 1.0.x keeps the model you had chosen; pick Auto in Settings to let RecBox choose.
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

- 1.2.0: Live text and the transcript reach the meeting kit's quality on a real meeting (replay table above;
  measured on an RTX 3060, not yet on a GTX 860M; on a 4-core processor profile the transcript is near it in 3
  of 5 windows and slower than real time): the separate graphics card is picked by its type and
  memory (never the integrated one), the spoken language is set once per recording (Auto votes over the first
  minute, a fixed language, or Vietnamese + English), Medium is added and Auto picks Large v3 Turbo on a card that
  holds it, never Tiny or Base beyond English. Silero cuts speech; tags, stock phrases, other scripts and repeats
  are filtered, unsure words marked `[?]` and mic echo marked; beam search on the graphics card, no carried-over
  text, temperature fallback. Falling behind decodes queued speech together instead of switching to Tiny. On the
  graphics card the transcript decodes the same short pieces as live text. `--replay` measures a saved recording.
  A log that cannot be written rolls and is capped like recbox.log; diagnostics keeps its 5 newest zips.
  mixed.wav and the 16 kHz copies no longer clip. recbox.log moves to its own file and says why if it cannot be
  written. The main screen holds the per-recording choices (sources, language, transcript, live text, translate)
  with what Auto picked, plain states with a transcript progress bar and Cancel, and live text as a side panel.
- 1.1.0: Runs on lower-end laptops: a Tiny model and an Auto model choice (the new default) that picks by RAM,
  the graphics card's own memory, processor cores and AVX2; the graphics card only when it has room (never
  integrated graphics); one core left free and the worker below normal priority; a hardware table above.
  New Live text screen: what is said while recording, You and Others with times, auto-scroll, Copy all,
  Translate to English, lag shown; saved as live-transcript.txt and .json beside the recording.
- 1.0.1: Boosts the mic to maximum while recording and restores it afterwards, filters hum and peak-normalizes the
  16 kHz copies (at most +12 dB), reads the level meters in dB for every capture format, and warns "Mic is very quiet".

## Project layout

```
src/RecBox.Core   capture (NAudio WASAPI), device watching, segment merge, 16 kHz copies, mix, session.json, Whisper
src/RecBox        the WPF window (WPF-UI), settings, Velopack updates
build.ps1         publish + installer + update feed
.github/workflows/release.yml   on a v* tag: build, pack, publish the GitHub release
```

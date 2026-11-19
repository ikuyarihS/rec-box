using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using RecBox.Core;
using RecBox.Core.Session;
using RecBox.Core.Settings;
using RecBox.Core.Transcription;

namespace RecBox;

/// <summary>
/// Headless proof run (no window, no UI, no audio device): plays a saved recording's mic_16k.wav and
/// speaker_16k.wav into live text at recording pace, exactly as the capture taps would, then runs the transcript
/// after Stop, the same code paths the app uses. Writes live-transcript.txt/.json, transcript.md/.srt and
/// replay.json (model, device, lag) into the folder. Run it on a COPY of a recording.
/// Usage: RecBox.exe --replay &lt;copy dir&gt; [--profile pc|cpu] [--speed 1] [--no-post] [--post-only]
/// --profile cpu: the hardware Auto sees on a low-end laptop (4 cores, 8 GB, no usable graphics card).
/// </summary>
public static class Replay
{
    public static bool IsCall(string[] args) => args.Length > 0 && args[0] == "--replay";

    private static StreamWriter? _out;

    private static void Say(string line)
    {
        var text = $"{DateTime.Now:HH:mm:ss} {line}";
        try { _out?.WriteLine(text); _out?.Flush(); } catch { }
        Log.Info($"replay: {line}");
    }

    public static int Run(string[] args)
    {
        if (args.Length < 2) return 2;
        var dir = Path.GetFullPath(args[1]);
        string Opt(string name, string def)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
        }
        var profile = Opt("--profile", "pc");
        var speed = double.Parse(Opt("--speed", "1"), CultureInfo.InvariantCulture);
        var post = !args.Contains("--no-post");
        var liveOn = !args.Contains("--post-only");
        _out = new StreamWriter(Path.Combine(dir, $"replay-{profile}.log"), append: true, new UTF8Encoding(false));
        try
        {
            return RunAsync(dir, profile, speed, liveOn, post).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Say($"FAILED: {ex}");
            return 1;
        }
        finally
        {
            _out.Dispose();
        }
    }

    private static HardwareProfile Hardware(string profile)
    {
        var real = HardwareProbe.Measure();
        if (profile != "cpu") return real;
        const long gb = 1024L * 1024 * 1024;
        // a GTX 860M-era laptop seen without its graphics card: 4 cores, 8 GB, AVX2
        return new HardwareProfile(8 * gb, Math.Min(real.FreeRam > 0 ? real.FreeRam : 5 * gb, 5 * gb), 4, 8, real.Avx2, null, false);
    }

    private static async Task<int> RunAsync(string dir, string profile, double speed, bool liveOn, bool post)
    {
        var manifest = SessionManifest.Load(dir) ?? throw new InvalidOperationException($"no session.json in {dir}");
        var hw = Hardware(profile);
        var settings = new AppSettings { WhisperModel = "auto", UseGpu = profile != "cpu", Language = Languages.Auto, TranscribeEnabled = true };
        Say($"replay of {manifest.Id} ({manifest.DurationSeconds:0} s), profile {profile}, speed {speed}x | {hw.Describe()}");
        var stats = new Dictionary<string, object?> { ["profile"] = profile, ["speed"] = speed, ["hardware"] = hw.Describe() };
        var language = Languages.Auto;
        // the app downloads the Auto pick before it runs; Plan alone would fall back to a smaller model on disk
        await EnsureAsync(ModelPicker.Pick(hw, TranscriptionUse.Live, settings.UseGpu, settings.Language).Model);
        if (post) await EnsureAsync(ModelPicker.Pick(hw, TranscriptionUse.File, settings.UseGpu, settings.Language).Model);

        if (liveOn)
        {
            var choice = ModelPicker.Plan(settings, hw, TranscriptionUse.Live);
            Say($"live choice: {choice.Reason}");
            await EnsureAsync(choice.Model);
            if (!SileroModel.IsPresent) await SileroModel.DownloadAsync(CancellationToken.None);

            var sw = new Stopwatch();
            Func<TimeSpan> clock = () => TimeSpan.FromTicks((long)(sw.Elapsed.Ticks * speed));
            var live = new LiveTranscriber(new LiveTranscriberOptions
            {
                Choice = choice,
                Language = settings.Language,
                Vad = new VadService(SileroModel.Path),
                Clock = clock,
            });
            live.StatusChanged += s => Say($"live status: {s}");
            live.LanguageChanged += d => Say($"live language: {d}");
            var run = new LiveRun();
            run.Begin(live);
            live.Start();

            using var mic = new WaveFileReader(Path.Combine(dir, "mic_16k.wav"));
            using var spk = new WaveFileReader(Path.Combine(dir, "speaker_16k.wav"));
            var block = mic.WaveFormat.AverageBytesPerSecond / 10; // 100 ms, as the capture taps deliver
            var bufMic = new byte[block];
            var bufSpk = new byte[block];
            sw.Start();
            var lastReport = 0.0;
            for (var k = 1; ; k++)
            {
                var due = TimeSpan.FromSeconds(k * 0.1);
                var wait = (due - clock()).TotalMilliseconds / speed;
                if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait));
                var nm = mic.Read(bufMic, 0, block);
                var ns = spk.Read(bufSpk, 0, block);
                if (nm <= 0 && ns <= 0) break;
                if (nm > 0) live.Feed(LiveSource.Mic, bufMic, nm, mic.WaveFormat);
                if (ns > 0) live.Feed(LiveSource.Speaker, bufSpk, ns, spk.WaveFormat);
                if (due.TotalSeconds - lastReport >= 60)
                {
                    lastReport = due.TotalSeconds;
                    Say($"at {due.TotalSeconds:0} s: {live.Lines.Count} line(s), lag {live.LagSeconds:0.0} s");
                }
            }
            var fed = clock().TotalSeconds;
            // the app's Stop: 3 s grace for the last chunks
            var result = await run.End(TimeSpan.FromSeconds(3))!;
            LiveTranscriptStore.Save(dir, manifest.Id, result, false);
            language = result.LanguageEffective;

            var lags = result.Lines.Where(l => !l.IsNote && l.Emitted is not null)
                .Select(l => (l.Emitted!.Value - l.End).TotalSeconds).OrderBy(x => x).ToArray();
            double Pct(double p) => lags.Length == 0 ? 0 : Math.Round(lags[Math.Min(lags.Length - 1, (int)(p * lags.Length))], 1);
            stats["live"] = new Dictionary<string, object?>
            {
                ["model"] = result.ModelKey,
                ["runtime"] = result.Runtime,
                ["device"] = result.Device,
                ["choice"] = choice.Reason,
                ["languageDetected"] = result.LanguageDetected,
                ["lines"] = result.Lines.Count(l => !l.IsNote),
                ["notes"] = result.Lines.Where(l => l.IsNote).Select(l => l.Text).ToArray(),
                ["echoTagged"] = result.Lines.Count(l => l.Echo),
                ["mergedChunks"] = result.MergedChunks,
                ["skippedSeconds"] = Math.Round(result.SkippedSeconds, 1),
                ["restarts"] = result.Restarts,
                ["maxLag"] = Math.Round(result.MaxLagSeconds, 1),
                ["lagP50"] = Pct(0.5),
                ["lagP95"] = Pct(0.95),
                ["lagMaxLine"] = lags.Length == 0 ? 0 : Math.Round(lags[^1], 1),
                ["fedSeconds"] = Math.Round(fed, 1),
            };
            Say($"live done: {JsonSerializer.Serialize(stats["live"])}");
        }

        if (post)
        {
            var info = await TranscriptionService.TranscribeSessionAsync(dir, manifest, settings,
                new Progress<string>(p => Say($"transcript: {p}")), CancellationToken.None, hardware: hw,
                language: Languages.IsAuto(language) ? null : language);
            stats["transcript"] = new Dictionary<string, object?>
            {
                ["status"] = info.Status,
                ["model"] = info.Model,
                ["runtime"] = info.Runtime,
                ["device"] = info.Device,
                ["language"] = info.Language,
                ["seconds"] = info.Seconds,
                ["error"] = info.Error,
            };
            Say($"transcript done: {JsonSerializer.Serialize(stats["transcript"])}");
        }

        File.WriteAllText(Path.Combine(dir, $"replay-{profile}.json"), JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    private static async Task EnsureAsync(WhisperModelInfo model)
    {
        if (model.IsPresent) return;
        Say($"downloading {model.Name} (once)");
        await TranscriptionService.DownloadAsync(model, null, CancellationToken.None);
    }
}

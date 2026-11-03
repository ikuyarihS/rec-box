using System.Diagnostics;
using System.Globalization;
using System.Text;
using RecBox.Core.Audio;
using RecBox.Core.Session;
using RecBox.Core.Settings;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace RecBox.Core.Transcription;

public sealed record WhisperModelInfo(string Key, string Name, string Detail, GgmlType Type, QuantizationType Quant, long ApproxBytes)
{
    public string FileName => $"ggml-{Key}.bin";
    public string Path => System.IO.Path.Combine(AppPaths.ModelsDir, FileName);
    public bool IsPresent => File.Exists(Path);
}

public static class WhisperModels
{
    public static readonly IReadOnlyList<WhisperModelInfo> All = new[]
    {
        new WhisperModelInfo("base", "Base", "Fastest. 60 MB download, runs on any PC", GgmlType.Base, QuantizationType.Q5_1, 59_707_625),
        new WhisperModelInfo("small", "Small", "More accurate. 190 MB download", GgmlType.Small, QuantizationType.Q5_1, 190_085_487),
        new WhisperModelInfo("large-v3-turbo", "Large v3 Turbo", "Most accurate. 574 MB download, best with a GPU", GgmlType.LargeV3Turbo, QuantizationType.Q5_0, 574_041_195),
    };

    public static WhisperModelInfo Get(string key) => All.FirstOrDefault(m => m.Key == key) ?? All[0];
}

public sealed record TranscriptLine(TimeSpan Start, TimeSpan End, string Speaker, string Text);

/// <summary>
/// Optional local speech-to-text with Whisper (whisper.cpp through Whisper.net).
/// Recording never depends on this: no model, no native runtime or a failed load only sets a status.
/// </summary>
public static class TranscriptionService
{
    public static async Task DownloadAsync(WhisperModelInfo model, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(AppPaths.ModelsDir);
        var tmp = model.Path + ".download";
        Log.Info($"downloading whisper model {model.Key}");
        await using (var src = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(model.Type, model.Quant, ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 16];
            long total = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                total += n;
                progress?.Report(Math.Min(1.0, (double)total / model.ApproxBytes));
            }
        }
        File.Move(tmp, model.Path, overwrite: true);
        progress?.Report(1.0);
        Log.Info($"whisper model {model.Key} saved to {model.Path} ({new FileInfo(model.Path).Length} bytes)");
    }

    /// <summary>
    /// Transcribes each track of a saved session in the worker process and writes transcript.md / transcript.srt
    /// next to it. A worker that crashes (native whisper.cpp) only ends that worker: the transcript is "failed"
    /// with its exit code and the reason. A failed GPU (Vulkan) attempt is retried once on the CPU and the
    /// result says so (GpuFailed), so the caller can remember to stay on the CPU.
    /// </summary>
    public static async Task<TranscriptInfo> TranscribeSessionAsync(string dir, SessionManifest manifest, AppSettings settings,
        IProgress<string>? progress, CancellationToken ct, WorkerRunner? runner = null)
    {
        runner ??= WorkerRunner.Default;
        var model = WhisperModels.Get(settings.WhisperModel);
        var info = new TranscriptInfo
        {
            Status = "pending",
            Model = model.Key,
            Mode = settings.Mode == TranscribeMode.TranslateToEnglish ? "translate-to-english" : "transcribe",
            Language = settings.Language,
        };

        if (!model.IsPresent)
        {
            info.Status = "model-missing";
            info.Error = $"The {model.Name} model is not downloaded yet. Open Settings and download it.";
            return Save(dir, manifest, info);
        }

        var modelBytes = new FileInfo(model.Path).Length;
        var (_, free) = SystemInfo.Memory();
        if (MemoryGuard.Refusal(model.Name, modelBytes, free) is { } refusal)
        {
            Log.Warn($"session {manifest.Id}: transcription refused: {refusal}");
            info.Status = "failed";
            info.Error = refusal;
            return Save(dir, manifest, info);
        }

        var sw = Stopwatch.StartNew();
        var lines = new List<TranscriptLine>();
        var temp = new List<string>();
        var output = Path.Combine(dir, "transcript.worker-tmp.json");
        temp.Add(output);
        try
        {
            var inputs = new List<string>();
            var speakers = new List<string>();
            var names = new List<string>();
            foreach (var track in manifest.Sources)
            {
                ct.ThrowIfCancellationRequested();
                var input = track.SpeechFile is not null ? Path.Combine(dir, track.SpeechFile) : null;
                if (input is null || !File.Exists(input))
                {
                    input = Path.Combine(dir, $"{track.Kind}.whisper-tmp.wav");
                    progress?.Report("Preparing audio for transcription");
                    SessionFinalizer.WriteSpeechCopy(Path.Combine(dir, track.File), input);
                    temp.Add(input);
                }
                inputs.Add(input);
                speakers.Add(track.Kind == "mic" ? "You" : "Others");
                names.Add(track.Kind == "mic" ? "microphone" : "system audio");
            }

            var trackProgress = new Progress<int>(n =>
            {
                if (n >= 1 && n <= names.Count) progress?.Report($"Transcribing {names[n - 1]}");
            });
            var gpu = settings.UseGpu;
            Log.Info($"session {manifest.Id}: transcription start, model {model.Key}, {(gpu ? "GPU (Vulkan) first" : "CPU")}, " +
                     $"{inputs.Count} track(s), runtime order {string.Join(" -> ", RuntimePicker.ForThisPc(gpu))}");
            WorkerRequest Request(bool useGpu) => new(model.Path, inputs, output, useGpu,
                settings.Language ?? "auto", settings.Mode == TranscribeMode.TranslateToEnglish);

            var outcome = await runner.RunAsync(Request(gpu), trackProgress, ct);
            if (gpu && !outcome.Ok && outcome.ExitCode != WorkerExit.NoMemory)
            {
                Log.Warn($"session {manifest.Id}: GPU attempt failed ({outcome.Reason}); retrying once on the CPU");
                info.GpuFailed = true;
                info.RuntimeNote = $"The graphics card (Vulkan) failed: {outcome.Reason}";
                progress?.Report("The graphics card failed; transcribing on the processor");
                outcome = await runner.RunAsync(Request(false), trackProgress, ct);
            }
            else if (gpu && outcome.Ok && outcome.Result!.Runtime is { } loaded && loaded != nameof(RuntimeLibrary.Vulkan))
            {
                Log.Warn($"session {manifest.Id}: Vulkan did not load, whisper used {loaded}");
                info.GpuFailed = true;
                info.RuntimeNote = $"The graphics card (Vulkan) is not available on this PC; used {loaded}.";
            }

            if (!outcome.Ok)
            {
                info.Status = "failed";
                info.ExitCode = outcome.ExitCode;
                info.Runtime = outcome.Result?.Runtime;
                info.Error = outcome.Reason;
                Log.Warn($"session {manifest.Id}: transcription failed: {outcome.Reason}");
            }
            else
            {
                var result = outcome.Result!;
                info.Runtime = result.Runtime;
                info.ExitCode = outcome.ExitCode;
                for (var t = 0; t < result.Tracks.Count && t < speakers.Count; t++)
                {
                    foreach (var seg in result.Tracks[t])
                    {
                        var text = seg.Text.Trim();
                        if (IsNoise(text)) continue;
                        lines.Add(new TranscriptLine(TimeSpan.FromSeconds(seg.Start), TimeSpan.FromSeconds(seg.End), speakers[t], text));
                    }
                }
                lines.Sort((a, b) => a.Start.CompareTo(b.Start));
                WriteMarkdown(Path.Combine(dir, "transcript.md"), manifest, info, lines);
                WriteSrt(Path.Combine(dir, "transcript.srt"), lines);
                info.Files = new List<string> { "transcript.md", "transcript.srt" };
                info.Status = "done";
                info.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
                Log.Info($"session {manifest.Id}: transcription end, {model.Key} on {info.Runtime}: {lines.Count} lines in {info.Seconds} s");
            }
        }
        catch (OperationCanceledException)
        {
            info.Status = "failed";
            info.Error = "Transcription was cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error("transcription failed", ex);
            info.Status = "failed";
            info.Error = $"Transcription failed: {ex.Message}";
        }
        finally
        {
            foreach (var t in temp)
            {
                try
                {
                    File.Delete(t);
                    Log.Info($"removed temporary transcription input {t}");
                }
                catch (Exception ex)
                {
                    Log.Error($"could not remove {t}", ex);
                }
            }
        }

        if (info.Status == "done")
        {
            manifest.Files.RemoveAll(f => f.Name.StartsWith("transcript.", StringComparison.Ordinal));
            manifest.Files.Add(new FileEntry { Name = "transcript.md", Description = "text of both tracks with time and speaker (You / Others)" });
            manifest.Files.Add(new FileEntry { Name = "transcript.srt", Description = "same transcript as subtitles" });
        }
        return Save(dir, manifest, info);
    }

    /// <summary>
    /// Called when RecBox closes or restarts: a transcript still "pending" in session.json becomes
    /// "interrupted" with the reason, so the folder never claims a transcript is on its way.
    /// </summary>
    public static bool MarkInterrupted(string dir, string reason) => MarkPending(dir, "interrupted", reason);

    /// <summary>A transcript job that threw before it wrote its own status: "pending" becomes "failed".</summary>
    public static bool MarkFailed(string dir, string reason) => MarkPending(dir, "failed", reason);

    private static bool MarkPending(string dir, string status, string reason)
    {
        // read-check-write under the same lock as the transcript's own save, so a transcript that
        // finishes at the same moment is never overwritten
        lock (ManifestWriteGate)
        {
            var m = SessionManifest.Load(dir);
            if (m is null || m.Transcript.Status != "pending") return false;
            m.Transcript.Status = status;
            m.Transcript.Error = reason;
            try
            {
                m.Save(dir);
                Log.Info($"session {m.Id}: transcript marked {status} ({reason})");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("could not update session.json", ex);
                return false;
            }
        }
    }

    /// <summary>Serializes the transcript's writes to session.json (they share session.json.tmp).</summary>
    private static readonly object ManifestWriteGate = new();

    private static TranscriptInfo Save(string dir, SessionManifest manifest, TranscriptInfo info)
    {
        manifest.Transcript = info;
        lock (ManifestWriteGate)
        {
            try { manifest.Save(dir); }
            catch (Exception ex) { Log.Error("could not update session.json", ex); }
        }
        return info;
    }

    /// <summary>Whisper's markers for non-speech, not words anyone said.</summary>
    private static bool IsNoise(string text) =>
        text.Length == 0 ||
        text is "[BLANK_AUDIO]" or "(blank audio)" or "[ Silence ]" or "[silence]" or "[MUSIC]" or "[Music]" or "(music)";

    private static string Stamp(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

    private static void WriteMarkdown(string path, SessionManifest m, TranscriptInfo info, List<TranscriptLine> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Recording {m.Id}");
        sb.AppendLine();
        sb.AppendLine($"- Started: {m.StartedLocal:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"- Length: {TimeSpan.FromSeconds(m.DurationSeconds):hh\\:mm\\:ss}");
        sb.AppendLine($"- Speakers: You = microphone, Others = system audio");
        sb.AppendLine($"- Whisper model: {info.Model}, mode: {info.Mode}, language: {info.Language}");
        sb.AppendLine();
        foreach (var l in lines) sb.AppendLine($"[{Stamp(l.Start)}] **{l.Speaker}:** {l.Text}");
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteSrt(string path, List<TranscriptLine> lines)
    {
        var sb = new StringBuilder();
        var i = 1;
        foreach (var l in lines)
        {
            sb.AppendLine((i++).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine($"{l.Start:hh\\:mm\\:ss\\,fff} --> {l.End:hh\\:mm\\:ss\\,fff}");
            sb.AppendLine($"{l.Speaker}: {l.Text}");
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString());
    }
}

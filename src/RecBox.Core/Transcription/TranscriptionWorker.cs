using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace RecBox.Core.Transcription;

/// <summary>
/// Exit codes of the transcription worker. A native whisper.cpp crash (no AVX, a broken Vulkan driver, out of
/// memory) cannot be caught in .NET, so Whisper runs in its own process: the crash ends only the worker, and
/// its Windows exit code tells why.
/// </summary>
public static class WorkerExit
{
    public const int Ok = 0;
    public const int Failed = 20;
    public const int NoMemory = 21;
    public const int BadArgs = 22;
    public const int ParentGone = 23;

    public static string Hex(int code) => $"0x{(uint)code:X8}";

    public static string Describe(int code) => (uint)code switch
    {
        0 => "finished",
        Failed => "the speech engine reported an error",
        NoMemory => "not enough free memory",
        BadArgs => "bad worker arguments",
        ParentGone => "RecBox closed",
        3 => "the speech engine aborted (often a graphics driver or memory problem)",
        0xC000001D => "the processor lacks an instruction the speech engine needs (AVX/AVX2)",
        0xC0000005 => "native crash (access violation), often a graphics driver problem",
        0xC0000409 => "native crash (fail-fast or stack buffer overrun)",
        0xC00000FD => "native crash (stack overflow)",
        0xC0000017 or 0xC000012D => "out of memory",
        0xE06D7363 => "native crash (unhandled C++ exception)",
        0xE0434352 => "unhandled .NET error in the worker",
        0xC0000135 => "a native library (DLL) is missing",
        0xC0000142 => "the worker could not start (DLL initialization failed)",
        _ => "the speech engine stopped unexpectedly",
    };

    /// <summary><see cref="Describe"/> for the screen, in the interface language (Describe stays English for the logs).</summary>
    public static string Text(int code) => Loc.T("wx_" + (uint)code switch
    {
        0 => "finished",
        Failed => "failed",
        NoMemory => "nomem",
        BadArgs => "badargs",
        ParentGone => "parent",
        3 => "abort",
        0xC000001D => "avx",
        0xC0000005 => "av",
        0xC0000409 => "failfast",
        0xC00000FD => "stack",
        0xC0000017 or 0xC000012D => "oom",
        0xE06D7363 => "cpp",
        0xE0434352 => "dotnet",
        0xC0000135 => "dll",
        0xC0000142 => "dllinit",
        _ => "unknown",
    });
}

/// <summary>Which whisper.cpp builds to try, from what the processor can do.</summary>
public static class RuntimePicker
{
    /// <summary>
    /// Vulkan (GPU) and the normal CPU build need AVX, AVX2 and FMA; without them only the no-AVX CPU build
    /// is safe (the others die with an illegal instruction). The no-AVX build is always the last fallback.
    /// </summary>
    public static IReadOnlyList<RuntimeLibrary> Order(bool useGpu, bool avx, bool avx2, bool fma)
    {
        var list = new List<RuntimeLibrary>();
        if (avx && avx2 && fma)
        {
            if (useGpu) list.Add(RuntimeLibrary.Vulkan);
            list.Add(RuntimeLibrary.Cpu);
        }
        list.Add(RuntimeLibrary.CpuNoAvx);
        return list;
    }

    public static IReadOnlyList<RuntimeLibrary> ForThisPc(bool useGpu) =>
        Order(useGpu, SystemInfo.Avx, SystemInfo.Avx2, SystemInfo.Fma);
}

/// <summary>Refuses to load a model that does not fit in free memory, instead of crashing.</summary>
public static class MemoryGuard
{
    /// <summary>Rough peak for whisper.cpp: twice the model file plus 300 MB of buffers.</summary>
    public static long RequiredBytes(long modelBytes) => modelBytes * 2 + 300L * 1024 * 1024;

    /// <summary>The message to show, or null when it fits (or Windows did not report free memory).</summary>
    /// <param name="smaller">The smaller models to suggest, for example "Base or Tiny".</param>
    public static string? Refusal(string modelName, long modelBytes, long availableBytes, string? smaller = null)
    {
        if (availableBytes <= 0) return null;
        var need = RequiredBytes(modelBytes);
        if (availableBytes >= need) return null;
        var pick = smaller is null ? Loc.T("mem_pick_any") : Loc.F("mem_pick_named", smaller);
        return Loc.F("mem_refusal", modelName, SystemInfo.Gb(need), SystemInfo.Gb(availableBytes), pick);
    }

    /// <summary>The models smaller than this one, for the refusal message: "Base or Tiny"; null for Tiny.</summary>
    public static string? Smaller(string modelKey)
    {
        var rank = WhisperModels.Rank(WhisperModels.Get(modelKey));
        if (WhisperModels.IsAuto(modelKey) || rank == 0) return null;
        return string.Join($" {Loc.T("or_word")} ", WhisperModels.All.Take(rank).Reverse().Take(2).Select(m => m.Name));
    }
}

/// <summary>Worker processes run below normal priority: recording (capture threads, disk writes) always wins.</summary>
public static class WorkerPriority
{
    public static void Lower(Process p)
    {
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) { Log.Warn($"could not lower the priority of worker {SafeId(p)}: {ex.Message}"); }
    }

    public static void LowerSelf()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            self.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch { }
    }

    private static int SafeId(Process p)
    {
        try { return p.Id; } catch { return 0; }
    }
}

/// <param name="Language">"auto" (detect once, then keep for every track), a code ("vi") or a mixed pair ("vi+en").</param>
/// <param name="Vad">The Silero model; null splits speech by loudness instead.</param>
public sealed record WorkerRequest(string Model, IReadOnlyList<string> Inputs, string Output, bool Gpu, string Language, bool Translate, int Threads = 0, string? Vad = null);

public sealed class WorkerSegment
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Whisper's probability that the window held no speech (0 when unknown).</summary>
    public double NoSpeech { get; set; }
    /// <summary>Mean log probability of the segment's text tokens (0 when unknown).</summary>
    public double AvgLogprob { get; set; }
}

/// <summary>What the worker writes to its output file.</summary>
public sealed class WorkerResult
{
    public bool Ok { get; set; }
    public string? Runtime { get; set; }
    public string? Error { get; set; }
    /// <summary>The graphics card whisper.cpp ran on ("Vulkan1 = NVIDIA GeForce RTX 3060 ..."), null on the processor.</summary>
    public string? Device { get; set; }
    /// <summary>The language every track was decoded in ("vi+en" for mixed), and what detection said when it was Auto.</summary>
    public string? Language { get; set; }
    public float? LanguageProbability { get; set; }
    public List<List<WorkerSegment>> Tracks { get; set; } = new();
}

/// <summary>The worker's decode settings, the owner's meeting kit carried over to whisper.cpp.</summary>
public static class Decode
{
    public const int BeamSize = 5;
    public const float TemperatureStep = 0.2f;
    /// <summary>
    /// Tokens never decoded: bracket and parenthesis openers and music notes, so "[Music]", "(applause)" and the like
    /// cannot start (whisper.cpp full-matches each vocabulary token; "[_" special tokens such as timestamps are left alone).
    /// </summary>
    public const string SuppressTokens = @"\s*(\[|\(|♪|♫)([^_].*)?";
    public const float NoSpeechThreshold = 0.6f;
    public const float LogprobThreshold = -1.0f;
    public const float EntropyThreshold = 2.4f;

    /// <summary>Fixed language, no previous text as prompt, beam 5 on the graphics card (greedy on the processor), temperature fallback.</summary>
    public static WhisperProcessorBuilder Configure(WhisperProcessorBuilder b, bool beam, string language, bool translate, int threads)
    {
        b = b.WithLanguage(language)
            .WithNoContext()
            .WithTemperature(0f)
            .WithTemperatureInc(TemperatureStep)
            .WithNoSpeechThreshold(NoSpeechThreshold)
            .WithLogProbThreshold(LogprobThreshold)
            .WithEntropyThreshold(EntropyThreshold)
            .WithSuppressRegex(SuppressTokens)
            .WithProbabilities();
        b = beam ? b.WithBeamSearchSamplingStrategy(s => s.WithBeamSize(BeamSize)) : b.WithGreedySamplingStrategy(_ => { });
        if (translate) b = b.WithTranslate();
        if (threads > 0) b = b.WithThreads(threads);
        return b;
    }

    public static string Describe(bool beam) =>
        $"{(beam ? $"beam {BeamSize}" : "greedy")}, no previous-text prompt, temperature 0 +{TemperatureStep}, no_speech {NoSpeechThreshold}, " +
        $"logprob {LogprobThreshold}, entropy {EntropyThreshold}, bracket tags suppressed";

    public static WorkerSegment Segment(SegmentData s, double offsetSeconds = 0)
    {
        double sum = 0;
        var n = 0;
        foreach (var t in s.Tokens ?? Array.Empty<WhisperToken>())
        {
            var text = t.Text ?? "";
            if (text.StartsWith("[_", StringComparison.Ordinal) || text.StartsWith("<|", StringComparison.Ordinal)) continue;
            sum += t.ProbabilityLog;
            n++;
        }
        return new WorkerSegment
        {
            Start = offsetSeconds + s.Start.TotalSeconds,
            End = offsetSeconds + s.End.TotalSeconds,
            Text = s.Text,
            NoSpeech = s.NoSpeechProbability,
            AvgLogprob = n > 0 ? sum / n : 0,
        };
    }

    /// <summary>
    /// Factory options: on the graphics card, the discrete one with the most memory of its own, by its Vulkan index
    /// (ggml numbers devices in Vulkan's order, so "device 0" is often the Intel chip). Logs the device.
    /// </summary>
    public static WhisperFactoryOptions Options(bool gpu, out string? device)
    {
        device = null;
        if (!gpu) return new WhisperFactoryOptions { UseGpu = false };
        var devices = VulkanDevices.List();
        foreach (var d in devices) Log.Info($"vulkan device {d.Describe()}");
        var pick = GpuDevicePick.Pick(devices);
        if (pick is null)
        {
            Log.Info("no Vulkan graphics device listed: whisper.cpp picks its default");
            return new WhisperFactoryOptions { UseGpu = true };
        }
        device = pick.Describe();
        Log.Info($"graphics device chosen: {device}");
        return new WhisperFactoryOptions { UseGpu = true, GpuDevice = pick.Index };
    }

    /// <summary>A WAV file as 16 kHz mono float (the 16 kHz copies already are; anything else is converted).</summary>
    public static float[] ReadWav(string path)
    {
        using var reader = new NAudio.Wave.WaveFileReader(path);
        NAudio.Wave.ISampleProvider sp = reader.ToSampleProvider();
        if (sp.WaveFormat.Channels == 2) sp = new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
        else if (sp.WaveFormat.Channels != 1) throw new InvalidDataException($"{Path.GetFileName(path)} has {sp.WaveFormat.Channels} channels; expected mono or stereo");
        if (sp.WaveFormat.SampleRate != KitSegmenter.Rate) sp = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(sp, KitSegmenter.Rate);
        var list = new List<float>((int)Math.Min(int.MaxValue / 2, reader.SampleCount));
        var buf = new float[16000];
        int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0) list.AddRange(buf.AsSpan(0, n).ToArray());
        return list.ToArray();
    }
}

public sealed record WorkerOutcome(int ExitCode, WorkerResult? Result, string Reason)
{
    public bool Ok => ExitCode == WorkerExit.Ok && Result?.Ok == true;
}

/// <summary>Starts the worker process and waits for it; never throws for a worker that crashed.</summary>
public sealed class WorkerRunner
{
    public static WorkerRunner Default { get; set; } = new();

    /// <summary>How to start the worker. Default: this same exe with --transcribe-worker. Tests swap it.</summary>
    public Func<WorkerRequest, ProcessStartInfo> Launch { get; init; } = DefaultLaunch;

    /// <summary>Called with the started worker, after its priority was lowered (tests read it).</summary>
    public Action<Process>? Started { get; init; }

    public static IReadOnlyList<string> Arguments(WorkerRequest r)
    {
        var a = new List<string> { TranscriptionWorker.Flag, "--model", r.Model, "--output", r.Output, "--runtime", r.Gpu ? "gpu" : "cpu",
            "--language", string.IsNullOrEmpty(r.Language) ? "auto" : r.Language, "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) };
        if (r.Threads > 0)
        {
            a.Add("--threads");
            a.Add(r.Threads.ToString(CultureInfo.InvariantCulture));
        }
        if (r.Translate) a.Add("--translate");
        if (r.Vad is { Length: > 0 } vad)
        {
            a.Add("--vad");
            a.Add(vad);
        }
        foreach (var i in r.Inputs)
        {
            a.Add("--input");
            a.Add(i);
        }
        return a;
    }

    public static ProcessStartInfo DefaultLaunch(WorkerRequest r)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("RecBox cannot find its own exe to start the speech engine.");
        var psi = new ProcessStartInfo(exe);
        foreach (var a in Arguments(r)) psi.ArgumentList.Add(a);
        return psi;
    }

    public async Task<WorkerOutcome> RunAsync(WorkerRequest r, IProgress<int>? trackProgress, CancellationToken ct)
    {
        try { File.Delete(r.Output); } catch { }
        ProcessStartInfo psi;
        try
        {
            psi = Launch(r);
        }
        catch (Exception ex)
        {
            Log.Error("transcription worker could not be prepared", ex);
            return new WorkerOutcome(-1, null, Loc.F("txe_not_started", ex.Message));
        }
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;

        using var p = new Process { StartInfo = psi };
        var pid = 0;
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (e.Data.StartsWith("@progress ", StringComparison.Ordinal) &&
                int.TryParse(e.Data.AsSpan(10).ToString().Split('/')[0], out var n))
                trackProgress?.Report(n);
            Log.Worker($"[{pid} out] {e.Data}");
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) Log.Worker($"[{pid} err] {e.Data}");
        };
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            Log.Error("transcription worker did not start", ex);
            return new WorkerOutcome(-1, null, Loc.F("txe_not_started", ex.Message));
        }
        pid = p.Id;
        WorkerPriority.Lower(p);
        Log.Info($"transcription worker {pid} started ({(r.Gpu ? "GPU first" : "CPU")}, model {Path.GetFileName(r.Model)}, {r.Inputs.Count} track(s), " +
                 $"{(r.Threads > 0 ? $"{r.Threads} thread(s)" : "default threads")}, below normal priority)");
        try { Started?.Invoke(p); } catch (Exception ex) { Log.Warn($"worker started hook failed: {ex.Message}"); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            Log.Info($"transcription worker {pid} stopped (cancelled)");
            throw;
        }
        p.WaitForExit(); // drains the redirected output
        var code = p.ExitCode;

        WorkerResult? result = null;
        try
        {
            if (File.Exists(r.Output))
                result = JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(r.Output), TranscriptionWorker.Json);
        }
        catch (Exception ex)
        {
            Log.Warn($"transcription worker {pid} result unreadable: {ex.Message}");
        }

        string reason;
        if (code == WorkerExit.Ok && result?.Ok == true) reason = "finished";
        else if (result?.Error is { Length: > 0 } err) reason = Loc.F("txe_with_code", err, WorkerExit.Hex(code));
        else if (code == WorkerExit.Ok) reason = Loc.F("txe_no_result", WorkerExit.Hex(code));
        else reason = Loc.F("txe_stopped", WorkerExit.Text(code), WorkerExit.Hex(code));
        Log.Info($"transcription worker {pid} exited with {WorkerExit.Hex(code)} ({WorkerExit.Describe(code)}), runtime {result?.Runtime ?? "unknown"}");
        return new WorkerOutcome(code, result, reason);
    }
}

/// <summary>The worker side: `RecBox.exe --transcribe-worker --model m --input a.wav [--input b.wav] --output r.json ...`.</summary>
public static class TranscriptionWorker
{
    public const string Flag = "--transcribe-worker";

    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static bool IsWorkerCall(string[] args) => args.Length > 0 && args[0] == Flag;

    public static int Run(string[] args)
    {
        Log.RedirectToConsole();
        WorkerPriority.LowerSelf();
        string? model = null, output = null, runtime = "cpu", language = "auto", vad = null;
        var translate = false;
        var parent = 0;
        var threads = 0;
        var inputs = new List<string>();
        try
        {
            for (var i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--model": model = Next(); break;
                    case "--output": output = Next(); break;
                    case "--runtime": runtime = Next(); break;
                    case "--language": language = Next(); break;
                    case "--parent": parent = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--input": inputs.Add(Next()); break;
                    case "--threads": threads = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--translate": translate = true; break;
                    case "--vad": vad = Next(); break;
                    default: throw new ArgumentException($"unknown argument {args[i]}");
                }
            }
            if (model is null || output is null || inputs.Count == 0) throw new ArgumentException("--model, --output and --input are required");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"bad arguments: {ex.Message}");
            return WorkerExit.BadArgs;
        }

        WatchParent(parent);
        var result = new WorkerResult();
        try
        {
            Log.Info($"worker {Environment.ProcessId} start: model={model} runtime={runtime} inputs={inputs.Count}");
            Log.Info(SystemInfo.Describe().Replace(Environment.NewLine, " | "));
            var modelBytes = new FileInfo(model).Length;
            var (_, free) = SystemInfo.Memory();
            if (MemoryGuard.Refusal(Path.GetFileName(model), modelBytes, free) is { } refusal)
            {
                Log.Warn(refusal);
                result.Error = refusal;
                WriteResult(output, result);
                return WorkerExit.NoMemory;
            }

            var gpu = runtime == "gpu";
            // "noavx" forces the no-AVX build (support and tests on a PC that has AVX)
            IReadOnlyList<RuntimeLibrary> order = runtime == "noavx" ? new[] { RuntimeLibrary.CpuNoAvx } : RuntimePicker.ForThisPc(gpu);
            RuntimeOptions.RuntimeLibraryOrder = order.ToList();
            Log.Info($"runtime order: {string.Join(" -> ", order)}");
            using var nativeLog = LogProvider.AddLogger((level, message) =>
            {
                if (!string.IsNullOrWhiteSpace(message)) Console.Error.WriteLine($"whisper {level}: {message.TrimEnd()}");
            });

            var options = Decode.Options(gpu, out var device);
            using var factory = WhisperFactory.FromPath(model, options);
            result.Runtime = RuntimeOptions.LoadedLibrary?.ToString();
            var onGpu = result.Runtime == nameof(RuntimeLibrary.Vulkan);
            result.Device = onGpu ? device : null;
            Log.Info($"runtime loaded: {result.Runtime ?? "unknown"}{(onGpu && device is not null ? $" on {device}" : "")}; decode: {Decode.Describe(onGpu)}");

            // speech of every track first (Silero, faster-whisper's defaults), so Auto can detect the language once
            var audio = inputs.Select(Decode.ReadWav).ToList();
            // on the graphics card: the kit's pieces (Silero cuts at 0.45 s pauses, at most 8 s), measured on the
            // 2026-11-19 replay to beat 30 s windows in every scored window; on the processor 30 s windows (4x fewer encodes)
            var pieces = onGpu;
            var spans = audio.Select(a => FileSpeech(a, vad, threads, pieces)).ToList();
            for (var t = 0; t < inputs.Count; t++)
                Log.Info($"track {t + 1}: {audio[t].Length / (double)KitSegmenter.Rate:0.0} s, speech {spans[t].Sum(s => s.End - s.Start) / (double)KitSegmenter.Rate:0.0} s in {spans[t].Count} span(s)");
            var windows = spans.Select(s => SpeechWindows.Group(s, pieces ? PieceSeconds : 30)).ToList();
            Log.Info($"speech {(pieces ? $"in pieces of at most {PieceSeconds} s (kit segmenter)" : "in windows of at most 30 s")}: {string.Join(", ", windows.Select(w => w.Count))}");

            using var detector = factory.CreateBuilder().WithLanguageDetection().WithThreads(Math.Max(1, threads)).Build();
            var fixedLanguage = language;
            if (Languages.IsAuto(language))
            {
                // one language for the session: a vote weighed by probability and speech over the first minute of
                // speech of every track, in recording order (one detection can be wrong at p > 0.8); every track keeps it
                var vote = new LanguageLock(Languages.Auto);
                var order2 = Enumerable.Range(0, inputs.Count)
                    .SelectMany(t => windows[t].Select(w => (Track: t, Window: w)))
                    .OrderBy(x => x.Window.Starts[0]).ToList();
                var tries = 0;
                foreach (var (t, w) in order2)
                {
                    if (vote.IsLocked || tries >= 8) break;
                    var (lang, p) = Detect(detector, w.Audio(audio[t]));
                    tries++;
                    Log.Info($"language vote {tries}: track {t + 1} at {w.Starts[0] / (double)KitSegmenter.Rate:0.0} s: {lang ?? "none"} (p {p:0.00})");
                    vote.Offer(lang, p, w.Samples / (double)KitSegmenter.Rate);
                }
                fixedLanguage = vote.Detected ?? vote.Leading ?? "en";
                Log.Info($"language for every track: {fixedLanguage} ({(vote.IsLocked ? "vote over the first minute of speech" : $"vote over {tries} window(s)")})");
            }
            result.Language = fixedLanguage;

            var processors = new Dictionary<string, WhisperProcessor>();
            try
            {
                WhisperProcessor For(string lang)
                {
                    if (!processors.TryGetValue(lang, out var pr))
                        processors[lang] = pr = Decode.Configure(factory.CreateBuilder(), onGpu, lang, translate, threads).Build();
                    return pr;
                }

                for (var t = 0; t < inputs.Count; t++)
                {
                    Console.Out.WriteLine($"@progress {t + 1}/{inputs.Count}");
                    Console.Out.Flush();
                    var segs = new List<WorkerSegment>();
                    foreach (var w in windows[t])
                    {
                        var samples = w.Audio(audio[t]);
                        var lang = fixedLanguage!;
                        if (Languages.IsMixed(lang))
                        {
                            var (d, p) = Detect(detector, samples);
                            lang = Languages.ChooseMixed(fixedLanguage!, d, p);
                        }
                        foreach (var s in For(lang).ProcessAsync(samples).ToBlockingEnumerable())
                        {
                            var seg = Decode.Segment(s);
                            var start = w.ToTrack(seg.Start) / (double)KitSegmenter.Rate;
                            var end = w.ToTrack(Math.Max(seg.Start, seg.End)) / (double)KitSegmenter.Rate;
                            seg.Start = start;
                            seg.End = Math.Max(start, end);
                            segs.Add(seg);
                        }
                    }
                    result.Tracks.Add(segs);
                    Log.Info($"track {t + 1}: {segs.Count} segment(s) from {windows[t].Count} speech window(s)");
                }
            }
            finally
            {
                foreach (var pr in processors.Values)
                {
                    try { pr.Dispose(); } catch { }
                }
            }
            result.Ok = true;
            WriteResult(output, result);
            Log.Info("worker done");
            return WorkerExit.Ok;
        }
        catch (Exception ex)
        {
            Log.Error("worker failed", ex);
            result.Error = Loc.F("txe_failed_on", result.Runtime ?? Loc.T("txe_this_pc"), ex.Message);
            try { WriteResult(output, result); } catch { }
            return WorkerExit.Failed;
        }
    }

    public const string LiveFlag = "--live-worker";

    public static bool IsLiveWorkerCall(string[] args) => args.Length > 0 && args[0] == LiveFlag;

    /// <summary>
    /// The live worker: `RecBox.exe --live-worker --model m --runtime cpu|gpu --language x --threads n --parent pid`.
    /// Loads the model once, prints "@ready {json}", then answers each JSON request line on stdin (one chunk WAV)
    /// with one "@live {json}" line, until stdin closes or RecBox ends. A native crash ends only this process;
    /// RecBox starts a new one.
    /// </summary>
    public static int RunLive(string[] args)
    {
        Log.RedirectToConsole();
        WorkerPriority.LowerSelf();
        string? model = null;
        string runtime = "cpu", language = "auto";
        int parent = 0, threads = 0;
        try
        {
            for (var i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--model": model = Next(); break;
                    case "--runtime": runtime = Next(); break;
                    case "--language": language = Next(); break;
                    case "--threads": threads = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--parent": parent = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    default: throw new ArgumentException($"unknown argument {args[i]}");
                }
            }
            if (model is null) throw new ArgumentException("--model is required");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"bad arguments: {ex.Message}");
            return WorkerExit.BadArgs;
        }

        WatchParent(parent);
        var processors = new Dictionary<string, WhisperProcessor>();
        try
        {
            Log.Info($"live worker {Environment.ProcessId} start: model={model} runtime={runtime} threads={threads}");
            var (_, free) = SystemInfo.Memory();
            if (MemoryGuard.Refusal(Path.GetFileName(model), new FileInfo(model).Length, free) is { } refusal)
            {
                Tagged("@ready", new LiveReady { Ok = false, Error = refusal });
                return WorkerExit.NoMemory;
            }
            var gpu = runtime == "gpu";
            IReadOnlyList<RuntimeLibrary> order = runtime == "noavx" ? new[] { RuntimeLibrary.CpuNoAvx } : RuntimePicker.ForThisPc(gpu);
            RuntimeOptions.RuntimeLibraryOrder = order.ToList();
            using var nativeLog = LogProvider.AddLogger((level, message) =>
            {
                if (!string.IsNullOrWhiteSpace(message)) Console.Error.WriteLine($"whisper {level}: {message.TrimEnd()}");
            });
            var options = Decode.Options(gpu, out var device);
            using var factory = WhisperFactory.FromPath(model, options);
            var runtimeName = RuntimeOptions.LoadedLibrary?.ToString();
            var onGpu = runtimeName == nameof(RuntimeLibrary.Vulkan);
            Log.Info($"live worker runtime {runtimeName ?? "unknown"}{(onGpu && device is not null ? $" on {device}" : "")}; decode: {Decode.Describe(onGpu)}");
            using var detector = factory.CreateBuilder().WithLanguageDetection().WithThreads(Math.Max(1, threads)).Build();
            Tagged("@ready", new LiveReady { Ok = true, Runtime = runtimeName, Device = onGpu ? device : null });

            string? line;
            while ((line = Console.In.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                LiveRequest? req;
                try { req = JsonSerializer.Deserialize<LiveRequest>(line, Json); }
                catch (Exception ex)
                {
                    Log.Warn($"live worker: unreadable request: {ex.Message}");
                    continue;
                }
                if (req is null) continue;
                var reply = new LiveReply { Id = req.Id };
                var sw = Stopwatch.StartNew();
                try
                {
                    var samples = Decode.ReadWav(req.Input);
                    var spec = string.IsNullOrWhiteSpace(req.Language) ? language : req.Language!;
                    var lang = spec;
                    if (req.Detect || Languages.IsAuto(spec) || Languages.IsMixed(spec))
                    {
                        var (d, p) = Detect(detector, samples);
                        reply.LanguageProbability = p;
                        reply.Detected = d;
                        if (Languages.IsMixed(spec)) lang = Languages.ChooseMixed(spec, d, p);
                        else if (Languages.IsAuto(spec)) lang = d ?? "en";
                    }
                    reply.Language = lang;
                    var key = $"{lang}|{req.Translate}";
                    if (!processors.TryGetValue(key, out var processor))
                        processors[key] = processor = Decode.Configure(factory.CreateBuilder(), onGpu, lang, req.Translate, threads).Build();
                    reply.Segments = processor.ProcessAsync(samples).ToBlockingEnumerable().Select(s => Decode.Segment(s)).ToList();
                    reply.Ok = true;
                }
                catch (Exception ex)
                {
                    reply.Error = ex.Message;
                }
                reply.Ms = sw.ElapsedMilliseconds;
                Tagged("@live", reply);
            }
            Log.Info("live worker: input closed, done");
            return WorkerExit.Ok;
        }
        catch (Exception ex)
        {
            Log.Error("live worker failed", ex);
            try { Tagged("@ready", new LiveReady { Ok = false, Error = Loc.F("lte_failed_start", ex.Message) }); } catch { }
            return WorkerExit.Failed;
        }
        finally
        {
            foreach (var p in processors.Values)
            {
                try { p.Dispose(); } catch { }
            }
        }
    }

    private static void Tagged(string tag, object payload)
    {
        lock (Console.Out)
        {
            Console.Out.WriteLine($"{tag} {JsonSerializer.Serialize(payload, payload.GetType(), Json)}");
            Console.Out.Flush();
        }
    }

    /// <summary>Whisper's language detection on up to 30 s of audio: the language and its probability (null when it fails).</summary>
    private static (string? Language, float P) Detect(WhisperProcessor detector, float[] samples)
    {
        try
        {
            var r = detector.DetectLanguageWithProbability(samples.Length > 30 * KitSegmenter.Rate ? samples[..(30 * KitSegmenter.Rate)] : samples);
            return (r.language, r.probability);
        }
        catch (Exception ex)
        {
            Log.Warn($"language detection failed: {ex.Message}");
            return (null, 0f);
        }
    }

    /// <summary>Longest piece of speech decoded at once on the graphics card (the kit's segmenter maximum).</summary>
    public const double PieceSeconds = 8;

    /// <summary>
    /// Silero over a whole track; loudness when there is no Silero model. Windows: faster-whisper's vad_filter
    /// defaults. Pieces: the kit's live settings (0.5, 450 ms silence, 120 ms pad, 200 ms minimum, 8 s maximum).
    /// </summary>
    private static IReadOnlyList<(int Start, int End)> FileSpeech(float[] audio, string? vadModel, int threads, bool pieces = false)
    {
        if (vadModel is { Length: > 0 } && File.Exists(vadModel))
        {
            try
            {
                using var vf = WhisperVadFactory.FromPath(vadModel);
                using var vp = vf.CreateBuilder()
                    .WithThreshold(0.5f)
                    .WithMinSpeechDuration(TimeSpan.FromMilliseconds(pieces ? 200 : 0))
                    .WithMinSilenceDuration(TimeSpan.FromMilliseconds(pieces ? 450 : 2000))
                    .WithSpeechPadding(TimeSpan.FromMilliseconds(pieces ? 120 : 400))
                    .WithMaxSpeechDuration(TimeSpan.FromSeconds(pieces ? PieceSeconds : 28))
                    .WithThreads(Math.Max(1, threads))
                    .WithUseGpu(false)
                    .Build();
                return vp.DetectSpeech(audio)
                    .Select(s => ((int)(s.Start.TotalSeconds * KitSegmenter.Rate), Math.Min(audio.Length, (int)(s.End.TotalSeconds * KitSegmenter.Rate))))
                    .Where(s => s.Item2 > s.Item1).ToList();
            }
            catch (Exception ex)
            {
                Log.Warn($"Silero failed on the track ({ex.Message}); splitting speech by loudness");
            }
        }
        else Log.Warn("no Silero model: splitting speech by loudness");
        return EnergyVad.Detect(audio, audio.Length);
    }

    public const string VadFlag = "--vad-worker";

    public static bool IsVadWorkerCall(string[] args) => args.Length > 0 && args[0] == VadFlag;

    /// <summary>
    /// The speech detector for live text: `RecBox.exe --vad-worker --model ggml-silero.bin --parent pid`. Prints "@ready",
    /// then answers each request line (16 kHz PCM16 in base64) with "@vad {id, spans}" in samples, using the kit's live
    /// settings: threshold 0.5, 450 ms min silence, 120 ms padding, 200 ms min speech.
    /// </summary>
    public static int RunVad(string[] args)
    {
        Log.RedirectToConsole();
        WorkerPriority.LowerSelf();
        string? model = null;
        var parent = 0;
        try
        {
            for (var i = 1; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                switch (args[i])
                {
                    case "--model": model = Next(); break;
                    case "--parent": parent = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    default: throw new ArgumentException($"unknown argument {args[i]}");
                }
            }
            if (model is null) throw new ArgumentException("--model is required");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"bad arguments: {ex.Message}");
            return WorkerExit.BadArgs;
        }
        WatchParent(parent);
        try
        {
            RuntimeOptions.RuntimeLibraryOrder = RuntimePicker.ForThisPc(false).ToList();
            using var vf = WhisperVadFactory.FromPath(model);
            using var vp = vf.CreateBuilder()
                .WithThreshold(0.5f)
                .WithMinSpeechDuration(TimeSpan.FromMilliseconds(200))
                .WithMinSilenceDuration(TimeSpan.FromMilliseconds(450))
                .WithSpeechPadding(TimeSpan.FromMilliseconds(120))
                .WithThreads(1)
                .WithUseGpu(false)
                .Build();
            Tagged("@ready", new LiveReady { Ok = true, Runtime = RuntimeOptions.LoadedLibrary?.ToString() });
            string? line;
            while ((line = Console.In.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                var reply = new VadService.Reply();
                try
                {
                    var req = JsonSerializer.Deserialize<VadService.Request>(line, Json)!;
                    reply.Id = req.Id;
                    var pcm = Convert.FromBase64String(req.Pcm);
                    var samples = new float[pcm.Length / 2];
                    for (var i = 0; i < samples.Length; i++) samples[i] = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8) / 32768f;
                    foreach (var s in vp.DetectSpeech(samples))
                    {
                        var a = (int)(s.Start.TotalSeconds * KitSegmenter.Rate);
                        var b = Math.Min(samples.Length, (int)(s.End.TotalSeconds * KitSegmenter.Rate));
                        if (b > a) reply.Spans.Add(new[] { a, b });
                    }
                    reply.Ok = true;
                }
                catch (Exception ex)
                {
                    reply.Error = ex.Message;
                }
                Tagged("@vad", reply);
            }
            return WorkerExit.Ok;
        }
        catch (Exception ex)
        {
            Log.Error("speech detector failed", ex);
            try { Tagged("@ready", new LiveReady { Ok = false, Error = ex.Message }); } catch { }
            return WorkerExit.Failed;
        }
    }

    private static void WriteResult(string path, WorkerResult result)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(result, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The worker ends when RecBox ends (closed or crashed), so no orphan keeps a model in memory.</summary>
    private static void WatchParent(int pid)
    {
        if (pid <= 0) return;
        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch
        {
            Environment.Exit(WorkerExit.ParentGone);
            return;
        }
        new Thread(() =>
        {
            try { parent.WaitForExit(); } catch { }
            Environment.Exit(WorkerExit.ParentGone);
        }) { IsBackground = true, Name = "parent watch" }.Start();
    }
}

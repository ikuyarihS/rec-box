using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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
        var pick = smaller is null ? "a smaller model (or Auto)" : $"the smaller {smaller} model (or Auto)";
        return $"Not enough free memory to load the {modelName} model: it needs about {SystemInfo.Gb(need)}, " +
               $"{SystemInfo.Gb(availableBytes)} is free. Close other apps and record again, or pick {pick} in Settings. " +
               "The recording itself is saved.";
    }

    /// <summary>The models smaller than this one, for the refusal message: "Base or Tiny"; null for Tiny.</summary>
    public static string? Smaller(string modelKey)
    {
        var rank = WhisperModels.Rank(WhisperModels.Get(modelKey));
        if (WhisperModels.IsAuto(modelKey) || rank == 0) return null;
        return string.Join(" or ", WhisperModels.All.Take(rank).Reverse().Take(2).Select(m => m.Name));
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

public sealed record WorkerRequest(string Model, IReadOnlyList<string> Inputs, string Output, bool Gpu, string Language, bool Translate, int Threads = 0);

public sealed class WorkerSegment
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>What the worker writes to its output file.</summary>
public sealed class WorkerResult
{
    public bool Ok { get; set; }
    public string? Runtime { get; set; }
    public string? Error { get; set; }
    public List<List<WorkerSegment>> Tracks { get; set; } = new();
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
            return new WorkerOutcome(-1, null, $"The speech engine could not start ({ex.Message}).");
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
            return new WorkerOutcome(-1, null, $"The speech engine could not start ({ex.Message}).");
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
        else if (result?.Error is { Length: > 0 } err) reason = $"{err} (worker exit code {WorkerExit.Hex(code)})";
        else if (code == WorkerExit.Ok) reason = $"The speech engine ended without a result (worker exit code {WorkerExit.Hex(code)}).";
        else reason = $"The speech engine stopped: {WorkerExit.Describe(code)} (worker exit code {WorkerExit.Hex(code)}). Recordings are not affected.";
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
        string? model = null, output = null, runtime = "cpu", language = "auto";
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

            using var factory = WhisperFactory.FromPath(model, new WhisperFactoryOptions { UseGpu = gpu });
            result.Runtime = RuntimeOptions.LoadedLibrary?.ToString();
            Log.Info($"runtime loaded: {result.Runtime ?? "unknown"}");

            for (var t = 0; t < inputs.Count; t++)
            {
                Console.Out.WriteLine($"@progress {t + 1}/{inputs.Count}");
                Console.Out.Flush();
                var builder = factory.CreateBuilder();
                builder = language is "" or "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(language);
                if (translate) builder = builder.WithTranslate();
                if (threads > 0) builder = builder.WithThreads(threads);
                using var processor = builder.Build();
                using var fs = File.OpenRead(inputs[t]);
                result.Tracks.Add(ProcessTrack(processor, fs).GetAwaiter().GetResult());
                Log.Info($"track {t + 1}: {result.Tracks[t].Count} segment(s)");
            }
            result.Ok = true;
            WriteResult(output, result);
            Log.Info("worker done");
            return WorkerExit.Ok;
        }
        catch (Exception ex)
        {
            Log.Error("worker failed", ex);
            result.Error = $"Transcription failed on {result.Runtime ?? "this PC"}: {ex.Message}";
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
        var processors = new Dictionary<bool, WhisperProcessor>();
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
            using var factory = WhisperFactory.FromPath(model, new WhisperFactoryOptions { UseGpu = gpu });
            Tagged("@ready", new LiveReady { Ok = true, Runtime = RuntimeOptions.LoadedLibrary?.ToString() });

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
                    if (!processors.TryGetValue(req.Translate, out var processor))
                    {
                        var builder = factory.CreateBuilder();
                        builder = language is "" or "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(language);
                        if (req.Translate) builder = builder.WithTranslate();
                        if (threads > 0) builder = builder.WithThreads(threads);
                        processors[req.Translate] = processor = builder.Build();
                    }
                    using var fs = File.OpenRead(req.Input);
                    reply.Segments = ProcessTrack(processor, fs).GetAwaiter().GetResult();
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
            try { Tagged("@ready", new LiveReady { Ok = false, Error = $"Live text failed to start: {ex.Message}" }); } catch { }
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

    private static async Task<List<WorkerSegment>> ProcessTrack(WhisperProcessor processor, Stream wav)
    {
        var list = new List<WorkerSegment>();
        await foreach (var seg in processor.ProcessAsync(wav))
            list.Add(new WorkerSegment { Start = seg.Start.TotalSeconds, End = seg.End.TotalSeconds, Text = seg.Text });
        return list;
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

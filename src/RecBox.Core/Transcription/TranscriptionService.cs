using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
    public const string AutoKey = "auto";

    /// <summary>Real models, smallest first (the order Auto steps down through).</summary>
    public static readonly IReadOnlyList<WhisperModelInfo> All = new[]
    {
        new WhisperModelInfo("tiny", "Tiny", "Fastest. 31 MB download, for older laptops and live text", GgmlType.Tiny, QuantizationType.Q5_1, 32_152_673),
        new WhisperModelInfo("base", "Base", "Fast. 60 MB download, runs on any PC", GgmlType.Base, QuantizationType.Q5_1, 59_707_625),
        new WhisperModelInfo("small", "Small", "More accurate. 190 MB download", GgmlType.Small, QuantizationType.Q5_1, 190_085_487),
        new WhisperModelInfo("large-v3-turbo", "Large v3 Turbo", "Most accurate. 574 MB download, best with a GPU", GgmlType.LargeV3Turbo, QuantizationType.Q5_0, 574_041_195),
    };

    /// <summary>The Settings entry "Auto": not a file, it stands for the model picked for this PC.</summary>
    public static readonly WhisperModelInfo Auto =
        new(AutoKey, "Auto (recommended)", "Picks the model for this PC: memory, graphics card, processor", GgmlType.Base, QuantizationType.Q5_1, 0);

    /// <summary>What Settings lists: Auto first, then every model.</summary>
    public static readonly IReadOnlyList<WhisperModelInfo> Choices = new[] { Auto }.Concat(All).ToArray();

    public static bool IsAuto(string? key) => string.IsNullOrEmpty(key) || key == AutoKey;

    /// <summary>A real model by key; unknown keys (and "auto") give Base.</summary>
    public static WhisperModelInfo Get(string key) => All.FirstOrDefault(m => m.Key == key) ?? All[1];

    /// <summary>A Settings entry by key (Auto or a model).</summary>
    public static WhisperModelInfo Choice(string? key) => IsAuto(key) ? Auto : Get(key!);

    public static int Rank(WhisperModelInfo m)
    {
        for (var i = 0; i < All.Count; i++) if (All[i].Key == m.Key) return i;
        return 1;
    }
}

public enum TranscriptionUse
{
    /// <summary>The full transcript after a recording.</summary>
    File,
    /// <summary>Live text while recording: has to keep up with speech.</summary>
    Live,
}

/// <summary>One display adapter as DXGI reports it.</summary>
public sealed record GpuAdapter(string Name, uint VendorId, long DedicatedBytes, long SharedBytes, bool Integrated);

/// <summary>What the PC can give transcription. Measured by HardwareProbe; tests build simulated ones.</summary>
/// <param name="Avx2">AVX, AVX2 and FMA all present (the normal whisper.cpp builds need all three).</param>
/// <param name="VulkanLoader">vulkan-1.dll is installed (a graphics driver with Vulkan support).</param>
public sealed record HardwareProfile(long TotalRam, long FreeRam, int PhysicalCores, int LogicalCores, bool Avx2, GpuAdapter? Gpu, bool VulkanLoader)
{
    public string Describe() =>
        $"{SystemInfo.Gb(TotalRam)} RAM ({SystemInfo.Gb(FreeRam)} free), {PhysicalCores} cores / {LogicalCores} threads, " +
        $"AVX2 {(Avx2 ? "yes" : "no")}, GPU {(Gpu is null ? "none" : $"{Gpu.Name} ({SystemInfo.Gb(Gpu.DedicatedBytes)} own memory{(Gpu.Integrated ? ", integrated" : "")})")}, " +
        $"Vulkan {(VulkanLoader ? "yes" : "no")}";
}

/// <summary>The model, device and thread count one transcription runs with, and why.</summary>
public sealed record ModelChoice(WhisperModelInfo Model, bool Gpu, int Threads, string Reason, string? Refusal = null);

/// <summary>Measures the PC: memory, physical cores, AVX2, the best graphics card (DXGI) and the Vulkan loader.</summary>
public static class HardwareProbe
{
    private const long Mb = 1024L * 1024;
    private static readonly Lazy<(GpuAdapter? Gpu, int Cores, bool Vulkan)> Fixed = new(() =>
    {
        var adapters = Adapters();
        var gpu = adapters.Where(a => !a.Integrated).OrderByDescending(a => a.DedicatedBytes).FirstOrDefault()
                  ?? adapters.FirstOrDefault();
        var vulkan = File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"));
        foreach (var a in adapters)
            Log.Info($"display adapter: {a.Name}, vendor 0x{a.VendorId:X4}, {SystemInfo.Gb(a.DedicatedBytes)} own memory, " +
                     $"{SystemInfo.Gb(a.SharedBytes)} shared{(a.Integrated ? ", integrated" : "")}");
        return (gpu, PhysicalCores(), vulkan);
    });

    public static HardwareProfile Measure()
    {
        var (total, free) = SystemInfo.Memory();
        var (gpu, cores, vulkan) = Fixed.Value;
        return new HardwareProfile(total, free, cores, Environment.ProcessorCount,
            SystemInfo.Avx && SystemInfo.Avx2 && SystemInfo.Fma, gpu, vulkan);
    }

    /// <summary>
    /// An integrated GPU shares system memory and is no faster than the processor for Whisper: anything with
    /// under 1 GB of its own memory, Intel graphics other than Arc, and AMD APU graphics.
    /// </summary>
    public static bool IsIntegrated(uint vendorId, string name, long dedicatedBytes)
    {
        if (dedicatedBytes < 1024 * Mb) return true;
        if (vendorId == 0x8086 && !name.Contains("Arc", StringComparison.OrdinalIgnoreCase)) return true;
        if (vendorId == 0x1002 && System.Text.RegularExpressions.Regex.IsMatch(name,
                @"Radeon(\(TM\))?\s*(Graphics|R\d Graphics|\d{3}M\b)|Vega \d+ Graphics|\bHD \d{4}[GD]\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;
        return false;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc1Fn(IntPtr self, out DxgiAdapterDesc1 desc);

    private static T Slot<T>(IntPtr com, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(com), index * IntPtr.Size));

    /// <summary>Hardware display adapters (software renderers skipped); empty when DXGI is unavailable.</summary>
    public static IReadOnlyList<GpuAdapter> Adapters()
    {
        var list = new List<GpuAdapter>();
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out var factory) < 0 || factory == IntPtr.Zero) return list;
            try
            {
                var enumAdapters = Slot<EnumAdapters1Fn>(factory, 12); // IUnknown 3 + IDXGIObject 4 + IDXGIFactory 5
                for (uint i = 0; i < 16; i++)
                {
                    if (enumAdapters(factory, i, out var adapter) < 0 || adapter == IntPtr.Zero) break;
                    try
                    {
                        if (Slot<GetDesc1Fn>(adapter, 10)(adapter, out var d) < 0) continue; // IUnknown 3 + IDXGIObject 4 + IDXGIAdapter 3
                        if ((d.Flags & 2) != 0 || d.VendorId == 0x1414) continue; // software adapter (Microsoft Basic Render)
                        var name = (d.Description ?? "").Trim();
                        var dedicated = (long)d.DedicatedVideoMemory;
                        list.Add(new GpuAdapter(name, d.VendorId, dedicated, (long)d.SharedSystemMemory, IsIntegrated(d.VendorId, name, dedicated)));
                    }
                    finally
                    {
                        Marshal.Release(adapter);
                    }
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"could not list display adapters (DXGI): {ex.Message}");
        }
        return list;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint length);

    /// <summary>Physical cores (not hyper-threads); the logical count when Windows does not say.</summary>
    public static int PhysicalCores()
    {
        try
        {
            uint len = 0;
            GetLogicalProcessorInformation(IntPtr.Zero, ref len);
            if (len == 0) return Environment.ProcessorCount;
            var buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformation(buf, ref len)) return Environment.ProcessorCount;
                var size = IntPtr.Size == 8 ? 32 : 24; // SYSTEM_LOGICAL_PROCESSOR_INFORMATION
                var cores = 0;
                for (var off = 0; off + size <= len; off += size)
                    if (Marshal.ReadInt32(buf, off + IntPtr.Size) == 0) cores++; // RelationProcessorCore
                return cores > 0 ? cores : Environment.ProcessorCount;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch
        {
            return Environment.ProcessorCount;
        }
    }
}

/// <summary>Whether the graphics card has room for a model: its own memory, not shared system memory.</summary>
public static class GpuFit
{
    private const long Mb = 1024L * 1024;

    /// <summary>Kept free for Windows and the display (the desktop compositor alone takes 200-400 MB).</summary>
    public const long HeadroomBytes = 512 * Mb;

    /// <summary>Model weights plus whisper.cpp compute buffers on Vulkan, rounded up.</summary>
    public static long NeedBytes(WhisperModelInfo m) => m.Key switch
    {
        "tiny" => 300 * Mb,
        "base" => 450 * Mb,
        "small" => 900 * Mb,
        _ => 1800 * Mb,
    };

    public static (bool Use, string Why) Check(HardwareProfile hw, WhisperModelInfo model, bool allowed)
    {
        if (!allowed) return (false, "the graphics card is off in Settings");
        if (!hw.Avx2) return (false, "the processor lacks AVX2, so only the no-AVX processor build runs");
        if (hw.Gpu is not { } gpu) return (false, "no graphics card found");
        if (gpu.Integrated) return (false, $"{gpu.Name} is integrated graphics (shares system memory), the processor is steadier");
        if (!hw.VulkanLoader) return (false, "no Vulkan graphics driver is installed");
        var need = NeedBytes(model) + HeadroomBytes;
        if (gpu.DedicatedBytes < need)
            return (false, $"{gpu.Name} has {SystemInfo.Gb(gpu.DedicatedBytes)} of its own memory, {model.Name} needs about {SystemInfo.Gb(need)}");
        return (true, $"{gpu.Name} has {SystemInfo.Gb(gpu.DedicatedBytes)} of its own memory");
    }
}

/// <summary>
/// Picks the model for this PC ("Auto") and the device and threads for any model. Never Small on a 2 GB card,
/// an integrated GPU or a 4 GB laptop; never the graphics card when it is integrated or too small.
/// </summary>
public static class ModelPicker
{
    private const long Gb = 1024L * 1024 * 1024;

    /// <summary>Physical cores minus one, so the capture threads always have a core.</summary>
    public static int Threads(int physicalCores) => Math.Max(1, physicalCores - 1);

    public static ModelChoice Pick(HardwareProfile hw, TranscriptionUse use, bool gpuAllowed)
    {
        var tiny = WhisperModels.Get("tiny");
        var @base = WhisperModels.Get("base");
        var small = WhisperModels.Get("small");
        var total = hw.TotalRam > 0 ? hw.TotalRam : 8 * Gb;
        var free = hw.FreeRam > 0 ? hw.FreeRam : total / 2;

        WhisperModelInfo model;
        string why;
        if (!hw.Avx2)
        {
            model = tiny;
            why = "the processor lacks AVX2, only the slower no-AVX build runs";
        }
        else if (total < 6 * Gb || free < 3 * Gb / 2)
        {
            model = tiny;
            why = $"{SystemInfo.Gb(total)} RAM with {SystemInfo.Gb(free)} free";
        }
        else if (use == TranscriptionUse.File)
        {
            var bigCard = GpuFit.Check(hw, small, gpuAllowed).Use && hw.Gpu!.DedicatedBytes >= 7 * Gb / 2 && free >= 5 * Gb / 2;
            model = bigCard ? small : @base;
            why = bigCard ? "a graphics card with 4 GB or more of its own memory" : "Small needs a graphics card with 4 GB or more; Base runs well on this PC";
        }
        else if (GpuFit.Check(hw, @base, gpuAllowed).Use)
        {
            model = @base;
            why = "the graphics card keeps live text close behind with Base";
        }
        else if (hw.PhysicalCores >= 6)
        {
            model = @base;
            why = $"{hw.PhysicalCores} processor cores keep up with Base";
        }
        else
        {
            model = tiny;
            why = $"{hw.PhysicalCores} processor cores and no usable graphics card: Tiny keeps live text close behind";
        }

        while (hw.FreeRam > 0 && MemoryGuard.RequiredBytes(model.ApproxBytes) > hw.FreeRam && model.Key != "tiny")
        {
            model = WhisperModels.All[WhisperModels.Rank(model) - 1];
            why += $"; stepped down to {model.Name} for free memory";
        }
        var refusal = MemoryGuard.Refusal(model.Name, model.ApproxBytes, hw.FreeRam);
        return Make(hw, model, gpuAllowed, $"Auto picked {model.Name} for {(use == TranscriptionUse.Live ? "live text" : "the transcript")}: {why}", refusal);
    }

    /// <summary>
    /// The model a transcription runs with: the one chosen in Settings, or the Auto pick. When the Auto pick is
    /// not downloaded, a smaller downloaded one is used; never a larger one (Auto stays in the PC's tier), so with
    /// nothing smaller on disk the pick itself comes back and the caller downloads it or reports it missing.
    /// For the full transcript, a graphics card measured without a processor speed for the same model runs the
    /// processor once, so GpuSpeed has the baseline to compare against.
    /// </summary>
    public static ModelChoice Plan(AppSettings settings, HardwareProfile hw, TranscriptionUse use, Func<WhisperModelInfo, bool>? present = null)
    {
        present ??= m => m.IsPresent;
        ModelChoice choice;
        if (!WhisperModels.IsAuto(settings.WhisperModel))
        {
            var chosen = WhisperModels.Get(settings.WhisperModel);
            choice = Make(hw, chosen, settings.UseGpu, $"{chosen.Name} (chosen in Settings)", null);
        }
        else
        {
            var pick = Pick(hw, use, settings.UseGpu);
            choice = pick;
            if (!present(pick.Model))
            {
                for (var i = WhisperModels.Rank(pick.Model) - 1; i >= 0; i--)
                {
                    var m = WhisperModels.All[i];
                    if (!present(m)) continue;
                    choice = Make(hw, m, settings.UseGpu, $"{pick.Reason}; {pick.Model.Name} is not downloaded, using {m.Name}", null);
                    break;
                }
            }
        }

        if (use == TranscriptionUse.File && choice.Gpu && GpuSpeed.NeedsCpuBaseline(settings, choice.Model.Key))
            choice = choice with
            {
                Gpu = false,
                Reason = $"{choice.Reason}; this time on the processor instead: measuring it once to compare with the graphics card " +
                         $"(it has a speed for {choice.Model.Name}, the processor none yet)",
            };
        return choice;
    }

    private static ModelChoice Make(HardwareProfile hw, WhisperModelInfo model, bool gpuAllowed, string why, string? refusal)
    {
        var (gpu, gpuWhy) = GpuFit.Check(hw, model, gpuAllowed);
        var threads = Threads(hw.PhysicalCores);
        return new ModelChoice(model, gpu, threads,
            $"{why}; {(gpu ? "on the graphics card (Vulkan)" : "on the processor")}: {gpuWhy}; {threads} thread(s) of {hw.PhysicalCores} cores", refusal);
    }
}

/// <summary>Remembers how fast each runtime transcribed, so a graphics card slower than the processor is turned off.</summary>
public static class GpuSpeed
{
    /// <summary>
    /// True when the graphics card has a measured speed for this model but the processor has none: without that
    /// baseline "slower than the processor" can never be decided, so the next full transcript runs on the processor once.
    /// </summary>
    public static bool NeedsCpuBaseline(AppSettings settings, string model) =>
        settings.Speeds.ContainsKey($"gpu:{model}") && !settings.Speeds.ContainsKey($"cpu:{model}");

    /// <summary>
    /// Stores the speed (seconds of audio per second of work) of a finished transcript and returns why the graphics
    /// card should be turned off, or null: it is turned off when it was slower than the processor with the same model.
    /// </summary>
    public static string? Record(AppSettings settings, string? runtime, string model, double audioSeconds, double wallSeconds)
    {
        if (audioSeconds <= 0 || wallSeconds <= 0 || string.IsNullOrEmpty(runtime)) return null;
        var onGpu = runtime == nameof(RuntimeLibrary.Vulkan);
        var speed = Math.Round(audioSeconds / wallSeconds, 2);
        settings.Speeds[$"{(onGpu ? "gpu" : "cpu")}:{model}"] = speed;
        if (settings.Speeds.TryGetValue($"gpu:{model}", out var gpu) && settings.Speeds.TryGetValue($"cpu:{model}", out var cpu) && gpu < cpu)
            return $"The graphics card was slower than the processor with {model} ({gpu:0.0}x against {cpu:0.0}x real time).";
        return null;
    }
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
        IProgress<string>? progress, CancellationToken ct, WorkerRunner? runner = null, HardwareProfile? hardware = null)
    {
        runner ??= WorkerRunner.Default;
        hardware ??= HardwareProbe.Measure();
        var choice = ModelPicker.Plan(settings, hardware, TranscriptionUse.File);
        var model = choice.Model;
        Log.Info($"session {manifest.Id}: model choice: {choice.Reason} | {hardware.Describe()}");
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
        if ((choice.Refusal ?? MemoryGuard.Refusal(model.Name, modelBytes, free, MemoryGuard.Smaller(model.Key))) is { } refusal)
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
            var gpu = choice.Gpu;
            Log.Info($"session {manifest.Id}: transcription start, model {model.Key}, {(gpu ? "GPU (Vulkan) first" : "CPU")}, " +
                     $"{choice.Threads} thread(s), {inputs.Count} track(s), runtime order {string.Join(" -> ", RuntimePicker.ForThisPc(gpu))}");
            WorkerRequest Request(bool useGpu) => new(model.Path, inputs, output, useGpu,
                settings.Language ?? "auto", settings.Mode == TranscribeMode.TranslateToEnglish, choice.Threads);

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

    /// <summary>Saves session.json under the same lock as the transcript's writes (the live transcript uses it).</summary>
    public static bool SaveManifest(string dir, SessionManifest manifest)
    {
        lock (ManifestWriteGate)
        {
            try
            {
                manifest.Save(dir);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("could not update session.json", ex);
                return false;
            }
        }
    }

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
    internal static bool IsNoise(string text) =>
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

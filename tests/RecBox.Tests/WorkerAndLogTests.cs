using System.Diagnostics;
using System.IO.Compression;
using RecBox.Core;
using RecBox.Core.Session;
using RecBox.Core.Settings;
using RecBox.Core.Transcription;
using NAudio.Wave;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;
using Xunit;

namespace RecBox.Tests;

[CollectionDefinition("serial", DisableParallelization = true)]
public sealed class SerialCollection;

/// <summary>
/// Transcription runs in a worker process: a worker that dies with a native crash code marks only that
/// transcript failed (with the code and a readable reason), the app and the queue keep going; a GPU crash is
/// retried once on the CPU. The log rolls at its size cap; crash and diagnostics files hold what they promise.
/// </summary>
[Collection("serial")]
public sealed class WorkerAndLogTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly string _home = Path.Combine(Path.GetTempPath(), "recbox-tests", "home-" + Guid.NewGuid().ToString("N"));
    private readonly string? _oldHome = Environment.GetEnvironmentVariable("RECBOX_HOME");

    public WorkerAndLogTests()
    {
        Directory.CreateDirectory(_home);
        Environment.SetEnvironmentVariable("RECBOX_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("RECBOX_HOME", _oldHome);
        try { Directory.Delete(_home, true); } catch { }
    }

    private const int IllegalInstruction = unchecked((int)0xC000001D);
    private const int AccessViolation = unchecked((int)0xC0000005);

    /// <summary>A fake worker: cmd.exe that exits with a crash code, or writes a result file and exits 0.</summary>
    private static ProcessStartInfo Crash(int code) => new("cmd.exe", $"/c exit {code}");

    private static ProcessStartInfo Succeed(WorkerRequest r) =>
        new("cmd.exe", "/c echo @progress 1/1& echo {\"ok\":true,\"runtime\":\"Cpu\",\"tracks\":[[{\"start\":1.5,\"end\":3,\"text\":\" hello from the worker\"}]]}> \"" + r.Output + "\"");

    [Fact]
    public async Task AWorkerThatCrashes_MarksTheTranscriptFailed_WithExitCodeAndReason_AndTheQueueGoesOn()
    {
        var a = Session("a");
        var b = Session("b");
        var settings = Settings(useGpu: false);
        var crashing = new WorkerRunner { Launch = _ => Crash(IllegalInstruction) };
        var working = new WorkerRunner { Launch = Succeed };
        var queue = new TranscriptionQueue();
        TranscriptInfo? infoA = null, infoB = null;

        var jobA = queue.Enqueue(a, async () => infoA = await TranscriptionService.TranscribeSessionAsync(a, SessionManifest.Load(a)!, settings, null, CancellationToken.None, crashing));
        var jobB = queue.Enqueue(b, async () => infoB = await TranscriptionService.TranscribeSessionAsync(b, SessionManifest.Load(b)!, settings, null, CancellationToken.None, working));
        await Task.WhenAll(jobA, jobB).WaitAsync(Wait);

        Assert.NotNull(infoA);
        Assert.Equal("failed", infoA!.Status);
        Assert.Equal(IllegalInstruction, infoA.ExitCode);
        Assert.Contains("0xC000001D", infoA.Error);
        Assert.Contains("AVX", infoA.Error);
        var onDisk = SessionManifest.Load(a)!.Transcript;
        Assert.Equal("failed", onDisk.Status);
        Assert.Contains("0xC000001D", onDisk.Error);
        Assert.False(File.Exists(Path.Combine(a, "transcript.worker-tmp.json")));

        // this process (the app) is alive and the next queued transcript still ran
        Assert.Equal("done", infoB!.Status);
        Assert.Contains("hello from the worker", File.ReadAllText(Path.Combine(b, "transcript.md")));
        Assert.Equal(0, queue.Count);
        Assert.Contains("0xC000001D", File.ReadAllText(Log.FilePath));
    }

    [Fact]
    public async Task AGpuCrash_IsRetriedOnceOnTheCpu_AndTheResultSaysTheGpuFailed()
    {
        var dir = Session("gpu");
        var launches = new List<bool>();
        var runner = new WorkerRunner
        {
            Launch = r =>
            {
                lock (launches) launches.Add(r.Gpu);
                return r.Gpu ? Crash(AccessViolation) : Succeed(r);
            },
        };

        var info = await TranscriptionService.TranscribeSessionAsync(dir, SessionManifest.Load(dir)!, Settings(useGpu: true), null, CancellationToken.None, runner, Strong)
            .WaitAsync(Wait);

        Assert.Equal(new[] { true, false }, launches);
        Assert.Equal("done", info.Status);
        // the worker's stdout is captured into worker.log
        Assert.Contains("@progress 1/1", File.ReadAllText(Log.WorkerFilePath));
        Assert.True(info.GpuFailed);
        Assert.Contains("0xC0000005", info.RuntimeNote);
        Assert.Equal("Cpu", info.Runtime);
        Assert.True(SessionManifest.Load(dir)!.Transcript.GpuFailed);
    }

    [Fact]
    public async Task BothAttemptsCrash_TranscriptFailed_NoThirdTry()
    {
        var dir = Session("both");
        var n = 0;
        var runner = new WorkerRunner { Launch = _ => { Interlocked.Increment(ref n); return Crash(AccessViolation); } };
        var info = await TranscriptionService.TranscribeSessionAsync(dir, SessionManifest.Load(dir)!, Settings(useGpu: true), null, CancellationToken.None, runner, Strong)
            .WaitAsync(Wait);
        Assert.Equal(2, n);
        Assert.Equal("failed", info.Status);
        Assert.Contains("access violation", info.Error);
    }

    [Fact]
    public async Task AWorkerExeThatCannotStart_IsAFailedTranscript_NotAnException()
    {
        var dir = Session("nostart");
        var runner = new WorkerRunner { Launch = _ => new ProcessStartInfo(Path.Combine(_home, "missing-worker.exe")) };
        var info = await TranscriptionService.TranscribeSessionAsync(dir, SessionManifest.Load(dir)!, Settings(useGpu: false), null, CancellationToken.None, runner);
        Assert.Equal("failed", info.Status);
        Assert.Contains("could not start", info.Error);
    }

    [Fact]
    public void MemoryGuard_RefusesAModelThatDoesNotFit()
    {
        const long mb = 1024 * 1024;
        var refusal = MemoryGuard.Refusal("Large v3 Turbo", 574 * mb, 900 * mb);
        Assert.NotNull(refusal);
        Assert.Contains("Not enough free memory", refusal);
        Assert.Null(MemoryGuard.Refusal("Base", 60 * mb, 8L * 1024 * mb));
        Assert.Null(MemoryGuard.Refusal("Base", 60 * mb, 0)); // unknown free memory never blocks
    }

    [Fact]
    public void RuntimeOrder_FollowsTheCpuFeatures()
    {
        Assert.Equal(new[] { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx }, RuntimePicker.Order(true, true, true, true));
        Assert.Equal(new[] { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx }, RuntimePicker.Order(false, true, true, true));
        Assert.Equal(new[] { RuntimeLibrary.CpuNoAvx }, RuntimePicker.Order(true, true, false, true));
        Assert.Equal(new[] { RuntimeLibrary.CpuNoAvx }, RuntimePicker.Order(true, false, false, false));
        Assert.Contains("AVX", WorkerExit.Describe(IllegalInstruction));
        Assert.Equal("0xC0000005", WorkerExit.Hex(AccessViolation));
    }

    [Fact]
    public void TheLogRolls_AtItsSizeCap_AndKeepsAFewFiles()
    {
        var (oldMax, oldKeep) = (Log.MaxBytes, Log.Keep);
        try
        {
            Log.MaxBytes = 2000;
            Log.Keep = 3;
            for (var i = 0; i < 300; i++) Log.Info($"line {i:D4} " + new string('x', 80));
            var files = Directory.GetFiles(AppPaths.LogsDir, "recbox*.log");
            Assert.Equal(3, files.Length);
            Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 2000, $"{f} is {new FileInfo(f).Length} bytes"));
            Assert.Contains("line 0299", File.ReadAllText(Log.FilePath));
            Assert.Contains(DateTimeOffset.Now.ToString("yyyy-MM-dd"), File.ReadAllText(Log.FilePath));
            Assert.DoesNotContain("line 0000", string.Concat(files.Select(File.ReadAllText)));
        }
        finally
        {
            Log.MaxBytes = oldMax;
            Log.Keep = oldKeep;
        }
    }

    [Fact]
    public void ACrashFile_HoldsTheStackTraceAndTheSystem()
    {
        Exception ex;
        try { throw new InvalidOperationException("boom for the test"); }
        catch (Exception e) { ex = e; }
        var path = Log.WriteCrash("test", ex);
        Assert.NotNull(path);
        var text = File.ReadAllText(path!);
        Assert.Contains("boom for the test", text);
        Assert.Contains(nameof(ACrashFile_HoldsTheStackTraceAndTheSystem), text);
        Assert.Contains("AVX2=", text);
        Assert.Contains("RAM:", text);
    }

    [Fact]
    public void Diagnostics_ZipsLogsSettingsAndSystemInfo_NeverAudio()
    {
        Log.Info("diagnostics test line");
        SettingsStore.Save(new AppSettings());
        var rec = Path.Combine(_home, "recordings", "s1");
        Directory.CreateDirectory(rec);
        File.WriteAllBytes(Path.Combine(rec, "mic.wav"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(AppPaths.LogsDir, "stray.wav"), new byte[10]);

        var zip = Diagnostics.CreateZip(Path.Combine(_home, "out"));
        using var z = ZipFile.OpenRead(zip);
        var names = z.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("logs/recbox.log", names);
        Assert.Contains("settings.json", names);
        Assert.Contains("system-info.txt", names);
        Assert.DoesNotContain(names, n => n.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    }

    // ================= low-end laptops: Auto model choice on simulated hardware =================

    private const long Mb = 1024L * 1024;
    private const long Gb = 1024L * Mb;

    private static GpuAdapter Adapter(string name, uint vendor, long dedicated) =>
        new(name, vendor, dedicated, 4 * Gb, HardwareProbe.IsIntegrated(vendor, name, dedicated));

    /// <summary>GTX 860M laptop: Maxwell with 2 GB of its own memory, i7-4710HQ (4 cores, AVX2), 8 GB RAM.</summary>
    private static readonly HardwareProfile Gtx860M = new(8 * Gb, 5 * Gb, 4, 8, true, Adapter("NVIDIA GeForce GTX 860M", 0x10DE, 2028 * Mb), true);
    /// <summary>Intel HD/UHD graphics only (shared memory), 4 cores, 8 GB RAM.</summary>
    private static readonly HardwareProfile IntelIgpu = new(8 * Gb, 4 * Gb, 4, 8, true, Adapter("Intel(R) UHD Graphics 620", 0x8086, 128 * Mb), true);
    /// <summary>AMD APU with a 2 GB carve-out that DXGI reports as its own memory.</summary>
    private static readonly HardwareProfile AmdApu = new(16 * Gb, 9 * Gb, 4, 8, true, Adapter("AMD Radeon(TM) Graphics", 0x1002, 2 * Gb), true);
    /// <summary>4 GB laptop, 2 cores, integrated graphics.</summary>
    private static readonly HardwareProfile Ram4Gb = new(4 * Gb, 1700 * Mb, 2, 4, true, Adapter("Intel(R) HD Graphics 520", 0x8086, 128 * Mb), true);
    /// <summary>Older processor without AVX2.</summary>
    private static readonly HardwareProfile NoAvx2 = new(8 * Gb, 5 * Gb, 4, 4, false, Adapter("NVIDIA GeForce GT 730", 0x10DE, 2 * Gb), true);
    /// <summary>Desktop with a 12 GB card.</summary>
    private static readonly HardwareProfile Strong = new(32 * Gb, 20 * Gb, 8, 16, true, Adapter("NVIDIA GeForce RTX 3060", 0x10DE, 12 * Gb), true);

    public static IEnumerable<object[]> LowEndProfiles() => new[]
    {
        new object[] { "gtx860m-2gb" }, new object[] { "intel-igpu" }, new object[] { "amd-apu" }, new object[] { "ram-4gb" }, new object[] { "no-avx2" },
    };

    private static HardwareProfile Profile(string name) => name switch
    {
        "gtx860m-2gb" => Gtx860M,
        "intel-igpu" => IntelIgpu,
        "amd-apu" => AmdApu,
        "ram-4gb" => Ram4Gb,
        "no-avx2" => NoAvx2,
        _ => Strong,
    };

    [Theory]
    [MemberData(nameof(LowEndProfiles))]
    public void Auto_OnLowEndLaptops_PicksTinyOrBase_NeverSmall(string profile)
    {
        foreach (var use in new[] { TranscriptionUse.File, TranscriptionUse.Live })
        {
            var choice = ModelPicker.Pick(Profile(profile), use, gpuAllowed: true);
            Assert.Contains(choice.Model.Key, new[] { "tiny", "base" });
            Assert.False(choice.Model.Key is "small" or "large-v3-turbo", $"{profile} {use}: {choice.Reason}");
            Assert.StartsWith("Auto picked", choice.Reason);
            Assert.Null(choice.Refusal);
        }
    }

    [Fact]
    public void Auto_PerProfile_ModelDeviceAndThreads()
    {
        var file = ModelPicker.Pick(Gtx860M, TranscriptionUse.File, true);
        Assert.Equal("base", file.Model.Key);
        Assert.True(file.Gpu, file.Reason); // 2 GB of its own memory holds Base plus headroom
        Assert.Equal(3, file.Threads);
        Assert.Equal("base", ModelPicker.Pick(Gtx860M, TranscriptionUse.Live, true).Model.Key);

        var igpuFile = ModelPicker.Pick(IntelIgpu, TranscriptionUse.File, true);
        var igpuLive = ModelPicker.Pick(IntelIgpu, TranscriptionUse.Live, true);
        Assert.Equal("base", igpuFile.Model.Key);
        Assert.Equal("tiny", igpuLive.Model.Key); // low-end live text: Tiny on the processor
        Assert.False(igpuFile.Gpu);
        Assert.False(igpuLive.Gpu);
        Assert.Contains("integrated", igpuFile.Reason);

        Assert.False(ModelPicker.Pick(AmdApu, TranscriptionUse.File, true).Gpu);
        Assert.Equal("tiny", ModelPicker.Pick(Ram4Gb, TranscriptionUse.File, true).Model.Key);
        Assert.Equal("tiny", ModelPicker.Pick(Ram4Gb, TranscriptionUse.Live, true).Model.Key);
        Assert.Equal(1, ModelPicker.Pick(Ram4Gb, TranscriptionUse.Live, true).Threads);
        var noAvx = ModelPicker.Pick(NoAvx2, TranscriptionUse.File, true);
        Assert.Equal("tiny", noAvx.Model.Key);
        Assert.False(noAvx.Gpu);

        var strong = ModelPicker.Pick(Strong, TranscriptionUse.File, true);
        Assert.Equal("small", strong.Model.Key);
        Assert.True(strong.Gpu);
        Assert.Equal(7, strong.Threads);
        Assert.False(ModelPicker.Pick(Strong, TranscriptionUse.File, gpuAllowed: false).Gpu); // UseGpu off (or turned off by RecBox) wins
    }

    [Fact]
    public void GpuFit_NeedsOwnMemoryPlusHeadroom_NeverIntegrated()
    {
        Assert.True(HardwareProbe.IsIntegrated(0x8086, "Intel(R) UHD Graphics 620", 128 * Mb));
        Assert.False(HardwareProbe.IsIntegrated(0x8086, "Intel(R) Arc(TM) A770 Graphics", 16 * Gb));
        Assert.True(HardwareProbe.IsIntegrated(0x1002, "AMD Radeon(TM) Graphics", 2 * Gb));
        Assert.True(HardwareProbe.IsIntegrated(0x1002, "AMD Radeon 780M", 2 * Gb));
        Assert.False(HardwareProbe.IsIntegrated(0x1002, "Radeon RX 580 Series", 8 * Gb));
        Assert.False(HardwareProbe.IsIntegrated(0x10DE, "NVIDIA GeForce GTX 860M", 2028 * Mb));

        Assert.True(GpuFit.Check(Gtx860M, WhisperModels.Get("base"), true).Use);
        Assert.False(GpuFit.Check(Gtx860M, WhisperModels.Get("large-v3-turbo"), true).Use);
        Assert.False(GpuFit.Check(IntelIgpu, WhisperModels.Get("tiny"), true).Use);
        Assert.False(GpuFit.Check(Gtx860M with { VulkanLoader = false }, WhisperModels.Get("tiny"), true).Use);
        Assert.False(GpuFit.Check(Gtx860M, WhisperModels.Get("tiny"), allowed: false).Use);
        Assert.False(GpuFit.Check(Gtx860M with { Gpu = null }, WhisperModels.Get("tiny"), true).Use);
    }

    [Fact]
    public void Threads_LeaveOneCoreForRecording()
    {
        Assert.Equal(1, ModelPicker.Threads(1));
        Assert.Equal(1, ModelPicker.Threads(2));
        Assert.Equal(3, ModelPicker.Threads(4));
        Assert.Equal(7, ModelPicker.Threads(8));
    }

    [Fact]
    public void ShortMemory_AutoDowngrades_AndARefusalSuggestsTheSmallerModel()
    {
        var busy = Strong with { FreeRam = 1200 * Mb };
        var choice = ModelPicker.Pick(busy, TranscriptionUse.File, true);
        Assert.Equal("tiny", choice.Model.Key);
        Assert.Contains("free", choice.Reason);

        var refusal = MemoryGuard.Refusal("Small", 190 * Mb, 500 * Mb, MemoryGuard.Smaller("small"));
        Assert.NotNull(refusal);
        Assert.Contains("Not enough free memory", refusal);
        Assert.Contains("Base or Tiny", refusal);
        Assert.Equal("Tiny", MemoryGuard.Smaller("base"));
        Assert.Null(MemoryGuard.Smaller("tiny"));
    }

    [Fact]
    public void Plan_AutoPickNotDownloaded_UsesASmallerDownloadedModel_AndAFixedModelStaysFixed()
    {
        var auto = new AppSettings { WhisperModel = "auto", UseGpu = true };
        var plan = ModelPicker.Plan(auto, IntelIgpu, TranscriptionUse.File, m => m.Key == "tiny");
        Assert.Equal("tiny", plan.Model.Key);
        Assert.Contains("not downloaded", plan.Reason);

        var fixedSmall = new AppSettings { WhisperModel = "small", UseGpu = true };
        var f = ModelPicker.Plan(fixedSmall, IntelIgpu, TranscriptionUse.File, _ => true);
        Assert.Equal("small", f.Model.Key);
        Assert.False(f.Gpu);
        Assert.Equal("auto", new AppSettings().WhisperModel); // Auto is the new default
        Assert.Equal("tiny", WhisperModels.All[0].Key);
        Assert.Equal(QuantizationType.Q5_1, WhisperModels.Get("tiny").Quant);
    }

    [Fact]
    public async Task Transcribe_WithAuto_LogsTheChoice_UsesThreads_AndKeepsTheIgpuOffTheGpu()
    {
        var dir = Session("auto-igpu");
        var requests = new List<WorkerRequest>();
        var runner = new WorkerRunner { Launch = r => { lock (requests) requests.Add(r); return Succeed(r); } };
        var settings = Settings(useGpu: true);
        settings.WhisperModel = "auto";
        var info = await TranscriptionService.TranscribeSessionAsync(dir, SessionManifest.Load(dir)!, settings, null, CancellationToken.None, runner, IntelIgpu)
            .WaitAsync(Wait);

        Assert.Equal("done", info.Status);
        Assert.Equal("base", info.Model);
        var r = Assert.Single(requests);
        Assert.False(r.Gpu);
        Assert.Equal(3, r.Threads);
        Assert.Contains("--threads", WorkerRunner.Arguments(r));
        var log = File.ReadAllText(Log.FilePath);
        Assert.Contains("Auto picked Base", log);
        Assert.Contains("integrated", log);
    }

    [Fact]
    public async Task TheWorker_RunsBelowNormalPriority()
    {
        var dir = Session("prio");
        ProcessPriorityClass? seen = null;
        var runner = new WorkerRunner
        {
            Launch = r => new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul & echo {\"ok\":true,\"runtime\":\"Cpu\",\"tracks\":[[]]}> \"" + r.Output + "\""),
            Started = p => { try { seen = p.PriorityClass; } catch { } },
        };
        var info = await TranscriptionService.TranscribeSessionAsync(dir, SessionManifest.Load(dir)!, Settings(useGpu: false), null, CancellationToken.None, runner, IntelIgpu)
            .WaitAsync(Wait);
        Assert.Equal("done", info.Status);
        Assert.Equal(ProcessPriorityClass.BelowNormal, seen);
    }

    [Fact]
    public void AGraphicsCardSlowerThanTheProcessor_IsTurnedOff()
    {
        var s = new AppSettings();
        Assert.Null(GpuSpeed.Record(s, "Cpu", "base", 60, 20)); // 3x on the processor
        var reason = GpuSpeed.Record(s, "Vulkan", "base", 60, 40); // 1.5x on the card
        Assert.NotNull(reason);
        Assert.Contains("slower than the processor", reason);
        var t = new AppSettings();
        Assert.Null(GpuSpeed.Record(t, "Cpu", "base", 60, 30));
        Assert.Null(GpuSpeed.Record(t, "Vulkan", "base", 60, 10));
        Assert.Equal(6.0, t.Clone().Speeds["gpu:base"]);
    }

    // ================= live text: voice chunking =================

    private const int Rate = 16000;

    private static float[] Noise(double seconds, double dbfs, int seed = 1)
    {
        var rnd = new Random(seed);
        var a = Math.Pow(10, dbfs / 20) * Math.Sqrt(3); // uniform noise RMS = a / sqrt(3)
        var x = new float[(int)(seconds * Rate)];
        for (var i = 0; i < x.Length; i++) x[i] = (float)((rnd.NextDouble() * 2 - 1) * a);
        return x;
    }

    /// <summary>Speech-like: 250 ms tone syllables with 100 ms gaps, over quiet noise.</summary>
    private static float[] Syllables(double seconds, double dbfs, double noiseDb = -60, int seed = 2)
    {
        var x = Noise(seconds, noiseDb, seed);
        var a = Math.Pow(10, dbfs / 20) * Math.Sqrt(2);
        for (var i = 0; i < x.Length; i++)
        {
            var t = i / (double)Rate;
            if (t % 0.35 < 0.25) x[i] += (float)(a * Math.Sin(2 * Math.PI * (180 + 40 * Math.Sin(t * 3)) * t));
        }
        return x;
    }

    private static float[] Cat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    private static List<SpeechChunk> Chunk(float[] audio, bool flush = true)
    {
        var c = new VoiceChunker(LiveSource.Mic);
        var list = new List<SpeechChunk>();
        for (var i = 0; i < audio.Length; i += 1600) list.AddRange(c.Push(audio.AsSpan(i, Math.Min(1600, audio.Length - i))));
        if (flush && c.Flush() is { } last) list.Add(last);
        return list;
    }

    [Fact]
    public void Vad_QuietRoomOrSteadyNoise_GivesNoChunks()
    {
        Assert.Empty(Chunk(Noise(10, -60)));
        Assert.Empty(Chunk(Noise(30, -40)));
        Assert.Empty(Chunk(new float[Rate * 10]));
    }

    [Fact]
    public void Vad_OnePhrase_IsOneChunk_AtItsTime()
    {
        var chunks = Chunk(Cat(Noise(3, -60, 3), Syllables(3, -25), Noise(3, -60, 4)));
        var c = Assert.Single(chunks);
        Assert.InRange(c.Start.TotalSeconds, 2.5, 3.1);
        Assert.InRange(c.Seconds, 2.9, 4.5);
        Assert.Equal(LiveSource.Mic, c.Source);
        Assert.Equal((int)Math.Round(c.Seconds * Rate), c.Samples.Length);
    }

    [Fact]
    public void Vad_LongSpeech_IsCutAtMaxLength()
    {
        var chunks = Chunk(Cat(Noise(2, -60, 5), Syllables(40, -25), Noise(2, -60, 6)));
        Assert.True(chunks.Count >= 3, $"{chunks.Count} chunk(s)");
        Assert.All(chunks, c => Assert.True(c.Seconds <= 15.01, $"{c.Seconds} s"));
        Assert.All(chunks.Take(chunks.Count - 1), c => Assert.True(c.Seconds >= 2, $"{c.Seconds} s"));
        Assert.InRange(chunks.Sum(c => c.Seconds), 36, 42);
        for (var i = 1; i < chunks.Count; i++) Assert.True(chunks[i].Start >= chunks[i - 1].End);
    }

    [Fact]
    public void Vad_AShortYes_IsKept_AClickIsNot()
    {
        var yes = Chunk(Cat(Noise(3, -60, 7), Syllables(0.6, -25), Noise(3, -60, 8)));
        var c = Assert.Single(yes);
        Assert.True(c.Seconds >= 2, $"{c.Seconds} s");
        var click = Cat(Noise(3, -60, 9), Syllables(0.06, -20), Noise(3, -60, 10));
        Assert.Empty(Chunk(click));
    }

    [Fact]
    public void Vad_NoiseAfterDigitalSilence_IsLearnedQuickly()
    {
        // loopback while nothing plays is digital silence; the floor must not stay at -120 so steady noise is speech forever
        var chunks = Chunk(Cat(new float[Rate * 5], Noise(25, -40, 11)));
        Assert.True(chunks.Count <= 1, $"{chunks.Count} chunk(s)");
        Assert.All(chunks, c => Assert.True(c.Start.TotalSeconds < 10, $"chunk at {c.Start}"));
        // speech after digital silence (the other side starts talking) is still found
        Assert.NotEmpty(Chunk(Cat(new float[Rate * 3], Syllables(3, -25, -90), new float[Rate * 3])));
    }

    [Fact]
    public void Vad_FlushReturnsThePhraseInProgress()
    {
        var c = new VoiceChunker(LiveSource.Speaker);
        Assert.Empty(c.Push(Cat(Noise(2, -60), Syllables(1.5, -25))));
        var last = c.Flush();
        Assert.NotNull(last);
        Assert.Equal(LiveSource.Speaker, last!.Source);
    }

    [Fact]
    public async Task LiveFeed_NeverBlocksTheCaptureThread_WhenTheLiveSideIsStuck()
    {
        using var stuck = new ManualResetEventSlim(false);
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var pushes = 0;
        var chunks = 0;
        var feed = new LiveFeed(LiveSource.Mic, () => TimeSpan.FromMilliseconds(10 * pushes), _ =>
        {
            Interlocked.Increment(ref chunks);
            stuck.Wait(TimeSpan.FromSeconds(20)); // a live side that does not come back
        });
        var buf = new byte[480 * 2 * 4]; // 10 ms of 48 kHz stereo float
        var slowest = TimeSpan.Zero;
        void PushAll(float[] mono, int buffers)
        {
            for (var i = 0; i < buffers; i++)
            {
                for (var f = 0; f < 480; f++)
                {
                    var s = mono[(i * 480 + f) / 3 % mono.Length];
                    BitConverter.TryWriteBytes(buf.AsSpan(f * 8), s);
                    BitConverter.TryWriteBytes(buf.AsSpan(f * 8 + 4), s);
                }
                var one = Stopwatch.StartNew();
                pushes++;
                feed.Push(buf, buf.Length, fmt);
                if (one.Elapsed > slowest) slowest = one.Elapsed;
            }
        }

        // one phrase: the live side gets a chunk and gets stuck on it
        var phrase = Cat(Noise(2, -60), Syllables(2, -20), Noise(1.5, -60));
        PushAll(phrase, phrase.Length / 160);
        await WaitFor(() => Volatile.Read(ref chunks) > 0, TimeSpan.FromSeconds(20));

        // a minute more of capture while it is stuck: every push still returns at once
        PushAll(Syllables(60, -20), 6000);
        Assert.True(slowest < TimeSpan.FromMilliseconds(50), $"slowest push {slowest.TotalMilliseconds:0} ms");
        Assert.True(feed.Dropped >= 6000 - 300, $"{feed.Dropped} dropped"); // only the live copy is dropped
        stuck.Set();
        await feed.CompleteAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void LiveFeed_ConvertsPcmStereoToMono()
    {
        var fmt = new WaveFormat(48000, 16, 2);
        var data = new byte[8];
        BitConverter.TryWriteBytes(data.AsSpan(0), (short)16384);
        BitConverter.TryWriteBytes(data.AsSpan(2), (short)0);
        BitConverter.TryWriteBytes(data.AsSpan(4), (short)-32768);
        BitConverter.TryWriteBytes(data.AsSpan(6), (short)-32768);
        var dst = Array.Empty<float>();
        Assert.Equal(2, LiveFeed.ToMono(data, data.Length, fmt, ref dst));
        Assert.Equal(0.25f, dst[0], 3);
        Assert.Equal(-1f, dst[1], 3);
    }

    // ================= live text: worker crash and restart, falling behind =================

    private static SpeechChunk LiveChunk(double start, double end, LiveSource src = LiveSource.Mic) =>
        new(src, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), new float[(int)((end - start) * Rate)]);

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout) throw new TimeoutException("condition not met in time");
            await Task.Delay(50);
        }
    }

    /// <summary>An in-process live worker: answers, crashes (throws) or fails to start as the test says.</summary>
    private sealed class FakeLiveWorker(LiveWorkerConfig config, Func<LiveWorkerConfig, bool> failStart, Func<LiveRequest, LiveReply?> answer) : ILiveWorker
    {
        public string? Runtime { get; private set; }

        public Task StartAsync(TimeSpan timeout, CancellationToken ct)
        {
            if (failStart(config)) throw new LiveWorkerCrashedException("fake start failure");
            Runtime = config.Gpu ? "Vulkan" : "Cpu";
            return Task.CompletedTask;
        }

        public Task<LiveReply> TranscribeAsync(LiveRequest request, TimeSpan timeout, CancellationToken ct) =>
            answer(request) is { } a ? Task.FromResult(a) : throw new LiveWorkerCrashedException("fake native crash", AccessViolation);

        public void Dispose() { }
    }

    private static LiveReply Said(LiveRequest r, string text) =>
        new() { Id = r.Id, Ok = true, Segments = { new WorkerSegment { Start = 0.5, End = 1.5, Text = " " + text } } };

    private LiveTranscriber Live(Func<LiveWorkerConfig, ILiveWorker> factory, Func<TimeSpan> clock, string model = "base", bool gpu = false) =>
        new(new LiveTranscriberOptions
        {
            Choice = new ModelChoice(WhisperModels.Get(model), gpu, 1, "test"),
            Tiny = WhisperModels.Get("tiny"),
            Clock = clock,
            WorkerFactory = factory,
            TempDir = Path.Combine(_home, "live-tmp-" + Guid.NewGuid().ToString("N")),
            StartTimeout = TimeSpan.FromSeconds(30),
            ChunkTimeout = TimeSpan.FromSeconds(30),
        });

    /// <summary>A real child process speaking the live protocol (PowerShell, no window): exits with an access violation on request 2.</summary>
    private static ProcessStartInfo FakeLiveProcess(LiveWorkerConfig _)
    {
        const string script = """
            [Console]::Out.WriteLine('@ready {"ok":true,"runtime":"Cpu"}')
            [Console]::Out.Flush()
            while (($l = [Console]::In.ReadLine()) -ne $null) {
              $r = $l | ConvertFrom-Json
              if ($r.id -eq 2) { exit -1073741819 }
              [Console]::Out.WriteLine('@live {"id":' + $r.id + ',"ok":true,"segments":[{"start":0.5,"end":1.5,"text":" chunk ' + $r.id + '"}]}')
              [Console]::Out.Flush()
            }
            """;
        var psi = new ProcessStartInfo("powershell.exe");
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
            psi.ArgumentList.Add(a);
        return psi;
    }

    [Fact]
    public async Task LiveWorkerProcess_ThatCrashes_IsRestarted_AndLiveTextGoesOn()
    {
        var starts = 0;
        var live = Live(c => { Interlocked.Increment(ref starts); return new ProcessLiveWorker(c, FakeLiveProcess); }, () => TimeSpan.FromSeconds(20));
        live.Start();
        live.Enqueue(LiveChunk(1, 4));
        await WaitFor(() => live.Lines.Any(l => l.Text == "chunk 1"), TimeSpan.FromSeconds(60));
        live.Enqueue(LiveChunk(5, 8, LiveSource.Speaker)); // request 2: the worker dies with 0xC0000005
        live.Enqueue(LiveChunk(9, 12));
        await WaitFor(() => live.Lines.Any(l => l.Text == "chunk 3"), TimeSpan.FromSeconds(60));
        var lines = await live.StopAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, starts);
        Assert.Equal(1, live.Restarts);
        Assert.False(live.Failed);
        Assert.Contains(lines, l => l.IsNote && l.Text.Contains("could not transcribe 00:05 to 00:08") && l.Text.Contains("0xC0000005"));
        var first = lines.First(l => l.Text == "chunk 1");
        Assert.Equal("You", first.Speaker);
        Assert.Equal(1.5, first.Start.TotalSeconds, 2);
        Assert.Contains("0xC0000005", File.ReadAllText(Log.FilePath));
    }

    [Fact]
    public async Task LiveWorker_CrashingThreeTimesInAMinute_StopsLiveTextAndSaysSo()
    {
        var starts = 0;
        var live = Live(c => { Interlocked.Increment(ref starts); return new FakeLiveWorker(c, _ => false, _ => null); }, () => TimeSpan.FromSeconds(20));
        for (var i = 0; i < 5; i++) live.Enqueue(LiveChunk(i * 3, i * 3 + 2.5));
        live.Start();
        await WaitFor(() => live.Failed, TimeSpan.FromSeconds(20));
        await live.StopAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(3, starts);
        Assert.Equal(3, live.Restarts);
        Assert.Contains("crashed 3 times", live.Status);
        Assert.Contains("recording goes on", live.Status);
    }

    [Fact]
    public async Task LiveWorker_GpuFailure_FallsBackToTheProcessor_AndIsReported()
    {
        var configs = new List<LiveWorkerConfig>();
        string? gpuReason = null;
        var live = Live(c =>
        {
            lock (configs) configs.Add(c);
            return new FakeLiveWorker(c, cfg => cfg.Gpu, r => Said(r, "hello"));
        }, () => TimeSpan.FromSeconds(10), gpu: true);
        live.GpuFailed += r => gpuReason = r;
        live.Enqueue(LiveChunk(1, 4));
        live.Start();
        await WaitFor(() => live.Lines.Any(l => l.Text == "hello"), TimeSpan.FromSeconds(20));
        await live.StopAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { true, false }, configs.Select(c => c.Gpu));
        Assert.NotNull(gpuReason);
        Assert.False(live.OnGpu);
    }

    [Fact]
    public void LivePolicy_SwitchesToTinyThenSkips_AndShowsTheLag()
    {
        Assert.Equal(LiveAction.Keep, LivePolicy.Decide(5, false, true));
        Assert.Equal(LiveAction.SwitchToTiny, LivePolicy.Decide(13, false, true));
        Assert.Equal(LiveAction.Keep, LivePolicy.Decide(13, true, true));
        Assert.Equal(LiveAction.Keep, LivePolicy.Decide(13, false, false));
        Assert.Equal(LiveAction.SkipBacklog, LivePolicy.Decide(31, true, true));
        Assert.Equal(LiveAction.SkipBacklog, LivePolicy.Decide(31, false, false));
        Assert.Equal("Live", LivePolicy.LagText(0.4));
        Assert.Equal("~3 s behind", LivePolicy.LagText(3.2));
    }

    [Fact]
    public async Task LiveText_FarBehind_DropsToTiny_ThenSkipsChunks_AndSaysSo()
    {
        Directory.CreateDirectory(AppPaths.ModelsDir);
        File.WriteAllBytes(WhisperModels.Get("tiny").Path, new byte[16]);
        var models = new List<string>();
        var statuses = new List<string>();
        // the recording is at 60 s and the queued speech ended at 20-29 s: 30 s or more behind
        var live = Live(c => { lock (models) models.Add(c.ModelKey); return new FakeLiveWorker(c, _ => false, r => Said(r, $"text {r.Id}")); },
            () => TimeSpan.FromSeconds(60));
        live.StatusChanged += s => { lock (statuses) statuses.Add(s); };
        live.Enqueue(LiveChunk(15, 20));
        live.Enqueue(LiveChunk(20, 23));
        live.Enqueue(LiveChunk(23, 26));
        live.Enqueue(LiveChunk(26, 29));
        live.Start();
        await WaitFor(() => live.Lines.Any(l => !l.IsNote), TimeSpan.FromSeconds(20));
        var lines = await live.StopAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("tiny", models.First());
        Assert.Equal("tiny", live.ModelKey);
        Assert.Contains(statuses, s => s.Contains("switched live text to Tiny"));
        Assert.Contains(lines, l => l.IsNote && l.Text.Contains("skipped"));
        Assert.True(live.SkippedSeconds > 0);
    }

    [Fact]
    public void LiveScroll_FollowsUntilTheUserScrollsUp()
    {
        var f = new LiveScrollFollow();
        Assert.True(f.OnScrollChanged(0, 0, 40)); // new line: scroll to the end
        Assert.False(f.OnScrollChanged(100, 500, 0)); // the user scrolled up
        Assert.False(f.Following);
        Assert.False(f.OnScrollChanged(100, 520, 20)); // new line while reading back: stay put
        f.OnScrollChanged(520, 520, 0); // back at the bottom
        Assert.True(f.Following);
        f.OnScrollChanged(10, 520, 0);
        f.JumpToLatest();
        Assert.True(f.Following);
    }

    [Fact]
    public void LiveTranscript_IsSavedBesideTheRecording_TextAndJson()
    {
        var dir = Path.Combine(_home, "live-save");
        Directory.CreateDirectory(dir);
        var lines = new[]
        {
            new LiveLine(TimeSpan.FromSeconds(4.2), TimeSpan.FromSeconds(6), "Others", "Can you hear me?"),
            new LiveLine(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3), "You", "Hello everyone"),
            new LiveLine(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(9), "RecBox", "(skipped 5 s of speech to catch up)", IsNote: true),
        };
        var files = LiveTranscriptStore.Save(dir, "s1", lines, "tiny", "Cpu", false, 1, 5);
        Assert.Equal(new[] { "live-transcript.txt", "live-transcript.json" }, files);
        var txt = File.ReadAllText(Path.Combine(dir, "live-transcript.txt"));
        Assert.True(txt.IndexOf("[00:01] You: Hello everyone", StringComparison.Ordinal) < txt.IndexOf("[00:04] Others: Can you hear me?", StringComparison.Ordinal));
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "live-transcript.json")));
        var arr = json.RootElement.GetProperty("lines");
        Assert.Equal(3, arr.GetArrayLength());
        Assert.Equal("mic", arr[0].GetProperty("source").GetString());
        Assert.Equal(1.5, arr[0].GetProperty("start").GetDouble());
        Assert.Equal("speaker", arr[1].GetProperty("source").GetString());
        Assert.Equal("note", arr[2].GetProperty("source").GetString());
        Assert.Equal("tiny", json.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void LiveText_IsOffByDefault_UnlessTranscriptionIsOn()
    {
        Assert.False(new AppSettings().LiveTranscribeOn);
        Assert.True(new AppSettings { TranscribeEnabled = true }.LiveTranscribeOn);
        Assert.False(new AppSettings { TranscribeEnabled = true, LiveTranscribe = false }.LiveTranscribeOn);
        Assert.True(new AppSettings { LiveTranscribe = true }.LiveTranscribeOn);
    }

    private AppSettings Settings(bool useGpu)
    {
        Directory.CreateDirectory(AppPaths.ModelsDir);
        var model = WhisperModels.Get("base");
        if (!File.Exists(model.Path)) File.WriteAllBytes(model.Path, new byte[4096]); // the fake worker never reads it
        return new AppSettings { OutputFolder = _home, TranscribeEnabled = true, WhisperModel = "base", UseGpu = useGpu };
    }

    /// <summary>A saved session with one mic track and its 16 kHz copy (the worker input).</summary>
    private string Session(string id)
    {
        var dir = Path.Combine(_home, "sessions", id);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "mic_16k.wav"), new byte[44]);
        new SessionManifest
        {
            Id = id,
            Sources = { new SourceTrack { Kind = "mic", File = "mic.wav", SpeechFile = "mic_16k.wav" } },
            Transcript = new TranscriptInfo { Status = "pending" },
        }.Save(dir);
        return dir;
    }
}

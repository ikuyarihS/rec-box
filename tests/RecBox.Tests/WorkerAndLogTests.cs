using System.Diagnostics;
using System.IO.Compression;
using RecBox.Core;
using RecBox.Core.Session;
using RecBox.Core.Settings;
using RecBox.Core.Transcription;
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

        var info = await TranscriptionService.TranscribeSessionAsync(dir, SessionManifest.Load(dir)!, Settings(useGpu: true), null, CancellationToken.None, runner)
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
        var info = await TranscriptionService.TranscribeSessionAsync(dir, SessionManifest.Load(dir)!, Settings(useGpu: true), null, CancellationToken.None, runner)
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

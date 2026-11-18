using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;
using Whisper.net.LibraryLoader;

namespace RecBox.Core.Transcription;

// ---------------------------------------------------------------------------------------------
// Live text while recording: capture tap -> LiveFeed (16 kHz mono) -> VoiceChunker (energy VAD)
// -> LiveTranscriber queue -> one long-lived live worker process -> lines on screen and on disk.
// Nothing here can block or slow the recording: the tap only copies a buffer into a bounded queue
// that drops the oldest buffer when full, and Whisper runs in another process below normal priority.
// ---------------------------------------------------------------------------------------------

/// <summary>One request line to the live worker: transcribe this chunk WAV.</summary>
public sealed class LiveRequest
{
    public int Id { get; set; }
    public string Input { get; set; } = "";
    public bool Translate { get; set; }
}

/// <summary>The live worker's answer to one request.</summary>
public sealed class LiveReply
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public long Ms { get; set; }
    public List<WorkerSegment> Segments { get; set; } = new();
}

/// <summary>The live worker's first line, once the model is loaded (or why it could not load).</summary>
public sealed class LiveReady
{
    public bool Ok { get; set; }
    public string? Runtime { get; set; }
    public string? Error { get; set; }
}

public enum LiveSource
{
    Mic,
    Speaker,
}

/// <summary>A stretch of speech from one source, 16 kHz mono, with its place in the recording.</summary>
public sealed record SpeechChunk(LiveSource Source, TimeSpan Start, TimeSpan End, float[] Samples)
{
    public double Seconds => (End - Start).TotalSeconds;
}

/// <summary>One line of live text: "You" (mic), "Others" (system audio) or a RecBox note.</summary>
public sealed record LiveLine(TimeSpan Start, TimeSpan End, string Speaker, string Text, bool IsNote = false)
{
    public static string SpeakerOf(LiveSource s) => s == LiveSource.Mic ? "You" : "Others";

    public string Source => IsNote ? "note" : Speaker == "You" ? "mic" : "speaker";

    public string Stamp => Start.ToString(Start.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

    public override string ToString() => IsNote ? $"[{Stamp}] {Text}" : $"[{Stamp}] {Speaker}: {Text}";
}

/// <summary>
/// A simple energy voice detector that cuts 16 kHz mono audio into chunks of speech for Whisper.
/// 30 ms frames; a frame is speech when it is 9 dB over the noise floor (the 15th percentile of the last 4 s)
/// and over -55 dBFS. A chunk starts with 300 ms of pre-roll, ends after 600 ms of silence once it is at least
/// 2 s long, and is cut at 15 s. A chunk with under 0.3 s of speech frames (a click, a cough) is dropped.
/// Digital silence (loopback while nothing plays) never lowers the floor below -64 dBFS, so the floor is not
/// dragged to -120 and steady room noise is learned within about 3.5 s.
/// </summary>
public sealed class VoiceChunker
{
    public const int Rate = 16000;
    public const int FrameSamples = 480;
    public const double DigitalSilenceDbfs = -90;
    private const int FloorWindowFrames = 133;
    private const double FloorPercentile = 0.15;

    private readonly LiveSource _source;
    private readonly float[] _frame = new float[FrameSamples];
    private readonly Queue<float[]> _preroll = new();
    private readonly List<float> _chunk = new();
    private readonly Queue<double> _floorWindow = new();
    private readonly double[] _sortBuf = new double[FloorWindowFrames];
    private int _frameFill;
    private long _frames;
    private bool _inChunk;
    private long _chunkStart;
    private int _voiced;
    private int _silenceRun;

    public VoiceChunker(LiveSource source) => _source = source;

    public double MinSeconds { get; init; } = 2;
    public double MaxSeconds { get; init; } = 15;
    public double HangoverSeconds { get; init; } = 0.6;
    public double PreRollSeconds { get; init; } = 0.3;
    public double MinVoicedSeconds { get; init; } = 0.3;
    public double MarginDb { get; init; } = 9;
    public double GateDbfs { get; init; } = -55;

    private static int Frames(double seconds) => (int)Math.Ceiling(seconds * Rate / FrameSamples);

    /// <summary>Samples taken in so far (the chunker's clock).</summary>
    public long Position => _frames * FrameSamples + _frameFill;

    public List<SpeechChunk> Push(ReadOnlySpan<float> samples)
    {
        var output = new List<SpeechChunk>();
        foreach (var s in samples)
        {
            _frame[_frameFill++] = s;
            if (_frameFill == FrameSamples)
            {
                ProcessFrame(output);
                _frameFill = 0;
            }
        }
        return output;
    }

    /// <summary>End of input: the chunk in progress is returned when it holds enough speech.</summary>
    public SpeechChunk? Flush()
    {
        if (!_inChunk) return null;
        var output = new List<SpeechChunk>();
        Emit(output);
        return output.FirstOrDefault();
    }

    private double? Floor()
    {
        var n = _floorWindow.Count;
        if (n == 0) return null;
        _floorWindow.CopyTo(_sortBuf, 0);
        Array.Sort(_sortBuf, 0, n);
        return _sortBuf[(int)(FloorPercentile * (n - 1))];
    }

    private void ProcessFrame(List<SpeechChunk> output)
    {
        double sum = 0;
        foreach (var x in _frame) sum += (double)x * x;
        var db = sum <= 0 ? -200 : 10 * Math.Log10(sum / FrameSamples);

        var speech = false;
        if (db > DigitalSilenceDbfs)
            speech = Floor() is { } floor && db > floor + MarginDb && db > GateDbfs;
        _floorWindow.Enqueue(Math.Max(db, GateDbfs - MarginDb));
        while (_floorWindow.Count > FloorWindowFrames) _floorWindow.Dequeue();

        var frame = (float[])_frame.Clone();
        var frameStart = _frames * FrameSamples;
        _frames++;

        if (!_inChunk)
        {
            if (!speech)
            {
                _preroll.Enqueue(frame);
                while (_preroll.Count > Frames(PreRollSeconds)) _preroll.Dequeue();
                return;
            }
            _inChunk = true;
            _chunkStart = frameStart - (long)_preroll.Count * FrameSamples;
            foreach (var p in _preroll) _chunk.AddRange(p);
            _preroll.Clear();
            _chunk.AddRange(frame);
            _voiced = 1;
            _silenceRun = 0;
            return;
        }

        _chunk.AddRange(frame);
        if (speech)
        {
            _voiced++;
            _silenceRun = 0;
        }
        else
        {
            _silenceRun++;
        }

        var seconds = _chunk.Count / (double)Rate;
        if (seconds >= MaxSeconds - 1e-9) Emit(output);
        else if (_silenceRun >= Frames(HangoverSeconds) && seconds >= MinSeconds) Emit(output);
    }

    private void Emit(List<SpeechChunk> output)
    {
        if (_voiced * FrameSamples / (double)Rate >= MinVoicedSeconds && _chunk.Count > 0)
        {
            var start = TimeSpan.FromSeconds(_chunkStart / (double)Rate);
            var end = TimeSpan.FromSeconds((_chunkStart + _chunk.Count) / (double)Rate);
            output.Add(new SpeechChunk(_source, start, end, _chunk.ToArray()));
        }
        _chunk.Clear();
        _inChunk = false;
        _voiced = 0;
        _silenceRun = 0;
        _preroll.Clear();
    }
}

/// <summary>
/// Takes captured buffers from the capture thread without ever blocking it, turns them into 16 kHz mono on its
/// own task, keeps them in step with the recording clock (loopback sends nothing while nothing plays, the gap
/// becomes silence) and hands chunks of speech on. When it cannot keep up the oldest buffers are dropped.
/// </summary>
public sealed class LiveFeed
{
    private readonly record struct Buf(byte[] Data, int Count, WaveFormat Format, TimeSpan At);

    private readonly Channel<Buf> _channel;
    private readonly VoiceChunker _chunker;
    private readonly Action<SpeechChunk> _onChunk;
    private readonly Func<TimeSpan> _clock;
    private readonly Task _consumer;
    private long _dropped;
    private long _out;
    private int _rate;
    private double _pos;
    private double _next;
    private double _acc;
    private int _accN;
    private float[] _mono = Array.Empty<float>();
    private readonly List<float> _resampled = new();

    public LiveFeed(LiveSource source, Func<TimeSpan> clock, Action<SpeechChunk> onChunk, int capacity = 256)
    {
        Source = source;
        _clock = clock;
        _onChunk = onChunk;
        _chunker = new VoiceChunker(source);
        _channel = Channel.CreateBounded<Buf>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            dropped =>
            {
                Interlocked.Increment(ref _dropped);
                ArrayPool<byte>.Shared.Return(dropped.Data);
            });
        _consumer = Task.Run(ConsumeAsync);
    }

    public LiveSource Source { get; }

    /// <summary>Buffers dropped because the live side fell behind (the recording itself never drops any).</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Capture thread: copies the buffer and returns at once.</summary>
    public void Push(byte[] data, int count, WaveFormat format)
    {
        if (count <= 0) return;
        var copy = ArrayPool<byte>.Shared.Rent(count);
        Buffer.BlockCopy(data, 0, copy, 0, count);
        if (!_channel.Writer.TryWrite(new Buf(copy, count, format, _clock())))
            ArrayPool<byte>.Shared.Return(copy); // completed
    }

    /// <summary>No more input: the last chunk in progress is handed on, then the task ends.</summary>
    public async Task CompleteAsync(TimeSpan timeout)
    {
        _channel.Writer.TryComplete();
        try { await _consumer.WaitAsync(timeout); }
        catch (TimeoutException) { Log.Warn($"live feed {Source}: did not finish in {timeout.TotalSeconds:0} s"); }
        catch (Exception ex) { Log.Error($"live feed {Source} failed", ex); }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var b in _channel.Reader.ReadAllAsync())
            {
                try
                {
                    Handle(b);
                }
                catch (Exception ex)
                {
                    Log.Error($"live feed {Source}: buffer skipped", ex);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(b.Data);
                }
            }
            if (_chunker.Flush() is { } last) _onChunk(last);
        }
        catch (Exception ex)
        {
            Log.Error($"live feed {Source} stopped", ex);
        }
    }

    private void Handle(Buf b)
    {
        var n = ToMono(b.Data, b.Count, b.Format, ref _mono);
        if (n == 0) return;
        if (b.Format.SampleRate != _rate)
        {
            _rate = b.Format.SampleRate;
            _pos = 0;
            _next = _rate / (double)VoiceChunker.Rate;
            _acc = 0;
            _accN = 0;
        }

        // keep in step with the recording clock: a gap before this buffer is silence
        var startSeconds = b.At.TotalSeconds - n / (double)_rate;
        var expected = (long)(startSeconds * VoiceChunker.Rate);
        if (expected - _out > VoiceChunker.Rate / 2)
        {
            var gap = new float[expected - _out];
            Feed(gap);
        }

        var ratio = _rate / (double)VoiceChunker.Rate;
        _resampled.Clear();
        for (var i = 0; i < n; i++)
        {
            _acc += _mono[i];
            _accN++;
            _pos += 1;
            if (_pos >= _next)
            {
                _resampled.Add((float)(_acc / _accN));
                _acc = 0;
                _accN = 0;
                _next += ratio;
            }
        }
        if (_resampled.Count > 0) Feed(_resampled.ToArray());
    }

    private void Feed(float[] samples)
    {
        _out += samples.Length;
        foreach (var c in _chunker.Push(samples)) _onChunk(c);
    }

    /// <summary>Any capture format (float 32, PCM 16/24/32, any channel count) to mono float.</summary>
    internal static int ToMono(byte[] data, int count, WaveFormat f, ref float[] dst)
    {
        var bytes = f.BitsPerSample / 8;
        var ch = Math.Max(1, f.Channels);
        if (bytes == 0) return 0;
        var frames = count / (bytes * ch);
        if (dst.Length < frames) dst = new float[frames];
        var isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat || (f is WaveFormatExtensible && bytes == 4 && f.Encoding != WaveFormatEncoding.Pcm);
        for (var i = 0; i < frames; i++)
        {
            double sum = 0;
            for (var c = 0; c < ch; c++)
            {
                var o = (i * ch + c) * bytes;
                sum += bytes switch
                {
                    4 when isFloat => BitConverter.ToSingle(data, o),
                    4 => BitConverter.ToInt32(data, o) / 2147483648.0,
                    3 => ((data[o] << 8 | data[o + 1] << 16 | data[o + 2] << 24) >> 8) / 8388608.0,
                    2 => BitConverter.ToInt16(data, o) / 32768.0,
                    _ => (data[o] - 128) / 128.0,
                };
            }
            dst[i] = (float)(sum / ch);
        }
        return frames;
    }
}

/// <summary>What one live worker process runs with.</summary>
public sealed record LiveWorkerConfig(string ModelPath, string ModelKey, bool Gpu, string Language, int Threads);

/// <summary>The live worker ended (native crash, killed, no answer); the transcriber starts a new one.</summary>
public sealed class LiveWorkerCrashedException : Exception
{
    public LiveWorkerCrashedException(string message, int? exitCode = null) : base(message) => ExitCode = exitCode;

    public int? ExitCode { get; }
}

public interface ILiveWorker : IDisposable
{
    /// <summary>The whisper.cpp build that loaded ("Vulkan", "Cpu", "CpuNoAvx"), known after StartAsync.</summary>
    string? Runtime { get; }

    Task StartAsync(TimeSpan timeout, CancellationToken ct);

    Task<LiveReply> TranscribeAsync(LiveRequest request, TimeSpan timeout, CancellationToken ct);
}

/// <summary>The live worker as a child process speaking JSON lines (RecBox.exe --live-worker).</summary>
public sealed class ProcessLiveWorker : ILiveWorker
{
    private readonly LiveWorkerConfig _config;
    private readonly Func<LiveWorkerConfig, ProcessStartInfo> _launch;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<LiveReply>> _pending = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _writeLock = new();
    private Process? _p;
    private int _pid;
    private volatile LiveWorkerCrashedException? _ended;

    public ProcessLiveWorker(LiveWorkerConfig config, Func<LiveWorkerConfig, ProcessStartInfo>? launch = null)
    {
        _config = config;
        _launch = launch ?? DefaultLaunch;
    }

    public string? Runtime { get; private set; }

    public static ProcessStartInfo DefaultLaunch(LiveWorkerConfig c)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("RecBox cannot find its own exe to start the speech engine.");
        var psi = new ProcessStartInfo(exe);
        foreach (var a in new[]
                 {
                     TranscriptionWorker.LiveFlag, "--model", c.ModelPath, "--runtime", c.Gpu ? "gpu" : "cpu",
                     "--language", string.IsNullOrEmpty(c.Language) ? "auto" : c.Language,
                     "--threads", c.Threads.ToString(CultureInfo.InvariantCulture),
                     "--parent", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                 })
            psi.ArgumentList.Add(a);
        return psi;
    }

    public async Task StartAsync(TimeSpan timeout, CancellationToken ct)
    {
        var psi = _launch(_config);
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        psi.StandardInputEncoding = new UTF8Encoding(false);
        var p = new Process { StartInfo = psi };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) Log.Worker($"[{_pid} live err] {e.Data}");
        };
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            p.Dispose();
            throw new LiveWorkerCrashedException($"The live speech engine could not start ({ex.Message}).");
        }
        _p = p;
        _pid = p.Id;
        WorkerPriority.Lower(p);
        Log.Info($"live worker {_pid} started (model {_config.ModelKey}, {(_config.Gpu ? "GPU first" : "CPU")}, {_config.Threads} thread(s), below normal priority)");
        p.BeginErrorReadLine();
        _ = Task.Run(ReadLoopAsync);
        try
        {
            await _ready.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            Kill();
            throw new LiveWorkerCrashedException($"The live speech engine did not load the model in {timeout.TotalSeconds:0} s.");
        }
    }

    public async Task<LiveReply> TranscribeAsync(LiveRequest request, TimeSpan timeout, CancellationToken ct)
    {
        if (_ended is { } ended) throw ended;
        var p = _p ?? throw new InvalidOperationException("live worker not started");
        var tcs = new TaskCompletionSource<LiveReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = tcs;
        try
        {
            var line = JsonSerializer.Serialize(request, TranscriptionWorker.Json);
            lock (_writeLock)
            {
                p.StandardInput.WriteLine(line);
                p.StandardInput.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _pending.TryRemove(request.Id, out _);
            throw _ended ?? new LiveWorkerCrashedException($"The live speech engine stopped ({ex.Message}).");
        }
        if (_ended is { } gone) tcs.TrySetException(gone);
        try
        {
            return await tcs.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            Kill();
            throw new LiveWorkerCrashedException($"The live speech engine did not answer in {timeout.TotalSeconds:0} s.");
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        var p = _p!;
        try
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) is not null)
            {
                if (line.StartsWith("@ready ", StringComparison.Ordinal))
                {
                    var ready = Parse<LiveReady>(line[7..]);
                    if (ready?.Ok == true)
                    {
                        Runtime = ready.Runtime;
                        Log.Info($"live worker {_pid} ready on {Runtime ?? "unknown"}");
                        _ready.TrySetResult();
                    }
                    else
                    {
                        _ready.TrySetException(new LiveWorkerCrashedException(ready?.Error ?? "The live speech engine could not load the model."));
                    }
                }
                else if (line.StartsWith("@live ", StringComparison.Ordinal))
                {
                    if (Parse<LiveReply>(line[6..]) is { } reply && _pending.TryGetValue(reply.Id, out var tcs)) tcs.TrySetResult(reply);
                }
                else
                {
                    Log.Worker($"[{_pid} live out] {line}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"live worker {_pid} output unreadable: {ex.Message}");
        }

        int? code = null;
        try
        {
            if (p.WaitForExit(3000)) code = p.ExitCode;
        }
        catch { }
        var why = code is { } c
            ? $"The live speech engine stopped: {WorkerExit.Describe(c)} (worker exit code {WorkerExit.Hex(c)})"
            : "The live speech engine stopped";
        Log.Info($"live worker {_pid} exited{(code is { } x ? $" with {WorkerExit.Hex(x)} ({WorkerExit.Describe(x)})" : "")}");
        var ended = new LiveWorkerCrashedException(why, code);
        _ended = ended;
        _ready.TrySetException(ended);
        foreach (var t in _pending.Values) t.TrySetException(ended);
    }

    private static T? Parse<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, TranscriptionWorker.Json); }
        catch { return null; }
    }

    private void Kill()
    {
        try { _p?.Kill(entireProcessTree: true); } catch { }
    }

    public void Dispose()
    {
        var p = _p;
        if (p is null) return;
        try
        {
            if (_ended is null)
            {
                try { p.StandardInput.Close(); } catch { }
                if (!p.WaitForExit(1500)) Kill();
            }
        }
        catch { }
        try { p.Dispose(); } catch { }
    }
}

public enum LiveAction
{
    Keep,
    SwitchToTiny,
    SkipBacklog,
}

/// <summary>What to do when live text falls behind the recording.</summary>
public static class LivePolicy
{
    public const double SwitchLagSeconds = 12;
    public const double SkipLagSeconds = 30;

    public static LiveAction Decide(double lagSeconds, bool onTiny, bool tinyAvailable)
    {
        if (lagSeconds >= SwitchLagSeconds && !onTiny && tinyAvailable) return LiveAction.SwitchToTiny;
        if (lagSeconds >= SkipLagSeconds) return LiveAction.SkipBacklog;
        return LiveAction.Keep;
    }

    public static string LagText(double lagSeconds) =>
        lagSeconds < 1.5 ? "Live" : $"~{Math.Round(lagSeconds).ToString("0", CultureInfo.InvariantCulture)} s behind";
}

/// <summary>Auto-scroll of the live text: follows new lines until the user scrolls up, again once back at the bottom.</summary>
public sealed class LiveScrollFollow
{
    public bool Following { get; private set; } = true;

    /// <summary>Feed every ScrollChanged; returns true when the view should scroll to the end now.</summary>
    public bool OnScrollChanged(double offset, double scrollableHeight, double extentChange)
    {
        if (extentChange != 0) return Following;
        Following = offset >= scrollableHeight - 4;
        return false;
    }

    public void JumpToLatest() => Following = true;
}

public sealed class LiveTranscriberOptions
{
    public required ModelChoice Choice { get; init; }
    /// <summary>Tiny, when downloaded: the fallback when live text falls behind.</summary>
    public WhisperModelInfo? Tiny { get; init; }
    public string Language { get; init; } = "auto";
    public Func<bool> Translate { get; init; } = () => false;
    public Func<LiveWorkerConfig, ILiveWorker> WorkerFactory { get; init; } = c => new ProcessLiveWorker(c);
    public required Func<TimeSpan> Clock { get; init; }
    public string TempDir { get; init; } = Path.Combine(Path.GetTempPath(), $"recbox-live-{Environment.ProcessId}");
    public int MaxCrashes { get; init; } = 3;
    public TimeSpan CrashWindow { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan ChunkTimeout { get; init; } = TimeSpan.FromSeconds(120);
}

/// <summary>
/// Runs live text for one recording: two feeds (mic, system audio), one queue, one live worker process that is
/// restarted when it crashes (at most MaxCrashes in CrashWindow, then live text stops and says why; the recording
/// and the full transcript after Stop are not affected). Falls back to Tiny on the processor, then skips queued
/// chunks, when it falls far behind, and says so.
/// </summary>
public sealed class LiveTranscriber
{
    private readonly LiveTranscriberOptions _o;
    private readonly Dictionary<LiveSource, LiveFeed> _feeds = new();
    private readonly List<SpeechChunk> _pending = new();
    private readonly List<LiveLine> _lines = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<DateTime> _crashes = new();
    private readonly object _lock = new();
    private LiveWorkerConfig _config;
    private ILiveWorker? _worker;
    private SpeechChunk? _inflight;
    private Task? _loop;
    private int _nextId;
    private volatile bool _failed;
    private bool _gpuReported;

    public LiveTranscriber(LiveTranscriberOptions options)
    {
        _o = options;
        var c = options.Choice;
        _config = new LiveWorkerConfig(c.Model.Path, c.Model.Key, c.Gpu, options.Language, c.Threads);
        Status = $"Live text: {c.Model.Name} on the {(c.Gpu ? "graphics card" : "processor")}";
        foreach (var s in new[] { LiveSource.Mic, LiveSource.Speaker })
            _feeds[s] = new LiveFeed(s, options.Clock, Enqueue);
    }

    public event Action<LiveLine>? LineAdded;
    public event Action<string>? StatusChanged;
    /// <summary>The graphics card failed for live text (raised once); the caller remembers to stay on the processor.</summary>
    public event Action<string>? GpuFailed;

    public string Status { get; private set; }
    public bool Failed => _failed;
    public int Restarts { get; private set; }
    public double SkippedSeconds { get; private set; }
    public string ModelKey => _config.ModelKey;
    public bool OnGpu => _config.Gpu;
    public string? Runtime { get; private set; }

    public long DroppedBuffers => _feeds.Values.Sum(f => f.Dropped);

    public IReadOnlyList<LiveLine> Lines
    {
        get { lock (_lock) return _lines.ToList(); }
    }

    /// <summary>How far the text trails the recording: from the end of the oldest chunk not transcribed yet.</summary>
    public double LagSeconds
    {
        get
        {
            lock (_lock)
            {
                var oldest = _inflight ?? _pending.MinBy(c => c.End);
                return oldest is null ? 0 : Math.Max(0, (_o.Clock() - oldest.End).TotalSeconds);
            }
        }
    }

    public void Start()
    {
        Directory.CreateDirectory(_o.TempDir);
        Log.Info($"live text start: {_o.Choice.Reason}");
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Capture thread (through SourceRecorder.Tap): never blocks.</summary>
    public void Feed(LiveSource source, byte[] data, int count, WaveFormat format)
    {
        if (_failed) return;
        _feeds[source].Push(data, count, format);
    }

    public void Enqueue(SpeechChunk chunk)
    {
        if (_failed) return;
        lock (_lock) _pending.Add(chunk);
        _signal.Release();
    }

    /// <summary>
    /// Stop: hands on the last chunks, gives the worker up to <paramref name="grace"/> to finish the queue, then
    /// ends it. What was not transcribed in time gets one note (the full transcript after Stop has it).
    /// </summary>
    public async Task<IReadOnlyList<LiveLine>> StopAsync(TimeSpan grace)
    {
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(_feeds.Values.Select(f => f.CompleteAsync(TimeSpan.FromSeconds(1))));
        _signal.Release();
        while (sw.Elapsed < grace && !_failed && _loop is { IsCompleted: false })
        {
            lock (_lock)
                if (_pending.Count == 0 && _inflight is null) break;
            await Task.Delay(50);
        }
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { }
        }
        double left;
        TimeSpan at;
        lock (_lock)
        {
            var rest = _pending.ToList();
            if (_inflight is { } f) rest.Add(f);
            left = rest.Sum(c => c.Seconds);
            at = rest.Count > 0 ? rest.Min(c => c.Start) : TimeSpan.Zero;
            _pending.Clear();
        }
        if (left > 0) Note(at, $"({left:0} s of speech at the end were not transcribed live; the full transcript has them)");
        DisposeWorker();
        try { Directory.Delete(_o.TempDir, true); } catch { }
        Log.Info($"live text stop: {Lines.Count} line(s), {Restarts} restart(s), {SkippedSeconds:0} s skipped, {DroppedBuffers} buffer(s) dropped");
        return Lines;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_failed)
            {
                await _signal.WaitAsync(ct);
                while (!ct.IsCancellationRequested && !_failed && TakeNext() is { } chunk)
                {
                    try
                    {
                        await ProcessAsync(chunk, ct);
                    }
                    finally
                    {
                        lock (_lock) _inflight = null;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("live text loop failed", ex);
            SetStatus($"Live text stopped: {ex.Message}. The recording goes on.");
        }
    }

    private SpeechChunk? TakeNext()
    {
        LiveAction action;
        double lag;
        TimeSpan firstStart;
        lock (_lock)
        {
            if (_pending.Count == 0) return null;
            _pending.Sort((a, b) => a.End.CompareTo(b.End));
            firstStart = _pending[0].Start;
            lag = Math.Max(0, (_o.Clock() - _pending[0].End).TotalSeconds);
            action = LivePolicy.Decide(lag, _config.ModelKey == "tiny", _o.Tiny is { IsPresent: true });
        }

        if (action == LiveAction.SwitchToTiny)
        {
            var tiny = _o.Tiny!;
            _config = _config with { ModelPath = tiny.Path, ModelKey = tiny.Key, Gpu = false };
            DisposeWorker();
            var msg = $"Falling behind ({LivePolicy.LagText(lag)}): switched live text to Tiny on the processor";
            Log.Warn($"live text: {msg}");
            SetStatus(msg);
            Note(firstStart, $"({msg})");
        }
        else if (action == LiveAction.SkipBacklog)
        {
            List<SpeechChunk> skipped;
            lock (_lock)
            {
                skipped = _pending.Take(_pending.Count - 1).ToList();
                _pending.RemoveRange(0, skipped.Count);
            }
            if (skipped.Count > 0)
            {
                var secs = skipped.Sum(c => c.Seconds);
                SkippedSeconds += secs;
                var msg = $"Falling behind ({LivePolicy.LagText(lag)}): skipped {secs:0} s of speech to catch up";
                Log.Warn($"live text: {msg}");
                SetStatus(msg);
                Note(skipped[0].Start, $"(skipped {secs:0} s of speech to catch up; the full transcript has it)");
            }
        }

        lock (_lock)
        {
            if (_pending.Count == 0) return null;
            var next = _pending[0];
            _pending.RemoveAt(0);
            _inflight = next;
            return next;
        }
    }

    private async Task ProcessAsync(SpeechChunk chunk, CancellationToken ct)
    {
        var worker = await EnsureWorkerAsync(ct);
        if (worker is null)
        {
            if (!_failed) Note(chunk.Start, $"(could not transcribe {Span(chunk)}: the speech engine did not start)");
            return;
        }

        var id = Interlocked.Increment(ref _nextId);
        var wav = Path.Combine(_o.TempDir, $"chunk-{id}.wav");
        try
        {
            Directory.CreateDirectory(_o.TempDir);
            using (var w = new WaveFileWriter(wav, new WaveFormat(VoiceChunker.Rate, 16, 1)))
                w.WriteSamples(chunk.Samples, 0, chunk.Samples.Length);

            LiveReply reply;
            try
            {
                reply = await worker.TranscribeAsync(new LiveRequest { Id = id, Input = wav, Translate = _o.Translate() }, _o.ChunkTimeout, ct);
            }
            catch (LiveWorkerCrashedException ex)
            {
                DisposeWorker();
                Note(chunk.Start, $"(could not transcribe {Span(chunk)}: {ex.Message}; restarting the speech engine)");
                if (_config.Gpu)
                {
                    _config = _config with { Gpu = false }; // restart on the processor, like a failed start
                    ReportGpu($"The graphics card (Vulkan) failed during live text: {ex.Message}");
                }
                RegisterCrash(ex.Message);
                return;
            }

            if (!reply.Ok)
            {
                Log.Warn($"live text: chunk {id} failed: {reply.Error}");
                Note(chunk.Start, $"(could not transcribe {Span(chunk)}: {reply.Error})");
                return;
            }
            foreach (var seg in reply.Segments)
            {
                var text = seg.Text.Trim();
                if (TranscriptionService.IsNoise(text)) continue;
                var line = new LiveLine(chunk.Start + TimeSpan.FromSeconds(seg.Start), chunk.Start + TimeSpan.FromSeconds(Math.Max(seg.End, seg.Start)),
                    LiveLine.SpeakerOf(chunk.Source), text);
                lock (_lock) _lines.Add(line);
                LineAdded?.Invoke(line);
            }
        }
        finally
        {
            try { File.Delete(wav); } catch { }
        }
    }

    private async Task<ILiveWorker?> EnsureWorkerAsync(CancellationToken ct)
    {
        if (_worker is { } live) return live;
        for (var attempt = 0; attempt < 2 && !_failed; attempt++)
        {
            var cfg = _config;
            var w = _o.WorkerFactory(cfg);
            try
            {
                SetStatus($"Live text: loading {WhisperModels.Get(cfg.ModelKey).Name} on the {(cfg.Gpu ? "graphics card" : "processor")}");
                await w.StartAsync(_o.StartTimeout, ct);
                _worker = w;
                Runtime = w.Runtime;
                if (cfg.Gpu && w.Runtime is { } rt && rt != nameof(RuntimeLibrary.Vulkan))
                {
                    _config = cfg with { Gpu = false };
                    ReportGpu($"The graphics card (Vulkan) is not available for live text; used {rt}.");
                }
                SetStatus($"Live text: {WhisperModels.Get(cfg.ModelKey).Name} on {(w.Runtime == nameof(RuntimeLibrary.Vulkan) ? "the graphics card" : "the processor")}");
                return w;
            }
            catch (OperationCanceledException)
            {
                w.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                w.Dispose();
                Log.Warn($"live text: worker did not start: {ex.Message}");
                if (cfg.Gpu)
                {
                    _config = cfg with { Gpu = false };
                    ReportGpu($"The graphics card (Vulkan) failed to start live text: {ex.Message}");
                    continue; // once more on the processor
                }
                RegisterCrash(ex.Message);
                return null;
            }
        }
        return null;
    }

    private void RegisterCrash(string reason)
    {
        var now = DateTime.UtcNow;
        _crashes.Enqueue(now);
        while (_crashes.Count > 0 && now - _crashes.Peek() > _o.CrashWindow) _crashes.Dequeue();
        Restarts++;
        if (_crashes.Count >= _o.MaxCrashes)
        {
            _failed = true;
            var msg = $"Live text stopped: the speech engine crashed {_crashes.Count} times in a minute ({reason}). " +
                      "The recording goes on, and the full transcript still runs after Stop.";
            Log.Warn(msg);
            SetStatus(msg);
            return;
        }
        Log.Warn($"live text: speech engine crashed ({reason}); starting a new one");
        SetStatus($"Live text: the speech engine crashed and was restarted ({reason})");
    }

    private void ReportGpu(string reason)
    {
        if (_gpuReported) return;
        _gpuReported = true;
        Log.Warn($"live text: {reason}");
        try { GpuFailed?.Invoke(reason); } catch (Exception ex) { Log.Error("gpu failed handler", ex); }
    }

    private void Note(TimeSpan at, string text)
    {
        var line = new LiveLine(at, at, "RecBox", text, IsNote: true);
        lock (_lock) _lines.Add(line);
        try { LineAdded?.Invoke(line); } catch (Exception ex) { Log.Error("live line handler", ex); }
    }

    private void SetStatus(string s)
    {
        Status = s;
        try { StatusChanged?.Invoke(s); } catch (Exception ex) { Log.Error("live status handler", ex); }
    }

    private void DisposeWorker()
    {
        var w = _worker;
        _worker = null;
        try { w?.Dispose(); } catch { }
    }

    private static string Span(SpeechChunk c) => $"{Stamp(c.Start)} to {Stamp(c.End)}";

    private static string Stamp(TimeSpan t) => t.ToString(t.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
}

/// <summary>What one recording's live text adds up to at Stop, over every off/on switch.</summary>
public sealed record LiveRunResult(IReadOnlyList<LiveLine> Lines, string ModelKey, string? Runtime, int Restarts, double SkippedSeconds, int Parts);

/// <summary>
/// The live text of one recording across Live text off/on switches. Turning it off stops the running transcriber
/// (its lines are kept) and frees the slot, so turning it back on while recording starts a new one; Stop saves
/// the lines of all of them, in order.
/// </summary>
public sealed class LiveRun
{
    private readonly List<(LiveTranscriber Live, Task<IReadOnlyList<LiveLine>> Lines)> _done = new();

    /// <summary>The transcriber running now; null when live text is off, not started yet or turned off.</summary>
    public LiveTranscriber? Current { get; private set; }

    /// <summary>Transcribers of this recording so far, the running one included.</summary>
    public int Parts => _done.Count + (Current is null ? 0 : 1);

    /// <summary>True when live text should be started for a recording that is running: nothing is running now.</summary>
    public bool NeedsStart(bool recording) => recording && Current is null;

    public void Begin(LiveTranscriber live)
    {
        if (Current is not null) throw new InvalidOperationException("live text is already running for this recording");
        Current = live;
    }

    /// <summary>Live text turned off mid-recording: the running transcriber stops now (no grace); false when none ran.</summary>
    public bool TurnOff()
    {
        if (Current is not { } live) return false;
        _done.Add((live, Task.Run(() => live.StopAsync(TimeSpan.Zero))));
        Current = null;
        return true;
    }

    /// <summary>Stop: ends the running transcriber with <paramref name="grace"/> and gathers every part; null when live text never ran.</summary>
    public Task<LiveRunResult>? End(TimeSpan grace)
    {
        if (Current is { } live)
        {
            _done.Add((live, Task.Run(() => live.StopAsync(grace))));
            Current = null;
        }
        if (_done.Count == 0) return null;
        var parts = _done.ToList();
        _done.Clear();
        return Gather(parts);
    }

    private static async Task<LiveRunResult> Gather(List<(LiveTranscriber Live, Task<IReadOnlyList<LiveLine>> Lines)> parts)
    {
        var lines = new List<LiveLine>();
        foreach (var p in parts) lines.AddRange(await p.Lines);
        var last = parts[^1].Live;
        return new LiveRunResult(lines, last.ModelKey, last.Runtime, parts.Sum(p => p.Live.Restarts), parts.Sum(p => p.Live.SkippedSeconds), parts.Count);
    }
}

/// <summary>Writes the live text beside the recording: live-transcript.txt and live-transcript.json.</summary>
public static class LiveTranscriptStore
{
    public const string TextFile = "live-transcript.txt";
    public const string JsonFile = "live-transcript.json";

    public sealed class JsonLine
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Source { get; set; } = "";
        public string Speaker { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public sealed class JsonDoc
    {
        public string SessionId { get; set; } = "";
        public string Model { get; set; } = "";
        public string? Runtime { get; set; }
        public bool Translate { get; set; }
        public int Restarts { get; set; }
        public double SkippedSeconds { get; set; }
        public List<JsonLine> Lines { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static IReadOnlyList<string> Save(string dir, string sessionId, IReadOnlyList<LiveLine> lines, string model, string? runtime,
        bool translate, int restarts, double skippedSeconds)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Live text of recording {sessionId} (written while recording; transcript.md is the full transcript)");
        sb.AppendLine($"Model: {model} on {runtime ?? "unknown"}{(translate ? ", translated to English" : "")}. You = microphone, Others = system audio.");
        sb.AppendLine();
        foreach (var l in lines.OrderBy(l => l.Start)) sb.AppendLine(l.ToString());
        File.WriteAllText(Path.Combine(dir, TextFile), sb.ToString());

        var doc = new JsonDoc
        {
            SessionId = sessionId,
            Model = model,
            Runtime = runtime,
            Translate = translate,
            Restarts = restarts,
            SkippedSeconds = Math.Round(skippedSeconds, 1),
            Lines = lines.OrderBy(l => l.Start).Select(l => new JsonLine
            {
                Start = Math.Round(l.Start.TotalSeconds, 2),
                End = Math.Round(l.End.TotalSeconds, 2),
                Source = l.Source,
                Speaker = l.Speaker,
                Text = l.Text,
            }).ToList(),
        };
        File.WriteAllText(Path.Combine(dir, JsonFile), JsonSerializer.Serialize(doc, Options));
        return new[] { TextFile, JsonFile };
    }
}

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
// Live text while recording: capture tap -> LiveFeed (16 kHz mono) -> KitSegmenter (Silero in the
// --vad-worker process; loudness only as a fallback) -> LiveTranscriber queue -> one long-lived live
// worker process -> filters, language lock, echo tag -> lines on screen and on disk.
// Nothing here can block or slow the recording: the tap only copies a buffer into a bounded queue
// that drops the oldest buffer when full, and Whisper runs in another process below normal priority.
// ---------------------------------------------------------------------------------------------

/// <summary>One request line to the live worker: transcribe this chunk WAV.</summary>
public sealed class LiveRequest
{
    public int Id { get; set; }
    public string Input { get; set; } = "";
    public bool Translate { get; set; }
    /// <summary>"auto" (detect on this chunk), a code, or a mixed pair ("vi+en"); null: the worker's --language.</summary>
    public string? Language { get; set; }
    /// <summary>Also detect the language (for the session vote) while decoding in <see cref="Language"/>.</summary>
    public bool Detect { get; set; }
}

/// <summary>The live worker's answer to one request.</summary>
public sealed class LiveReply
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public long Ms { get; set; }
    /// <summary>The language this chunk was decoded in, and detection's probability when it detected.</summary>
    public string? Language { get; set; }
    public float LanguageProbability { get; set; }
    /// <summary>What detection said on this chunk (null when it did not detect).</summary>
    public string? Detected { get; set; }
    public List<WorkerSegment> Segments { get; set; } = new();
}

/// <summary>The live worker's first line, once the model is loaded (or why it could not load).</summary>
public sealed class LiveReady
{
    public bool Ok { get; set; }
    public string? Runtime { get; set; }
    /// <summary>The graphics card it runs on, null on the processor.</summary>
    public string? Device { get; set; }
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

/// <summary>
/// One line of live text: "You" (mic), "Others" (system audio) or a RecBox note. Echo: a mic line that repeats what
/// the speakers played at that moment (kept, marked); "[?]" inside the text marks a low-confidence piece.
/// </summary>
public sealed record LiveLine(TimeSpan Start, TimeSpan End, string Speaker, string Text, bool IsNote = false, bool Echo = false)
{
    /// <summary>Recording time when the line was shown (its lag is Emitted - End).</summary>
    public TimeSpan? Emitted { get; init; }
    /// <summary>The language the line was decoded in.</summary>
    public string? Language { get; init; }

    public static string SpeakerOf(LiveSource s) => s == LiveSource.Mic ? "You" : "Others";

    public string Source => IsNote ? "note" : Speaker == "You" ? "mic" : "speaker";

    public string Stamp => Start.ToString(Start.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);

    public override string ToString() => IsNote ? $"[{Stamp}] {Text}" : $"[{Stamp}] {Speaker}{(Echo ? " (echo?)" : "")}: {Text}";
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
    private readonly VoiceChunker? _chunker;
    private readonly KitSegmenter? _segmenter;
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

    /// <param name="vad">The speech detector for the kit's segmenter; null uses the loudness chunker (VoiceChunker).</param>
    public LiveFeed(LiveSource source, Func<TimeSpan> clock, Action<SpeechChunk> onChunk, int capacity = 256, SpeechDetector? vad = null)
    {
        Source = source;
        _clock = clock;
        _onChunk = onChunk;
        if (vad is null) _chunker = new VoiceChunker(source);
        else _segmenter = new KitSegmenter(source, vad);
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

    /// <summary>True once any audio arrived (system audio that never played anything has none).</summary>
    public bool HasAudio => Interlocked.Read(ref _out) > 0;

    /// <summary>Recording time up to which this source is decided: emitted as chunks or dropped as silence.</summary>
    public double DecidedUntil => _segmenter?.DecidedUntil ?? (_chunker?.Position ?? 0) / (double)VoiceChunker.Rate;

    private long _lastPushTicks = -1;

    /// <summary>
    /// True when no buffer arrived for <paramref name="seconds"/> (or never): loopback delivers nothing while nothing
    /// plays, so a quiet system audio side has nothing to echo and a mic line need not wait for it.
    /// </summary>
    public bool QuietFor(double seconds)
    {
        var last = Interlocked.Read(ref _lastPushTicks);
        return last < 0 || (_clock() - TimeSpan.FromTicks(last)).TotalSeconds > seconds;
    }

    /// <summary>Times the speech detector was unavailable and loudness was used instead.</summary>
    public int VadFallbacks => _segmenter?.Fallbacks ?? 0;

    /// <summary>Capture thread: copies the buffer and returns at once.</summary>
    public void Push(byte[] data, int count, WaveFormat format)
    {
        if (count <= 0) return;
        var copy = ArrayPool<byte>.Shared.Rent(count);
        Buffer.BlockCopy(data, 0, copy, 0, count);
        var at = _clock();
        Interlocked.Exchange(ref _lastPushTicks, at.Ticks);
        if (!_channel.Writer.TryWrite(new Buf(copy, count, format, at)))
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
            if ((_segmenter is not null ? _segmenter.Flush() : _chunker!.Flush()) is { } last) _onChunk(last);
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
        var produced = Interlocked.Read(ref _out);
        if (expected - produced > VoiceChunker.Rate / 2)
        {
            var gap = new float[expected - produced];
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
        Interlocked.Add(ref _out, samples.Length);
        foreach (var c in _segmenter is not null ? _segmenter.Push(samples) : _chunker!.Push(samples)) _onChunk(c);
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

    /// <summary>The graphics card it runs on ("Vulkan1 = NVIDIA GeForce RTX 3060 ..."), null on the processor.</summary>
    string? Device => null;

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

    public string? Device { get; private set; }

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
                        Device = ready.Device;
                        Log.Info($"live worker {_pid} ready on {Runtime ?? "unknown"}{(Device is null ? "" : $", {Device}")}");
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

/// <summary>How far behind live text is, in words (falling behind itself is handled by CatchUp: merge, then skip; never a smaller model).</summary>
public static class LivePolicy
{
    public static string LagText(double lagSeconds) => CatchUp.LagText(lagSeconds);
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
    /// <summary>The language setting: "auto" (detect once, then keep), a code, or "vi+en".</summary>
    public string Language { get; init; } = "auto";
    /// <summary>Silero for the kit's segmenter; null cuts by loudness (VoiceChunker).</summary>
    public VadService? Vad { get; init; }
    /// <summary>Mic lines wait for the system audio to catch up, so an echo of the speakers is tagged (kit: ECHO_HOLD).</summary>
    public bool EchoHold { get; init; } = true;
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
/// and the full transcript after Stop are not affected). Falling behind never switches to a smaller model: queued
/// chunks of one source are decoded together, and only far behind are they skipped, with a note (CatchUp).
/// Every chunk goes through the kit's filters; the language is locked once (LanguageLock); mic lines wait for the
/// system audio so an echo of the speakers is tagged.
/// </summary>
public sealed class LiveTranscriber
{
    private readonly LiveTranscriberOptions _o;
    private readonly Dictionary<LiveSource, LiveFeed> _feeds = new();
    private readonly List<SpeechChunk> _pending = new();
    private readonly List<LiveLine> _lines = new();
    private readonly List<(LiveLine Line, double Deadline)> _held = new();
    private readonly List<TimedText> _speakerRecent = new();
    private readonly Dictionary<LiveSource, TranscriptFilter.RepeatGuard> _repeats = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Queue<DateTime> _crashes = new();
    private readonly object _lock = new();
    private LiveWorkerConfig _config;
    private ILiveWorker? _worker;
    private List<SpeechChunk>? _inflight;
    private Task? _loop;
    private int _nextId;
    private volatile bool _failed;
    private bool _gpuReported;

    public LiveTranscriber(LiveTranscriberOptions options)
    {
        _o = options;
        var c = options.Choice;
        _config = new LiveWorkerConfig(c.Model.Path, c.Model.Key, c.Gpu, options.Language, c.Threads);
        Language = new LanguageLock(options.Language);
        Status = $"Live text: {c.Model.Name} on the {(c.Gpu ? "graphics card" : "processor")}";
        SpeechDetector? vad = options.Vad is { } v ? v.Detect : null;
        foreach (var s in new[] { LiveSource.Mic, LiveSource.Speaker })
            _feeds[s] = new LiveFeed(s, options.Clock, Enqueue, vad: vad);
    }

    public event Action<LiveLine>? LineAdded;
    public event Action<string>? StatusChanged;
    /// <summary>The language was detected, or changed: the new description ("Vietnamese (detected)").</summary>
    public event Action<string>? LanguageChanged;
    /// <summary>The graphics card failed for live text (raised once); the caller remembers to stay on the processor.</summary>
    public event Action<string>? GpuFailed;

    public string Status { get; private set; }
    public bool Failed => _failed;
    public int Restarts { get; private set; }
    public double SkippedSeconds { get; private set; }
    public string ModelKey => _config.ModelKey;
    public bool OnGpu => _config.Gpu;
    public string? Runtime { get; private set; }
    /// <summary>The graphics card the live worker runs on, null on the processor.</summary>
    public string? Device { get; private set; }
    /// <summary>The session's language: the setting, and what Auto detected.</summary>
    public LanguageLock Language { get; }
    /// <summary>Chunks decoded together because live text was behind.</summary>
    public int MergedChunks { get; private set; }
    /// <summary>Lines whose words were repeated 3+ times in a row by the model and dropped after the first.</summary>
    public int LoopLinesDropped { get; private set; }
    /// <summary>The worst lag seen, in seconds (from the end of a chunk to its text).</summary>
    public double MaxLagSeconds { get; private set; }

    /// <summary>The language picker changed during the recording: applies from the next chunk.</summary>
    public void SetLanguage(string setting)
    {
        Language.Change(setting);
        Log.Info($"live text language set to {setting}");
        try { LanguageChanged?.Invoke(Language.Describe()); } catch (Exception ex) { Log.Error("live language handler", ex); }
    }

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
                var oldest = _inflight?.FirstOrDefault() ?? _pending.MinBy(c => c.End);
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
        // the speech detector process belongs to this transcriber: both feeds are done with it
        try { _o.Vad?.Dispose(); } catch (Exception ex) { Log.Warn($"live text: speech detector did not close: {ex.Message}"); }
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
            if (_inflight is { } f) rest.AddRange(f);
            left = rest.Sum(c => c.Seconds);
            at = rest.Count > 0 ? rest.Min(c => c.Start) : TimeSpan.Zero;
            _pending.Clear();
        }
        ReleaseHeld(force: true);
        if (left > 0) Note(at, $"({left:0} s of speech at the end were not transcribed live; the full transcript has them)");
        DisposeWorker();
        try { Directory.Delete(_o.TempDir, true); } catch { }
        Log.Info($"live text stop: {Lines.Count} line(s), {Restarts} restart(s), {SkippedSeconds:0} s skipped, {MergedChunks} chunk(s) merged, " +
                 $"{LoopLinesDropped} repeated line(s) dropped, max lag {MaxLagSeconds:0.0} s, {DroppedBuffers} buffer(s) dropped, " +
                 $"language {Language.Describe()}, VAD fallbacks {_feeds.Values.Sum(f => f.VadFallbacks)}");
        return Lines;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_failed)
            {
                // wakes for each chunk, and every half second while mic lines wait for the system audio (echo hold)
                await _signal.WaitAsync(TimeSpan.FromMilliseconds(500), ct);
                ReleaseHeld(force: false);
                while (!ct.IsCancellationRequested && !_failed && TakeNext() is { } batch)
                {
                    try
                    {
                        await ProcessAsync(batch, ct);
                    }
                    finally
                    {
                        lock (_lock) _inflight = null;
                    }
                    ReleaseHeld(force: false);
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

    /// <summary>
    /// The next chunks to decode: one, or (behind by 8 s or more) the queued chunks of the same source decoded as one
    /// window; far behind (3 minutes), older speech is skipped with a note. Never a smaller model.
    /// </summary>
    private List<SpeechChunk>? TakeNext()
    {
        List<SpeechChunk> batch, skipped;
        double lag;
        lock (_lock)
        {
            if (_pending.Count == 0) return null;
            var now = _o.Clock();
            (batch, skipped) = CatchUp.Take(_pending, now);
            lag = Math.Max(0, (now - batch[0].End).TotalSeconds);
            _inflight = batch;
        }
        if (skipped.Count > 0)
        {
            var secs = skipped.Sum(c => c.Seconds);
            SkippedSeconds += secs;
            var msg = $"Far behind ({LivePolicy.LagText(lag)}): skipped {secs:0} s of speech to catch up";
            Log.Warn($"live text: {msg}");
            SetStatus(msg);
            Note(skipped.Min(c => c.Start), $"(skipped {secs:0} s of speech to catch up; the full transcript has it)");
        }
        if (batch.Count > 1)
        {
            MergedChunks += batch.Count - 1;
            var msg = $"Catching up ({LivePolicy.LagText(lag)}): {batch.Count} pieces of speech decoded together";
            Log.Info($"live text: {msg}");
            SetStatus(msg);
        }
        return batch;
    }

    private async Task ProcessAsync(List<SpeechChunk> batch, CancellationToken ct)
    {
        var chunk = CatchUp.Merge(batch);
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

            var spec = Language.Next;
            var detect = Language.NeedsDetection;
            LiveReply reply;
            try
            {
                reply = await worker.TranscribeAsync(new LiveRequest { Id = id, Input = wav, Translate = _o.Translate(), Language = spec, Detect = detect },
                    _o.ChunkTimeout, ct);
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
            if (detect && reply.Detected is { } detected)
            {
                var before = Language.Describe();
                if (Language.Offer(detected, reply.LanguageProbability, batch.Sum(c => c.Seconds), chunk.Source == LiveSource.Speaker ? 1.0 : 0.5))
                    Log.Info($"live text: language locked to {Language.Detected} after the vote ({Language.Describe()})");
                var now = Language.Describe();
                if (now != before)
                {
                    try { LanguageChanged?.Invoke(now); } catch (Exception ex) { Log.Error("live language handler", ex); }
                }
            }

            var decoded = reply.Language ?? spec;
            var allowed = Languages.Allowed(spec, decoded);
            var emitted = _o.Clock();
            // one line per chunk the segmenter cut (a merged batch is split back by each segment's middle)
            for (var i = 0; i < batch.Count; i++)
            {
                var c = batch[i];
                var from = (c.Start - chunk.Start).TotalSeconds;
                var to = i + 1 < batch.Count ? (batch[i + 1].Start - chunk.Start).TotalSeconds : double.MaxValue;
                var segs = reply.Segments.Where(s => (s.Start + s.End) / 2 >= (i == 0 ? double.MinValue : from) && (s.Start + s.End) / 2 < to).ToList();
                var pieces = TranscriptFilter.Pieces(segs, allowed);
                if (pieces.Count == 0) continue;
                var text = TranscriptFilter.Join(pieces);
                if (TranscriptFilter.IsNoise(text.Replace("[?]", "").Trim())) continue;
                if (RepeatsItself(c.Source, text, c.Start))
                {
                    LoopLinesDropped++;
                    Log.Info($"live text: chunk {id} dropped a repeated line ({c.Source})");
                    continue;
                }
                var start = chunk.Start + TimeSpan.FromSeconds(Math.Max(from, segs.Min(s => s.Start)));
                var end = chunk.Start + TimeSpan.FromSeconds(Math.Max(segs.Max(s => s.End), segs.Min(s => s.Start)));
                if (end > c.End) end = c.End;
                if (start > end) start = c.Start;
                var line = new LiveLine(start, end, LiveLine.SpeakerOf(c.Source), text) { Emitted = emitted, Language = decoded };
                var lag = Math.Max(0, (emitted - c.End).TotalSeconds);
                MaxLagSeconds = Math.Max(MaxLagSeconds, lag);
                Log.Info(string.Create(CultureInfo.InvariantCulture,
                    $"live line chunk={id} src={c.Source} start={start.TotalSeconds:0.00} end={end.TotalSeconds:0.00} emittedAt={emitted.TotalSeconds:0.00} lag={lag:0.0} lang={decoded} p={reply.LanguageProbability:0.00} ms={reply.Ms} merged={batch.Count}"));
                if (c.Source == LiveSource.Mic && _o.EchoHold) Hold(line);
                else Add(line);
            }
        }
        finally
        {
            try { File.Delete(wav); } catch { }
        }
    }

    /// <summary>The model looping over chunks: the same words as this source's last line (4+ words), or as its last two lines.</summary>
    private bool RepeatsItself(LiveSource source, string text, TimeSpan at)
    {
        if (!_repeats.TryGetValue(source, out var guard)) _repeats[source] = guard = new TranscriptFilter.RepeatGuard();
        return guard.IsRepeat(text, at.TotalSeconds);
    }

    private void Add(LiveLine line)
    {
        if (line.Speaker != "You" && !line.IsNote)
        {
            lock (_lock)
            {
                _speakerRecent.Add(new TimedText(line.Start.TotalSeconds, line.End.TotalSeconds, line.Text));
                _speakerRecent.RemoveAll(r => r.End < line.Start.TotalSeconds - 60);
            }
        }
        lock (_lock) _lines.Add(line);
        try { LineAdded?.Invoke(line); } catch (Exception ex) { Log.Error("live line handler", ex); }
    }

    /// <summary>A mic line waits until the system audio around it is transcribed (or 8 s pass), then gets its echo tag.</summary>
    private void Hold(LiveLine line)
    {
        lock (_lock) _held.Add((line, line.End.TotalSeconds + EchoTag.Hold));
    }

    private void ReleaseHeld(bool force)
    {
        List<LiveLine> ready;
        lock (_lock)
        {
            if (_held.Count == 0) return;
            var now = _o.Clock().TotalSeconds;
            var speaker = _feeds[LiveSource.Speaker];
            ready = new List<LiveLine>();
            foreach (var (line, deadline) in _held.ToList())
            {
                var need = line.End.TotalSeconds + 0.5;
                var speakerBusy = _pending.Any(c => c.Source == LiveSource.Speaker && c.Start.TotalSeconds < need)
                                  || _inflight?.Any(c => c.Source == LiveSource.Speaker && c.Start.TotalSeconds < need) == true;
                var speakerDone = (speaker.DecidedUntil >= need || speaker.QuietFor(1.0)) && !speakerBusy;
                if (force || speakerDone || now >= deadline)
                {
                    ready.Add(line);
                    _held.Remove((line, deadline));
                }
            }
        }
        foreach (var line in ready)
        {
            List<TimedText> remote;
            lock (_lock) remote = _speakerRecent.ToList();
            var echo = EchoTag.Check(new TimedText(line.Start.TotalSeconds, line.End.TotalSeconds, line.Text), remote);
            if (echo) Log.Info(string.Create(CultureInfo.InvariantCulture, $"live text: mic line at {line.Start.TotalSeconds:0.0} s tagged echo of the system audio"));
            Add(echo ? line with { Echo = true } : line);
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
                Device = w.Runtime == nameof(RuntimeLibrary.Vulkan) ? w.Device : null;
                if (cfg.Gpu && w.Runtime is { } rt && rt != nameof(RuntimeLibrary.Vulkan))
                {
                    _config = cfg with { Gpu = false };
                    ReportGpu($"The graphics card (Vulkan) is not available for live text; used {rt}.");
                }
                SetStatus($"Live text: {WhisperModels.Get(cfg.ModelKey).Name} on {DeviceName.Where(Device, w.Runtime == nameof(RuntimeLibrary.Vulkan))}");
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
public sealed record LiveRunResult(IReadOnlyList<LiveLine> Lines, string ModelKey, string? Runtime, int Restarts, double SkippedSeconds, int Parts)
{
    /// <summary>The language setting at Stop ("auto", a code, "vi+en").</summary>
    public string LanguageSetting { get; init; } = Languages.Auto;
    /// <summary>What Auto detected (null for a fixed setting or when nothing was heard).</summary>
    public string? LanguageDetected { get; init; }
    /// <summary>What the transcript after Stop should use: the setting, or the detected language.</summary>
    public string LanguageEffective { get; init; } = Languages.Auto;
    public string? Device { get; init; }
    public int MergedChunks { get; init; }
    public double MaxLagSeconds { get; init; }
}

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
        var detected = parts.Select(p => p.Live.Language.Detected ?? p.Live.Language.Leading).LastOrDefault(d => d is not null);
        return new LiveRunResult(lines, last.ModelKey, last.Runtime, parts.Sum(p => p.Live.Restarts), parts.Sum(p => p.Live.SkippedSeconds), parts.Count)
        {
            LanguageSetting = last.Language.Setting,
            LanguageDetected = Languages.IsAuto(last.Language.Setting) ? detected : null,
            LanguageEffective = Languages.IsAuto(last.Language.Setting) ? detected ?? Languages.Auto : last.Language.Setting,
            Device = parts.Select(p => p.Live.Device).LastOrDefault(d => d is not null),
            MergedChunks = parts.Sum(p => p.Live.MergedChunks),
            MaxLagSeconds = parts.Max(p => p.Live.MaxLagSeconds),
        };
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
        public bool Echo { get; set; }
        public double? Emitted { get; set; }
        public string? Language { get; set; }
    }

    public sealed class JsonDoc
    {
        public string SessionId { get; set; } = "";
        public string Model { get; set; } = "";
        public string? Runtime { get; set; }
        public string? Device { get; set; }
        public string LanguageSetting { get; set; } = Languages.Auto;
        public string? LanguageDetected { get; set; }
        public bool Translate { get; set; }
        public int Restarts { get; set; }
        public double SkippedSeconds { get; set; }
        public int MergedChunks { get; set; }
        public double MaxLagSeconds { get; set; }
        public List<JsonLine> Lines { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static IReadOnlyList<string> Save(string dir, string sessionId, IReadOnlyList<LiveLine> lines, string model, string? runtime,
        bool translate, int restarts, double skippedSeconds) =>
        Save(dir, sessionId, new LiveRunResult(lines, model, runtime, restarts, skippedSeconds, 1), translate);

    public static IReadOnlyList<string> Save(string dir, string sessionId, LiveRunResult run, bool translate)
    {
        var lines = run.Lines;
        var model = WhisperModels.Find(run.ModelKey)?.Name ?? run.ModelKey;
        var runtime = run.Device ?? run.Runtime;
        var lang = Languages.IsAuto(run.LanguageSetting)
            ? run.LanguageDetected is { } d ? $"{Languages.Name(d)} (detected)" : "Auto"
            : Languages.Name(run.LanguageSetting);
        var sb = new StringBuilder();
        sb.AppendLine($"Live text of recording {sessionId} (written while recording; transcript.md is the full transcript)");
        sb.AppendLine($"Model: {model} on {runtime ?? "unknown"}{(translate ? ", translated to English" : "")}. Language: {lang}. You = microphone, Others = system audio.");
        sb.AppendLine();
        foreach (var l in lines.OrderBy(l => l.Start)) sb.AppendLine(l.ToString());
        File.WriteAllText(Path.Combine(dir, TextFile), sb.ToString());

        var doc = new JsonDoc
        {
            SessionId = sessionId,
            Model = run.ModelKey,
            Runtime = run.Runtime,
            Device = run.Device,
            LanguageSetting = run.LanguageSetting,
            LanguageDetected = run.LanguageDetected,
            Translate = translate,
            Restarts = run.Restarts,
            SkippedSeconds = Math.Round(run.SkippedSeconds, 1),
            MergedChunks = run.MergedChunks,
            MaxLagSeconds = Math.Round(run.MaxLagSeconds, 1),
            Lines = lines.OrderBy(l => l.Start).Select(l => new JsonLine
            {
                Start = Math.Round(l.Start.TotalSeconds, 2),
                End = Math.Round(l.End.TotalSeconds, 2),
                Source = l.Source,
                Speaker = l.Speaker,
                Text = l.Text,
                Echo = l.Echo,
                Emitted = l.Emitted is { } e ? Math.Round(e.TotalSeconds, 2) : null,
                Language = l.Language,
            }).ToList(),
        };
        File.WriteAllText(Path.Combine(dir, JsonFile), JsonSerializer.Serialize(doc, Options));
        return new[] { TextFile, JsonFile };
    }
}

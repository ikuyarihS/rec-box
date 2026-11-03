using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RecBox.Core.Session;

namespace RecBox.Core.Audio;

/// <summary>
/// Records one source (mic or system audio) into one or more segment files.
/// A new segment starts when the device changes (Windows default switched, or the device was lost),
/// so a recording keeps going across a headset or speaker swap.
/// Gaps where the device delivered no data (loopback sends nothing while nothing plays) are filled
/// with silence from the session clock, so mic and system tracks stay aligned in time.
/// </summary>
public sealed class SourceRecorder
{
    /// <summary>A silence gap shorter than this is treated as normal buffer jitter, not filled.</summary>
    private const double GapThresholdSeconds = 0.2;
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly string _dir;
    private readonly Stopwatch _clock;
    private readonly string? _fixedId;

    private IWaveIn? _capture;
    private MMDevice? _device;
    private WaveFileWriter? _writer;
    private SegmentInfo? _seg;
    private long _segBytes;
    private TimeSpan _lastFlush;
    private bool _closing;
    private bool _stopped;
    private float _level;
    private long _levelTicks;

    public SourceRecorder(SourceKind kind, string? deviceId, string dir, Stopwatch clock)
    {
        Kind = kind;
        _fixedId = deviceId;
        _dir = dir;
        _clock = clock;
    }

    public SourceKind Kind { get; }
    public bool FollowsDefault => _fixedId is null;
    public List<SegmentInfo> Segments { get; } = new();
    public string? CurrentDeviceId { get; private set; }
    public string? CurrentDeviceName { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Raised (thread pool) with a short human message when the recorder switches or loses a device.</summary>
    public event Action<SourceRecorder, string>? Notice;

    /// <summary>Peak level 0..1 of the last buffer; decays to 0 when no data arrives.</summary>
    public float Level
    {
        get
        {
            var age = Stopwatch.GetElapsedTime(Interlocked.Read(ref _levelTicks));
            return age > TimeSpan.FromMilliseconds(300) ? 0f : _level;
        }
    }

    public string KindName => Kind == SourceKind.Mic ? "mic" : "speaker";

    public bool Start() => OpenSegment(_fixedId, "start");

    /// <summary>Switch to another device mid-recording (same path the default-device watcher uses).</summary>
    public bool SwitchTo(string? deviceId, string reason, bool force = false)
    {
        if (_stopped) return false;
        if (!force && deviceId is not null && deviceId == CurrentDeviceId) return true;
        CloseSegment();
        Log.Info($"{KindName}: switching device ({reason})");
        var ok = OpenSegment(deviceId, reason);
        if (ok) Notice?.Invoke(this, $"Switched to {CurrentDeviceName}");
        return ok;
    }

    public void Stop()
    {
        _stopped = true;
        CloseSegment();
    }

    private bool OpenSegment(string? id, string reason)
    {
        var dev = AudioDevices.Open(Kind, id);
        if (dev is null)
        {
            LastError = Kind == SourceKind.Mic ? "No microphone available" : "No playback device available";
            return false;
        }

        IWaveIn cap;
        try
        {
            cap = Kind == SourceKind.Mic
                ? new WasapiCapture(dev, true, 100)
                : new WasapiLoopbackCapture(dev);
        }
        catch (Exception ex)
        {
            LastError = $"Could not open {dev.FriendlyName}: {ex.Message}";
            Log.Error($"{KindName}: open {dev.FriendlyName} failed", ex);
            dev.Dispose();
            return false;
        }

        lock (_lock)
        {
            var index = Segments.Count + 1;
            var file = $"{KindName}.part{index}.wav";
            var fmt = WriterFormat(cap.WaveFormat);
            _writer = new WaveFileWriter(Path.Combine(_dir, file), fmt);
            _seg = new SegmentInfo
            {
                Index = index,
                DeviceId = dev.ID,
                Device = dev.FriendlyName,
                Reason = reason,
                StartSeconds = _clock.Elapsed.TotalSeconds,
                File = file,
                SampleRate = fmt.SampleRate,
                Channels = fmt.Channels,
                Encoding = $"{fmt.Encoding} {fmt.BitsPerSample}-bit",
            };
            Segments.Add(_seg);
            _segBytes = 0;
            _lastFlush = _clock.Elapsed;
            _capture = cap;
            _device = dev;
            _closing = false;
            CurrentDeviceId = dev.ID;
            CurrentDeviceName = dev.FriendlyName;
        }

        cap.DataAvailable += OnData;
        cap.RecordingStopped += OnStopped;
        try
        {
            cap.StartRecording();
        }
        catch (Exception ex)
        {
            LastError = $"Could not start {dev.FriendlyName}: {ex.Message}";
            Log.Error($"{KindName}: start {dev.FriendlyName} failed", ex);
            CloseSegment();
            return false;
        }
        Log.Info($"{KindName}: segment {_seg!.Index} on '{dev.FriendlyName}' {_seg.SampleRate} Hz x{_seg.Channels} ({reason})");
        LastError = null;
        return true;
    }

    /// <summary>WASAPI mix formats are usually WAVE_FORMAT_EXTENSIBLE; store them as the plain equivalent.</summary>
    private static WaveFormat WriterFormat(WaveFormat f)
    {
        if (f is WaveFormatExtensible ext)
        {
            return ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT
                ? WaveFormat.CreateIeeeFloatWaveFormat(f.SampleRate, f.Channels)
                : new WaveFormat(f.SampleRate, f.BitsPerSample, f.Channels);
        }
        return f;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(sender, _capture) || _writer is null || _seg is null) return;
            var fmt = _writer.WaveFormat;
            double bps = fmt.AverageBytesPerSecond;

            // fill a silence gap (loopback delivers nothing while nothing plays)
            var segElapsed = _clock.Elapsed.TotalSeconds - _seg.StartSeconds;
            var expectedBefore = (long)(segElapsed * bps) - e.BytesRecorded;
            var gap = expectedBefore - _segBytes;
            if (gap > bps * GapThresholdSeconds)
            {
                gap -= gap % fmt.BlockAlign;
                WriteSilence(gap);
            }

            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _segBytes += e.BytesRecorded;
            _seg.DurationSeconds = _segBytes / bps;

            _level = Peak(e.Buffer, e.BytesRecorded, fmt);
            Interlocked.Exchange(ref _levelTicks, Stopwatch.GetTimestamp());

            if (_clock.Elapsed - _lastFlush > FlushEvery)
            {
                _writer.Flush(); // keeps the WAV header valid if the app is killed
                _lastFlush = _clock.Elapsed;
            }
        }
    }

    private void WriteSilence(long bytes)
    {
        var zero = new byte[Math.Min(bytes, 1 << 16)];
        var left = bytes;
        while (left > 0)
        {
            var n = (int)Math.Min(left, zero.Length);
            _writer!.Write(zero, 0, n);
            left -= n;
        }
        _segBytes += bytes;
    }

    private static float Peak(byte[] buf, int count, WaveFormat fmt)
    {
        float peak = 0;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            for (var i = 0; i + 4 <= count; i += 16)
            {
                var v = Math.Abs(BitConverter.ToSingle(buf, i));
                if (v > peak) peak = v;
            }
        }
        else if (fmt.BitsPerSample == 16)
        {
            for (var i = 0; i + 2 <= count; i += 8)
            {
                var v = Math.Abs(BitConverter.ToInt16(buf, i) / 32768f);
                if (v > peak) peak = v;
            }
        }
        return Math.Clamp(peak, 0f, 1f);
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, _capture) || _closing || _stopped) return;
        // the device went away (unplugged, disabled, driver reset): reopen and keep recording
        Log.Warn($"{KindName}: capture stopped unexpectedly on '{CurrentDeviceName}': {e.Exception?.Message ?? "no error"}");
        Task.Run(Recover);
    }

    private async Task Recover()
    {
        CloseSegment();
        for (var attempt = 1; attempt <= 5 && !_stopped; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
            if (_stopped) return;
            // a fixed device first; after two misses (or when following the default) use the default
            var id = attempt <= 2 ? _fixedId : null;
            if (OpenSegment(id, "device lost, reopened"))
            {
                Notice?.Invoke(this, $"Device changed, now recording {CurrentDeviceName}");
                return;
            }
        }
        if (!_stopped)
        {
            LastError = "Device lost and no replacement found";
            Notice?.Invoke(this, LastError);
        }
    }

    private void CloseSegment()
    {
        IWaveIn? cap;
        MMDevice? dev;
        lock (_lock)
        {
            cap = _capture;
            dev = _device;
            _closing = true;
        }
        if (cap is not null)
        {
            try
            {
                cap.Dispose(); // stops and joins the capture thread; last buffers still land in the file
            }
            catch (Exception ex)
            {
                Log.Error($"{KindName}: closing capture failed", ex);
            }
            cap.DataAvailable -= OnData;
            cap.RecordingStopped -= OnStopped;
        }
        lock (_lock)
        {
            _capture = null;
            _device = null;
            if (_writer is not null)
            {
                try { _writer.Dispose(); }
                catch (Exception ex) { Log.Error($"{KindName}: closing segment file failed", ex); }
                _writer = null;
            }
            _seg = null;
        }
        try { dev?.Dispose(); } catch { }
    }
}

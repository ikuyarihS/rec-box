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
    private const int RecoverAttempts = 5;
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecoverStep = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Serializes every open and close of a segment (Start, SwitchTo, Recover, Stop), so two reopen paths
    /// can never both open a segment and a segment is never opened after Stop.
    /// </summary>
    private readonly object _gate = new();
    /// <summary>Guards the live capture/writer fields that the capture thread also touches.</summary>
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
    private volatile bool _closing;
    private volatile bool _stopped;
    /// <summary>Bumped by every switch, recovery and Stop; a recovery loop exits once a newer one exists.</summary>
    private int _recoverGen;
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

    /// <summary>Called with the device id each time a capture really started on a device, under the open/close gate.</summary>
    public Action<string>? DeviceOpened { get; set; }

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

    public bool Start()
    {
        lock (_gate) return OpenSegment(_fixedId, "start");
    }

    /// <summary>
    /// Switch to another device mid-recording (same path the default-device watcher uses).
    /// When the new device cannot be opened, the recorder retries it, falls back to the previous device
    /// and then the default, and raises Notice so the user knows; it never silently stops recording.
    /// </summary>
    public bool SwitchTo(string? deviceId, string reason, bool force = false)
    {
        string? previous;
        int gen;
        bool ok;
        string? name;
        lock (_gate)
        {
            if (_stopped) return false;
            if (!force && deviceId is not null && deviceId == CurrentDeviceId && _capture is not null) return true;
            previous = CurrentDeviceId;
            gen = Interlocked.Increment(ref _recoverGen); // supersedes any recovery still waiting
            CloseSegment();
            Log.Info($"{KindName}: switching device ({reason})");
            ok = OpenSegment(deviceId, reason);
            name = CurrentDeviceName;
        }
        if (ok)
        {
            Notice?.Invoke(this, $"Switched to {name}");
            return true;
        }

        var error = LastError ?? "device unavailable";
        Log.Warn($"{KindName}: switch to {deviceId ?? "(default)"} failed: {error}; retrying, then falling back");
        Notice?.Invoke(this, $"Could not switch device ({error}), retrying");
        _ = Task.Run(() => Recover(gen, null, deviceId, previous, "switch failed, reopened"));
        return false;
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            Interlocked.Increment(ref _recoverGen);
            CloseSegment();
        }
    }

    /// <summary>Caller holds _gate. Refuses (and releases the new device) after Stop or while a capture is live.</summary>
    private bool OpenSegment(string? id, string reason)
    {
        Debug.Assert(Monitor.IsEntered(_gate));
        if (_stopped) return false;
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

        SegmentInfo seg;
        lock (_lock)
        {
            if (_stopped || _capture is not null)
            {
                // never orphan a live segment by overwriting it, and never open after Stop
                LastError = _stopped ? "Recording stopped" : "A capture is already live";
                Log.Warn($"{KindName}: open on '{dev.FriendlyName}' refused ({LastError})");
                try { cap.Dispose(); } catch { }
                dev.Dispose();
                return false;
            }
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
            seg = _seg;
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
        Log.Info($"{KindName}: segment {seg.Index} on '{dev.FriendlyName}' {seg.SampleRate} Hz x{seg.Channels} ({reason})");
        LastError = null;
        try
        {
            DeviceOpened?.Invoke(dev.ID);
        }
        catch (Exception ex)
        {
            Log.Error($"{KindName}: device opened handler failed", ex);
        }
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

            _level = LevelMeter.Peak(e.Buffer, e.BytesRecorded, fmt);
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

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is not IWaveIn lost || !ReferenceEquals(sender, _capture) || _closing || _stopped) return;
        // the device went away (unplugged, disabled, driver reset): reopen and keep recording
        Log.Warn($"{KindName}: capture stopped unexpectedly on '{CurrentDeviceName}': {e.Exception?.Message ?? "no error"}");
        var gen = Interlocked.Increment(ref _recoverGen);
        var previous = CurrentDeviceId;
        _ = Task.Run(() => Recover(gen, lost, _fixedId, previous, "device lost, reopened"));
    }

    /// <summary>
    /// Reopens after a lost device or a failed switch. Tries <paramref name="preferred"/> twice, then the
    /// <paramref name="previous"/> device, then the Windows default. Every step runs under _gate and exits
    /// when Stop ran, a newer switch or recovery started, or someone else already reopened.
    /// </summary>
    private async Task Recover(int gen, IWaveIn? lostCapture, string? preferred, string? previous, string reason)
    {
        lock (_gate)
        {
            if (!Current(gen)) return;
            if (lostCapture is not null)
            {
                if (ReferenceEquals(_capture, lostCapture)) CloseSegment();
                else if (_capture is not null) return; // already replaced by a switch
            }
        }

        for (var attempt = 1; attempt <= RecoverAttempts; attempt++)
        {
            await Task.Delay(RecoverStep * attempt);
            string? name;
            lock (_gate)
            {
                if (!Current(gen) || _capture is not null) return;
                var id = attempt <= 2 ? preferred : attempt == 3 && previous is not null ? previous : null;
                if (!OpenSegment(id, reason)) continue;
                name = CurrentDeviceName;
            }
            Log.Info($"{KindName}: recovered on '{name}' after {attempt} attempt(s)");
            Notice?.Invoke(this, $"Device changed, now recording {name}");
            return;
        }

        bool gaveUp;
        lock (_gate) gaveUp = Current(gen) && _capture is null;
        if (gaveUp)
        {
            LastError = "Device lost and no replacement found";
            Log.Warn($"{KindName}: {LastError}");
            Notice?.Invoke(this, LastError);
        }
    }

    private bool Current(int gen) => !_stopped && gen == Volatile.Read(ref _recoverGen);

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

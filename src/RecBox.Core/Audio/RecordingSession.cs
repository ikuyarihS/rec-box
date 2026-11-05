using System.Diagnostics;
using RecBox.Core.Session;
using RecBox.Core.Settings;

namespace RecBox.Core.Audio;

/// <summary>One recording: a folder, a shared clock, and a recorder per enabled source.</summary>
public sealed class RecordingSession
{
    private readonly DeviceWatcher? _watcher;
    private readonly List<string> _warnings = new();
    private readonly object _saveGate = new();
    private Task<SessionManifest>? _save;
    private readonly MicBooster? _booster;

    private RecordingSession(string id, string dir, AppSettings settings, MicBooster? booster)
    {
        Id = id;
        Directory = dir;
        Settings = settings.Clone();
        StartedLocal = DateTimeOffset.Now;
        StartedUtc = DateTime.UtcNow;
        Clock = Stopwatch.StartNew();

        if (settings.MicEnabled) Mic = new SourceRecorder(SourceKind.Mic, settings.MicDeviceId, dir, Clock);
        if (settings.SpeakerEnabled) Speaker = new SourceRecorder(SourceKind.Speaker, settings.SpeakerDeviceId, dir, Clock);
        _booster = booster;
        if (Mic is not null && booster is not null) Mic.DeviceOpened = booster.Apply;

        _watcher = new DeviceWatcher();
        _watcher.DefaultChanged += OnDefaultChanged;
    }

    /// <summary>Test seam: a session over part files already on disk, no devices and no watcher.</summary>
    private RecordingSession(string dir, AppSettings settings, MicBooster? booster, IEnumerable<SourceRecorder> recorders)
    {
        _booster = booster;
        Id = Path.GetFileName(dir);
        Directory = dir;
        Settings = settings.Clone();
        StartedLocal = DateTimeOffset.Now;
        StartedUtc = DateTime.UtcNow;
        Clock = Stopwatch.StartNew();
        foreach (var r in recorders)
        {
            if (r.Kind == SourceKind.Mic) Mic = r; else Speaker = r;
        }
        if (Mic is not null && booster is not null) Mic.DeviceOpened = booster.Apply;
    }

    /// <summary>Builds a session from recorders whose segment files are already written (tests only).</summary>
    internal static RecordingSession FromParts(string dir, AppSettings settings, params SourceRecorder[] recorders) =>
        new(dir, settings, null, recorders);

    /// <summary>Same, with a booster; the test raises Mic.DeviceOpened the way a real capture open does.</summary>
    internal static RecordingSession FromParts(string dir, AppSettings settings, MicBooster booster, params SourceRecorder[] recorders) =>
        new(dir, settings, settings.BoostMicWhileRecording ? booster : null, recorders);

    /// <summary>
    /// Stops and saves the session exactly once. Every caller (the Stop button, closing the window,
    /// restart to update) gets the same Task, so two finalizes never run over the same part files.
    /// The progress of the first caller is the one reported.
    /// </summary>
    public Task<SessionManifest> StopAndSaveAsync(IProgress<string>? progress = null)
    {
        lock (_saveGate)
        {
            return _save ??= Task.Run(() =>
            {
                Stop();
                return SessionFinalizer.Finalize(this, progress);
            });
        }
    }

    public string Id { get; }
    public string Directory { get; }
    public AppSettings Settings { get; }
    public DateTimeOffset StartedLocal { get; }
    public DateTime StartedUtc { get; }
    public DateTime EndedUtc { get; private set; }
    public double DurationSeconds { get; private set; }
    public Stopwatch Clock { get; }
    public SourceRecorder? Mic { get; private set; }
    public SourceRecorder? Speaker { get; private set; }
    public IReadOnlyList<string> Warnings => _warnings;
    public IEnumerable<SourceRecorder> Recorders => new[] { Mic, Speaker }.OfType<SourceRecorder>();
    public TimeSpan Elapsed => Clock.Elapsed;

    /// <summary>Raised (thread pool) when a source switched or lost its device.</summary>
    public event Action<string>? Notice;

    /// <summary>Starts recording. Throws InvalidOperationException with a user-facing message when nothing can record.</summary>
    public static RecordingSession Start(AppSettings settings)
    {
        if (!settings.MicEnabled && !settings.SpeakerEnabled)
            throw new InvalidOperationException("Turn on the microphone or system audio first.");

        System.IO.Directory.CreateDirectory(settings.OutputFolder);
        var id = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var dir = Path.Combine(settings.OutputFolder, id);
        for (var n = 2; System.IO.Directory.Exists(dir); n++) dir = Path.Combine(settings.OutputFolder, $"{id}_{n}");
        id = Path.GetFileName(dir);
        System.IO.Directory.CreateDirectory(dir);

        var s = new RecordingSession(id, dir, settings, settings.BoostMicWhileRecording ? MicBooster.Shared : null);
        var errors = new List<string>();
        try
        {
            foreach (var r in s.Recorders.ToList())
            {
                if (r.Start())
                {
                    r.Notice += (rec, msg) => s.Notice?.Invoke($"{(rec.Kind == SourceKind.Mic ? "Microphone" : "System audio")}: {msg}");
                    continue;
                }
                errors.Add(r.LastError ?? "unknown error");
                s._warnings.Add($"{r.KindName} not recorded: {r.LastError}");
                if (r.Kind == SourceKind.Mic) s.Mic = null; else s.Speaker = null;
            }
        }
        catch
        {
            // the caller never gets this session: a source already started must not keep recording or stay boosted
            s.Stop();
            throw;
        }

        if (!s.Recorders.Any())
        {
            s._watcher?.Dispose();
            try
            {
                System.IO.Directory.Delete(dir); // empty folder of a session that never started
                Log.Info($"removed empty session folder {dir} (no source could start)");
            }
            catch (Exception ex)
            {
                Log.Error($"could not remove empty folder {dir}", ex);
            }
            throw new InvalidOperationException(string.Join(". ", errors));
        }

        Log.Info($"session {id} started in {dir}: {string.Join(", ", s.Recorders.Select(r => $"{r.KindName}='{r.CurrentDeviceName}'"))}");
        return s;
    }

    /// <summary>Same path the default-device watcher takes; public so the switch can be driven directly.</summary>
    public bool SwitchSource(SourceKind kind, string? deviceId, string reason, bool force = false)
    {
        var rec = kind == SourceKind.Mic ? Mic : Speaker;
        return rec is not null && rec.SwitchTo(deviceId, reason, force);
    }

    private void OnDefaultChanged(SourceKind kind, string deviceId)
    {
        var rec = kind == SourceKind.Mic ? Mic : Speaker;
        if (rec is null || !rec.FollowsDefault) return;
        Log.Info($"{rec.KindName}: Windows default changed to {deviceId}");
        // SwitchTo skips a no-op (same device, still live), refuses after Stop, and on failure
        // retries, falls back to the previous device or default, and raises Notice for the UI.
        rec.SwitchTo(deviceId, "default device changed");
    }

    public void Stop()
    {
        DurationSeconds = Clock.Elapsed.TotalSeconds;
        EndedUtc = DateTime.UtcNow;
        Clock.Stop();
        if (_watcher is not null)
        {
            _watcher.DefaultChanged -= OnDefaultChanged;
            _watcher.Dispose();
        }
        try
        {
            Parallel.ForEach(Recorders, r => r.Stop());
        }
        finally
        {
            // here and not after the save: every exit path stops first, and the save can take minutes
            _booster?.RestoreAll();
        }
        Log.Info($"session {Id} stopped after {DurationSeconds:F1} s");
    }
}

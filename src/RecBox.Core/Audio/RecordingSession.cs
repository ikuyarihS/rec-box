using System.Diagnostics;
using RecBox.Core.Settings;

namespace RecBox.Core.Audio;

/// <summary>One recording: a folder, a shared clock, and a recorder per enabled source.</summary>
public sealed class RecordingSession
{
    private readonly DeviceWatcher? _watcher;
    private readonly List<string> _warnings = new();

    private RecordingSession(string id, string dir, AppSettings settings)
    {
        Id = id;
        Directory = dir;
        Settings = settings.Clone();
        StartedLocal = DateTimeOffset.Now;
        StartedUtc = DateTime.UtcNow;
        Clock = Stopwatch.StartNew();

        if (settings.MicEnabled) Mic = new SourceRecorder(SourceKind.Mic, settings.MicDeviceId, dir, Clock);
        if (settings.SpeakerEnabled) Speaker = new SourceRecorder(SourceKind.Speaker, settings.SpeakerDeviceId, dir, Clock);

        _watcher = new DeviceWatcher();
        _watcher.DefaultChanged += OnDefaultChanged;
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

        var s = new RecordingSession(id, dir, settings);
        var errors = new List<string>();
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
        Parallel.ForEach(Recorders, r => r.Stop());
        Log.Info($"session {Id} stopped after {DurationSeconds:F1} s");
    }
}

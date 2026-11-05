using System.Diagnostics;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace RecBox.Core.Audio;

/// <summary>A hardware level control on the capture path, one channel of a "Microphone Boost" part.</summary>
public interface IMicBoostControl
{
    /// <summary>Stable across opens of the device, so a saved value finds its control again.</summary>
    string Key { get; }
    string Name { get; }
    float MaxDb { get; }
    float LevelDb { get; set; }
}

public interface IMicEndpoint : IDisposable
{
    string Id { get; }
    string Name { get; }
    /// <summary>Master volume scalar 0..1.</summary>
    float Volume { get; set; }
    bool Mute { get; set; }
    /// <summary>Empty when the device has no hardware boost.</summary>
    IReadOnlyList<IMicBoostControl> BoostControls { get; }
}

public interface IMicEndpointProvider
{
    /// <summary>Null when the device is not available right now (unplugged, disabled).</summary>
    IMicEndpoint? Open(string deviceId);
}

/// <summary>The original values of one device. Only values RecBox changed are set.</summary>
public sealed class MicBoostEntry
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public float? Volume { get; set; }
    public bool? Mute { get; set; }
    public Dictionary<string, float> BoostDb { get; set; } = new();

    internal bool IsEmpty => Volume is null && Mute is null && BoostDb.Count == 0;

    internal MicBoostEntry Clone()
    {
        var copy = (MicBoostEntry)MemberwiseClone();
        copy.BoostDb = new Dictionary<string, float>(BoostDb);
        return copy;
    }
}

/// <summary>What the crash file holds: whose boost it is and every value still to restore.</summary>
public sealed class MicBoostState
{
    public int OwnerPid { get; set; }
    public DateTime OwnerStartedUtc { get; set; }
    public List<MicBoostEntry> Devices { get; set; } = new();

    /// <summary>Replaces the file in one step, so a crash never leaves half of it.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Throws when the file is unreadable or not a state file.</summary>
    public static MicBoostState Load(string path) =>
        JsonSerializer.Deserialize<MicBoostState>(File.ReadAllText(path))
        ?? throw new JsonException("empty mic boost state");
}

/// <summary>
/// Raises the captured microphone to its maximum while recording and puts every changed value back.
/// The originals reach the crash file before anything changes, so a kill at any moment can be undone at the
/// next start. Audio APIs run on a pool thread with a bounded wait: a hung driver or a failure here is logged and
/// never stops a recording or a close.
/// </summary>
public sealed class MicBooster
{
    public const string StateFilePattern = "mic-boost-*.json";
    private const float VolumeEpsilon = 0.001f;
    private const float BoostEpsilonDb = 0.01f;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private static readonly object SharedGate = new();
    private static MicBooster? _shared;

    private readonly object _lock = new();
    private readonly IMicEndpointProvider _provider;
    private readonly string _stateFile;
    private readonly TimeSpan _timeout;
    private readonly Action<string> _log;
    private readonly MicBoostState _state;
    /// <summary>Bumped by RestoreAll: a boost still queued from before it must not run after it.</summary>
    private int _restoreGen;

    public MicBooster(IMicEndpointProvider provider, string stateFile, TimeSpan? timeout = null, Action<string>? log = null)
    {
        _provider = provider;
        _stateFile = stateFile;
        _timeout = timeout ?? DefaultTimeout;
        _log = log ?? Log.Info;
        using var self = Process.GetCurrentProcess();
        _state = new MicBoostState { OwnerPid = Environment.ProcessId, OwnerStartedUtc = self.StartTime.ToUniversalTime() };
    }

    /// <summary>The app's one booster, created by the first recording that boosts.</summary>
    public static MicBooster Shared
    {
        get
        {
            lock (SharedGate)
                return _shared ??= new MicBooster(new WasapiMicEndpointProvider(), AppPaths.MicBoostStateFile(Environment.ProcessId));
        }
    }

    /// <summary>Test seam: replaces the shared booster.</summary>
    internal static void SetShared(MicBooster? booster)
    {
        lock (SharedGate) _shared = booster;
    }

    /// <summary>For exit hooks: restores if any recording used the booster. Safe to call at any time.</summary>
    public static void RestoreSharedIfUsed()
    {
        MicBooster? shared;
        lock (SharedGate) shared = _shared;
        shared?.RestoreAll();
    }

    /// <summary>
    /// The capture is now running on this device: boost it and restore any other device boosted before.
    /// Calling it again for the same device keeps the saved originals (they are not the current values).
    /// </summary>
    public void Apply(string deviceId)
    {
        var gen = Volatile.Read(ref _restoreGen);
        Run($"boost of {deviceId}", () =>
        {
            if (gen != Volatile.Read(ref _restoreGen))
            {
                Log.Warn($"mic boost: skipped a late boost of {deviceId}, the recording already stopped");
                return;
            }
            ApplyCore(deviceId);
        });
    }

    /// <summary>Restores every changed value. A device that cannot be reached stays in the crash file.</summary>
    public void RestoreAll()
    {
        Interlocked.Increment(ref _restoreGen);
        Run("restore", RestoreCore);
    }

    private void Run(string what, Action work)
    {
        var task = Task.Run(() =>
        {
            try
            {
                lock (_lock) work();
            }
            catch (Exception ex)
            {
                Log.Error($"mic boost: {what} failed", ex);
            }
        });
        if (!task.Wait(_timeout)) Log.Warn($"mic boost: {what} still running after {_timeout.TotalSeconds:0.#} s, not waiting for it");
    }

    private void ApplyCore(string id)
    {
        foreach (var other in _state.Devices.Where(d => d.DeviceId != id).ToList())
        {
            if (RestoreEntry(_provider, other, _log)) _state.Devices.Remove(other);
        }
        Persist();

        IMicEndpoint? opened;
        try
        {
            opened = _provider.Open(id);
        }
        catch (Exception ex)
        {
            Log.Warn($"mic boost: could not open {id}: {ex.Message}");
            return;
        }
        if (opened is null)
        {
            Log.Warn($"mic boost: {id} is not available, nothing changed");
            return;
        }

        using var ep = opened;
        var existing = _state.Devices.FirstOrDefault(d => d.DeviceId == id);
        var entry = existing?.Clone() ?? new MicBoostEntry { DeviceId = id, DeviceName = ep.Name };

        var volume = ep.Volume;
        var muted = ep.Mute;
        var raise = new List<(IMicBoostControl Control, float Level, float Max)>();
        foreach (var c in ep.BoostControls)
        {
            try
            {
                var level = c.LevelDb;
                var max = c.MaxDb;
                if (level < max - BoostEpsilonDb) raise.Add((c, level, max));
            }
            catch (Exception ex)
            {
                Log.Warn($"mic boost: '{ep.Name}' control {c.Name} unreadable, skipped: {ex.Message}");
            }
        }
        var raiseVolume = volume < 1f - VolumeEpsilon;
        if (!raiseVolume && !muted && raise.Count == 0)
        {
            _log($"mic boost: '{ep.Name}' already at maximum, nothing changed");
            return;
        }

        // the original is whatever was there before the first change: never overwrite a saved one
        var addedVolume = raiseVolume && entry.Volume is null;
        var addedMute = muted && entry.Mute is null;
        if (addedVolume) entry.Volume = volume;
        if (addedMute) entry.Mute = muted;
        var addedBoosts = new HashSet<string>();
        foreach (var r in raise)
        {
            if (entry.BoostDb.TryAdd(r.Control.Key, r.Level)) addedBoosts.Add(r.Control.Key);
        }

        // write the originals first; without them a crash could not be undone, so change nothing
        if (existing is not null) _state.Devices.Remove(existing);
        _state.Devices.Add(entry);
        try
        {
            _state.Save(_stateFile);
        }
        catch (Exception ex)
        {
            _state.Devices.Remove(entry);
            if (existing is not null) _state.Devices.Add(existing);
            Log.Error($"mic boost: could not save the original values of '{ep.Name}' to {_stateFile}, not boosting", ex);
            return;
        }

        var dirty = false;
        if (muted)
        {
            try
            {
                ep.Mute = false;
                _log($"mic boost: '{ep.Name}' mute on -> {(ep.Mute ? "on" : "off")}");
            }
            catch (Exception ex)
            {
                Log.Warn($"mic boost: '{ep.Name}' could not unmute: {ex.Message}");
                if (addedMute) { entry.Mute = null; dirty = true; }
            }
        }
        if (raiseVolume)
        {
            try
            {
                ep.Volume = 1f;
                _log($"mic boost: '{ep.Name}' volume {Percent(volume)} -> {Percent(ep.Volume)}");
            }
            catch (Exception ex)
            {
                Log.Warn($"mic boost: '{ep.Name}' could not set the volume: {ex.Message}");
                if (addedVolume) { entry.Volume = null; dirty = true; }
            }
        }
        foreach (var (control, level, max) in raise)
        {
            try
            {
                control.LevelDb = max;
                _log($"mic boost: '{ep.Name}' {control.Name} {level:0.##} dB -> {control.LevelDb:0.##} dB");
            }
            catch (Exception ex)
            {
                Log.Warn($"mic boost: '{ep.Name}' could not raise {control.Name}: {ex.Message}");
                if (addedBoosts.Contains(control.Key)) { entry.BoostDb.Remove(control.Key); dirty = true; }
            }
        }

        if (entry.IsEmpty) _state.Devices.Remove(entry);
        if (dirty) Persist();
    }

    private void RestoreCore()
    {
        foreach (var entry in _state.Devices.ToList())
        {
            if (RestoreEntry(_provider, entry, _log)) _state.Devices.Remove(entry);
        }
        Persist();
    }

    /// <summary>Restores what it can and clears those values from the entry. True when nothing is left to restore.</summary>
    private static bool RestoreEntry(IMicEndpointProvider provider, MicBoostEntry e, Action<string> log)
    {
        IMicEndpoint? opened;
        try
        {
            opened = provider.Open(e.DeviceId);
        }
        catch (Exception ex)
        {
            Log.Warn($"mic restore: could not open '{e.DeviceName}': {ex.Message}");
            return false;
        }
        if (opened is null)
        {
            Log.Warn($"mic restore: '{e.DeviceName}' is not available, its values stay saved");
            return false;
        }

        using var ep = opened;
        if (e.Volume is { } volume)
        {
            try
            {
                var before = ep.Volume;
                ep.Volume = volume;
                log($"mic restore: '{e.DeviceName}' volume {Percent(before)} -> {Percent(ep.Volume)}");
                e.Volume = null;
            }
            catch (Exception ex)
            {
                Log.Warn($"mic restore: '{e.DeviceName}' volume not restored: {ex.Message}");
            }
        }
        if (e.BoostDb.Count > 0)
        {
            var controls = ep.BoostControls;
            foreach (var (key, db) in e.BoostDb.ToList())
            {
                var control = controls.FirstOrDefault(c => c.Key == key);
                if (control is null)
                {
                    Log.Warn($"mic restore: '{e.DeviceName}' no longer has boost control {key}");
                    e.BoostDb.Remove(key);
                    continue;
                }
                try
                {
                    var before = control.LevelDb;
                    control.LevelDb = db;
                    log($"mic restore: '{e.DeviceName}' {control.Name} {before:0.##} dB -> {control.LevelDb:0.##} dB");
                    e.BoostDb.Remove(key);
                }
                catch (Exception ex)
                {
                    Log.Warn($"mic restore: '{e.DeviceName}' {control.Name} not restored: {ex.Message}");
                }
            }
        }
        if (e.Mute is { } mute)
        {
            try
            {
                var before = ep.Mute;
                ep.Mute = mute;
                log($"mic restore: '{e.DeviceName}' mute {(before ? "on" : "off")} -> {(ep.Mute ? "on" : "off")}");
                e.Mute = null;
            }
            catch (Exception ex)
            {
                Log.Warn($"mic restore: '{e.DeviceName}' mute not restored: {ex.Message}");
            }
        }
        return e.IsEmpty;
    }

    private void Persist()
    {
        try
        {
            if (_state.Devices.Count > 0) _state.Save(_stateFile);
            else if (File.Exists(_stateFile)) File.Delete(_stateFile);
        }
        catch (Exception ex)
        {
            Log.Error($"mic boost: could not update {_stateFile}", ex);
        }
    }

    private static string Percent(float scalar) => $"{scalar * 100:0.#}%";

    /// <summary>
    /// App start: restores what a crashed or killed RecBox left boosted, then deletes its file. A file whose owner
    /// is still running belongs to another open RecBox and is left alone. A bad file never stops the start.
    /// </summary>
    public static void RecoverFromCrash(IMicEndpointProvider provider, string dir, Func<MicBoostState, bool>? ownerAlive = null, Action<string>? log = null)
    {
        if (!Directory.Exists(dir)) return;
        ownerAlive ??= IsOwnerAlive;
        log ??= Log.Info;
        foreach (var file in Directory.GetFiles(dir, StateFilePattern))
        {
            try
            {
                MicBoostState? state = null;
                try
                {
                    state = MicBoostState.Load(file);
                }
                catch (Exception ex)
                {
                    Log.Warn($"mic restore: {file} is unreadable, removing it: {ex.Message}");
                }
                if (state is not null)
                {
                    if (ownerAlive(state)) continue;
                    log($"mic restore: found {Path.GetFileName(file)} from a RecBox that did not finish (pid {state.OwnerPid})");
                    foreach (var entry in state.Devices)
                    {
                        if (!RestoreEntry(provider, entry, log))
                            Log.Warn($"mic restore: '{entry.DeviceName}' could not be fully restored, giving up on it");
                    }
                }
                File.Delete(file);
            }
            catch (Exception ex)
            {
                Log.Error($"mic restore: recovering {file} failed", ex);
            }
        }
    }

    /// <summary>RecoverFromCrash on the real devices, without holding up the start for long.</summary>
    public static void RecoverAtStartup(string dir)
    {
        var task = Task.Run(() =>
        {
            try
            {
                RecoverFromCrash(new WasapiMicEndpointProvider(), dir);
            }
            catch (Exception ex)
            {
                Log.Error("mic restore at startup failed", ex);
            }
        });
        if (!task.Wait(DefaultTimeout)) Log.Warn("mic restore at startup is taking long, continuing the start");
    }

    private static bool IsOwnerAlive(MicBoostState state)
    {
        try
        {
            using var p = Process.GetProcessById(state.OwnerPid);
            return Math.Abs((p.StartTime.ToUniversalTime() - state.OwnerStartedUtc).TotalSeconds) < 2;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Real endpoints through NAudio. Open and use on one MTA thread; dispose after each operation.</summary>
public sealed class WasapiMicEndpointProvider : IMicEndpointProvider
{
    public IMicEndpoint? Open(string deviceId)
    {
        using var en = new MMDeviceEnumerator();
        var d = en.GetDevice(deviceId);
        if (d.State != DeviceState.Active)
        {
            d.Dispose();
            return null;
        }
        return new WasapiMicEndpoint(d);
    }

    private sealed class WasapiMicEndpoint : IMicEndpoint
    {
        /// <summary>Audio endpoints have ids like {0.0.1.00000000}.{guid}; adapter filters start with {2}.</summary>
        private const string EndpointIdPrefix = "{0.0.";

        private readonly MMDevice _device;
        private readonly List<MMDevice> _filters = new();
        private IReadOnlyList<IMicBoostControl>? _controls;

        public WasapiMicEndpoint(MMDevice device) => _device = device;

        public string Id => _device.ID;
        public string Name => _device.FriendlyName;

        public float Volume
        {
            get => _device.AudioEndpointVolume.MasterVolumeLevelScalar;
            set => _device.AudioEndpointVolume.MasterVolumeLevelScalar = value;
        }

        public bool Mute
        {
            get => _device.AudioEndpointVolume.Mute;
            set => _device.AudioEndpointVolume.Mute = value;
        }

        public IReadOnlyList<IMicBoostControl> BoostControls => _controls ??= FindBoosts();

        /// <summary>
        /// A capture endpoint attaches at the jack of the adapter's topology filter, so the signal path to search runs
        /// from there along the outgoing parts, across the bridge to the wave filter, looking for parts named like a boost.
        /// </summary>
        private IReadOnlyList<IMicBoostControl> FindBoosts()
        {
            var found = new List<IMicBoostControl>();
            try
            {
                var topology = _device.DeviceTopology;
                for (uint i = 0; i < topology.ConnectorCount; i++)
                {
                    var connector = topology.GetConnector(i);
                    if (!connector.IsConnected) continue;
                    Walk(connector.ConnectedTo.Part, connector.ConnectedToDeviceId, new HashSet<string>(), found);
                }
            }
            catch (Exception ex)
            {
                Log.Info($"mic boost: no hardware boost on '{Name}' ({ex.Message})");
                return found;
            }
            if (found.Count == 0) Log.Info($"mic boost: '{Name}' has no hardware boost control");
            return found;
        }

        private void Walk(Part part, string filterId, HashSet<string> seen, List<IMicBoostControl> found)
        {
            if (!seen.Add(part.GlobalId)) return;
            if (part.Name.Contains("boost", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var level = part.AudioVolumeLevel;
                    if (level is not null)
                    {
                        for (uint ch = 0; ch < level.ChannelCount; ch++)
                            found.Add(new PartBoostControl(part, level, ch));
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"mic boost: part '{part.Name}' has no level control ({ex.Message})");
                }
            }

            PartsList? outgoing = null;
            try
            {
                outgoing = part.PartsOutgoing;
            }
            catch
            {
                // the end of the path: nothing follows this part
            }
            if (outgoing is not null)
            {
                for (uint i = 0; i < outgoing.Count; i++) Walk(outgoing[i], filterId, seen, found);
            }
            if (part.PartType == PartTypeEnum.Connector) Cross(part, filterId, seen, found);
        }

        /// <summary>
        /// A path can span two filters joined by a connector, so a connector part continues in whatever its connector
        /// links to, except back to an endpoint. The filter is opened by id: NAudio's Part.DeviceTopology throws
        /// InvalidOleVariantTypeException.
        /// </summary>
        private void Cross(Part part, string filterId, HashSet<string> seen, List<IMicBoostControl> found)
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                var filter = en.GetDevice(filterId);
                _filters.Add(filter);
                var topology = filter.DeviceTopology;
                for (uint i = 0; i < topology.ConnectorCount; i++)
                {
                    var connector = topology.GetConnector(i);
                    if (connector.Part.LocalId != part.LocalId || !connector.IsConnected) continue;
                    var next = connector.ConnectedToDeviceId;
                    if (next.StartsWith(EndpointIdPrefix, StringComparison.Ordinal)) continue;
                    Walk(connector.ConnectedTo.Part, next, seen, found);
                }
            }
            catch (Exception ex)
            {
                Log.Info($"mic boost: stopped at connector '{part.Name}' ({ex.Message})");
            }
        }

        public void Dispose()
        {
            foreach (var filter in _filters) filter.Dispose();
            _device.Dispose();
        }
    }

    private sealed class PartBoostControl : IMicBoostControl
    {
        private readonly NAudio.Wasapi.CoreAudioApi.AudioVolumeLevel _level;
        private readonly uint _channel;

        public PartBoostControl(Part part, NAudio.Wasapi.CoreAudioApi.AudioVolumeLevel level, uint channel)
        {
            _level = level;
            _channel = channel;
            Key = $"{part.GlobalId}#{channel}";
            Name = $"{part.Name} (channel {channel})";
            level.GetLevelRange(channel, out _, out var max, out _);
            MaxDb = max;
        }

        public string Key { get; }
        public string Name { get; }
        public float MaxDb { get; }

        public float LevelDb
        {
            get => _level.GetLevel(_channel);
            set => _level.SetLevel(_channel, value);
        }
    }
}

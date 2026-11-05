using System.Diagnostics;
using NAudio.Wave;
using RecBox.Core.Audio;
using RecBox.Core.Session;
using RecBox.Core.Settings;
using Xunit;

namespace RecBox.Tests;

/// <summary>Every path out of a recording puts the fake endpoint back; the crash file survives what they cannot.</summary>
public sealed class MicBoostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recbox-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeProvider _provider = new();
    private readonly List<string> _log = new();
    private readonly string _stateFile;

    public MicBoostTests()
    {
        Directory.CreateDirectory(_root);
        _stateFile = Path.Combine(_root, "mic-boost-1.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private MicBooster Booster(TimeSpan? timeout = null) =>
        new(_provider, _stateFile, timeout, line => { lock (_log) _log.Add(line); });

    private FakeEndpoint Device(string id, float volume = 0.4f, bool mute = true, params FakeControl[] boosts) =>
        _provider.Devices[id] = new FakeEndpoint(id, $"Mic {id}", volume, mute, boosts);

    private static void AssertOriginal(FakeEndpoint d, float volume = 0.4f, bool mute = true)
    {
        Assert.Equal(volume, d.Volume);
        Assert.Equal(mute, d.Mute);
    }

    private static void AssertBoosted(FakeEndpoint d)
    {
        Assert.Equal(1f, d.Volume);
        Assert.False(d.Mute);
    }

    // ---------- applying ----------
    [Fact]
    public void Apply_UnmutesAndMaxes_ThenRestoreAll_PutsBackAndRemovesTheFile()
    {
        var d = Device("A");
        var b = Booster();

        b.Apply("A");
        AssertBoosted(d);
        Assert.True(File.Exists(_stateFile));

        b.RestoreAll();
        AssertOriginal(d);
        Assert.False(File.Exists(_stateFile));
    }

    [Fact]
    public void HardwareBoost_IsRaisedToMax_AndRestoredPerControl()
    {
        var left = new FakeControl("part#0", 0, 30);
        var right = new FakeControl("part#1", 10, 30);
        var d = Device("A", 0.5f, false, left, right);
        var b = Booster();

        b.Apply("A");
        Assert.Equal(30, left.LevelDb);
        Assert.Equal(30, right.LevelDb);

        b.RestoreAll();
        Assert.Equal(0, left.LevelDb);
        Assert.Equal(10, right.LevelDb);
        AssertOriginal(d, 0.5f, false);
    }

    [Fact]
    public void NoHardwareBoost_IsSkippedCleanly()
    {
        var d = Device("A", 0.5f, false);
        var b = Booster();
        b.Apply("A");
        AssertBoosted(d);
        b.RestoreAll();
        AssertOriginal(d, 0.5f, false);
    }

    [Fact]
    public void AlreadyAtMaximum_ChangesAndRestoresNothing()
    {
        var c = new FakeControl("part#0", 30, 30);
        var d = Device("A", 1f, false, c);
        var b = Booster();

        b.Apply("A");
        Assert.False(File.Exists(_stateFile));
        b.RestoreAll();

        Assert.Equal(0, d.VolumeWrites);
        Assert.Equal(0, d.MuteWrites);
        Assert.Equal(0, c.Writes);
    }

    [Fact]
    public void OnlyTheValuesThatChanged_AreRestored()
    {
        var d = Device("A", 0.5f, false);
        var b = Booster();
        b.Apply("A");
        b.RestoreAll();
        Assert.Equal(0, d.MuteWrites);
        Assert.Equal(2, d.VolumeWrites);
    }

    [Fact]
    public void ApplyAgain_OnTheSameDevice_KeepsTheTrueOriginal()
    {
        var d = Device("A");
        var b = Booster();

        b.Apply("A");
        b.Apply("A"); // a forced reopen of the same device reads 100% and unmuted by now
        Assert.Equal(0.4f, MicBoostState.Load(_stateFile).Devices.Single().Volume);

        b.RestoreAll();
        AssertOriginal(d);
    }

    [Fact]
    public void Apply_ReassertsAfterSomethingLoweredTheVolume_AndStillRestoresTheFirstOriginal()
    {
        var d = Device("A");
        var b = Booster();
        b.Apply("A");
        d.Volume = 0.7f;

        b.Apply("A");
        AssertBoosted(d);

        b.RestoreAll();
        AssertOriginal(d);
    }

    [Fact]
    public void Logs_DeviceName_And_BeforeAfter_ForEveryChangeAndRestore()
    {
        var c = new FakeControl("part#0", 6, 30);
        Device("A", 0.4f, true, c);
        var b = Booster();
        b.Apply("A");
        b.RestoreAll();

        var all = string.Join("\n", _log);
        Assert.Contains("mic boost: 'Mic A' mute on -> off", all);
        Assert.Contains("mic boost: 'Mic A' volume 40% -> 100%", all);
        Assert.Contains("mic boost: 'Mic A' Boost part#0 6 dB -> 30 dB", all);
        Assert.Contains("mic restore: 'Mic A' volume 100% -> 40%", all);
        Assert.Contains("mic restore: 'Mic A' mute off -> on", all);
        Assert.Contains("mic restore: 'Mic A' Boost part#0 30 dB -> 6 dB", all);
    }

    // ---------- switching devices ----------
    [Fact]
    public void Switch_RestoresTheOldDevice_AndBoostsTheNew_AndBackAgain()
    {
        var a = Device("A");
        var c = Device("B", 0.6f, false);
        var b = Booster();

        b.Apply("A");
        b.Apply("B");
        AssertOriginal(a);
        AssertBoosted(c);

        b.Apply("A");
        AssertBoosted(a);
        AssertOriginal(c, 0.6f, false);

        b.RestoreAll();
        AssertOriginal(a);
        AssertOriginal(c, 0.6f, false);
        Assert.False(File.Exists(_stateFile));
    }

    [Fact]
    public void OldDeviceUnplugged_OnSwitch_IsRestoredOnceItIsBack_WithItsOriginal()
    {
        var a = Device("A");
        var c = Device("B", 0.6f, false);
        var b = Booster();
        b.Apply("A");

        a.Unplugged = true;
        b.Apply("B");
        AssertBoosted(c);
        Assert.Contains(MicBoostState.Load(_stateFile).Devices, e => e.DeviceId == "A" && e.Volume == 0.4f);

        a.Unplugged = false;
        b.RestoreAll();
        AssertOriginal(a);
        AssertOriginal(c, 0.6f, false);
        Assert.False(File.Exists(_stateFile));
    }

    [Fact]
    public void BackToADevicePendingRestore_KeepsItsOriginal()
    {
        var a = Device("A");
        Device("B", 0.6f, false);
        var b = Booster();
        b.Apply("A");
        a.Unplugged = true;
        b.Apply("B");
        a.Unplugged = false;

        // A still reads 100% here: its saved 40% must survive this boost, not be replaced by 100%
        b.Apply("A");
        AssertBoosted(a);
        b.RestoreAll();
        AssertOriginal(a);
    }

    // ---------- every exit path of a real session ----------
    private RecordingSession Session(MicBooster booster, bool boostSetting = true)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var clock = Stopwatch.StartNew();
        var settings = new AppSettings { OutputFolder = _root, SpeakerEnabled = false, BoostMicWhileRecording = boostSetting };
        return RecordingSession.FromPartsBoosted(dir, settings, booster, PartRecorder(dir, clock));
    }

    [Fact]
    public async Task StopButton_Restores()
    {
        var d = Device("A");
        var s = Session(Booster());
        s.Mic!.DeviceOpened!("A");
        AssertBoosted(d);

        await s.StopAndSaveAsync();

        AssertOriginal(d);
        Assert.False(File.Exists(_stateFile));
    }

    [Fact]
    public void Stop_RestoresBeforeTheSaveStarts()
    {
        var d = Device("A");
        var s = Session(Booster());
        s.Mic!.DeviceOpened!("A");

        s.Stop(); // what the save task runs first; nothing is saved yet

        AssertOriginal(d);
        Assert.Null(SessionManifest.Load(s.Directory));
    }

    [Fact]
    public async Task CloseWhileSaving_RestoresOnce()
    {
        var d = Device("A");
        var s = Session(Booster());
        s.Mic!.DeviceOpened!("A");
        var writes = d.VolumeWrites;

        var first = s.StopAndSaveAsync();
        var second = s.StopAndSaveAsync();
        Assert.Same(first, second);
        await first;

        AssertOriginal(d);
        Assert.Equal(writes + 1, d.VolumeWrites);
    }

    [Fact]
    public void ShutdownClose_BlockingOnTheSave_Restores()
    {
        var d = Device("A");
        var s = Session(Booster());
        s.Mic!.DeviceOpened!("A");

#pragma warning disable xUnit1031 // blocking is the path under test: MainViewModel.FinishBeforeExitNow
        s.StopAndSaveAsync().GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        AssertOriginal(d);
        Assert.NotNull(SessionManifest.Load(s.Directory));
    }

    [Fact]
    public void SettingOff_TouchesNothing()
    {
        var d = Device("A");
        var s = Session(Booster(), boostSetting: false);

        Assert.Null(s.Mic!.DeviceOpened);
        s.Stop();

        Assert.Equal(0, d.VolumeWrites);
        Assert.Equal(0, d.MuteWrites);
        AssertOriginal(d);
    }

    [Fact]
    public void RestartForTheNextRecording_ReadsFreshOriginals()
    {
        var d = Device("A");
        var b = Booster();
        b.Apply("A");
        b.RestoreAll();
        d.Volume = 0.8f; // the user moved it between recordings

        b.Apply("A");
        b.RestoreAll();

        AssertOriginal(d, 0.8f, true);
    }

    [Fact]
    public void ProcessExitHook_RestoresTheSharedBooster_AndIsSafeTwice()
    {
        var d = Device("A");
        var b = Booster();
        MicBooster.SetShared(b);
        try
        {
            b.Apply("A");
            MicBooster.RestoreSharedIfUsed();
            AssertOriginal(d);
            MicBooster.RestoreSharedIfUsed();
            AssertOriginal(d);
        }
        finally
        {
            MicBooster.SetShared(null);
        }
    }

    // ---------- crash file ----------
    [Fact]
    public void OriginalsAreOnDisk_BeforeAnyValueChanges()
    {
        var c = new FakeControl("part#0", 6, 30);
        var d = Device("A", 0.4f, true, c);
        MicBoostEntry? seen = null;
        d.BeforeWrite = () => seen ??= MicBoostState.Load(_stateFile).Devices.Single();

        Booster().Apply("A");

        Assert.NotNull(seen);
        Assert.Equal(0.4f, seen!.Volume);
        Assert.True(seen.Mute);
        Assert.Equal(6, seen.BoostDb["part#0"]);
    }

    [Fact]
    public void CrashFile_RoundTrips_AndNextStartRestoresAndDeletesIt()
    {
        var c = new FakeControl("part#0", 6, 30);
        var d = Device("A", 0.4f, true, c);
        Booster().Apply("A"); // killed here: no restore runs
        AssertBoosted(d);
        Assert.Equal(30, c.LevelDb);

        var state = MicBoostState.Load(_stateFile);
        Assert.Equal(Environment.ProcessId, state.OwnerPid);
        var entry = state.Devices.Single();
        Assert.Equal("A", entry.DeviceId);
        Assert.Equal("Mic A", entry.DeviceName);

        MicBooster.RecoverFromCrash(_provider, _root, _ => false, _log.Add);

        AssertOriginal(d);
        Assert.Equal(6, c.LevelDb);
        Assert.False(File.Exists(_stateFile));
    }

    [Fact]
    public void Recovery_LeavesTheFileOfAnotherRunningRecBoxAlone()
    {
        var d = Device("A");
        Booster().Apply("A");

        MicBooster.RecoverFromCrash(_provider, _root, _ => true, _log.Add);

        AssertBoosted(d);
        Assert.True(File.Exists(_stateFile));
    }

    [Fact]
    public void Recovery_DefaultOwnerCheck_SeesThisProcessAlive_AndAnOldStartTimeDead()
    {
        var d = Device("A");
        Booster().Apply("A");

        MicBooster.RecoverFromCrash(_provider, _root, log: _log.Add);
        AssertBoosted(d); // this test process is the owner and is running

        var state = MicBoostState.Load(_stateFile);
        state.OwnerStartedUtc = state.OwnerStartedUtc.AddDays(-3); // same pid number, reused by another program
        state.Save(_stateFile);
        MicBooster.RecoverFromCrash(_provider, _root, log: _log.Add);
        AssertOriginal(d);
    }

    [Theory]
    [InlineData("not json {{{")]
    [InlineData("null")]
    [InlineData("")]
    public void Recovery_CorruptFile_IsRemoved_WithoutThrowing(string content)
    {
        File.WriteAllText(_stateFile, content);
        MicBooster.RecoverFromCrash(_provider, _root, _ => false, _log.Add);
        Assert.False(File.Exists(_stateFile));
    }

    [Fact]
    public void Recovery_DeviceGone_DeletesTheFileAndKeepsGoing()
    {
        var a = Device("A");
        Device("B", 0.6f, false);
        var state = new MicBoostState
        {
            OwnerPid = 1,
            Devices =
            {
                new MicBoostEntry { DeviceId = "gone", DeviceName = "Gone", Volume = 0.1f },
                new MicBoostEntry { DeviceId = "A", DeviceName = "Mic A", Volume = 0.25f },
            },
        };
        state.Save(_stateFile);

        MicBooster.RecoverFromCrash(_provider, _root, _ => false, _log.Add);

        Assert.Equal(0.25f, a.Volume);
        Assert.False(File.Exists(_stateFile));
    }

    [Fact]
    public void Recovery_MissingFolder_DoesNothing() =>
        MicBooster.RecoverFromCrash(_provider, Path.Combine(_root, "nope"), _ => false, _log.Add);

    // ---------- failures never stop a recording ----------
    [Fact]
    public void CrashFileCannotBeWritten_ChangesNothing()
    {
        var d = Device("A");
        var blocker = Path.Combine(_root, "a-file");
        File.WriteAllText(blocker, "");
        var b = new MicBooster(_provider, Path.Combine(blocker, "mic-boost-1.json"), null, _log.Add);

        b.Apply("A");

        Assert.Equal(0, d.VolumeWrites);
        Assert.Equal(0, d.MuteWrites);
        b.RestoreAll();
        AssertOriginal(d);
    }

    [Fact]
    public void AWriteThatFailed_IsNotInTheUndoLog()
    {
        var d = Device("A");
        d.ThrowOnVolumeWrite = true;
        var b = Booster();

        b.Apply("A");

        Assert.False(d.Mute);
        var entry = MicBoostState.Load(_stateFile).Devices.Single();
        Assert.Null(entry.Volume);
        Assert.True(entry.Mute);

        d.ThrowOnVolumeWrite = false;
        var writes = d.VolumeWrites;
        b.RestoreAll();
        Assert.Equal(writes, d.VolumeWrites);
        AssertOriginal(d);
    }

    [Fact]
    public void DeviceThatThrowsOnOpen_DoesNotThrowOutOfApplyOrRestore()
    {
        var b = Booster();
        _provider.ThrowOnOpen = true;
        b.Apply("A");
        b.RestoreAll();
    }

    [Fact]
    public void RestoreThatFails_KeepsTheValueForTheNextStart()
    {
        var d = Device("A");
        var b = Booster();
        b.Apply("A");

        d.ThrowOnVolumeWrite = true;
        b.RestoreAll();
        Assert.Equal(0.4f, MicBoostState.Load(_stateFile).Devices.Single().Volume);
        Assert.True(d.Mute); // the rest went back

        d.ThrowOnVolumeWrite = false;
        MicBooster.RecoverFromCrash(_provider, _root, _ => false, _log.Add);
        AssertOriginal(d);
        Assert.False(File.Exists(_stateFile));
    }

    [Fact]
    public void HungDriver_DoesNotHoldTheCaller_AndALateBoostNeverRunsAfterTheRestore()
    {
        var a = Device("A");
        var c = Device("B", 0.6f, false);
        using var release = new ManualResetEventSlim(false);
        _provider.OpenGate = release;
        var b = Booster(TimeSpan.FromMilliseconds(50));

        var watch = Stopwatch.StartNew();
        b.Apply("A");
        b.Apply("B");
        b.RestoreAll();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"held the caller for {watch.Elapsed}");

        release.Set();
        Assert.True(SpinWait.SpinUntil(() => !File.Exists(_stateFile) && a.Volume == 0.4f, TimeSpan.FromSeconds(5)));
        Thread.Sleep(300);
        AssertOriginal(a);
        Assert.Equal(0, c.VolumeWrites);
    }

    /// <summary>A recorder with one short part file on disk, the shape capture leaves behind.</summary>
    private static SourceRecorder PartRecorder(string dir, Stopwatch clock)
    {
        var rec = new SourceRecorder(SourceKind.Mic, null, dir, clock);
        var file = $"{rec.KindName}.part1.wav";
        using (var w = new WaveFileWriter(Path.Combine(dir, file), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
        {
            var buf = new float[48000 * 2];
            for (var i = 0; i < buf.Length; i++) buf[i] = (float)(0.1 * Math.Sin(i * 0.05));
            w.WriteSamples(buf, 0, buf.Length);
        }
        rec.Segments.Add(new SegmentInfo
        {
            Index = 1,
            Device = "Mic A",
            DeviceId = "A",
            Reason = "start",
            File = file,
            DurationSeconds = 1,
            SampleRate = 48000,
            Channels = 2,
            Encoding = "IeeeFloat 32-bit",
        });
        return rec;
    }

    private sealed class FakeProvider : IMicEndpointProvider
    {
        public Dictionary<string, FakeEndpoint> Devices { get; } = new();
        public bool ThrowOnOpen { get; set; }
        public ManualResetEventSlim? OpenGate { get; set; }

        public IMicEndpoint? Open(string deviceId)
        {
            OpenGate?.Wait();
            if (ThrowOnOpen) throw new InvalidOperationException("no such device");
            return Devices.TryGetValue(deviceId, out var d) && !d.Unplugged ? d : null;
        }
    }

    private sealed class FakeControl : IMicBoostControl
    {
        private float _level;

        public FakeControl(string key, float level, float max)
        {
            Key = key;
            _level = level;
            MaxDb = max;
        }

        public string Key { get; }
        public string Name => $"Boost {Key}";
        public float MaxDb { get; }
        public int Writes { get; private set; }
        public Action? BeforeWrite { get; set; }

        public float LevelDb
        {
            get => _level;
            set
            {
                BeforeWrite?.Invoke();
                Writes++;
                _level = value;
            }
        }
    }

    private sealed class FakeEndpoint : IMicEndpoint
    {
        private float _volume;
        private bool _mute;

        public FakeEndpoint(string id, string name, float volume, bool mute, FakeControl[] boosts)
        {
            Id = id;
            Name = name;
            _volume = volume;
            _mute = mute;
            BoostControls = boosts;
        }

        public string Id { get; }
        public string Name { get; }
        public IReadOnlyList<IMicBoostControl> BoostControls { get; }
        public bool Unplugged { get; set; }
        public bool ThrowOnVolumeWrite { get; set; }
        public int VolumeWrites { get; private set; }
        public int MuteWrites { get; private set; }

        public Action? BeforeWrite
        {
            set
            {
                _before = value;
                foreach (var c in BoostControls.OfType<FakeControl>()) c.BeforeWrite = value;
            }
        }

        private Action? _before;

        public float Volume
        {
            get => _volume;
            set
            {
                _before?.Invoke();
                if (ThrowOnVolumeWrite) throw new InvalidOperationException("volume locked");
                VolumeWrites++;
                _volume = value;
            }
        }

        public bool Mute
        {
            get => _mute;
            set
            {
                _before?.Invoke();
                MuteWrites++;
                _mute = value;
            }
        }

        public void Dispose() { }
    }
}

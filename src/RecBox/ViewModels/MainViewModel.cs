using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RecBox.Core;
using RecBox.Core.Audio;
using RecBox.Core.Session;
using RecBox.Core.Settings;
using RecBox.Core.Transcription;
using RecBox.Services;

namespace RecBox.ViewModels;

public sealed record DeviceOption(string? Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record LanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

public sealed record SessionItem(string Name, string Directory, string Summary);

/// <summary>One line on the Live text screen.</summary>
public sealed record LiveLineItem(string Time, string Label, string Text, bool IsYou, bool IsNote)
{
    public static LiveLineItem From(LiveLine l) =>
        new(l.Stamp, l.IsNote ? "" : $"{l.Speaker}: ", l.Text, !l.IsNote && l.Speaker == "You", l.IsNote);
}

public partial class MainViewModel : ObservableObject
{
    private const string DefaultDeviceName = "Default";

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _tick;
    private readonly UpdateService _updates = new();
    private RecordingSession? _session;
    private RecordingSession? _savingSession;
    private Task<SessionManifest?>? _saving;
    private readonly TranscriptionQueue _transcripts = new();
    private readonly QuietDetector _micQuiet = new();
    private bool _loading;
    private LiveRun _liveRun = new();
    private RecordingSession? _liveSession;
    private LiveTranscriptWindow? _liveWindow;
    private volatile bool _liveTranslateFlag;

    public MainViewModel()
    {
        _loading = true;
        _settings = SettingsStore.Load();
        _micEnabled = _settings.MicEnabled;
        _speakerEnabled = _settings.SpeakerEnabled;
        _outputFolder = _settings.OutputFolder;
        _saveSpeechCopy = _settings.SaveSpeechCopy;
        _saveMixed = _settings.SaveMixed;
        _boostMic = _settings.BoostMicWhileRecording;
        _cleanSpeechCopies = _settings.CleanSpeechCopies;
        _transcribeEnabled = _settings.TranscribeEnabled;
        _translateToEnglish = _settings.Mode == TranscribeMode.TranslateToEnglish;
        _useGpu = _settings.UseGpu;
        _gpuNote = _settings.UseGpu ? null : _settings.GpuDisabledReason;
        _checkUpdatesOnStart = _settings.CheckUpdatesOnStart;
        _selectedModel = WhisperModels.Choice(_settings.WhisperModel);
        _liveEnabled = _settings.LiveTranscribeOn;
        _liveTranslate = _liveTranslateFlag = _settings.LiveTranslate;
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code == _settings.Language) ?? Languages[0];
        RefreshDevices();
        LoadRecent();
        UpdateModelStatus();
        _loading = false;

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _tick.Tick += (_, _) => OnTick();

        _updates.Configure(_settings.UpdateUrl);
        VersionText = $"RecBox {_updates.CurrentVersion}";
        if (_settings.CheckUpdatesOnStart) _ = CheckUpdatesAsync();
    }

    public ObservableCollection<DeviceOption> MicDevices { get; } = new();
    public ObservableCollection<DeviceOption> SpeakerDevices { get; } = new();
    public ObservableCollection<SessionItem> Recent { get; } = new();
    public IReadOnlyList<WhisperModelInfo> Models => WhisperModels.Choices;
    public ObservableCollection<LiveLineItem> LiveLines { get; } = new();

    public IReadOnlyList<LanguageOption> Languages { get; } = new[]
    {
        new LanguageOption("auto", "Detect automatically"),
        new LanguageOption("en", "English"),
        new LanguageOption("vi", "Vietnamese"),
        new LanguageOption("ja", "Japanese"),
        new LanguageOption("ko", "Korean"),
        new LanguageOption("zh", "Chinese"),
        new LanguageOption("fr", "French"),
        new LanguageOption("de", "German"),
        new LanguageOption("es", "Spanish"),
    };

    // ---------- recording state ----------
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _hintText = "Press Space or click to start";
    [ObservableProperty] private string _elapsedText = "00:00:00";
    [ObservableProperty] private float _micLevel;
    [ObservableProperty] private float _speakerLevel;
    [ObservableProperty] private string? _micQuietWarning;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string? _error;

    [ObservableProperty] private bool _micEnabled;
    [ObservableProperty] private bool _speakerEnabled;
    [ObservableProperty] private DeviceOption? _selectedMicDevice;
    [ObservableProperty] private DeviceOption? _selectedSpeakerDevice;
    [ObservableProperty] private string _micDeviceText = "";
    [ObservableProperty] private string _speakerDeviceText = "";

    [ObservableProperty] private string? _lastSessionDir;
    [ObservableProperty] private string? _lastSessionTitle;
    [ObservableProperty] private string? _lastSessionDetail;
    [ObservableProperty] private string? _transcriptText;

    // ---------- settings ----------
    [ObservableProperty] private bool _settingsOpen;
    [ObservableProperty] private string _outputFolder;
    [ObservableProperty] private bool _saveSpeechCopy;
    [ObservableProperty] private bool _saveMixed;
    [ObservableProperty] private bool _boostMic;
    [ObservableProperty] private bool _cleanSpeechCopies;
    [ObservableProperty] private bool _transcribeEnabled;
    [ObservableProperty] private WhisperModelInfo _selectedModel;
    [ObservableProperty] private LanguageOption _selectedLanguage;
    [ObservableProperty] private bool _translateToEnglish;
    [ObservableProperty] private bool _useGpu;
    [ObservableProperty] private bool _checkUpdatesOnStart;
    [ObservableProperty] private string _modelStatus = "";
    [ObservableProperty] private bool _modelMissing;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _versionText = "";
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _updateReady;
    [ObservableProperty] private string? _updateLink;
    [ObservableProperty] private string? _gpuNote;
    [ObservableProperty] private string? _diagnosticsStatus;
    [ObservableProperty] private string? _autoModelText;
    [ObservableProperty] private bool _liveEnabled;
    [ObservableProperty] private bool _liveTranslate;
    [ObservableProperty] private string _liveStatus = "Live text shows here while you record.";
    [ObservableProperty] private string _liveLag = "Not recording";

    public bool CanEditSources => !IsRecording && !IsBusy;
    public string RecordButtonName => IsRecording ? "Stop recording" : "Start recording";

    partial void OnIsRecordingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditSources));
        OnPropertyChanged(nameof(RecordButtonName));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanEditSources));

    partial void OnMicEnabledChanged(bool value) => Persist(s => s.MicEnabled = value);
    partial void OnSpeakerEnabledChanged(bool value) => Persist(s => s.SpeakerEnabled = value);
    partial void OnSelectedMicDeviceChanged(DeviceOption? value)
    {
        if (!_loading) Log.Info($"microphone device chosen: {value?.Name ?? "(none)"} id={value?.Id ?? "default"}");
        Persist(s => s.MicDeviceId = value?.Id);
    }

    partial void OnSelectedSpeakerDeviceChanged(DeviceOption? value)
    {
        if (!_loading) Log.Info($"system audio device chosen: {value?.Name ?? "(none)"} id={value?.Id ?? "default"}");
        Persist(s => s.SpeakerDeviceId = value?.Id);
    }
    partial void OnOutputFolderChanged(string value) => Persist(s => s.OutputFolder = value);
    partial void OnSaveSpeechCopyChanged(bool value) => Persist(s => s.SaveSpeechCopy = value);
    partial void OnSaveMixedChanged(bool value) => Persist(s => s.SaveMixed = value);
    partial void OnBoostMicChanged(bool value) => Persist(s => s.BoostMicWhileRecording = value);
    partial void OnCleanSpeechCopiesChanged(bool value) => Persist(s => s.CleanSpeechCopies = value);
    partial void OnTranslateToEnglishChanged(bool value) =>
        Persist(s => s.Mode = value ? TranscribeMode.TranslateToEnglish : TranscribeMode.Transcribe);
    partial void OnUseGpuChanged(bool value)
    {
        if (!_loading) Log.Info($"use graphics card for transcription: {value}");
        Persist(s =>
        {
            s.UseGpu = value;
            if (value) s.GpuDisabledReason = null;
        });
        if (value) GpuNote = null;
        UpdateModelStatus(); // the Auto pick and its reason depend on the graphics card setting
    }
    partial void OnCheckUpdatesOnStartChanged(bool value) => Persist(s => s.CheckUpdatesOnStart = value);
    partial void OnSelectedLanguageChanged(LanguageOption value) => Persist(s => s.Language = value.Code);

    partial void OnTranscribeEnabledChanged(bool value)
    {
        Persist(s => s.TranscribeEnabled = value);
        // live text follows transcription until the user sets it: refresh the toggle (and start/stop it) now
        if (!_loading && _settings.LiveTranscribe is null && LiveEnabled != _settings.LiveTranscribeOn) FollowLive(_settings.LiveTranscribeOn);
        UpdateModelStatus();
    }

    private bool _liveFollowing;

    /// <summary>Sets LiveEnabled because transcription changed, without pinning LiveTranscribe (it keeps following).</summary>
    private void FollowLive(bool on)
    {
        _liveFollowing = true;
        try
        {
            LiveEnabled = on;
        }
        finally
        {
            _liveFollowing = false;
        }
    }

    partial void OnSelectedModelChanged(WhisperModelInfo value)
    {
        Persist(s => s.WhisperModel = value.Key);
        UpdateModelStatus();
    }

    partial void OnLiveEnabledChanged(bool value)
    {
        if (_loading) return;
        Log.Info($"live text while recording: {value}{(_liveFollowing ? " (follows Transcribe after each recording)" : "")}");
        if (!_liveFollowing) Persist(s => s.LiveTranscribe = value);
        if (value)
        {
            // a transcriber turned off earlier in this recording is already out of the slot, so this starts a new one
            if (IsRecording) LiveStatus = "Live text turned on again.";
            _ = EnsureLiveModelsAsync().ContinueWith(t =>
            {
                if (t.Result && LiveEnabled && _session is { } s && _liveRun.NeedsStart(IsRecording)) StartLive(s);
                else if (!t.Result && IsRecording && LiveEnabled) LiveStatus = "Live text could not start: its model is not downloaded yet. The recording goes on.";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        else if (_liveRun.Current is not null)
        {
            // stop feeding now; the lines so far are saved with the recording at Stop
            DetachTaps();
            _liveRun.TurnOff();
            _liveSession = null;
            LiveStatus = "Live text turned off. The lines so far are saved with the recording.";
            LiveLag = "Off";
        }
    }

    partial void OnLiveTranslateChanged(bool value)
    {
        _liveTranslateFlag = value;
        Persist(s => s.LiveTranslate = value);
    }

    private void Persist(Action<AppSettings> change)
    {
        if (_loading) return;
        change(_settings);
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Log.Error("saving settings failed", ex);
            Error = $"Settings could not be saved: {ex.Message}";
        }
    }

    // ---------- devices ----------
    [RelayCommand]
    public void RefreshDevices()
    {
        if (IsRecording) return;
        var wasLoading = _loading;
        _loading = true;
        Fill(MicDevices, AudioDevices.List(SourceKind.Mic));
        Fill(SpeakerDevices, AudioDevices.List(SourceKind.Speaker));
        SelectedMicDevice = MicDevices.FirstOrDefault(d => d.Id == _settings.MicDeviceId) ?? MicDevices[0];
        SelectedSpeakerDevice = SpeakerDevices.FirstOrDefault(d => d.Id == _settings.SpeakerDeviceId) ?? SpeakerDevices[0];
        _loading = wasLoading;
        MicDeviceText = MicDevices.Count > 1 ? "" : "No microphone found";
        SpeakerDeviceText = SpeakerDevices.Count > 1 ? "" : "No speakers or headphones found";
    }

    private static void Fill(ObservableCollection<DeviceOption> target, IReadOnlyList<AudioDevice> devices)
    {
        target.Clear();
        var def = devices.FirstOrDefault(d => d.IsDefault);
        target.Add(new DeviceOption(null, def is null ? DefaultDeviceName : $"{DefaultDeviceName}: {def.Name}"));
        // the "Default" entry follows Windows when it switches devices, also mid-recording
        foreach (var d in devices) target.Add(new DeviceOption(d.Id, d.Name));
    }

    // ---------- record / stop ----------
    [RelayCommand]
    private async Task ToggleRecordAsync()
    {
        if (IsBusy || SettingsOpen) return;
        Log.Info(IsRecording ? "stop requested" : "record requested");
        if (IsRecording) await StopAsync();
        else Start();
    }

    private void Start()
    {
        Error = null;
        Notice = null;
        try
        {
            _session = RecordingSession.Start(_settings);
        }
        catch (Exception ex)
        {
            Log.Error("start failed", ex);
            Error = ex is InvalidOperationException ? ex.Message : $"Could not start recording: {ex.Message}";
            return;
        }

        _session.Notice += msg => Application.Current?.Dispatcher.BeginInvoke(() => Notice = msg);
        if (_session.Warnings.Count > 0) Notice = string.Join(". ", _session.Warnings);
        _micQuiet.Reset();
        MicQuietWarning = null;
        IsRecording = true;
        StatusText = "Recording";
        HintText = DescribeSources(_session);
        _tick.Start();
        LiveLines.Clear();
        _liveRun = new LiveRun();
        _liveSession = null;
        if (LiveEnabled) StartLive(_session);
        else LiveLag = "Off";
    }

    // ---------- live text ----------
    /// <summary>
    /// Starts live text for this recording: the model picked for live use on this PC, one live worker process,
    /// taps on the recorders that only copy buffers. A missing model is downloaded first (once), then live text
    /// starts if the recording is still running. Never throws: live text failing never touches the recording.
    /// </summary>
    private void StartLive(RecordingSession session)
    {
        try
        {
            var hw = HardwareProbe.Measure();
            var liveSettings = _settings.Clone();
            liveSettings.WhisperModel = WhisperModels.AutoKey;
            var choice = ModelPicker.Plan(liveSettings, hw, TranscriptionUse.Live);
            Log.Info($"live text model choice: {choice.Reason} | {hw.Describe()}");
            if (choice.Refusal is { } refusal)
            {
                LiveStatus = refusal;
                LiveLag = "Off";
                return;
            }
            if (!choice.Model.IsPresent)
            {
                LiveStatus = $"Downloading {choice.Model.Name} for live text (once)...";
                LiveLag = "Waiting for the model";
                _ = EnsureLiveModelsAsync().ContinueWith(t =>
                {
                    if (t.Result && ReferenceEquals(_session, session) && LiveEnabled && _liveRun.NeedsStart(IsRecording)) StartLive(session);
                    else if (!t.Result && ReferenceEquals(_session, session) && IsRecording)
                    {
                        LiveStatus = "Live text could not start: its model is not downloaded yet. The recording goes on.";
                        LiveLag = "Off";
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            var live = new LiveTranscriber(new LiveTranscriberOptions
            {
                Choice = choice,
                Tiny = WhisperModels.Get("tiny"),
                Language = _settings.Language,
                Translate = () => _liveTranslateFlag,
                Clock = () => session.Elapsed,
            });
            live.LineAdded += l => dispatcher?.BeginInvoke(() => LiveLines.Add(LiveLineItem.From(l)));
            live.StatusChanged += s => dispatcher?.BeginInvoke(() =>
            {
                if (ReferenceEquals(_liveRun.Current, live)) LiveStatus = s;
            });
            live.GpuFailed += r => dispatcher?.BeginInvoke(() => TurnOffGpu(r));
            _liveRun.Begin(live);
            if (session.Mic is { } mic) mic.Tap = (b, n, f) => live.Feed(LiveSource.Mic, b, n, f);
            if (session.Speaker is { } spk) spk.Tap = (b, n, f) => live.Feed(LiveSource.Speaker, b, n, f);
            _liveSession = session;
            LiveStatus = live.Status;
            LiveLag = "Live";
            live.Start();
        }
        catch (Exception ex)
        {
            Log.Error("live text could not start", ex);
            LiveStatus = $"Live text could not start: {ex.Message}. The recording goes on.";
            LiveLag = "Off";
        }
    }

    private void DetachTaps()
    {
        if (_liveSession is not { } s) return;
        if (s.Mic is { } mic) mic.Tap = null;
        if (s.Speaker is { } spk) spk.Tap = null;
    }

    /// <summary>Stops live text (grace for the last chunks) and gathers every part of it, or null when it never ran.</summary>
    private Task<LiveRunResult>? EndLive(TimeSpan grace)
    {
        DetachTaps();
        _liveSession = null;
        var result = _liveRun.End(grace);
        if (result is not null) LiveLag = "Stopped";
        return result;
    }

    /// <summary>Writes live-transcript.txt and .json beside the recording and lists them in session.json.</summary>
    private static void SaveLiveFiles(string dir, SessionManifest manifest, LiveRunResult live, bool translate)
    {
        var lines = live.Lines;
        try
        {
            var files = LiveTranscriptStore.Save(dir, manifest.Id, lines, live.ModelKey, live.Runtime, translate, live.Restarts, live.SkippedSeconds);
            lock (manifest)
            {
                manifest.Files.RemoveAll(f => f.Name.StartsWith("live-transcript.", StringComparison.Ordinal));
                manifest.Files.Add(new FileEntry { Name = files[0], Description = "live text written while recording, with time and speaker (You / Others)" });
                manifest.Files.Add(new FileEntry { Name = files[1], Description = "same live text with start/end seconds and source (mic, speaker, note)" });
            }
            TranscriptionService.SaveManifest(dir, manifest);
            Log.Info($"session {manifest.Id}: live text saved ({lines.Count} line(s))");
        }
        catch (Exception ex)
        {
            Log.Error("saving the live text failed", ex);
        }
    }

    private void TurnOffGpu(string reason)
    {
        if (!UseGpu) return;
        // remember: the next transcripts go straight to the processor (Settings shows why; turn it back on there)
        UseGpu = false;
        Persist(s => s.GpuDisabledReason = reason);
        GpuNote = $"Turned off by RecBox: {reason}";
        Log.Warn($"graphics card turned off for transcription: {reason}");
    }

    /// <summary>Downloads the live model picked for this PC and Tiny (the fallback) when missing. True when both are there.</summary>
    private async Task<bool> EnsureLiveModelsAsync()
    {
        var hw = HardwareProbe.Measure();
        var liveSettings = _settings.Clone();
        liveSettings.WhisperModel = WhisperModels.AutoKey;
        var pick = ModelPicker.Pick(hw, TranscriptionUse.Live, liveSettings.UseGpu).Model;
        var need = new[] { pick, WhisperModels.Get("tiny") }.DistinctBy(m => m.Key).Where(m => !m.IsPresent).ToList();
        if (need.Count == 0) return true;
        return await DownloadModelsAsync(need);
    }

    [RelayCommand]
    private void OpenLive()
    {
        if (_liveWindow is { } w)
        {
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            return;
        }
        Log.Info("live text window opened");
        _liveWindow = new LiveTranscriptWindow(this);
        _liveWindow.Closed += (_, _) => _liveWindow = null;
        _liveWindow.Show();
    }

    [RelayCommand]
    private void CopyLive()
    {
        try
        {
            var text = string.Join(Environment.NewLine, LiveLines.Select(l => $"[{l.Time}] {l.Label}{l.Text}"));
            if (text.Length == 0) return;
            Clipboard.SetText(text);
            LiveStatus = $"Copied {LiveLines.Count} line(s).";
        }
        catch (Exception ex)
        {
            LiveStatus = $"Could not copy: {ex.Message}";
        }
    }

    private static string DescribeSources(RecordingSession s)
    {
        var parts = new List<string>();
        if (s.Mic is not null) parts.Add($"Mic: {s.Mic.CurrentDeviceName}");
        if (s.Speaker is not null) parts.Add($"Audio: {s.Speaker.CurrentDeviceName}");
        return string.Join("   ", parts);
    }

    private async Task StopAsync()
    {
        var session = _session;
        if (session is null) return;
        var manifest = await SaveCurrentAsync();
        // Stop ends with the save: the transcript runs in the background (queued behind an older one),
        // so the record button is free again right now
        if (manifest is not null && _settings.TranscribeEnabled) QueueTranscript(session.Directory, manifest);
    }

    private void QueueTranscript(string dir, SessionManifest manifest)
    {
        var settings = _settings.Clone();
        TranscriptText = _transcripts.Count > 0
            ? "Transcript queued: it starts when the one before it is done. You can record again."
            : "Transcribing in the background. You can record again.";
        _ = _transcripts.Enqueue(dir, () => TranscribeAsync(dir, manifest, settings));
    }

    /// <summary>
    /// The save of the current session. Stop, closing the window and restart to update all get this same
    /// Task, and under it the session's own single finalize, so a recording is never finalized twice.
    /// </summary>
    private Task<SessionManifest?> SaveCurrentAsync()
    {
        var session = _session;
        if (session is null) return Task.FromResult<SessionManifest?>(null);
        if (_saving is not null && ReferenceEquals(_savingSession, session)) return _saving;
        _savingSession = session;
        return _saving = SaveAsync(session);
    }

    /// <summary>True while a recording or its save is still running, so closing has to wait for it.</summary>
    public bool HasWorkToFinish => _session is not null || _transcripts.Count > 0;

    /// <summary>Never throws: a failed save is shown in Error and logged.</summary>
    private async Task<SessionManifest?> SaveAsync(RecordingSession session)
    {
        _tick.Stop();
        ElapsedText = session.Elapsed.ToString(@"hh\:mm\:ss");
        IsRecording = false;
        IsBusy = true;
        StatusText = "Saving";
        HintText = "Writing your files";
        MicLevel = 0;
        SpeakerLevel = 0;
        _micQuiet.Reset();
        MicQuietWarning = null;

        // live text stops with the recording; it finishes its last chunks while the files are written
        var live = EndLive(TimeSpan.FromSeconds(3));
        var progress = new Progress<string>(m => HintText = m);
        SessionManifest manifest;
        try
        {
            manifest = await session.StopAndSaveAsync(progress);
        }
        catch (Exception ex)
        {
            Log.Error("saving session failed", ex);
            Error = $"The recording could not be saved completely: {ex.Message}. Raw parts are in {session.Directory}";
            IsBusy = false;
            StatusText = "Ready";
            HintText = "Press Space or click to start";
            _session = null;
            return null;
        }
        _session = null;

        if (live is { } l)
        {
            HintText = "Saving the live text";
            var result = await l;
            var translate = LiveTranslate;
            await Task.Run(() => SaveLiveFiles(session.Directory, manifest, result, translate));
            LiveStatus = $"Saved {result.Lines.Count} live line(s) as live-transcript.txt beside the recording.";
        }

        ShowLast(session.Directory, manifest);
        AddRecent(session.Directory, manifest);
        if (manifest.Warnings.Count > 0) Notice = string.Join(". ", manifest.Warnings);

        StatusText = "Saved";
        HintText = "Press Space or click to record again";
        IsBusy = false;
        return manifest;
    }

    /// <summary>
    /// One queued transcript job, run by the queue on the UI thread. The transcript line belongs to the
    /// last saved session: a job for an older session never writes it, nor any recording state.
    /// </summary>
    private async Task TranscribeAsync(string dir, SessionManifest manifest, AppSettings settings)
    {
        if (LastSessionDir == dir) TranscriptText = "Transcribing in the background. You can record again.";
        var progress = new Progress<string>(m =>
        {
            if (LastSessionDir == dir) TranscriptText = m;
        });
        var info = await Task.Run(() => TranscriptionService.TranscribeSessionAsync(dir, manifest, settings, progress, CancellationToken.None));
        if (info.GpuFailed == true && UseGpu)
            TurnOffGpu(info.RuntimeNote ?? "The graphics card (Vulkan) failed.");
        else if (info.Status == "done" && info.Model is { } model)
        {
            // a graphics card slower than the processor is remembered like one that failed
            var slower = GpuSpeed.Record(_settings, info.Runtime, model, manifest.DurationSeconds * Math.Max(1, manifest.Sources.Count), info.Seconds ?? 0);
            Persist(_ => { });
            if (slower is not null) TurnOffGpu(slower);
        }
        if (LastSessionDir == dir)
        {
            TranscriptText = info.Status switch
            {
                "done" => $"Transcript ready: transcript.md ({info.Seconds:0.#} s on {info.Runtime ?? "CPU"})",
                _ => info.Error ?? "Transcription did not finish.",
            };
            ShowLast(dir, manifest);
        }
        var i = Recent.ToList().FindIndex(r => r.Directory == dir);
        if (i >= 0) Recent[i] = ToItem(dir, manifest);
    }

    private void OnTick()
    {
        var s = _session;
        if (s is null || !IsRecording) return;
        ElapsedText = s.Elapsed.ToString(@"hh\:mm\:ss");
        MicLevel = s.Mic?.Level ?? 0;
        SpeakerLevel = s.Speaker?.Level ?? 0;
        if (_liveRun.Current is { } live) LiveLag = live.Failed ? "Stopped" : LivePolicy.LagText(live.LagSeconds);

        if (s.Mic is null) return;
        var wasQuiet = _micQuiet.IsQuiet;
        var quiet = _micQuiet.Update(MicLevel, s.Elapsed);
        if (quiet == wasQuiet) return;
        Log.Info(quiet ? $"mic level near silence for {QuietDetector.Hold.TotalSeconds:0} s on '{s.Mic.CurrentDeviceName}'" : "mic level back above near silence");
        MicQuietWarning = quiet ? "Mic is very quiet. Check it is the right device and is not muted." : null;
    }

    private void ShowLast(string dir, SessionManifest m)
    {
        LastSessionDir = dir;
        LastSessionTitle = $"Saved {Path.GetFileName(dir)}";
        var files = m.Files.Count(f => f.Name != SessionManifest.FileName);
        LastSessionDetail = $"{TimeSpan.FromSeconds(m.DurationSeconds):hh\\:mm\\:ss}  ·  {files} audio and text files  ·  session.json describes them";
    }

    // ---------- recent ----------
    private void LoadRecent()
    {
        Recent.Clear();
        try
        {
            if (!Directory.Exists(_settings.OutputFolder)) return;
            foreach (var dir in Directory.GetDirectories(_settings.OutputFolder).OrderByDescending(d => d).Take(6))
            {
                var m = SessionManifest.Load(dir);
                if (m is null) continue;
                Recent.Add(ToItem(dir, m));
            }
        }
        catch (Exception ex)
        {
            Log.Error("listing recent recordings failed", ex);
        }
    }

    private static SessionItem ToItem(string dir, SessionManifest m)
    {
        var who = string.Join(" + ", m.Sources.Select(s => s.Kind == "mic" ? "mic" : "system audio"));
        var t = m.Transcript.Status == "done" ? "  ·  transcript" : "";
        return new SessionItem(m.StartedLocal.ToString("ddd d MMM, HH:mm"), dir,
            $"{TimeSpan.FromSeconds(m.DurationSeconds):hh\\:mm\\:ss}  ·  {who}{t}");
    }

    private void AddRecent(string dir, SessionManifest m)
    {
        Recent.Insert(0, ToItem(dir, m));
        while (Recent.Count > 6) Recent.RemoveAt(Recent.Count - 1);
    }

    [RelayCommand]
    private static void OpenFolder(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void CopyPath(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            Clipboard.SetText(dir);
            Notice = "Folder path copied. Paste it into your LLM tool or file picker.";
        }
        catch (Exception ex)
        {
            Error = $"Could not copy: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenOutputFolder() => OpenFolder(OutputFolder);

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Save recordings to", InitialDirectory = OutputFolder };
        if (dlg.ShowDialog() == true)
        {
            OutputFolder = dlg.FolderName;
            LoadRecent();
        }
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        if (!SettingsOpen && IsBusy) return; // Settings (and Restart to update) stay closed while saving
        if (!SettingsOpen) RefreshDevices();
        SettingsOpen = !SettingsOpen;
    }

    [RelayCommand]
    private void DismissMessages()
    {
        Notice = null;
        Error = null;
    }

    // ---------- transcription model ----------
    /// <summary>The model the next transcript uses: the one chosen, or what Auto picks for this PC.</summary>
    private WhisperModelInfo FileModel()
    {
        if (!WhisperModels.IsAuto(SelectedModel.Key)) return SelectedModel;
        try
        {
            var choice = ModelPicker.Plan(_settings, HardwareProbe.Measure(), TranscriptionUse.File, _ => true);
            AutoModelText = choice.Reason;
            return choice.Model;
        }
        catch (Exception ex)
        {
            Log.Error("auto model choice failed", ex);
            return WhisperModels.Get("base");
        }
    }

    private void UpdateModelStatus()
    {
        if (!WhisperModels.IsAuto(SelectedModel.Key)) AutoModelText = null;
        var m = FileModel();
        var prefix = WhisperModels.IsAuto(SelectedModel.Key) ? $"Auto uses {m.Name} on this PC. " : "";
        ModelMissing = !m.IsPresent;
        var status = m.IsPresent ? "Downloaded and ready." : "Not downloaded yet. One download, then it works offline.";
        if (!m.IsPresent && WhisperModels.IsAuto(SelectedModel.Key))
        {
            // Auto never runs a larger model than its pick; a smaller one on disk fills in until the download
            var now = WhisperModels.All.Take(WhisperModels.Rank(m)).LastOrDefault(x => x.IsPresent);
            if (now is not null) status += $" Until then transcripts use {now.Name}.";
        }
        ModelStatus = prefix + status;
    }

    [RelayCommand]
    private async Task DownloadModelAsync() => await DownloadModelsAsync(new[] { FileModel() });

    /// <summary>The one download flow (Settings button, live text): progress bar, error message, status. True when all arrived.</summary>
    private async Task<bool> DownloadModelsAsync(IReadOnlyList<WhisperModelInfo> models)
    {
        if (IsDownloading) return false;
        IsDownloading = true;
        DownloadProgress = 0;
        try
        {
            foreach (var model in models)
            {
                if (model.IsPresent) continue;
                DownloadProgress = 0;
                var progress = new Progress<double>(p => DownloadProgress = p * 100);
                await Task.Run(() => TranscriptionService.DownloadAsync(model, progress, CancellationToken.None));
            }
            return models.All(m => m.IsPresent);
        }
        catch (Exception ex)
        {
            Log.Error("model download failed", ex);
            Error = $"Download failed: {ex.Message}. Check the internet connection and try again.";
            return false;
        }
        finally
        {
            IsDownloading = false;
            UpdateModelStatus();
        }
    }

    // ---------- updates ----------
    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        UpdateStatus = "Checking for updates";
        UpdateStatus = await _updates.CheckAndDownloadAsync();
        UpdateReady = _updates.UpdateReady;
        UpdateLink = _updates.ReleasePageUrl;
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        if (UpdateLink is not { Length: > 0 } url) return;
        Log.Info($"opening release page {url}");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // ---------- help: logs and diagnostics ----------
    [RelayCommand]
    private void OpenLogFolder()
    {
        Log.Info("open log folder");
        OpenFolder(AppPaths.LogsDir);
    }

    /// <summary>Zips logs, crash files, settings and system info (never audio), puts the zip on the clipboard.</summary>
    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        DiagnosticsStatus = "Collecting logs";
        try
        {
            var zip = await Task.Run(() => Diagnostics.CreateZip());
            try
            {
                Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { zip });
                DiagnosticsStatus = $"Copied {Path.GetFileName(zip)}. Paste it into a chat or an email. No recordings are in it.";
            }
            catch (Exception ex)
            {
                Log.Warn($"diagnostics: clipboard busy: {ex.Message}");
                DiagnosticsStatus = $"Saved {zip}. Send that file.";
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("diagnostics zip failed", ex);
            DiagnosticsStatus = $"Could not collect the logs: {ex.Message}. Use Open log folder instead.";
        }
    }

    [RelayCommand]
    private async Task RestartToUpdateAsync()
    {
        if (IsRecording)
        {
            Notice = "Stop the recording first, then restart to update.";
            return;
        }
        if (_session is not null) Notice = "Saving the recording first, then RecBox restarts to update.";
        await PrepareToExitAsync();
        _updates.RestartToApply();
    }

    /// <summary>
    /// Called before the window closes or the app restarts: waits for the save already running (or saves
    /// a recording still in progress) through the same single save, then records in session.json that a
    /// transcript that will not finish now was interrupted.
    /// </summary>
    public async Task PrepareToExitAsync()
    {
        var session = _session;
        if (session is not null)
        {
            Log.Info(IsRecording ? "closing while recording: saving first" : "closing while saving: waiting for the save");
            await SaveCurrentAsync();
        }
        MarkTranscriptsInterrupted(session);
    }

    /// <summary>
    /// The window is closing and the close cannot be held (Windows logoff or restart: Application.Shutdown
    /// ignores Cancel), so the process ends right after this returns. Blocks until the session's single
    /// save is done: the same Task Stop or an earlier close already started, never a second finalize.
    /// The save runs on the thread pool and posts its progress, so blocking the UI thread cannot deadlock it.
    /// </summary>
    public void FinishBeforeExitNow()
    {
        var session = _session;
        if (session is not null)
        {
            _tick.Stop();
            Log.Info("closing and the close cannot wait (shutdown): saving now");
            // live text gets no grace here: the recording's save comes first, the live lines so far are written after it
            var live = EndLive(TimeSpan.Zero);
            try
            {
                var manifest = session.StopAndSaveAsync().GetAwaiter().GetResult();
                if (live is { } l && l.Wait(TimeSpan.FromSeconds(2)))
                    SaveLiveFiles(session.Directory, manifest, l.Result, LiveTranslate);
            }
            catch (Exception ex)
            {
                Log.Error("saving at shutdown failed, part files kept", ex);
            }
        }
        MarkTranscriptsInterrupted(session);
    }

    private void MarkTranscriptsInterrupted(RecordingSession? session)
    {
        // every session still queued or transcribing, and no queued one starts after this
        var dirs = _transcripts.Close().Prepend(session?.Directory).OfType<string>().Distinct();
        foreach (var dir in dirs)
            TranscriptionService.MarkInterrupted(dir, "RecBox was closed before the transcript was finished.");
    }
}

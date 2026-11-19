using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RecBox.Core;
using RecBox.Core.Audio;
using RecBox.Core.Coaching;
using RecBox.Core.Session;
using RecBox.Core.Settings;
using RecBox.Core.Transcription;
using RecBox.Services;
using Lang = RecBox.Core.Transcription.Languages;

namespace RecBox.ViewModels;

/// <summary>
/// A device in a picker. The "Default" entry's name is in the interface language; a language switch relabels the same
/// object (the picker's selection is never replaced).
/// </summary>
public sealed class DeviceOption : ObservableObject
{
    private readonly string _name;

    /// <param name="defaultOf">For the Default entry (Id null): the device Windows uses now.</param>
    public DeviceOption(string? id, string name, string? defaultOf = null)
    {
        Id = id;
        _name = name;
        DefaultOf = defaultOf;
    }

    public string? Id { get; }
    public string? DefaultOf { get; }
    public string Name => Id is not null ? _name : DefaultOf is null ? Loc.T("device_default") : Loc.F("device_default_is", DefaultOf);
    public void Relabel() => OnPropertyChanged(nameof(Name));
    public override string ToString() => Name;
}

/// <summary>A code in a picker (language, coach language, Translate to, interface language), named in the interface language.</summary>
public sealed class LanguageOption : ObservableObject
{
    private readonly Func<string, string> _label;

    public LanguageOption(string code, Func<string, string> label)
    {
        Code = code;
        _label = label;
    }

    public string Code { get; }
    public string Name => _label(Code);
    public void Relabel() => OnPropertyChanged(nameof(Name));
    public override string ToString() => Name;
}

/// <summary>A coaching style in the picker, named in the interface language (its English name goes in the prompt).</summary>
public sealed class StyleOption : ObservableObject
{
    public StyleOption(CoachStyle style) => Style = style;
    public CoachStyle Style { get; }
    public string Name => Style.Display;
    public void Relabel() => OnPropertyChanged(nameof(Name));
    public override string ToString() => Name;
}

public sealed record SessionItem(string Name, string Directory, string Summary);

/// <summary>One line on the Live text screen; <see cref="Translation"/> arrives later when Translate to uses the AI.</summary>
public sealed class LiveLineItem : ObservableObject
{
    private string? _translation;

    public LiveLineItem(string time, string label, string text, bool isYou, bool isNote, string key)
    {
        Time = time;
        Label = label;
        Text = text;
        IsYou = isYou;
        IsNote = isNote;
        Key = key;
    }

    public string Time { get; }
    public string Label { get; }
    public string Text { get; }
    public bool IsYou { get; }
    public bool IsNote { get; }
    /// <summary>The live line's key (<see cref="LiveTranslator.Key"/>), to match its translation.</summary>
    public string Key { get; }

    public string? Translation
    {
        get => _translation;
        set => SetProperty(ref _translation, value);
    }

    /// <param name="you">The user's name from the meeting brief (null: "You").</param>
    /// <param name="others">The other side's names from the brief (null: "Others").</param>
    /// <param name="fix">The brief's name spelling fix (the kit's fix_names), for speech lines.</param>
    public static LiveLineItem From(LiveLine l, string? you = null, string? others = null, Func<string, string>? fix = null)
    {
        var who = l.Speaker == "You" ? you ?? Loc.T("who_you") : l.Speaker == "Others" ? others ?? Loc.T("who_others") : l.Speaker;
        var text = l.IsNote || fix is null ? l.Text : fix(l.Text);
        return new(l.Stamp, l.IsNote ? "" : $"{who}{(l.Echo ? " " + Loc.T("who_echo") : "")}: ", text, !l.IsNote && l.Speaker == "You", l.IsNote,
            LiveTranslator.Key(l.Start, l.Speaker, l.Text));
    }
}

/// <summary>An AI tool in the picker: "Claude Code on this PC", or "Codex on this PC (not installed)".</summary>
public sealed class CoachProviderOption : ObservableObject
{
    public CoachProviderOption(CoachProviderKind kind)
    {
        Kind = kind;
        Installed = CoachProviders.IsInstalled(kind);
    }

    public CoachProviderKind Kind { get; }
    /// <summary>Checked when made and at each <see cref="Relabel"/> (a tool installed while RecBox runs shows up then).</summary>
    public bool Installed { get; private set; }
    public string Name => Loc.T("ai_option_" + Kind) + (Installed ? "" : " " + Loc.T("ai_not_installed"));

    public void Relabel()
    {
        Installed = CoachProviders.IsInstalled(Kind);
        OnPropertyChanged(nameof(Installed));
        OnPropertyChanged(nameof(Name));
    }

    public override string ToString() => Name;
}

public partial class MainViewModel : ObservableObject
{
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
    private volatile bool _liveTranslateFlag;
    private CancellationTokenSource? _transcriptCts;
    private string? _liveDetected;

    public MainViewModel()
    {
        _loading = true;
        _settings = SettingsStore.Load();
        // the interface language first: every text below is made in it
        Loc.Instance.Set(Loc.Resolve(_settings.UiLanguage));
        Loc.Instance.Changed += OnUiLanguageSwitched;
        _selectedUiLanguage = UiLanguageOptions.First(o => o.Code == (_settings.UiLanguage ?? ""));
        _translate = new TranslateChoice(_settings.TranslateTo);
        _selectedTranslate = TranslateOptions.First(o => o.Code == _translate.Current);
        _defaultTranslate = TranslateOptions.First(o => o.Code == _translate.Default);
        _liveTranslateFlag = _translate.WhisperTranslates;
        _micEnabled = _settings.MicEnabled;
        _speakerEnabled = _settings.SpeakerEnabled;
        _outputFolder = _settings.OutputFolder;
        _saveSpeechCopy = _settings.SaveSpeechCopy;
        _saveMixed = _settings.SaveMixed;
        _boostMic = _settings.BoostMicWhileRecording;
        _cleanSpeechCopies = _settings.CleanSpeechCopies;
        _transcribeEnabled = _settings.TranscribeEnabled;
        _useGpu = _settings.UseGpu;
        _gpuNote = _settings.UseGpu ? null : _settings.GpuDisabledReason;
        _checkUpdatesOnStart = _settings.CheckUpdatesOnStart;
        _selectedModel = WhisperModels.Choice(_settings.WhisperModel);
        _liveEnabled = _settings.LiveTranscribeOn;
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code == _settings.Language) ?? Languages[0];
        _livePanelOpen = _settings.LivePanelOpen;
        _livePanelWidth = Math.Clamp(_settings.LivePanelWidth, 300, 900);
        var coach = _settings.Coach ??= new CoachSettings();
        coach.Migrate();
        _coachEnabled = coach.Enabled;
        _coachProvider = CoachProviderOptions.FirstOrDefault(p => p.Kind == coach.Provider) ?? CoachProviderOptions[0];
        _coachModelText = coach.ModelFor(_coachProvider.Kind);
        _coachEndpoint = coach.Endpoint;
        _coachEndpointModel = coach.EndpointModel;
        _coachGoal = coach.Goal;
        _coachBrief = coach.Brief;
        _coachRole = coach.Role;
        _coachStyle = CoachStyleOptions.FirstOrDefault(o => o.Style.Key == coach.Style) ?? CoachStyleOptions[0];
        _coachInstructions = coach.Instructions;
        _coachLanguage = CoachLanguageOptions.FirstOrDefault(l => l.Code == coach.OutputLanguage) ?? CoachLanguageOptions[0];
        _captureSlides = coach.CaptureSlides;
        _slideWindow = coach.SlideWindow;
        SetIdleTexts();
        Say(nameof(CoachStatus), () => Loc.T("coach_shows_here"));
        UpdateCoachTexts();
        RefreshDevices();
        LoadRecent();
        UpdateModelStatus();
        UpdateEngineText();
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
    /// <summary>The Settings model list. Its texts follow the interface language through <see cref="ModelTextConverter"/>.</summary>
    public IReadOnlyList<WhisperModelInfo> Models => WhisperModels.Choices;
    public ObservableCollection<LiveLineItem> LiveLines { get; } = new();

    /// <summary>The language picker on the main screen and the live panel: Auto (detect once, then keep), each language, Vietnamese + English.</summary>
    public IReadOnlyList<LanguageOption> Languages { get; } =
        RecBox.Core.Transcription.Languages.Choices.Select(c => new LanguageOption(c.Code, Lang.ChoiceDisplay)).ToArray();

    // ---------- interface language ----------
    /// <summary>Settings: Follow Windows (""), English, Tiếng Việt.</summary>
    public IReadOnlyList<LanguageOption> UiLanguageOptions { get; } = new[] { "", "en", "vi" }
        .Select(c => new LanguageOption(c, code => code switch { "en" => Loc.T("ui_lang_en"), "vi" => Loc.T("ui_lang_vi"), _ => Loc.T("ui_follow_windows") }))
        .ToArray();

    [ObservableProperty] private LanguageOption _selectedUiLanguage;

    /// <summary>True while the interface is in Vietnamese (the header switch marks the language in use).</summary>
    public bool IsVietnamese => Loc.Instance.Language == "vi";

    partial void OnSelectedUiLanguageChanged(LanguageOption value)
    {
        if (_loading) return;
        var saved = value.Code.Length == 0 ? null : value.Code;
        Persist(s => s.UiLanguage = saved);
        Log.Info($"interface language: {(saved ?? "follow Windows")} -> {Loc.Resolve(saved)}");
        Loc.Instance.Set(Loc.Resolve(saved));
    }

    /// <summary>The header switch "English | Tiếng Việt": one click to the other language, saved.</summary>
    [RelayCommand]
    private void SwitchUiLanguage(string? code)
    {
        var to = code is "en" or "vi" ? code : IsVietnamese ? "en" : "vi";
        SelectedUiLanguage = UiLanguageOptions.First(o => o.Code == to);
    }

    /// <summary>
    /// The interface language changed: every text on screen follows at once. Bound texts follow Loc by themselves; the
    /// texts made here are made again from what they say (<see cref="Say"/>), and the picker entries relabel in place, so
    /// no selection is lost and nothing is rebuilt.
    /// </summary>
    private void OnUiLanguageSwitched()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            foreach (var o in Languages.Concat(UiLanguageOptions).Concat(CoachLanguageOptions).Concat(TranslateOptions)) o.Relabel();
            foreach (var o in CoachStyleOptions) o.Relabel();
            foreach (var o in CoachProviderOptions) o.Relabel();
            foreach (var d in MicDevices.Concat(SpeakerDevices)) d.Relabel();
            RefreshTexts();
            OnPropertyChanged(nameof(IsVietnamese));
            OnPropertyChanged(nameof(RecordButtonName));
            OnPropertyChanged(nameof(CoachModelHint));
            UpdateModelStatus();
            UpdateEngineText();
            UpdateCoachTexts();
            if (_lastSnap is { } snap) CoachUsage = snap.UsageText;
            UpdateTranslateTexts();
            LoadRecent();
        }
        catch (Exception ex)
        {
            Log.Error("interface language switch", ex);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    /// <summary>The window closed (or a screenshot is done): this view model no longer follows language switches.</summary>
    public void Detach() => Loc.Instance.Changed -= OnUiLanguageSwitched;

    /// <summary>The texts this view model made, by property: made again in the new language on a switch.</summary>
    private readonly Dictionary<string, Func<string?>> _texts = new();
    private bool _saying;

    /// <summary>Sets a text property and remembers how it was made, so a language switch can make it again.</summary>
    private void Say(string property, Func<string?> text)
    {
        var p = GetType().GetProperty(property) ?? throw new ArgumentException(property);
        _texts[property] = text;
        _saying = true;
        try { p.SetValue(this, text()); }
        finally { _saying = false; }
    }

    private void RefreshTexts()
    {
        foreach (var (name, text) in _texts.ToList())
        {
            _saying = true;
            try { GetType().GetProperty(name)!.SetValue(this, text()); }
            catch (Exception ex) { Log.Error($"text {name}", ex); }
            finally { _saying = false; }
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        // a text set straight (a status from live text or the coach) is no longer made by its old recipe
        if (!_saying && e.PropertyName is { } n && _texts.ContainsKey(n)) _texts.Remove(n);
        base.OnPropertyChanged(e);
    }

    private void SetIdleTexts()
    {
        Say(nameof(StatusText), () => Loc.T("status_ready"));
        Say(nameof(HintText), () => Loc.T("hint_start"));
        Say(nameof(LiveStatus), () => Loc.T("live_shows_here"));
        Say(nameof(LiveLag), () => Loc.T("lag_not_recording"));
    }

    // ---------- recording state ----------
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _hintText = "";
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
    [ObservableProperty] private string _liveStatus = "";
    [ObservableProperty] private string _liveLag = "";

    // ---------- this recording: the choices next to Record, and what Auto picked ----------
    [ObservableProperty] private bool _livePanelOpen;
    [ObservableProperty] private double _livePanelWidth;
    /// <summary>What the language picker means now: "Detects the language once, then keeps it", "Vietnamese (detected)".</summary>
    [ObservableProperty] private string _languagePickedText = "";
    /// <summary>The model and device the transcript after Stop runs with, in plain words.</summary>
    [ObservableProperty] private string _engineText = "";
    /// <summary>The model and device live text runs with, in plain words.</summary>
    [ObservableProperty] private string _liveEngineText = "";
    [ObservableProperty] private bool _isTranscribing;
    [ObservableProperty] private string? _transcribeProgress;

    public bool CanEditSources => !IsRecording && !IsBusy;

    partial void OnLivePanelOpenChanged(bool value)
    {
        if (!_loading) Log.Info($"live text panel {(value ? "opened" : "closed")}");
        Persist(s => s.LivePanelOpen = value);
    }

    partial void OnLivePanelWidthChanged(double value) => Persist(s => s.LivePanelWidth = Math.Round(value));
    public string RecordButtonName => IsRecording ? Loc.T("record_stop") : Loc.T("record_start");

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
    partial void OnSelectedLanguageChanged(LanguageOption value)
    {
        Persist(s => s.Language = value.Code);
        if (_loading) return;
        Log.Info($"language chosen: {value.Code}{(IsRecording ? " (during the recording: live text uses it from the next piece of speech)" : "")}");
        _liveDetected = null;
        if (_liveRun.Current is { } live)
        {
            live.SetLanguage(value.Code);
            _coach?.SetLanguages(CoachLanguages.ForGate(value.Code, live.Language.Detected));
        }
        else UpdateLanguagePicked(null);
        UpdateModelStatus();
        UpdateEngineText();
    }

    /// <summary>What the language picker shows under it: what was chosen, and what Auto detected.</summary>
    private void UpdateLanguagePicked(string? liveDescription)
    {
        var code = SelectedLanguage.Code;
        if (liveDescription is not null) LanguagePickedText = Lang.IsAuto(code) ? Loc.F("picked_live", liveDescription) : Loc.F("using_live", liveDescription);
        else if (Lang.IsAuto(code))
            LanguagePickedText = _liveDetected is { } d ? Loc.F("last_detected", Lang.Display(d)) : Loc.T("auto_listens");
        else if (Lang.IsAnyOf(code)) LanguagePickedText = Loc.F("anyof_text", Lang.Display(code));
        else if (Lang.IsMixed(code))
        {
            var (primary, extra) = Lang.Split(code);
            LanguagePickedText = Loc.F("mixed_text", Lang.Display(primary), string.Join($" {Loc.T("word_or")} ", extra.Select(Lang.Display)));
        }
        else LanguagePickedText = Loc.F("single_text", Lang.Display(code));
    }

    /// <summary>The engine lines: which model on which device, for the transcript and for live text, in plain words.</summary>
    private void UpdateEngineText()
    {
        try
        {
            var hw = HardwareProbe.Measure();
            var file = ModelPicker.Plan(_settings, hw, TranscriptionUse.File, _ => true);
            EngineText = Loc.F("engine_on", file.Model.Name, Where(file.Gpu, hw));
            var liveSettings = _settings.Clone();
            liveSettings.WhisperModel = WhisperModels.AutoKey;
            var live = ModelPicker.Plan(liveSettings, hw, TranscriptionUse.Live, _ => true);
            LiveEngineText = _liveRun.Current is { } running
                ? Loc.F("engine_on", WhisperModels.Get(running.ModelKey).Name, DeviceName.Where(running.Device, running.OnGpu))
                : Loc.F("engine_on", live.Model.Name, Where(live.Gpu, hw));
        }
        catch (Exception ex)
        {
            Log.Error("engine text failed", ex);
        }
        UpdateLanguagePicked(_liveRun.Current?.Language.Describe());
    }

    private static string Where(bool gpu, HardwareProfile hw) =>
        gpu && hw.Gpu is { } g ? Loc.F("where_named", g.Name) : Loc.F("where_cpu_cores", hw.PhysicalCores);

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
            if (IsRecording) Say(nameof(LiveStatus), () => Loc.T("live_on_again"));
            _ = EnsureLiveModelsAsync().ContinueWith(t =>
            {
                if (t.Result && LiveEnabled && _session is { } s && _liveRun.NeedsStart(IsRecording)) StartLive(s);
                else if (!t.Result && IsRecording && LiveEnabled) Say(nameof(LiveStatus), () => Loc.T("live_no_model"));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        else if (_liveRun.Current is not null)
        {
            // stop feeding now; the lines so far are saved with the recording at Stop
            DetachTaps();
            _liveRun.TurnOff();
            _liveSession = null;
            Say(nameof(LiveStatus), () => Loc.T("live_turned_off"));
            Say(nameof(LiveLag), () => Loc.T("lag_off"));
        }
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
            Say(nameof(Error), () => Loc.F("settings_not_saved", ex.Message));
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
        Say(nameof(MicDeviceText), () => MicDevices.Count > 1 ? "" : Loc.T("no_mic_found"));
        Say(nameof(SpeakerDeviceText), () => SpeakerDevices.Count > 1 ? "" : Loc.T("no_speaker_found"));
    }

    private static void Fill(ObservableCollection<DeviceOption> target, IReadOnlyList<AudioDevice> devices)
    {
        target.Clear();
        var def = devices.FirstOrDefault(d => d.IsDefault);
        target.Add(new DeviceOption(null, "", def?.Name));
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
            if (ex is InvalidOperationException) Error = ex.Message;
            else Say(nameof(Error), () => Loc.F("start_failed", ex.Message));
            return;
        }

        var started = _session;
        _session.Notice += msg =>
        {
            // the kit's [SYS] line: in the live text (saved with it) and to the coach, which never wakes on it
            try { _liveRun.Current?.AddNote(started.Elapsed, $"({msg})"); } catch (Exception ex) { Log.Error("live note", ex); }
            Application.Current?.Dispatcher.BeginInvoke(() => Notice = msg);
        };
        if (_session.Warnings.Count > 0) Notice = string.Join(". ", _session.Warnings);
        ClearCoachView();
        if (CaptureSlides && !string.IsNullOrWhiteSpace(SlideWindow))
        {
            try
            {
                _slides = new SlideCapture(started.Directory, SlideWindow, () => started.Elapsed);
                _slides.Note += m => { try { _liveRun.Current?.AddNote(started.Elapsed, $"({m})"); } catch { } };
                Log.Info($"slides: capturing the window titled *{SlideWindow}* when it changes");
            }
            catch (Exception ex)
            {
                Log.Error("slide capture could not start", ex);
            }
        }
        _micQuiet.Reset();
        MicQuietWarning = null;
        IsRecording = true;
        Say(nameof(StatusText), () => Loc.T("status_recording"));
        Say(nameof(HintText), () => DescribeSources(started));
        _tick.Start();
        LiveLines.Clear();
        _liveRun = new LiveRun();
        _liveSession = null;
        if (LiveEnabled) StartLive(_session);
        else Say(nameof(LiveLag), () => Loc.T("lag_off"));
        if (CoachEnabled && !LiveEnabled) Say(nameof(CoachStatus), () => Loc.T("coach_needs_live"));
        UpdateTranslateTexts();
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
                Say(nameof(LiveLag), () => Loc.T("lag_off"));
                return;
            }
            if (!choice.Model.IsPresent)
            {
                Say(nameof(LiveStatus), () => Loc.F("live_downloading", choice.Model.Name));
                Say(nameof(LiveLag), () => Loc.T("lag_waiting_model"));
                _ = EnsureLiveModelsAsync().ContinueWith(t =>
                {
                    if (t.Result && ReferenceEquals(_session, session) && LiveEnabled && _liveRun.NeedsStart(IsRecording)) StartLive(session);
                    else if (!t.Result && ReferenceEquals(_session, session) && IsRecording)
                    {
                        Say(nameof(LiveStatus), () => Loc.T("live_no_model"));
                        Say(nameof(LiveLag), () => Loc.T("lag_off"));
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            VadService? vad = null;
            if (SileroModel.IsPresent) vad = new VadService(SileroModel.Path);
            else
            {
                Log.Warn("live text: no Silero model, speech is cut by loudness");
                _ = EnsureSileroAsync(); // for the next recording
            }
            var live = new LiveTranscriber(new LiveTranscriberOptions
            {
                Choice = choice,
                Language = SelectedLanguage.Code,
                Vad = vad,
                Translate = () => _liveTranslateFlag,
                Clock = () => session.Elapsed,
            });
            live.LineAdded += l =>
            {
                // the coach and the translator only queue the line (never wait); failing ones never touch live text
                try { _coach?.Post(new CoachLine(l.Start, l.Speaker, l.IsNote ? l.Text : FixNames(l.Text), l.Echo, l.IsNote)); }
                catch (Exception ex) { Log.Error("coach post", ex); }
                if (!l.IsNote)
                {
                    try { _translator?.Post(LiveTranslator.Key(l.Start, l.Speaker, l.Text), FixNames(l.Text), l.Language); }
                    catch (Exception ex) { Log.Error("translation post", ex); }
                }
                dispatcher?.BeginInvoke(() =>
                {
                    var item = LiveLineItem.From(l, _youName, _othersName, FixNames);
                    item.Translation = _translator?.Get(item.Key);
                    LiveLines.Add(item);
                });
            };
            live.StatusChanged += s => dispatcher?.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_liveRun.Current, live)) return;
                LiveStatus = s;
                LiveEngineText = Loc.F("engine_on", WhisperModels.Get(live.ModelKey).Name, DeviceName.Where(live.Device, live.OnGpu));
            });
            live.LanguageChanged += d => dispatcher?.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_liveRun.Current, live)) return;
                _liveDetected = live.Language.Detected ?? live.Language.Leading;
                UpdateLanguagePicked(d);
                // Auto: once live text locks a language the coach's junk filter takes it plus English (the kit's set before)
                _coach?.SetLanguages(CoachLanguages.ForGate(SelectedLanguage.Code, live.Language.Detected));
            });
            live.GpuFailed += r => dispatcher?.BeginInvoke(() => TurnOffGpu(r));
            UpdateLanguagePicked(live.Language.Describe());
            _liveRun.Begin(live);
            if (session.Mic is { } mic) mic.Tap = (b, n, f) => live.Feed(LiveSource.Mic, b, n, f);
            if (session.Speaker is { } spk) spk.Tap = (b, n, f) => live.Feed(LiveSource.Speaker, b, n, f);
            _liveSession = session;
            LiveStatus = live.Status;
            LiveLag = LivePolicy.LagText(0);
            live.Start();
            if (CoachEnabled) StartCoach(session);
            StartTranslator();
        }
        catch (Exception ex)
        {
            Log.Error("live text could not start", ex);
            Say(nameof(LiveStatus), () => Loc.F("live_start_failed", ex.Message));
            Say(nameof(LiveLag), () => Loc.T("lag_off"));
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
        if (result is not null) Say(nameof(LiveLag), () => Loc.T("lag_stopped"));
        return result;
    }

    /// <summary>
    /// The brief's names at Stop (Me: / Them:, name spellings) go into session.json, so live-transcript.txt / .json,
    /// transcript.md / .srt and a later Transcribe again use them like the live panel does.
    /// </summary>
    private void SetSpeakerNames(SessionManifest manifest)
    {
        var names = SpeakerNames.From(CoachGoal, CoachBrief);
        lock (manifest) manifest.Speakers = names;
        if (names is not null) Log.Info($"session {manifest.Id}: speaker names from the brief ({(names.You is null ? "" : "you, ")}{(names.Others is null ? "" : "others, ")}{names.Names.Count} name spelling(s))");
    }

    /// <summary>Writes live-transcript.txt and .json beside the recording and lists them in session.json.</summary>
    private static void SaveLiveFiles(string dir, SessionManifest manifest, LiveRunResult live, bool translate,
        IReadOnlyDictionary<string, string>? translations = null, string? translateTo = null)
    {
        var lines = live.Lines;
        try
        {
            SpeakerNames? names;
            lock (manifest) names = manifest.Speakers;
            var files = LiveTranscriptStore.Save(dir, manifest.Id, live, translate, names, translations, translateTo);
            var translated = translations is { Count: > 0 } && TranslateTargets.NeedsAi(translateTo);
            lock (manifest)
            {
                manifest.Files.RemoveAll(f => f.Name.StartsWith("live-transcript.", StringComparison.Ordinal));
                manifest.Files.Add(new FileEntry { Name = files[0], Description = "live text written while recording, with time and speaker (You / Others)" + (translated ? $", each line's {TranslateTargets.EnglishName(translateTo!)} translation under it" : "") });
                manifest.Files.Add(new FileEntry { Name = files[1], Description = "same live text with start/end/shown seconds, source (mic, speaker, note), echo tag and language" + (translated ? ", plus each line's translation" : "") });
                manifest.Language ??= new LanguageInfo();
                manifest.Language.Detected = live.LanguageDetected;
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
        Say(nameof(GpuNote), () => Loc.F("gpu_turned_off", reason));
        Log.Warn($"graphics card turned off for transcription: {reason}");
    }

    /// <summary>
    /// Downloads, when missing, the live model picked for this PC, the transcript's Auto pick when Transcribe is on,
    /// and the Silero speech detector (2 MB). True when the live model is there (Silero missing only means loudness cuts).
    /// </summary>
    private async Task<bool> EnsureLiveModelsAsync()
    {
        var hw = HardwareProbe.Measure();
        var liveSettings = _settings.Clone();
        liveSettings.WhisperModel = WhisperModels.AutoKey;
        var pick = ModelPicker.Pick(hw, TranscriptionUse.Live, liveSettings.UseGpu, SelectedLanguage.Code).Model;
        var list = new List<WhisperModelInfo> { pick };
        if (TranscribeEnabled && WhisperModels.IsAuto(_settings.WhisperModel))
            list.Add(ModelPicker.Pick(hw, TranscriptionUse.File, _settings.UseGpu, SelectedLanguage.Code).Model);
        await EnsureSileroAsync();
        var need = list.DistinctBy(m => m.Key).Where(m => !m.IsPresent).ToList();
        if (need.Count == 0) return true;
        await DownloadModelsAsync(need);
        return pick.IsPresent;
    }

    private static async Task EnsureSileroAsync()
    {
        if (SileroModel.IsPresent) return;
        try
        {
            await Task.Run(() => SileroModel.DownloadAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            Log.Warn($"Silero download failed: {ex.Message} (speech is found by loudness until it is there)");
        }
    }

    /// <summary>The Live text button: opens or closes the side panel of the main window (never a new window).</summary>
    [RelayCommand]
    private void OpenLive() => LivePanelOpen = !LivePanelOpen;

    /// <summary>Stops the transcript running now; the recording is kept, the transcript can be made again later.</summary>
    [RelayCommand]
    private void CancelTranscript()
    {
        if (_transcriptCts is not { } cts) return;
        Log.Info("transcript cancelled by the user");
        Say(nameof(TranscribeProgress), () => Loc.T("tx_cancelling"));
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    [RelayCommand]
    private void CopyLive()
    {
        try
        {
            var text = string.Join(Environment.NewLine, LiveLines.Select(l =>
                $"[{l.Time}] {l.Label}{l.Text}" + (l.Translation is { } t ? $"{Environment.NewLine}    [{_translatingTo}] {t}" : "")));
            if (text.Length == 0) return;
            Clipboard.SetText(text);
            var n = LiveLines.Count;
            Say(nameof(LiveStatus), () => Loc.F("copied_lines", n));
        }
        catch (Exception ex)
        {
            Say(nameof(LiveStatus), () => Loc.F("copy_failed", ex.Message));
        }
    }

    private static string DescribeSources(RecordingSession s)
    {
        var parts = new List<string>();
        if (s.Mic is not null) parts.Add(Loc.F("src_mic_short", s.Mic.CurrentDeviceName));
        if (s.Speaker is not null) parts.Add(Loc.F("src_audio_short", s.Speaker.CurrentDeviceName));
        return string.Join("   ", parts);
    }

    private async Task StopAsync()
    {
        var session = _session;
        if (session is null) return;
        var translateTo = _translate.Current;
        var manifest = await SaveCurrentAsync();
        // Stop ends with the save: the transcript runs in the background (queued behind an older one),
        // so the record button is free again right now
        if (manifest is not null && _settings.TranscribeEnabled) QueueTranscript(session.Directory, manifest, manifest.Language?.Used, translateTo);
    }

    /// <param name="language">The language chosen at Stop (or what live text detected); null: the setting.</param>
    /// <param name="translateTo">This recording's Translate to (null: the one picked now).</param>
    private void QueueTranscript(string dir, SessionManifest manifest, string? language = null, string? translateTo = null)
    {
        var settings = _settings.Clone();
        settings.TranslateTo = translateTo ?? _translate.Current;
        var queued = _transcripts.Count > 0;
        Say(nameof(TranscriptText), () => queued ? Loc.T("tx_queued") : Loc.T("tx_background"));
        _ = _transcripts.Enqueue(dir, () => TranscribeAsync(dir, manifest, settings, language));
    }

    /// <summary>The language of a recording at Stop: the picker, or (Auto) what live text detected, saved in session.json.</summary>
    private LanguageInfo LanguageAtStop(LiveRunResult? live)
    {
        var chosen = SelectedLanguage.Code;
        var detected = live?.LanguageDetected;
        return new LanguageInfo
        {
            Chosen = chosen,
            Detected = detected,
            Used = Lang.IsAuto(chosen) ? detected ?? Lang.Auto : chosen,
        };
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
        Say(nameof(StatusText), () => Loc.T("status_saving"));
        Say(nameof(HintText), () => Loc.T("hint_writing"));
        MicLevel = 0;
        SpeakerLevel = 0;
        _micQuiet.Reset();
        MicQuietWarning = null;

        // live text stops with the recording; it finishes its last chunks while the files are written
        var live = EndLive(TimeSpan.FromSeconds(3));
        var translator = _translator;
        _translator = null;
        var translateTo = _translate.Current;
        var whisperTranslates = _translate.WhisperTranslates;
        var progress = new Progress<string>(m => HintText = m);
        SessionManifest? manifest = null;
        var coachFinished = false;
        try
        {
            try
            {
                manifest = await session.StopAndSaveAsync(progress);
            }
            catch (Exception ex)
            {
                Log.Error("saving session failed", ex);
                var dir = session.Directory;
                Say(nameof(Error), () => Loc.F("save_failed", ex.Message, dir));
                IsBusy = false;
                Say(nameof(StatusText), () => Loc.T("status_ready"));
                Say(nameof(HintText), () => Loc.T("hint_start"));
                _session = null;
                return null;
            }
            _session = null;
            SetSpeakerNames(manifest);

            // translation of live lines ends with live text: lines still waiting get a short grace, a call in flight is killed
            IReadOnlyDictionary<string, string>? translations = null;
            if (translator is not null)
            {
                await translator.StopAsync(TimeSpan.FromSeconds(3));
                translations = translator.Results;
                RecordTranslation(session.Directory, manifest, translator, null);
            }

            LiveRunResult? liveResult = null;
            if (live is { } l)
            {
                Say(nameof(HintText), () => Loc.T("hint_saving_live"));
                liveResult = await l;
                lock (manifest) manifest.Language = LanguageAtStop(liveResult);
                var r = liveResult;
                var m = manifest;
                await Task.Run(() => SaveLiveFiles(session.Directory, m, r, whisperTranslates, translations, translateTo));
                var n = liveResult.Lines.Count;
                Say(nameof(LiveStatus), () => Loc.F("live_saved", n));
                if (liveResult.LanguageDetected is { } d) _liveDetected = d;
            }
            else
            {
                manifest.Language = LanguageAtStop(null);
                TranscriptionService.SaveManifest(session.Directory, manifest);
            }
            if (translations is { Count: > 0 }) _liveTranslations[session.Directory] = translations;
            // the coach's last call may finish in the background (10 s at most); Record is free again now
            coachFinished = true;
            FinishCoach(session.Directory, manifest, TimeSpan.FromSeconds(10));
            UpdateLanguagePicked(null);

            ShowLast(session.Directory, manifest);
            AddRecent(session.Directory, manifest);
            if (manifest.Warnings.Count > 0) Notice = string.Join(". ", manifest.Warnings);

            Say(nameof(StatusText), () => Loc.T("status_saved"));
            Say(nameof(HintText), () => Loc.T("hint_again"));
            IsBusy = false;
            return manifest;
        }
        finally
        {
            // a save that failed (or threw after the save) still ends the coach and the translation: their AI tools are
            // killed and the coach notes so far are written beside the recording's parts
            if (!coachFinished)
            {
                try { FinishCoach(session.Directory, manifest, TimeSpan.Zero); }
                catch (Exception ex) { Log.Error("ending the coach after a failed save", ex); }
            }
            try { translator?.Dispose(); }
            catch (Exception ex) { Log.Error("ending the translation after the save", ex); }
            // this recording's Translate to was a one-time pick: the next one starts from the saved default
            _translate.RecordingEnded();
            SyncTranslatePicker();
        }
    }

    /// <summary>
    /// One queued transcript job, run by the queue on the UI thread. The transcript line belongs to the
    /// last saved session: a job for an older session never writes it, nor any recording state.
    /// </summary>
    private async Task TranscribeAsync(string dir, SessionManifest manifest, AppSettings settings, string? language)
    {
        if (LastSessionDir == dir) Say(nameof(TranscriptText), () => Loc.T("tx_background"));
        using var cts = new CancellationTokenSource();
        _transcriptCts = cts;
        IsTranscribing = true;
        Say(nameof(TranscribeProgress), () => Loc.F("tx_transcribing_dir", Path.GetFileName(dir)));
        var progress = new Progress<string>(m =>
        {
            TranscribeProgress = m;
            if (LastSessionDir == dir) TranscriptText = m;
        });
        TranscriptInfo info;
        try
        {
            info = await Task.Run(() => TranscriptionService.TranscribeSessionAsync(dir, manifest, settings, progress, cts.Token, language: language));
        }
        finally
        {
            _transcriptCts = null;
            IsTranscribing = false;
            TranscribeProgress = null;
        }
        if (info.GpuFailed == true && UseGpu)
            TurnOffGpu(info.RuntimeNote ?? Loc.T("tx_gpu_failed_short"));
        else if (info.Status == "done" && info.Model is { } model)
        {
            // a graphics card slower than the processor is remembered like one that failed
            var slower = GpuSpeed.Record(_settings, info.Runtime, model, manifest.DurationSeconds * Math.Max(1, manifest.Sources.Count), info.Seconds ?? 0);
            Persist(_ => { });
            if (slower is not null) TurnOffGpu(slower);
        }
        if (LastSessionDir == dir)
        {
            if (info.Status == "done")
                Say(nameof(TranscriptText), () => Loc.F("tx_ready", (info.Seconds ?? 0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                    DeviceName.Where(info.Device, info.Runtime == "Vulkan"), Lang.Display(info.Language)));
            else if (info.Error is { } err) TranscriptText = err;
            else Say(nameof(TranscriptText), () => Loc.T("tx_not_finished"));
            ShowLast(dir, manifest);
        }
        var i = Recent.ToList().FindIndex(r => r.Directory == dir);
        if (i >= 0) Recent[i] = ToItem(dir, manifest);
        // Translate to another language than English: the transcript goes through the AI too, beside the original
        if (info.Status == "done" && TranslateTargets.NeedsAi(settings.TranslateTo))
            await TranslateTranscriptAsync(dir, manifest, settings.TranslateTo, settings.Coach ?? new CoachSettings());
    }

    /// <summary>
    /// transcript.&lt;lang&gt;.md by the picked AI, after the transcript (still in the transcript queue, so one at a time;
    /// nothing waits on it). Lines live text already translated are reused. A failure is said, never thrown.
    /// </summary>
    private async Task TranslateTranscriptAsync(string dir, SessionManifest manifest, string to, CoachSettings coach)
    {
        ICoachProvider provider;
        try
        {
            provider = _makeProvider(coach);
        }
        catch (CoachProviderException ex)
        {
            Log.Warn($"transcript translation not started: {ex.Message}");
            if (LastSessionDir == dir) Say(nameof(TranslateStatus), () => Loc.F("translate_transcript_failed", ex.Message));
            RecordTranslation(dir, manifest, null, new TranscriptTranslator.Result(null, 0, 0, new TranslationStats(), ex.Message), to, coach);
            return;
        }
        var known = _liveTranslations.TryGetValue(dir, out var live) ? KnownByText(live) : null;
        var name = TranslateTargets.Display(to);
        IsTranscribing = true;
        Say(nameof(TranscribeProgress), () => Loc.F("translate_transcript_progress", TranslateTargets.Display(to), 0, "..."));
        using var cts = new CancellationTokenSource();
        _transcriptCts = cts;
        var dispatcher = Application.Current?.Dispatcher;
        TranscriptTranslator.Result result;
        try
        {
            result = await Task.Run(() => TranscriptTranslator.TranslateAsync(provider, to, dir, known,
                (done, all) => dispatcher?.BeginInvoke(() => Say(nameof(TranscribeProgress), () => Loc.F("translate_transcript_progress", TranslateTargets.Display(to), done, all))),
                cts.Token));
        }
        catch (Exception ex)
        {
            Log.Error("transcript translation failed", ex);
            result = new TranscriptTranslator.Result(null, 0, 0, new TranslationStats(), ex is OperationCanceledException ? Loc.T("tx_cancelling") : ex.Message);
        }
        finally
        {
            _transcriptCts = null;
            IsTranscribing = false;
            TranscribeProgress = null;
        }
        RecordTranslation(dir, manifest, null, result, to, coach, provider);
        if (LastSessionDir == dir)
        {
            if (result.File is { } f && result.Error is null) Say(nameof(TranslateStatus), () => Loc.F("translate_transcript_done", f));
            else Say(nameof(TranslateStatus), () => Loc.F("translate_transcript_failed", result.Error ?? ""));
            ShowLast(dir, manifest);
        }
        Log.Info($"session {manifest.Id}: transcript translated to {to} by {provider.Name}: {result.Translated}/{result.Lines} line(s){(result.Error is { } e ? $", stopped: {e}" : "")} (named {name})");
    }

    /// <summary>Live translations by original text, for the transcript (its lines often match the live ones).</summary>
    private static IReadOnlyDictionary<string, string> KnownByText(IReadOnlyDictionary<string, string> byKey)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in byKey)
        {
            var text = k.Split('|', 3) is { Length: 3 } p ? p[2] : null;
            if (text is not null) d[text] = v;
        }
        return d;
    }

    /// <summary>session.json "translation": the live part (translator) and, after the transcript, its file.</summary>
    private void RecordTranslation(string dir, SessionManifest manifest, LiveTranslator? live, TranscriptTranslator.Result? transcript,
        string? to = null, CoachSettings? coach = null, ICoachProvider? provider = null)
    {
        try
        {
            TranslationInfo info;
            lock (manifest) info = manifest.Translation ?? new TranslationInfo();
            if (live is not null)
            {
                info.To = live.To;
                info.Provider = live.Provider.Name;
                info.Model = live.Provider.Model;
                info.SentOut = !live.Provider.IsLocal;
                info.LiveLines = live.Stats.Lines;
                info.Calls += live.Stats.Calls;
                info.Failures += live.Stats.Failures;
                info.InputTokens += live.Stats.InputTokens;
                info.OutputTokens += live.Stats.OutputTokens;
                info.TokensEstimated |= live.Stats.TokensEstimated;
                info.Error = live.Stats.LastError;
            }
            if (transcript is not null)
            {
                info.To = TranslateTargets.Normalize(to);
                if (provider is not null)
                {
                    info.Provider = provider.Name;
                    info.Model = provider.Model;
                    info.SentOut = !provider.IsLocal;
                }
                else if (coach is not null) info.SentOut = CoachPrivacy.IsCloud(coach);
                info.TranscriptLines = transcript.Translated;
                info.Calls += transcript.Stats.Calls;
                info.Failures += transcript.Stats.Failures;
                info.InputTokens += transcript.Stats.InputTokens;
                info.OutputTokens += transcript.Stats.OutputTokens;
                info.TokensEstimated |= transcript.Stats.TokensEstimated;
                info.File = transcript.File;
                info.Error = transcript.Error;
            }
            TranscriptTranslator.Record(dir, manifest, info);
        }
        catch (Exception ex)
        {
            Log.Error("recording the translation in session.json failed", ex);
        }
    }

    private void OnTick()
    {
        var s = _session;
        if (s is null || !IsRecording) return;
        ElapsedText = s.Elapsed.ToString(@"hh\:mm\:ss");
        MicLevel = s.Mic?.Level ?? 0;
        SpeakerLevel = s.Speaker?.Level ?? 0;
        if (_liveRun.Current is { } live) LiveLag = live.Failed ? Loc.T("lag_stopped") : LivePolicy.LagText(live.LagSeconds);
        // a coach that could not start (the AI tool installed meanwhile, the local server started) tries again every 10 s
        if (CoachEnabled && _liveRun.Current is not null && _coachRun.RetryDue(DateTime.UtcNow)) ApplyCoachChoice();

        if (s.Mic is null) return;
        var wasQuiet = _micQuiet.IsQuiet;
        var quiet = _micQuiet.Update(MicLevel, s.Elapsed);
        if (quiet == wasQuiet) return;
        Log.Info(quiet ? $"mic level near silence for {QuietDetector.Hold.TotalSeconds:0} s on '{s.Mic.CurrentDeviceName}'" : "mic level back above near silence");
        if (quiet) Say(nameof(MicQuietWarning), () => Loc.T("mic_quiet"));
        else MicQuietWarning = null;
    }

    private void ShowLast(string dir, SessionManifest m)
    {
        LastSessionDir = dir;
        Say(nameof(LastSessionTitle), () => Loc.F("saved_title", Path.GetFileName(dir)));
        int files;
        lock (m) files = m.Files.Count(f => f.Name != SessionManifest.FileName);
        var length = TimeSpan.FromSeconds(m.DurationSeconds).ToString(@"hh\:mm\:ss");
        Say(nameof(LastSessionDetail), () => Loc.F("saved_detail", length, files));
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
        var who = string.Join(" + ", m.Sources.Select(s => s.Kind == "mic" ? Loc.T("recent_mic") : Loc.T("recent_speaker")));
        var t = m.Transcript.Status == "done" ? "  ·  " + Loc.T("recent_transcript") : "";
        var culture = System.Globalization.CultureInfo.GetCultureInfo(Loc.Instance.Language == "vi" ? "vi-VN" : "en-US");
        return new SessionItem(m.StartedLocal.ToString("ddd d MMM, HH:mm", culture), dir,
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
            Say(nameof(Notice), () => Loc.T("path_copied"));
        }
        catch (Exception ex)
        {
            Say(nameof(Error), () => Loc.F("copy_failed", ex.Message));
        }
    }

    [RelayCommand]
    private void OpenOutputFolder() => OpenFolder(OutputFolder);

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("dlg_save_to"), InitialDirectory = OutputFolder };
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
        var prefix = WhisperModels.IsAuto(SelectedModel.Key) ? Loc.F("model_auto_uses", m.Name) + " " : "";
        ModelMissing = !m.IsPresent;
        var status = m.IsPresent ? Loc.T("model_ready") : Loc.T("model_missing");
        if (!m.IsPresent && WhisperModels.IsAuto(SelectedModel.Key))
        {
            // Auto never runs a larger model than its pick; a smaller one on disk fills in until the download
            var now = WhisperModels.All.Take(WhisperModels.Rank(m)).LastOrDefault(x => x.IsPresent);
            if (now is not null) status += " " + Loc.F("model_until", now.Name);
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
            Say(nameof(Error), () => Loc.F("download_failed", ex.Message));
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
        Say(nameof(UpdateStatus), () => Loc.T("upd_checking"));
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
        Say(nameof(DiagnosticsStatus), () => Loc.T("diag_collecting"));
        try
        {
            var zip = await Task.Run(() => Diagnostics.CreateZip());
            try
            {
                Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { zip });
                Say(nameof(DiagnosticsStatus), () => Loc.F("diag_copied", Path.GetFileName(zip)));
            }
            catch (Exception ex)
            {
                Log.Warn($"diagnostics: clipboard busy: {ex.Message}");
                Say(nameof(DiagnosticsStatus), () => Loc.F("diag_saved", zip));
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("diagnostics zip failed", ex);
            Say(nameof(DiagnosticsStatus), () => Loc.F("diag_failed", ex.Message));
        }
    }

    [RelayCommand]
    private async Task RestartToUpdateAsync()
    {
        if (IsRecording)
        {
            Say(nameof(Notice), () => Loc.T("upd_stop_first"));
            return;
        }
        if (_session is not null) Say(nameof(Notice), () => Loc.T("upd_saving_first"));
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
        if (_coachSaving is { IsCompleted: false } coachSaving) await Task.WhenAny(coachSaving, Task.Delay(TimeSpan.FromSeconds(12)));
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
            var translator = _translator;
            _translator = null;
            SessionManifest? manifest = null;
            try
            {
                manifest = session.StopAndSaveAsync().GetAwaiter().GetResult();
                SetSpeakerNames(manifest);
                // the translation call in flight is killed now; the lines translated so far are saved with the live text
                translator?.Dispose();
                if (live is { } l && l.Wait(TimeSpan.FromSeconds(2)))
                    SaveLiveFiles(session.Directory, manifest, l.Result, _translate.WhisperTranslates, translator?.Results, _translate.Current);
            }
            catch (Exception ex)
            {
                Log.Error("saving at shutdown failed, part files kept", ex);
            }
            finally
            {
                // whatever failed above: the coach's AI tool is killed and its notes so far are written (no wait for its call)
                try { FinishCoach(session.Directory, manifest, TimeSpan.Zero, wait: true); }
                catch (Exception ex) { Log.Error("ending the coach at shutdown", ex); }
                try { translator?.Dispose(); } catch (Exception ex) { Log.Error("ending the translation at shutdown", ex); }
            }
        }
        try { _coachSaving?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        MarkTranscriptsInterrupted(session);
    }

    private void MarkTranscriptsInterrupted(RecordingSession? session)
    {
        // every session still queued or transcribing, and no queued one starts after this
        var dirs = _transcripts.Close().Prepend(session?.Directory).OfType<string>().Distinct();
        foreach (var dir in dirs)
            TranscriptionService.MarkInterrupted(dir, "RecBox was closed before the transcript was finished.");
    }

    // ---------- live coach ----------
    /// <summary>The coach of the recording in progress: its engine, the AI it really uses, and a failed start to retry.</summary>
    private readonly CoachRun _coachRun = new();
    private CoachEngine? _coach => _coachRun.Engine;
    private SlideCapture? _slides;
    private Task? _coachSaving;
    private ICoachToast? _toast;
    private string? _youName;
    private string? _othersName;
    private IReadOnlyList<NameTarget> _nameTargets = Array.Empty<NameTarget>();

    /// <summary>The AI tools on this PC (each with its own sign-in) and a model server on this PC. RecBox keeps no keys.</summary>
    public IReadOnlyList<CoachProviderOption> CoachProviderOptions { get; } = new[]
    {
        new CoachProviderOption(CoachProviderKind.ClaudeCli),
        new CoachProviderOption(CoachProviderKind.CodexCli),
        new CoachProviderOption(CoachProviderKind.GeminiCli),
        new CoachProviderOption(CoachProviderKind.OpenAiCompatible),
    };
    /// <summary>Suggestions for Claude Code's model box (any id or alias may be typed).</summary>
    public IReadOnlyList<string> CoachModelOptions { get; } = CoachModels.Claude.Select(m => m.Id).ToArray();
    public IReadOnlyList<StyleOption> CoachStyleOptions { get; } = CoachStyles.All.Select(s => new StyleOption(s)).ToArray();
    public IReadOnlyList<LanguageOption> CoachLanguageOptions { get; } = CoachLanguages.All.Select(l => new LanguageOption(l.Code, CoachLanguages.Display)).ToArray();

    // ---------- Translate to ----------
    /// <summary>Off, English (whisper, offline), and the languages the picked AI translates to.</summary>
    public IReadOnlyList<LanguageOption> TranslateOptions { get; } = TranslateTargets.All.Select(c => new LanguageOption(c, TranslateTargets.Display)).ToArray();
    private readonly TranslateChoice _translate;
    /// <summary>The live lines' translator of the recording in progress (null when Translate to is Off or English).</summary>
    private LiveTranslator? _translator;
    private string _translatingTo = "";
    /// <summary>Live translations of recent recordings, reused when their transcript is translated.</summary>
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _liveTranslations = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Makes the AI for the coach and the translation (tests and the screenshots never reach a real tool).</summary>
    private readonly Func<CoachSettings, ICoachProvider> _makeProvider = s => CoachProviders.Create(s);

    /// <summary>The main screen picker: this recording's Translate to (one click; the next recording starts from the default).</summary>
    [ObservableProperty] private LanguageOption _selectedTranslate;
    /// <summary>Settings: the Translate to every recording starts with (saved).</summary>
    [ObservableProperty] private LanguageOption _defaultTranslate;
    [ObservableProperty] private string _translateStatus = "";
    [ObservableProperty] private string _translateUsage = "";
    /// <summary>Translate to needs the AI and none is ready: the picker says so and shows the AI choice right there.</summary>
    [ObservableProperty] private bool _translateNeedsSetup;
    [ObservableProperty] private bool _translateUsesAi;
    [ObservableProperty] private string _tokenEstimateText = "";

    partial void OnSelectedTranslateChanged(LanguageOption value)
    {
        if (_loading) return;
        _translate.Pick(value.Code);
        _liveTranslateFlag = _translate.WhisperTranslates;
        Log.Info($"translate to: {(value.Code.Length == 0 ? "off" : value.Code)} for {(IsRecording ? "this recording" : "the next recording")} (default {(_translate.Default.Length == 0 ? "off" : _translate.Default)})");
        if (IsRecording) StartTranslator();
        UpdateCoachTexts();
    }

    partial void OnDefaultTranslateChanged(LanguageOption value)
    {
        if (_loading) return;
        _translate.SetDefault(value.Code);
        Persist(s => s.TranslateTo = _translate.Default);
        SyncTranslatePicker();
        UpdateCoachTexts();
    }

    /// <summary>The main screen picker shows this recording's choice (the same object of the list: never replaced).</summary>
    private void SyncTranslatePicker()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            SelectedTranslate = TranslateOptions.First(o => o.Code == _translate.Current);
            DefaultTranslate = TranslateOptions.First(o => o.Code == _translate.Default);
        }
        finally
        {
            _loading = wasLoading;
        }
        _liveTranslateFlag = _translate.WhisperTranslates;
        UpdateCoachTexts();
    }

    /// <summary>
    /// The translator of live lines for this recording: started when Translate to needs the AI and live text runs,
    /// replaced when the target or the AI changes (lines translated so far are kept), ended when it is Off or English.
    /// </summary>
    private void StartTranslator()
    {
        var to = _translate.Current;
        if (_translator is { } running && (running.To != to || !TranslateTargets.NeedsAi(to)))
        {
            _translator = null;
            var old = running;
            _ = Task.Run(() => old.Dispose());
        }
        if (!TranslateTargets.NeedsAi(to) || !IsRecording || _liveRun.Current is null || _translator is not null)
        {
            UpdateTranslateTexts();
            return;
        }
        try
        {
            var provider = _makeProvider(CurrentCoach());
            var t = new LiveTranslator(provider, to);
            var dispatcher = Application.Current?.Dispatcher;
            t.Translated += (key, text) => dispatcher?.BeginInvoke(() =>
            {
                foreach (var item in LiveLines)
                    if (item.Key == key) item.Translation = text;
                if (ReferenceEquals(_translator, t)) UpdateTranslateTexts();
            });
            t.Changed += () => dispatcher?.BeginInvoke(() =>
            {
                if (ReferenceEquals(_translator, t)) UpdateTranslateTexts();
            });
            _translator = t;
            _translatingTo = to;
            Log.Info($"translation of live lines to {to} through {provider.Name} (model {(provider.Model.Length == 0 ? "the tool's default" : provider.Model)}), sends the text out: {!provider.IsLocal}");
        }
        catch (CoachProviderException ex)
        {
            Log.Warn($"translation could not start: {ex.Message}");
            Say(nameof(TranslateStatus), () => Loc.F("translate_cannot_start", ex.Message));
            return;
        }
        UpdateTranslateTexts();
    }

    /// <summary>What the Translate to picker says under it, and whether it offers the AI choice inline.</summary>
    private void UpdateTranslateTexts()
    {
        var to = _translate.Current;
        TranslateUsesAi = TranslateTargets.NeedsAi(to);
        var ready = AiReady(CurrentCoach());
        TranslateNeedsSetup = TranslateUsesAi && !ready;
        if (_translator is { } t)
        {
            if (t.Status.Length > 0) TranslateStatus = t.Status;
            else Say(nameof(TranslateStatus), () => t.Provider.IsLocal ? Loc.F("translate_by_local", t.Provider.Name) : Loc.F("translate_by", CoachPrivacy.Receiver(CurrentCoach())));
            var s = t.Stats;
            Say(nameof(TranslateUsage), () => Loc.F("translate_live_usage", s.Lines, s.InputTokens.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture),
                s.OutputTokens.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture)) + (s.TokensEstimated ? " " + Loc.T("usage_estimated") : ""));
            return;
        }
        TranslateUsage = "";
        if (TranslateTargets.IsEnglish(to)) Say(nameof(TranslateStatus), () => Loc.T("translate_english"));
        else if (!TranslateUsesAi) TranslateStatus = "";
        else if (!ready) Say(nameof(TranslateStatus), () => Loc.F("translate_needs_ai", TranslateTargets.Display(_translate.Current)));
        else Say(nameof(TranslateStatus), () => CoachPrivacy.IsCloud(CurrentCoach())
            ? Loc.F("translate_by", CoachPrivacy.Receiver(CurrentCoach()))
            : Loc.F("translate_by_local", CoachPrivacy.Receiver(CurrentCoach())));
    }

    /// <summary>The picked AI can be used: its tool is installed, or the local server address is a local one.</summary>
    private bool AiReady(CoachSettings c) => c.Provider == CoachProviderKind.OpenAiCompatible
        ? CoachPrivacy.IsLocalEndpoint(c.Endpoint)
        : CoachProviderOptions.FirstOrDefault(o => o.Kind == c.Provider)?.Installed ?? false;
    public ObservableCollection<CoachEntry> CoachCues { get; } = new();
    public ObservableCollection<CoachQuestion> CoachQuestions { get; } = new();
    public ObservableCollection<CoachNextStep> CoachNextSteps { get; } = new();
    /// <summary>Newest first.</summary>
    public ObservableCollection<CoachEntry> CoachNotes { get; } = new();
    public ObservableCollection<CoachEntry> CoachAlerts { get; } = new();

    [ObservableProperty] private bool _coachEnabled;
    [ObservableProperty] private bool _coachSetupOpen;
    [ObservableProperty] private CoachProviderOption _coachProvider;
    /// <summary>The model typed for the picked AI tool (empty: the tool's own default).</summary>
    [ObservableProperty] private string _coachModelText;
    [ObservableProperty] private string _coachEndpoint;
    [ObservableProperty] private string _coachEndpointModel;
    [ObservableProperty] private string _coachGoal;
    [ObservableProperty] private string _coachBrief;
    [ObservableProperty] private string _coachRole;
    [ObservableProperty] private StyleOption _coachStyle;
    [ObservableProperty] private string _coachInstructions;
    [ObservableProperty] private LanguageOption _coachLanguage;
    /// <summary>Whether the picked AI tool is installed, and whose sign-in it uses (no keys in RecBox).</summary>
    [ObservableProperty] private string _coachToolStatus = "";
    [ObservableProperty] private bool _captureSlides;
    [ObservableProperty] private string _slideWindow;
    /// <summary>Where the transcript goes: "Nothing leaves your computer" only while that is true.</summary>
    [ObservableProperty] private string _privacyText = CoachPrivacy.Offline;
    [ObservableProperty] private bool _coachSendsOut;
    [ObservableProperty] private string _coachSummary = "";
    [ObservableProperty] private string _coachStatus = "";
    [ObservableProperty] private string _coachUsage = "";
    [ObservableProperty] private string? _latestCue;
    /// <summary>The live panel shows the coach instead of the live text.</summary>
    [ObservableProperty] private bool _showCoachTab;
    private CoachSnapshot? _lastSnap;

    public bool IsClaudeProvider => CoachProvider.Kind is CoachProviderKind.ClaudeCli or CoachProviderKind.Anthropic;
    public bool IsEndpointProvider => CoachProvider.Kind == CoachProviderKind.OpenAiCompatible;
    /// <summary>Codex or Gemini CLI: a plain model box (empty: the tool's own default).</summary>
    public bool IsOtherToolProvider => CoachProvider.Kind is CoachProviderKind.CodexCli or CoachProviderKind.GeminiCli;
    public string CoachModelHint => IsClaudeProvider ? Loc.T("model_hint_claude") : Loc.T("model_hint_tool");

    /// <summary>The coach settings as the screen shows them now (also while the screenshots skip saving).</summary>
    private CoachSettings CurrentCoach()
    {
        var c = (_settings.Coach ?? new CoachSettings()).Clone();
        c.Enabled = CoachEnabled;
        c.Provider = CoachProvider?.Kind ?? c.Provider;
        if (c.Provider != CoachProviderKind.OpenAiCompatible) c.SetModelFor(c.Provider, CoachModelText ?? "");
        c.Endpoint = CoachEndpoint ?? c.Endpoint;
        c.EndpointModel = CoachEndpointModel ?? c.EndpointModel;
        c.Goal = CoachGoal ?? "";
        c.Brief = CoachBrief ?? "";
        c.Role = CoachRole ?? "";
        c.Style = CoachStyle?.Style.Key ?? c.Style;
        c.Instructions = CoachInstructions ?? "";
        c.OutputLanguage = CoachLanguage?.Code ?? c.OutputLanguage;
        c.CaptureSlides = CaptureSlides;
        c.SlideWindow = SlideWindow ?? "";
        return c;
    }

    /// <summary>The privacy line and the summary: the AI the running coach really uses while one runs, else the picked one.</summary>
    private void UpdateCoachTexts()
    {
        var c = _coachRun.Described(CurrentCoach());
        var translateTo = _translate.Current;
        PrivacyText = CoachPrivacy.Text(c, translateTo);
        CoachSendsOut = CoachPrivacy.SendsOut(c, translateTo);
        var names = BriefNames.Parse(c.Brief);
        _youName = names.You;
        _othersName = names.Others;
        _nameTargets = BriefNames.NameTargets(BriefNames.Terms(c.Goal + "\n" + c.Brief));
        CoachToolStatus = ToolStatus(c);
        // before a recording: the tokens an hour these settings use (no prices anywhere)
        long perHour = 0;
        if (c.Enabled && c.Provider != CoachProviderKind.Fake) perHour += UsageEstimate.CoachTokensPerHour(c);
        if (TranslateTargets.NeedsAi(translateTo)) perHour += UsageEstimate.TranslationTokensPerHour();
        TokenEstimateText = perHour > 0 ? UsageEstimate.Text(perHour) : "";
        UpdateTranslateTexts();
        if (!c.Enabled)
        {
            CoachSummary = Loc.T("coach_summary_off");
            return;
        }
        var model = c.Provider == CoachProviderKind.OpenAiCompatible ? c.EndpointModel : CoachModels.Describe(c.ModelFor(c.Provider));
        var installed = AiReady(c);
        var tool = c.Provider switch
        {
            CoachProviderKind.OpenAiCompatible => Loc.F("ai_local_server", CoachPrivacy.Receiver(c)),
            CoachProviderKind.CodexCli => Loc.T("ai_codex"),
            CoachProviderKind.GeminiCli => Loc.T("ai_gemini"),
            _ => Loc.T("ai_claude"),
        };
        var where = installed || c.Provider == CoachProviderKind.OpenAiCompatible ? tool : Loc.F("ai_missing_suffix", tool);
        CoachSummary = Loc.F("coach_summary", model, where);
    }

    private string ToolStatus(CoachSettings c)
    {
        if (c.Provider == CoachProviderKind.OpenAiCompatible)
            return CoachPrivacy.IsLocalEndpoint(c.Endpoint) ? Loc.T("tool_status_local") : Loc.T("endpoint_not_local");
        var name = c.Provider switch
        {
            CoachProviderKind.CodexCli => Loc.T("ai_codex"),
            CoachProviderKind.GeminiCli => Loc.T("ai_gemini"),
            _ => Loc.T("ai_claude"),
        };
        return AiReady(c) ? Loc.F("tool_status_ready", name) : Loc.F("tool_status_missing", name);
    }

    private string FixNames(string text) => _nameTargets.Count == 0 ? text : BriefNames.FixNames(text, _nameTargets).Text;

    private void PersistCoach(Action<CoachSettings> change)
    {
        Persist(s => change(s.Coach ??= new CoachSettings()));
        UpdateCoachTexts();
    }

    /// <summary>A goal, brief, role or style change during the recording: the coach uses it from its next call (the kit's re-aim).</summary>
    private void CoachSetupChanged(Action<CoachSettings> change)
    {
        PersistCoach(change);
        if (_coach is { } c && !_loading) c.UpdateSetup(CoachSetup.From(CurrentCoach()));
    }

    partial void OnCoachEnabledChanged(bool value)
    {
        PersistCoach(s => s.Enabled = value);
        if (_loading) return;
        Log.Info($"live coach {(value ? "on" : "off")} ({CoachProvider.Kind}, sends the live text out: {CoachSendsOut})");
        if (_coach is { } c) c.Paused = !value;
        else if (value && IsRecording && _session is { } s && _liveRun.Current is not null) StartCoach(s);
        if (value && IsRecording && !LiveEnabled) Say(nameof(CoachStatus), () => Loc.T("coach_needs_live"));
    }

    partial void OnCoachProviderChanged(CoachProviderOption value)
    {
        PersistCoach(s => s.Provider = value.Kind);
        // each tool keeps its own model: show the one typed for this tool
        var wasLoading = _loading;
        _loading = true;
        try { CoachModelText = (_settings.Coach ?? new CoachSettings()).ModelFor(value.Kind); }
        finally { _loading = wasLoading; }
        OnPropertyChanged(nameof(IsClaudeProvider));
        OnPropertyChanged(nameof(IsEndpointProvider));
        OnPropertyChanged(nameof(IsOtherToolProvider));
        OnPropertyChanged(nameof(CoachModelHint));
        ApplyCoachChoice();
        RestartTranslatorForAi();
    }

    partial void OnCoachModelTextChanged(string value)
    {
        if (_loading) return;
        var kind = CoachProvider.Kind;
        PersistCoach(s => s.SetModelFor(kind, value ?? ""));
        ApplyCoachChoice();
        RestartTranslatorForAi();
    }

    /// <summary>Another AI picked during a recording: the live translation moves to it (lines translated so far are kept on screen).</summary>
    private void RestartTranslatorForAi()
    {
        if (_loading || _translator is not { } t) return;
        _translator = null;
        _ = Task.Run(() => t.Dispose());
        StartTranslator();
    }

    partial void OnCoachEndpointChanged(string value)
    {
        PersistCoach(s => s.Endpoint = value.Trim());
        ApplyCoachChoice();
        RestartTranslatorForAi();
    }

    partial void OnCoachEndpointModelChanged(string value)
    {
        PersistCoach(s => s.EndpointModel = value.Trim());
        ApplyCoachChoice();
        RestartTranslatorForAi();
    }

    /// <summary>
    /// The AI choice changed or a key was saved (force) during a recording with live text: a running coach moves to the
    /// new AI when it can be made (else keeps its AI and says why), and a coach that could not start tries again now.
    /// The privacy line follows the AI that really runs.
    /// </summary>
    private void ApplyCoachChoice(bool force = false)
    {
        if (!_loading && IsRecording && _session is { } session && _liveRun.Current is not null)
        {
            var hadEngine = _coach is not null;
            try
            {
                var c = CurrentCoach();
                var note = _coachRun.Apply(c, p => BuildCoach(session, c, p), force);
                if (!hadEngine && _coach is { } started)
                {
                    ApplyCoach(started.Snapshot());
                    Log.Info($"coach started after a fix: {started.Provider.Name}, model {started.Provider.Model}, sends the live text out: {!started.Provider.IsLocal}");
                }
                if (note is not null)
                {
                    CoachStatus = note;
                    Log.Info($"coach choice: {note}");
                }
            }
            catch (Exception ex)
            {
                Log.Error("the coach choice could not be applied", ex);
            }
        }
        UpdateCoachTexts();
    }
    partial void OnCoachGoalChanged(string value) => CoachSetupChanged(s => s.Goal = value);
    partial void OnCoachBriefChanged(string value) => CoachSetupChanged(s => s.Brief = value);
    partial void OnCoachRoleChanged(string value) => CoachSetupChanged(s => s.Role = value);
    partial void OnCoachStyleChanged(StyleOption value) => CoachSetupChanged(s => s.Style = value.Style.Key);
    partial void OnCoachInstructionsChanged(string value) => CoachSetupChanged(s => s.Instructions = value);
    partial void OnCoachLanguageChanged(LanguageOption value) => CoachSetupChanged(s => s.OutputLanguage = value.Code);
    partial void OnCaptureSlidesChanged(bool value) => PersistCoach(s => s.CaptureSlides = value);
    partial void OnSlideWindowChanged(string value) => PersistCoach(s => s.SlideWindow = value.Trim());
    partial void OnCoachSetupOpenChanged(bool value)
    {
        if (!value) return;
        // a tool installed while RecBox runs shows as ready when the setup opens
        foreach (var o in CoachProviderOptions) o.Relabel();
        UpdateCoachTexts();
    }

    [RelayCommand]
    private void ToggleCoachSetup() => CoachSetupOpen = !CoachSetupOpen;

    [RelayCommand]
    private void ShowCoach() => ShowCoachTab = true;

    [RelayCommand]
    private void ShowLiveText() => ShowCoachTab = false;

    /// <summary>The kit's LEAD file: a brief picked from disk fills the brief box (it stays editable).</summary>
    [RelayCommand]
    private void LoadBriefFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("dlg_brief"), Filter = Loc.T("dlg_brief_filter") };
        if (dlg.ShowDialog(Application.Current?.MainWindow) != true) return;
        try
        {
            var text = File.ReadAllText(dlg.FileName);
            CoachBrief = text.Length > 60000 ? text[..60000] : text;
            PersistCoach(s => s.BriefFile = dlg.FileName);
            Log.Info($"coach brief loaded from a file ({text.Length} characters)");
        }
        catch (Exception ex)
        {
            Say(nameof(Error), () => Loc.F("brief_read_failed", ex.Message));
        }
    }

    /// <summary>The kit's after-meeting pass: transcribe a saved recording again (the language at its Stop).</summary>
    [RelayCommand]
    private void TranscribeAgain(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        var m = SessionManifest.Load(dir);
        if (m is null)
        {
            Say(nameof(Error), () => Loc.T("no_session_json"));
            return;
        }
        Log.Info($"transcribe again: {Path.GetFileName(dir)}");
        if (LastSessionDir != dir) ShowLast(dir, m);
        QueueTranscript(dir, m, m.Language?.Used);
    }

    private void ClearCoachView()
    {
        CoachCues.Clear();
        CoachQuestions.Clear();
        CoachNextSteps.Clear();
        CoachNotes.Clear();
        CoachAlerts.Clear();
        LatestCue = null;
        CoachUsage = "";
        _lastSnap = null;
        Say(nameof(CoachStatus), () => CoachEnabled ? Loc.T("coach_starts_with_live") : Loc.T("coach_is_off"));
    }

    /// <summary>Starts the coach for this recording. Never throws: a coach that cannot start only says why.</summary>
    private void StartCoach(RecordingSession session)
    {
        if (_coach is not null) return;
        try
        {
            var c = CurrentCoach();
            if (_coachRun.TryStart(c, p => BuildCoach(session, c, p)))
            {
                var engine = _coach!;
                ApplyCoach(engine.Snapshot());
                Log.Info($"coach started: {engine.Provider.Name}, model {engine.Provider.Model}, sends the live text out: {!engine.Provider.IsLocal}");
            }
            else
            {
                // says what to fix; the coach starts by itself once it can (key saved, choice changed, or the 10 s retry)
                if (_coachRun.Error is { } err) CoachStatus = err;
                else Say(nameof(CoachStatus), () => Loc.T("coach_not_started"));
                Log.Warn($"coach did not start: {_coachRun.Error}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("coach could not start", ex);
            Say(nameof(CoachStatus), () => Loc.F("coach_start_failed", ex.Message));
        }
        UpdateCoachTexts();
    }

    private CoachEngine BuildCoach(RecordingSession session, CoachSettings c, ICoachProvider provider)
    {
        // the same rule as the replay: the set language (or the one live text locked) plus English; the kit's set before a lock
        var langs = CoachLanguages.ForGate(SelectedLanguage.Code, _liveRun.Current?.Language.Detected);
        if (!App.Headless) _toast ??= new CoachToast(Application.Current.Dispatcher);
        var engine = new CoachEngine(new CoachOptions
        {
            Provider = provider,
            Setup = CoachSetup.From(c),
            Clock = () => session.Elapsed,
            Toast = _toast,
            MaxWakes = Math.Max(1, c.MaxWakes),
            Languages = langs,
        });
        var dispatcher = Application.Current?.Dispatcher;
        engine.Changed += () => dispatcher?.BeginInvoke(() =>
        {
            if (ReferenceEquals(_coach, engine)) ApplyCoach(engine.Snapshot());
        });
        return engine;
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    private void ApplyCoach(CoachSnapshot s)
    {
        Fill(CoachCues, Enumerable.Reverse(s.Cues));
        Fill(CoachQuestions, s.Questions);
        Fill(CoachNextSteps, s.NextSteps);
        Fill(CoachNotes, Enumerable.Reverse(s.Notes));
        Fill(CoachAlerts, Enumerable.Reverse(s.Alerts));
        LatestCue = s.Cues.Count > 0 ? $"{s.Cues[^1].Time}  {s.Cues[^1].Text}" : null;
        CoachStatus = s.Status;
        CoachUsage = s.UsageText;
        _lastSnap = s;
    }

    /// <summary>
    /// Stops the coach and the slide capture of a stopped recording and writes coach.md, coach.json, slides\ and their
    /// session.json entries. The coach's last call may finish within the grace, in the background (wait: false), so
    /// Record is free again at once; at shutdown (wait: true) the notes so far are written before this returns.
    /// </summary>
    /// <param name="manifest">Null when the recording's save failed: the coach still ends (its AI tool killed) and coach.md /
    /// coach.json go beside the recording's parts, without a session.json entry.</param>
    private void FinishCoach(string dir, SessionManifest? manifest, TimeSpan grace, bool wait = false)
    {
        var coach = _coachRun.Stop();
        UpdateCoachTexts();
        var slides = _slides;
        _slides = null;
        if (slides is not null)
        {
            try
            {
                var saved = slides.Stop();
                if (manifest is not null) SaveSlides(dir, manifest, saved, SlideWindow);
            }
            catch (Exception ex) { Log.Error("saving the slides failed", ex); }
        }
        if (coach is null) return;
        var sentOut = !coach.Snapshot().Local;
        var dispatcher = Application.Current?.Dispatcher;
        var task = Task.Run(async () =>
        {
            CoachSnapshot snap;
            try
            {
                snap = await coach.StopAsync(grace).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // a second fault (the save may already have failed): log it, the tools are still killed below
                Log.Error("stopping the coach", ex);
                snap = coach.Snapshot();
            }
            finally
            {
                // the call still running after the grace was killed by StopAsync; Dispose makes sure of it
                coach.Dispose();
            }
            if (manifest is not null) SaveCoachFiles(dir, manifest, snap, sentOut);
            else
            {
                try { CoachStore.Save(dir, Path.GetFileName(dir), snap); }
                catch (Exception ex) { Log.Error("saving the coach notes of a failed save", ex); }
            }
            dispatcher?.BeginInvoke(() =>
            {
                ApplyCoach(snap);
                var cues = snap.Cues.Count;
                var answers = snap.Questions.Count(q => q.Answer.Length > 0);
                Say(nameof(CoachStatus), () => Loc.F("coach_saved", cues, answers));
            });
        });
        _coachSaving = task;
        if (wait)
        {
            try { task.Wait(TimeSpan.FromSeconds(5)); } catch (Exception ex) { Log.Error("coach save at shutdown", ex); }
        }
    }

    internal static void SaveCoachFiles(string dir, SessionManifest manifest, CoachSnapshot snap, bool sentOut)
    {
        try
        {
            CoachStore.SaveForSession(dir, manifest, snap, sentOut);
            Log.Info($"session {manifest.Id}: coach notes saved ({snap.Calls} call(s), {snap.Cues.Count} cue(s))");
        }
        catch (Exception ex)
        {
            Log.Error("saving the coach notes failed", ex);
        }
    }

    private static void SaveSlides(string dir, SessionManifest manifest, IReadOnlyList<(double At, string File)> saved, string window)
    {
        if (saved.Count == 0) return;
        lock (manifest)
        {
            manifest.Slides = new SlidesInfo { Window = window, Files = saved.Select(s => $"{SlideCapture.Folder}/{s.File}").ToList() };
            manifest.Files.RemoveAll(f => f.Name == SlideCapture.Folder);
            manifest.Files.Add(new FileEntry { Name = SlideCapture.Folder, Description = $"{saved.Count} picture(s) of the window \"{window}\", saved when it showed a new slide (slide-hh-mm-ss.png, time from the recording start)" });
        }
        TranscriptionService.SaveManifest(dir, manifest);
        Log.Info($"session {manifest.Id}: {saved.Count} slide(s) saved");
    }

    /// <summary>
    /// For the off-screen screenshots (RecBox.exe --render-ui): puts the screen in a state without recording,
    /// transcribing or touching any device. "idle", "recording" or "transcribing".
    /// </summary>
    public void ShowPreview(string state, bool livePanel)
    {
        _loading = true;
        try
        {
            LivePanelOpen = livePanel;
            // the usual setup: both sources, the transcript and live text on (whatever the render's settings file says)
            MicEnabled = SpeakerEnabled = TranscribeEnabled = LiveEnabled = true;
            LiveLines.Clear();
            IsRecording = false;
            IsBusy = false;
            IsTranscribing = false;
            TranscribeProgress = null;
            TranscriptText = null;
            SetIdleTexts();
            ElapsedText = "00:00:00";
            MicLevel = SpeakerLevel = 0;
            UpdateLanguagePicked(null);
            ClearCoachView();
            CoachEnabled = state == "coach";
            CoachSetupOpen = state == "coach";
            ShowCoachTab = state == "coach";
            if (state == "coach")
            {
                CoachProvider = CoachProviderOptions[0];
                CoachModelText = CoachModels.Default;
                OnPropertyChanged(nameof(IsClaudeProvider));
                OnPropertyChanged(nameof(IsEndpointProvider));
                OnPropertyChanged(nameof(IsOtherToolProvider));
                CoachGoal = Loc.T("preview_goal");
                CoachRole = Loc.T("preview_role");
                CoachBrief = Loc.T("preview_brief");
                ApplyCoach(new CoachSnapshot
                {
                    Provider = "Claude Code",
                    Model = CoachModels.Default,
                    Cues = { new CoachEntry { At = 734, Time = "00:12:14", Text = Loc.T("preview_cue") } },
                    Questions =
                    {
                        new CoachQuestion { Id = "Q1", Question = Loc.T("preview_q1"), Answer = Loc.T("preview_a1"), FromBrief = true, AnsweredAt = 725 },
                        new CoachQuestion { Id = "Q2", Question = Loc.T("preview_q2"), FromBrief = true },
                    },
                    NextSteps = { new CoachNextStep { At = 739, Time = "00:12:19", Action = Loc.T("preview_next"), Who = "Lan", When = Loc.T("preview_when") } },
                    Notes =
                    {
                        new CoachEntry { At = 721, Time = "00:12:01", Text = Loc.T("preview_note1") },
                        new CoachEntry { At = 727, Time = "00:12:07", Text = Loc.T("preview_note2") },
                    },
                    Calls = 14,
                    InputTokens = 41200,
                    OutputTokens = 2300,
                    CacheReadTokens = 18400,
                    Status = Loc.F("coach_last_answer", "00:12:19"),
                });
                UpdateCoachTexts();
            }
            if (state is "recording" or "coach")
            {
                IsRecording = true;
                Say(nameof(StatusText), () => Loc.T("status_recording"));
                Say(nameof(HintText), () => Loc.F("src_mic_short", "Microphone Array") + "   " + Loc.F("src_audio_short", "Speakers"));
                ElapsedText = "00:12:41";
                MicLevel = 0.35f;
                SpeakerLevel = 0.6f;
                LiveLag = LivePolicy.LagText(0);
                Say(nameof(LiveStatus), () => Loc.F("live_text_is", LiveEngineText));
                UpdateLanguagePicked(Loc.F("lang_detected", Lang.Display("vi")));
                foreach (var l in new[]
                         {
                             new LiveLine(TimeSpan.FromSeconds(721), TimeSpan.FromSeconds(726), "Others", "Bây giờ mình xem lại phần retry của luồng gọi ra nhé."),
                             new LiveLine(TimeSpan.FromSeconds(727), TimeSpan.FromSeconds(731), "You", "Dạ, phần retry em đã sửa theo góp ý hôm trước rồi anh."),
                             new LiveLine(TimeSpan.FromSeconds(733), TimeSpan.FromSeconds(739), "Others", "[?] Ok, vậy em demo lại cho anh xem trường hợp mất kết nối."),
                             new LiveLine(TimeSpan.FromSeconds(740), TimeSpan.FromSeconds(744), "You", "Ok, vậy em demo lại cho anh xem.", Echo: true),
                             new LiveLine(TimeSpan.FromSeconds(745), TimeSpan.FromSeconds(745), "RecBox", $"({Loc.F("live_catching_up", LivePolicy.LagText(9), 3)})", IsNote: true),
                         })
                    LiveLines.Add(LiveLineItem.From(l));
            }
            else if (state == "transcribing")
            {
                Say(nameof(StatusText), () => Loc.T("status_saved"));
                Say(nameof(HintText), () => Loc.T("hint_again"));
                ElapsedText = "00:18:04";
                LastSessionDir = Path.Combine(OutputFolder, "2026-11-19_10-16-03");
                Say(nameof(LastSessionTitle), () => Loc.F("saved_title", "2026-11-19_10-16-03"));
                Say(nameof(LastSessionDetail), () => Loc.F("saved_detail", "00:18:04", 8));
                IsTranscribing = true;
                Say(nameof(TranscribeProgress), () => Loc.F("tx_transcribing", Loc.T("tx_track_speaker")));
                Say(nameof(TranscriptText), () => Loc.F("tx_transcribing", Loc.T("tx_track_speaker")));
                Say(nameof(LiveLag), () => Loc.T("lag_stopped"));
                Say(nameof(LiveStatus), () => Loc.F("live_saved", 214));
            }
        }
        finally
        {
            _loading = false;
        }
    }
}

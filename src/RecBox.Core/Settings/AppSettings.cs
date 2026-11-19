using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecBox.Core.Settings;

public enum TranscribeMode
{
    Transcribe,
    TranslateToEnglish,
}

public sealed class AppSettings
{
    public string OutputFolder { get; set; } = AppPaths.DefaultRecordingsDir;

    public bool MicEnabled { get; set; } = true;
    public bool SpeakerEnabled { get; set; } = true;

    /// <summary>Endpoint id, or null to follow the Windows default device (switches mid-recording).</summary>
    public string? MicDeviceId { get; set; }
    public string? SpeakerDeviceId { get; set; }

    /// <summary>Also write 16 kHz mono copies (small, what speech models expect).</summary>
    public bool SaveSpeechCopy { get; set; } = true;

    /// <summary>When both sources record, also write one mixed track.</summary>
    public bool SaveMixed { get; set; } = true;

    /// <summary>Raise the captured mic to its maximum while recording, restored afterwards.</summary>
    public bool BoostMicWhileRecording { get; set; } = true;

    /// <summary>Write mic_16k and mixed_16k high-passed and peak-normalized; the full-rate WAVs stay raw.</summary>
    public bool CleanSpeechCopies { get; set; } = true;

    public bool TranscribeEnabled { get; set; }
    /// <summary>"auto" (pick for this PC) or a model key: tiny, base, small, medium, large-v3-turbo.</summary>
    public string WhisperModel { get; set; } = "auto";

    /// <summary>Live text while recording. Null follows TranscribeEnabled (off unless transcription is on).</summary>
    public bool? LiveTranscribe { get; set; }
    /// <summary>Legacy (1.3.0 and before): live text in English. Read once into <see cref="TranslateTo"/> = "en".</summary>
    public bool LiveTranslate { get; set; }
    /// <summary>
    /// "Translate to": "" (off), "en" (whisper's own translation, offline), or vi / ja / zh / ko / fr / de / es (text
    /// translation by the AI picked for the coach). The default for new recordings; one recording may override it.
    /// </summary>
    public string TranslateTo { get; set; } = "";
    /// <summary>The interface language: "en", "vi", or null to follow the Windows display language.</summary>
    public string? UiLanguage { get; set; }

    /// <summary>True when whisper itself translates (Translate to English).</summary>
    [JsonIgnore]
    public bool WhisperTranslates => Coaching.TranslateTargets.IsEnglish(TranslateTo);
    /// <summary>The live text side panel of the main window is open (remembered between runs).</summary>
    public bool LivePanelOpen { get; set; } = true;
    /// <summary>Width of the live text side panel, in device-independent pixels.</summary>
    public double LivePanelWidth { get; set; } = 400;

    [JsonIgnore]
    public bool LiveTranscribeOn => LiveTranscribe ?? TranscribeEnabled;

    /// <summary>Measured speed per runtime and model ("gpu:base" = seconds of audio per second of work).</summary>
    public Dictionary<string, double> Speeds { get; set; } = new();
    /// <summary>Legacy: TranslateToEnglish is read once into <see cref="TranslateTo"/> = "en"; RecBox writes Transcribe.</summary>
    public TranscribeMode Mode { get; set; } = TranscribeMode.Transcribe;
    /// <summary>"auto" (detect once per recording, then keep), a Whisper language code such as "en", "vi", "ja", or "vi+en".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Try the GPU (Vulkan) first, CPU otherwise. Turn off if a graphics driver misbehaves.</summary>
    public bool UseGpu { get; set; } = true;
    /// <summary>Why RecBox turned UseGpu off by itself (Vulkan failed or crashed), shown in Settings.</summary>
    public string? GpuDisabledReason { get; set; }

    public bool CheckUpdatesOnStart { get; set; } = true;
    /// <summary>Overrides the update feed baked in at build time (GitHub repo URL or a folder URL).</summary>
    public string? UpdateUrl { get; set; }

    /// <summary>The live coach: off by default, its AI tool and model, and the last meeting setup (no keys anywhere).</summary>
    public Coaching.CoachSettings Coach { get; set; } = new();

    /// <summary>
    /// Settings of older versions: "Translate to English" (Mode or the live toggle) becomes Translate to = English; the
    /// removed Anthropic API choice becomes Claude Code. Returns true when something changed.
    /// </summary>
    public bool Migrate()
    {
        var changed = false;
        Coach ??= new Coaching.CoachSettings();
        if (Coach.Provider == Coaching.CoachProviderKind.Anthropic) changed = true;
        Coach.Migrate();
        if (Mode == TranscribeMode.TranslateToEnglish || LiveTranslate)
        {
            if (string.IsNullOrEmpty(TranslateTo)) TranslateTo = "en";
            Mode = TranscribeMode.Transcribe;
            LiveTranslate = false;
            changed = true;
        }
        var t = Coaching.TranslateTargets.Normalize(TranslateTo);
        if (t != (TranslateTo ?? ""))
        {
            TranslateTo = t;
            changed = true;
        }
        if (UiLanguage is { } ui && ui is not ("en" or "vi"))
        {
            UiLanguage = null;
            changed = true;
        }
        return changed;
    }

    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.Speeds = new Dictionary<string, double>(Speeds);
        c.Coach = (Coach ?? new Coaching.CoachSettings()).Clone();
        return c;
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options);
                if (s is not null)
                {
                    if (string.IsNullOrWhiteSpace(s.OutputFolder)) s.OutputFolder = AppPaths.DefaultRecordingsDir;
                    if (s.Migrate()) Log.Info("settings of an older version moved to Translate to / Claude Code");
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("settings unreadable, using defaults", ex);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.Home);
        var tmp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
    }
}

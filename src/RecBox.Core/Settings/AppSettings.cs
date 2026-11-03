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

    public bool TranscribeEnabled { get; set; }
    public string WhisperModel { get; set; } = "base";
    public TranscribeMode Mode { get; set; } = TranscribeMode.Transcribe;
    /// <summary>"auto" or a Whisper language code such as "en", "vi", "ja".</summary>
    public string Language { get; set; } = "auto";
    /// <summary>Try the GPU (Vulkan) first, CPU otherwise. Turn off if a graphics driver misbehaves.</summary>
    public bool UseGpu { get; set; } = true;
    /// <summary>Why RecBox turned UseGpu off by itself (Vulkan failed or crashed), shown in Settings.</summary>
    public string? GpuDisabledReason { get; set; }

    public bool CheckUpdatesOnStart { get; set; } = true;
    /// <summary>Overrides the update feed baked in at build time (GitHub repo URL or a folder URL).</summary>
    public string? UpdateUrl { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
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

using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecBox.Core.Session;

/// <summary>One stretch of a source recorded on one device.</summary>
public sealed class SegmentInfo
{
    public int Index { get; set; }
    public string Device { get; set; } = "";
    public string DeviceId { get; set; } = "";
    /// <summary>Why this segment started: start, default device changed, device lost, reopened.</summary>
    public string Reason { get; set; } = "";
    /// <summary>Seconds from the session start.</summary>
    public double StartSeconds { get; set; }
    public double DurationSeconds { get; set; }
    /// <summary>Temporary part file; merged into the track file when the session is saved.</summary>
    public string File { get; set; } = "";
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public string Encoding { get; set; } = "";
}

public sealed class SourceTrack
{
    /// <summary>"mic" or "speaker".</summary>
    public string Kind { get; set; } = "";
    /// <summary>Who this track is, for a reader: "You (microphone)" or "Others (system audio)".</summary>
    public string Label { get; set; } = "";
    public string File { get; set; } = "";
    public string? SpeechFile { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public double DurationSeconds { get; set; }
    public List<SegmentInfo> Segments { get; set; } = new();
}

public sealed class FileEntry
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class LanguageInfo
{
    /// <summary>The setting at Stop: "auto", a code, or "vi+en".</summary>
    public string Chosen { get; set; } = "auto";
    /// <summary>What live text detected (Auto), null when it did not run or heard nothing.</summary>
    public string? Detected { get; set; }
    /// <summary>What the transcript after Stop uses.</summary>
    public string Used { get; set; } = "auto";
}

public sealed class TranscriptInfo
{
    /// <summary>off, pending, done, failed, model-missing, interrupted (RecBox closed before it finished).</summary>
    public string Status { get; set; } = "off";
    public string? Model { get; set; }
    public string? Mode { get; set; }
    /// <summary>The language the transcript was decoded in (a code, or "vi+en").</summary>
    public string? Language { get; set; }
    /// <summary>The language setting it came from: "auto" (then <see cref="Language"/> is what was detected), a code, or "vi+en".</summary>
    public string? LanguageSetting { get; set; }
    public string? Runtime { get; set; }
    /// <summary>The graphics card it ran on (null on the processor).</summary>
    public string? Device { get; set; }
    /// <summary>Exit code of the transcription worker process (0 = finished; a Windows crash code otherwise).</summary>
    public int? ExitCode { get; set; }
    /// <summary>True when the graphics card (Vulkan) failed or was missing and the processor was used.</summary>
    public bool? GpuFailed { get; set; }
    public string? RuntimeNote { get; set; }
    public double? Seconds { get; set; }
    public List<string> Files { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>What was done to the 16 kHz copies. Absent when nothing was processed; the full-rate files are always raw.</summary>
public sealed class SpeechProcessingInfo
{
    public double HighPassHz { get; set; }
    public double CeilingDbfs { get; set; }
    public double MaxGainDb { get; set; }
    public List<ProcessedFile> Files { get; set; } = new();
}

public sealed class ProcessedFile
{
    public string File { get; set; } = "";
    /// <summary>processed; silent (every sample is zero, plain copy kept); failed (plain copy kept, see Error).</summary>
    public string Status { get; set; } = "";
    public double? GainDb { get; set; }
    public double? InputPeakDbfs { get; set; }
    public double? MicGainDb { get; set; }
    public double? SpeakerGainDb { get; set; }
    public double? MicInputPeakDbfs { get; set; }
    public double? SpeakerInputPeakDbfs { get; set; }
    /// <summary>True when the gain hit its maximum, so the output peak is under the ceiling.</summary>
    public bool? GainCapped { get; set; }
    public double? OutputPeakDbfs { get; set; }
    /// <summary>True when the limiter had to act on at least one sample.</summary>
    public bool? Limited { get; set; }
    public string? Error { get; set; }
}

/// <summary>What the live coach did for this recording; its notes are coach.md and coach.json beside it.</summary>
public sealed class CoachInfo
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>True when the transcript was sent outside this PC (a cloud provider).</summary>
    public bool SentOut { get; set; }
    public int Calls { get; set; }
    public int Failures { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    /// <summary>Input tokens the tool read from its cache (when it reports them).</summary>
    public long CachedTokens { get; set; }
    /// <summary>True when some call reported no usage and RecBox counted chars / 4 (no prices are kept anywhere).</summary>
    public bool TokensEstimated { get; set; }
    public int Cues { get; set; }
    public int Questions { get; set; }
    public int QuestionsAnswered { get; set; }
    public int NextSteps { get; set; }
    public string? Goal { get; set; }
    public List<string> Files { get; set; } = new();
}

/// <summary>The translation of live text and the transcript ("Translate to" another language than English, by the picked AI).</summary>
public sealed class TranslationInfo
{
    /// <summary>The language code: vi, ja, zh, ko, fr, de, es (English is whisper's own translation, in Transcript).</summary>
    public string To { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>True when the text went outside this PC (an AI tool's vendor).</summary>
    public bool SentOut { get; set; }
    public int LiveLines { get; set; }
    public int TranscriptLines { get; set; }
    public int Calls { get; set; }
    public int Failures { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public bool TokensEstimated { get; set; }
    /// <summary>The translated transcript, transcript.&lt;lang&gt;.md (absent when it failed or there was no transcript).</summary>
    public string? File { get; set; }
    public string? Error { get; set; }
}

/// <summary>Slides captured from the meeting window (slides\slide-hh-mm-ss.png, time from the recording start).</summary>
public sealed class SlidesInfo
{
    public string Window { get; set; } = "";
    public List<string> Files { get; set; } = new();
}

public sealed class SessionManifest
{
    public const string FileName = "session.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string App { get; set; } = "RecBox";
    public string AppVersion { get; set; } = "";
    public string Id { get; set; } = "";
    public DateTimeOffset StartedLocal { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
    public double DurationSeconds { get; set; }
    public List<SourceTrack> Sources { get; set; } = new();
    public string? MixedFile { get; set; }
    public string? MixedSpeechFile { get; set; }
    public SpeechProcessingInfo? SpeechProcessing { get; set; }
    /// <summary>Every file in the folder and what it holds, for whoever (or whatever LLM) reads it later.</summary>
    public List<FileEntry> Files { get; set; } = new();
    public TranscriptInfo Transcript { get; set; } = new();
    /// <summary>The recording's language: what was chosen (the picker at Stop) and what live text detected.</summary>
    public LanguageInfo? Language { get; set; }
    /// <summary>Who the tracks are and the name spellings from the meeting brief (absent when the brief names nobody); the saved texts use them.</summary>
    public Coaching.SpeakerNames? Speakers { get; set; }
    /// <summary>The live coach (absent when it was off).</summary>
    public CoachInfo? Coach { get; set; }
    /// <summary>The translation to another language than English (absent when Translate to was Off or English).</summary>
    public TranslationInfo? Translation { get; set; }
    /// <summary>Slides captured during the recording (absent when slide capture was off or saved nothing).</summary>
    public SlidesInfo? Slides { get; set; }
    public List<string> Warnings { get; set; } = new();

    public void Save(string dir)
    {
        var path = Path.Combine(dir, FileName);
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        System.IO.File.Move(tmp, path, overwrite: true);
    }

    public static SessionManifest? Load(string dir)
    {
        try
        {
            var path = Path.Combine(dir, FileName);
            return System.IO.File.Exists(path)
                ? JsonSerializer.Deserialize<SessionManifest>(System.IO.File.ReadAllText(path), Options)
                : null;
        }
        catch (Exception ex)
        {
            Log.Error($"unreadable {FileName} in {dir}", ex);
            return null;
        }
    }
}

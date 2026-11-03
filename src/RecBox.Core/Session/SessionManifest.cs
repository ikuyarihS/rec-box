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

public sealed class TranscriptInfo
{
    /// <summary>off, pending, done, failed, model-missing, interrupted (RecBox closed before it finished).</summary>
    public string Status { get; set; } = "off";
    public string? Model { get; set; }
    public string? Mode { get; set; }
    public string? Language { get; set; }
    public string? Runtime { get; set; }
    public double? Seconds { get; set; }
    public List<string> Files { get; set; } = new();
    public string? Error { get; set; }
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
    /// <summary>Every file in the folder and what it holds, for whoever (or whatever LLM) reads it later.</summary>
    public List<FileEntry> Files { get; set; } = new();
    public TranscriptInfo Transcript { get; set; } = new();
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

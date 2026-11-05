using System.Reflection;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using RecBox.Core.Session;

namespace RecBox.Core.Audio;

/// <summary>
/// Turns a stopped session's segment files into the final folder:
/// mic.wav / speaker.wav (full quality, 16-bit), optional *_16k.wav (16 kHz mono for speech models),
/// optional mixed.wav, and session.json describing all of it.
/// </summary>
public static class SessionFinalizer
{
    public const int SpeechRate = 16000;
    private const int MixRate = 48000;

    public static string AppVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(SessionFinalizer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static SessionManifest Finalize(RecordingSession session, IProgress<string>? progress = null)
    {
        var dir = session.Directory;
        var settings = session.Settings;
        var m = new SessionManifest
        {
            AppVersion = AppVersion,
            Id = session.Id,
            StartedLocal = session.StartedLocal,
            StartedUtc = session.StartedUtc,
            EndedUtc = session.EndedUtc,
            DurationSeconds = Math.Round(session.DurationSeconds, 3),
            Transcript = new TranscriptInfo { Status = settings.TranscribeEnabled ? "pending" : "off" },
        };
        m.Warnings.AddRange(session.Warnings);

        foreach (var rec in session.Recorders)
        {
            progress?.Report(rec.Kind == SourceKind.Mic ? "Saving microphone track" : "Saving system audio track");
            try
            {
                var track = BuildTrack(dir, rec, session.DurationSeconds, settings.SaveSpeechCopy);
                m.Sources.Add(track);
                RemoveParts(dir, rec.Segments);
            }
            catch (Exception ex)
            {
                Log.Error($"{rec.KindName}: merging segments failed, part files kept", ex);
                m.Warnings.Add($"{rec.KindName}: could not merge segments ({ex.Message}); raw part files kept in the folder");
            }
        }

        var cleaned = settings.CleanSpeechCopies ? SpeechProcessor.NewInfo() : null;

        if (settings.SaveMixed && m.Sources.Count == 2)
        {
            progress?.Report("Saving mixed track");
            try
            {
                Mix(m.Sources.Select(s => Path.Combine(dir, s.File)), Path.Combine(dir, "mixed.wav"), MixRate, 2);
                m.MixedFile = "mixed.wav";
                if (settings.SaveSpeechCopy && m.Sources.All(s => s.SpeechFile is not null))
                {
                    // from the plain 16 kHz copies, so this runs before mic_16k.wav is cleaned in place
                    WriteMixedSpeech(m, dir, cleaned);
                    m.MixedSpeechFile = "mixed_16k.wav";
                }
            }
            catch (Exception ex)
            {
                Log.Error("mixing failed", ex);
                m.Warnings.Add($"mixed track not written ({ex.Message})");
            }
        }

        if (cleaned is not null && m.Sources.FirstOrDefault(s => s.Kind == "mic") is { SpeechFile: { } micSpeech })
        {
            progress?.Report("Cleaning up the speech copy");
            var path = Path.Combine(dir, micSpeech);
            Note(m, cleaned, SpeechProcessor.Normalize(path, path));
        }
        if (cleaned?.Files.Count > 0) m.SpeechProcessing = cleaned;

        DescribeFiles(m);
        m.Save(dir);
        Log.Info($"session {m.Id} saved: {string.Join(", ", m.Files.Select(f => f.Name))}");
        return m;
    }

    private static SourceTrack BuildTrack(string dir, SourceRecorder rec, double sessionSeconds, bool speechCopy)
    {
        var segs = rec.Segments.Where(s => File.Exists(Path.Combine(dir, s.File))).OrderBy(s => s.StartSeconds).ToList();
        if (segs.Count == 0) throw new InvalidOperationException("no segment files");

        var rate = segs[0].SampleRate;
        var channels = Math.Min(2, segs[0].Channels);
        var silenceFmt = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        var readers = new List<WaveFileReader>();
        try
        {
            var parts = new List<ISampleProvider>();
            double cursor = 0;
            foreach (var seg in segs)
            {
                var gap = seg.StartSeconds - cursor;
                if (gap > 0.001) parts.Add(new SilenceSampleProvider(silenceFmt, gap));
                var reader = new WaveFileReader(Path.Combine(dir, seg.File));
                readers.Add(reader);
                var real = reader.TotalTime.TotalSeconds;
                seg.DurationSeconds = Math.Round(real, 3);
                parts.Add(Pipe.Shape(reader.ToSampleProvider(), rate, channels));
                cursor = Math.Max(cursor, seg.StartSeconds) + real;
            }
            var tail = sessionSeconds - cursor;
            if (tail > 0.001) parts.Add(new SilenceSampleProvider(silenceFmt, tail));

            var file = $"{rec.KindName}.wav";
            WaveFileWriter.CreateWaveFile16(Path.Combine(dir, file), new ConcatenatingSampleProvider(parts));

            var track = new SourceTrack
            {
                Kind = rec.KindName,
                Label = rec.Kind == SourceKind.Mic ? "You (microphone)" : "Others (system audio)",
                File = file,
                SampleRate = rate,
                Channels = channels,
                Segments = segs,
            };
            foreach (var r in readers) r.Dispose();
            readers.Clear();

            using (var check = new WaveFileReader(Path.Combine(dir, file)))
                track.DurationSeconds = Math.Round(check.TotalTime.TotalSeconds, 3);

            if (speechCopy)
            {
                track.SpeechFile = $"{rec.KindName}_16k.wav";
                using var src = new WaveFileReader(Path.Combine(dir, file));
                WaveFileWriter.CreateWaveFile16(Path.Combine(dir, track.SpeechFile), Pipe.Shape(src.ToSampleProvider(), SpeechRate, 1));
            }
            return track;
        }
        finally
        {
            foreach (var r in readers) r.Dispose();
        }
    }

    /// <summary>Writes a 16 kHz mono 16-bit copy of any WAV file (what Whisper and most ASR expect).</summary>
    public static void WriteSpeechCopy(string source, string target)
    {
        using var src = new WaveFileReader(source);
        WaveFileWriter.CreateWaveFile16(target, Pipe.Shape(src.ToSampleProvider(), SpeechRate, 1));
    }

    private static void Mix(IEnumerable<string> files, string target, int rate, int channels)
    {
        var readers = files.Select(f => new WaveFileReader(f)).ToList();
        try
        {
            var mixer = new MixingSampleProvider(readers.Select(r => Pipe.Shape(r.ToSampleProvider(), rate, channels)));
            WaveFileWriter.CreateWaveFile16(target, mixer);
        }
        finally
        {
            foreach (var r in readers) r.Dispose();
        }
    }

    private static void WriteMixedSpeech(SessionManifest m, string dir, SpeechProcessingInfo? cleaned)
    {
        var target = Path.Combine(dir, "mixed_16k.wav");
        var plain = m.Sources.Select(s => Path.Combine(dir, s.SpeechFile!)).ToList();
        if (cleaned is not null)
        {
            var mic = m.Sources.Single(s => s.Kind == "mic");
            var speaker = m.Sources.Single(s => s.Kind == "speaker");
            var result = SpeechProcessor.MixNormalized(Path.Combine(dir, mic.SpeechFile!), Path.Combine(dir, speaker.SpeechFile!), target);
            Note(m, cleaned, result);
            if (result.Status == SpeechProcessor.Processed) return;
        }
        Mix(plain, target, SpeechRate, 1);
    }

    private static void Note(SessionManifest m, SpeechProcessingInfo cleaned, ProcessedFile result)
    {
        cleaned.Files.Add(result);
        Log.Info($"speech copy {result.File}: {result.Status}, gain {result.GainDb?.ToString() ?? $"mic {result.MicGainDb} speaker {result.SpeakerGainDb}"} dB, output peak {result.OutputPeakDbfs} dBFS");
        if (result.Status == SpeechProcessor.Failed)
            m.Warnings.Add($"{result.File}: could not be cleaned up, the plain copy was kept ({result.Error})");
    }

    private static void RemoveParts(string dir, IEnumerable<SegmentInfo> segs)
    {
        var removed = new List<string>();
        foreach (var s in segs)
        {
            var p = Path.Combine(dir, s.File);
            try
            {
                if (File.Exists(p))
                {
                    var size = new FileInfo(p).Length;
                    File.Delete(p);
                    removed.Add($"{s.File} ({size} bytes)");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"could not remove part file {p}", ex);
            }
        }
        if (removed.Count > 0) Log.Info($"removed merged part files in {dir}: {string.Join(", ", removed)}");
    }

    private static void DescribeFiles(SessionManifest m)
    {
        // says "cleaned" only for a file that really was: a failed or silent one is still the plain copy
        string Clean(string file, string how) =>
            m.SpeechProcessing?.Files.Any(f => f.File == file && f.Status == SpeechProcessor.Processed) == true ? $", {how}" : "";

        foreach (var s in m.Sources)
        {
            var who = s.Kind == "mic" ? "your microphone (you)" : "system audio (the other side of a call, videos, anything playing)";
            m.Files.Add(new FileEntry { Name = s.File, Description = $"{who}, {s.SampleRate} Hz, {s.Channels} ch, 16-bit, raw" });
            if (s.SpeechFile is not null)
                m.Files.Add(new FileEntry { Name = s.SpeechFile, Description = $"{who}, 16 kHz mono{Clean(s.SpeechFile, "hum filtered and peak-normalized")}, for speech recognition or an LLM" });
        }
        if (m.MixedFile is not null)
            m.Files.Add(new FileEntry { Name = m.MixedFile, Description = "microphone and system audio mixed, 48 kHz stereo, raw" });
        if (m.MixedSpeechFile is not null)
            m.Files.Add(new FileEntry { Name = m.MixedSpeechFile, Description = $"microphone and system audio mixed, 16 kHz mono{Clean(m.MixedSpeechFile, "hum filtered, each source peak-normalized, then limited")}, for speech recognition or an LLM" });
        m.Files.Add(new FileEntry { Name = SessionManifest.FileName, Description = "this file: times, devices, device switches, file list" });
    }
}

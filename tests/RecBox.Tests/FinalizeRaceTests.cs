using System.Diagnostics;
using NAudio.Wave;
using RecBox.Core.Audio;
using RecBox.Core.Session;
using RecBox.Core.Settings;
using RecBox.Core.Transcription;
using Xunit;

namespace RecBox.Tests;

/// <summary>
/// The window closed while "Saving": the Stop path and the close path both save the same session.
/// Exactly one finalize may run; session.json must list both tracks plus mixed.wav and mixed_16k.wav.
/// </summary>
public sealed class FinalizeRaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recbox-tests", Guid.NewGuid().ToString("N"));

    public FinalizeRaceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Theory]
    [InlineData(40, 100)]
    [InlineData(40, 5)]
    public async Task CloseWhileSaving_RunsOneFinalize_AndKeepsBothTracksAndMix(int seconds, int closeAfterMs)
    {
        var dir = Path.Combine(_root, $"race-{seconds}-{closeAfterMs}");
        Directory.CreateDirectory(dir);
        var settings = new AppSettings
        {
            OutputFolder = _root,
            MicEnabled = true,
            SpeakerEnabled = true,
            SaveSpeechCopy = true,
            SaveMixed = true,
            TranscribeEnabled = false,
        };
        var clock = Stopwatch.StartNew();
        var mic = PartRecorder(SourceKind.Mic, dir, clock, seconds, 440);
        var speaker = PartRecorder(SourceKind.Speaker, dir, clock, seconds, 660);
        var session = RecordingSession.FromParts(dir, settings, mic, speaker);

        // Stop button: the save starts on the thread pool
        var stopSave = session.StopAndSaveAsync(new Progress<string>(_ => { }));
        await Task.Delay(closeAfterMs);
        // window closed while Saving: the close path asks for the save from another thread
        Task<SessionManifest>? closeSave = null;
        await Task.Run(() => { closeSave = session.StopAndSaveAsync(); });

        Assert.Same(stopSave, closeSave);
        var fromStop = await stopSave;
        var fromClose = await closeSave!;
        Assert.Same(fromStop, fromClose);

        var m = SessionManifest.Load(dir);
        Assert.NotNull(m);
        Assert.Equal(new[] { "mic", "speaker" }, m!.Sources.Select(s => s.Kind).OrderBy(k => k).ToArray());
        Assert.Equal("mixed.wav", m.MixedFile);
        Assert.Equal("mixed_16k.wav", m.MixedSpeechFile);
        Assert.Empty(m.Warnings);
        foreach (var name in new[] { "mic.wav", "mic_16k.wav", "speaker.wav", "speaker_16k.wav", "mixed.wav", "mixed_16k.wav" })
        {
            Assert.True(File.Exists(Path.Combine(dir, name)), $"{name} missing");
            Assert.Contains(m.Files, f => f.Name == name);
        }
        foreach (var t in m.Sources) Assert.InRange(t.DurationSeconds, seconds - 0.1, seconds + 0.1);
        Assert.Empty(Directory.GetFiles(dir, "*.part*.wav"));
    }

    [Fact]
    public void MarkInterrupted_TurnsPendingIntoInterruptedWithReason_AndLeavesOthersAlone()
    {
        var pending = Path.Combine(_root, "pending");
        var done = Path.Combine(_root, "done");
        Directory.CreateDirectory(pending);
        Directory.CreateDirectory(done);
        new SessionManifest { Id = "pending", Transcript = new TranscriptInfo { Status = "pending" } }.Save(pending);
        new SessionManifest { Id = "done", Transcript = new TranscriptInfo { Status = "done" } }.Save(done);

        Assert.True(TranscriptionService.MarkInterrupted(pending, "closed"));
        Assert.False(TranscriptionService.MarkInterrupted(done, "closed"));

        var p = SessionManifest.Load(pending)!;
        Assert.Equal("interrupted", p.Transcript.Status);
        Assert.Equal("closed", p.Transcript.Error);
        Assert.Equal("done", SessionManifest.Load(done)!.Transcript.Status);
    }

    /// <summary>A recorder that never opened a device, with one part file of a tone already on disk.</summary>
    private static SourceRecorder PartRecorder(SourceKind kind, string dir, Stopwatch clock, int seconds, double hz)
    {
        const int rate = 48000;
        const int channels = 2;
        var rec = new SourceRecorder(kind, null, dir, clock);
        var file = $"{rec.KindName}.part1.wav";
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
        using (var w = new WaveFileWriter(Path.Combine(dir, file), fmt))
        {
            var buf = new float[rate * channels];
            for (var s = 0; s < seconds; s++)
            {
                for (var i = 0; i < rate; i++)
                {
                    var v = (float)(0.2 * Math.Sin(2 * Math.PI * hz * (s * rate + i) / rate));
                    buf[2 * i] = v;
                    buf[2 * i + 1] = v;
                }
                w.WriteSamples(buf, 0, buf.Length);
            }
        }
        rec.Segments.Add(new SegmentInfo
        {
            Index = 1,
            Device = "test",
            DeviceId = "test",
            Reason = "start",
            StartSeconds = 0,
            DurationSeconds = seconds,
            File = file,
            SampleRate = rate,
            Channels = channels,
            Encoding = "IeeeFloat 32-bit",
        });
        return rec;
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;
using RecBox.Core.Audio;
using RecBox.Core.Session;
using RecBox.Core.Settings;
using Xunit;

namespace RecBox.Tests;

public sealed class SpeechProcessorTests : IDisposable
{
    private const int Rate = 16000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "recbox-tests", Guid.NewGuid().ToString("N"));

    public SpeechProcessorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static double Db(double linear) => 20 * Math.Log10(linear);
    private static double FromDb(double db) => Math.Pow(10, db / 20);
    private static double Ceiling => FromDb(SpeechProcessor.CeilingDbfs);

    private string Wav(string name, double seconds, Func<double, double> at, int rate = Rate, int channels = 1)
    {
        var path = Path.Combine(_root, name);
        var count = (int)(seconds * rate);
        var buf = new float[count * channels];
        for (var i = 0; i < count; i++)
        {
            var v = (float)at((double)i / rate);
            for (var c = 0; c < channels; c++) buf[i * channels + c] = v;
        }
        using var w = new WaveFileWriter(path, new WaveFormat(rate, 16, channels));
        w.WriteSamples(buf, 0, buf.Length);
        return path;
    }

    /// <summary>A sine whose RMS is the given level.</summary>
    private static Func<double, double> Sine(double rmsDb, double hz = 1000) =>
        t => FromDb(rmsDb) * Math.Sqrt(2) * Math.Sin(2 * Math.PI * hz * t);

    private static float[] Samples(string path)
    {
        using var r = new WaveFileReader(path);
        var s = r.ToSampleProvider();
        var all = new List<float>();
        var buf = new float[8192];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.Take(n));
        return all.ToArray();
    }

    private static double Rms(IEnumerable<float> xs)
    {
        var a = xs.ToArray();
        return Db(Math.Sqrt(a.Sum(x => (double)x * x) / a.Length));
    }

    private static double RmsRange(float[] s, double from, double to) =>
        Rms(s.Skip((int)(from * Rate)).Take((int)((to - from) * Rate)));

    // ---------- normalize ----------
    [Theory]
    [InlineData(-45)]
    [InlineData(-30)]
    [InlineData(-20)]
    [InlineData(-8)]
    public void Normalize_HitsTheTarget(double inputDb)
    {
        var src = Wav("in.wav", 3, Sine(inputDb));
        var dst = Path.Combine(_root, "out.wav");

        var r = SpeechProcessor.Normalize(src, dst);

        Assert.Equal(SpeechProcessor.Processed, r.Status);
        var measured = Rms(Samples(dst));
        Assert.InRange(measured, SpeechProcessor.TargetRmsDbfs - 1, SpeechProcessor.TargetRmsDbfs + 1);
        Assert.InRange(r.OutputRmsDbfs!.Value, measured - 1, measured + 1);
        Assert.InRange(r.GainDb!.Value, SpeechProcessor.TargetRmsDbfs - inputDb - 1, SpeechProcessor.TargetRmsDbfs - inputDb + 1);
        Assert.False(r.Limited);
    }

    [Fact]
    public void Normalize_MeasuresSpeechNotThePauses()
    {
        // 25% speech at -40 dBFS, the rest silence: a plain file RMS would ask for far too much gain
        var src = Wav("in.wav", 8, t => (t % 4) < 1 ? Sine(-40)(t) : 0);
        var dst = Path.Combine(_root, "out.wav");

        SpeechProcessor.Normalize(src, dst);

        var s = Samples(dst);
        Assert.InRange(RmsRange(s, 0.1, 0.9), -21, -19);
    }

    [Fact]
    public void Normalize_KeepsLengthAndAlignment()
    {
        var src = Wav("in.wav", 2, t => t is >= 1 and < 1.5 ? Sine(-30)(t) : 0, 48000, 2);
        var dst = Path.Combine(_root, "out.wav");

        SpeechProcessor.Normalize(src, dst);

        var s = Samples(dst);
        Assert.InRange(s.Length, 2 * Rate - 200, 2 * Rate + 200);
        Assert.InRange(RmsRange(s, 1.1, 1.4), -21, -19);
        Assert.True(RmsRange(s, 0.2, 0.9) < -80, "the delay line moved audio into the silence before it");
    }

    public static TheoryData<string, Func<double, double>> Clippers => new()
    {
        { "full scale sine", t => 0.999 * Math.Sin(2 * Math.PI * 1000 * t) },
        { "full scale square", t => Math.Sin(2 * Math.PI * 500 * t) > 0 ? 0.999 : -0.999 },
        { "quiet speech with loud clicks", t => (t % 0.5) < 0.0005 ? 0.99 : Sine(-40)(t) },
        { "one click in silence", t => Math.Abs(t - 1) < 0.0005 ? 0.99 : 0 },
        { "quiet speech and one loud burst", t => t is > 1 and < 1.1 ? 0.95 * Math.Sin(2 * Math.PI * 300 * t) : Sine(-45)(t) },
        { "noise bursts", t => Math.Sin(t * 12345.678) * Math.Sin(t * 987.65) * ((t % 0.3) < 0.1 ? 1 : 0.003) },
    };

    [Theory]
    [MemberData(nameof(Clippers))]
    public void Normalize_NeverClips(string name, Func<double, double> signal)
    {
        var src = Wav($"{name}.wav", 3, signal);
        var dst = Path.Combine(_root, $"{name}.out.wav");

        var r = SpeechProcessor.Normalize(src, dst);

        Assert.Equal(SpeechProcessor.Processed, r.Status);
        var peak = Samples(dst).Max(Math.Abs);
        Assert.True(peak <= Ceiling + 1.0 / 32768, $"{name}: peak {Db(peak):0.00} dBFS");
        Assert.True(r.OutputPeakDbfs <= SpeechProcessor.CeilingDbfs + 0.1);
    }

    [Fact]
    public void Normalize_LimiterDoesNotPullSpeechOffTheTarget()
    {
        var src = Wav("in.wav", 4, t => (t % 1) < 0.0005 ? 0.99 : Sine(-40)(t));
        var dst = Path.Combine(_root, "out.wav");

        var r = SpeechProcessor.Normalize(src, dst);

        Assert.True(r.Limited);
        Assert.InRange(Rms(Samples(dst)), -21.5, -18.5);
    }

    [Fact]
    public void Normalize_ALongThump_DoesNotHideTheQuietSpeechFromTheMeasure()
    {
        // 300 ms near full scale in 8 s of speech at -40 dBFS: the speech is more than 30 dB under the thump
        var src = Wav("in.wav", 8, t => t is > 4 and < 4.3 ? 0.9 * Math.Sin(2 * Math.PI * 200 * t) : Sine(-40)(t));
        var dst = Path.Combine(_root, "out.wav");

        var r = SpeechProcessor.Normalize(src, dst);

        var s = Samples(dst);
        Assert.InRange(RmsRange(s, 0.5, 3.5), -21, -19);
        Assert.InRange(RmsRange(s, 5, 7.5), -21, -19);
        Assert.True(r.Limited);
        Assert.True(s.Max(Math.Abs) <= Ceiling + 1.0 / 32768);
    }

    [Fact]
    public void Normalize_GainIsCapped_AndSaysSo()
    {
        var src = Wav("in.wav", 2, Sine(-54));
        var dst = Path.Combine(_root, "out.wav");

        var r = SpeechProcessor.Normalize(src, dst);

        Assert.Equal(SpeechProcessor.MaxGainDb, r.GainDb);
        Assert.True(r.GainCapped);
        Assert.InRange(Rms(Samples(dst)), -25, -23);
    }

    [Fact]
    public void Normalize_InPlace_ReplacesTheFile_AndLeavesNoTemp()
    {
        var path = Wav("same.wav", 2, Sine(-40));
        var r = SpeechProcessor.Normalize(path, path);
        Assert.Equal(SpeechProcessor.Processed, r.Status);
        Assert.InRange(Rms(Samples(path)), -21, -19);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(2.0)]
    public void Normalize_SilentOrEmpty_KeepsTheFile_AndSaysSilent(double seconds)
    {
        var path = Wav("silent.wav", seconds, _ => 0);
        var before = File.ReadAllBytes(path);

        var r = SpeechProcessor.Normalize(path, path);

        Assert.Equal(SpeechProcessor.Silent, r.Status);
        Assert.Null(r.GainDb);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Normalize_TooShortForOneFrame_StillWorks()
    {
        var src = Wav("short.wav", 0.01, Sine(-30));
        var dst = Path.Combine(_root, "out.wav");
        var r = SpeechProcessor.Normalize(src, dst);
        Assert.Equal(SpeechProcessor.Processed, r.Status);
        Assert.Equal(160, Samples(dst).Length);
    }

    [Fact]
    public void Normalize_MissingSource_FailsWithoutThrowing()
    {
        var r = SpeechProcessor.Normalize(Path.Combine(_root, "nope.wav"), Path.Combine(_root, "out.wav"));
        Assert.Equal(SpeechProcessor.Failed, r.Status);
        Assert.NotNull(r.Error);
    }

    // ---------- filter and limiter ----------
    [Fact]
    public void HighPass_CutsHumAndDc_AndPassesSpeech()
    {
        double Through(Func<double, double> signal)
        {
            var hp = new HighPassFilter(Rate, SpeechProcessor.HighPassHz);
            // ToArray first: LINQ does not run the selector on what a Range.Select.Skip skips, and the filter must see it all
            var all = Enumerable.Range(0, Rate * 2).Select(i => hp.Process(signal((double)i / Rate))).ToArray();
            var output = all.Skip(Rate).ToArray();
            return Math.Sqrt(output.Sum(x => x * x) / output.Length);
        }

        var speech = Through(t => Math.Sin(2 * Math.PI * 1000 * t));
        var hum = Through(t => Math.Sin(2 * Math.PI * 50 * t));
        var dc = Through(_ => 0.5);

        Assert.InRange(Db(speech / Math.Sqrt(0.5)), -0.1, 0.1);
        Assert.InRange(Db(hum / Math.Sqrt(0.5)), -10, -7.5);
        Assert.True(dc < 1e-3);
    }

    [Fact]
    public void Limiter_KeepsEverySample_InOrder_UnderTheCeiling()
    {
        var limiter = new PeakLimiter(Rate, 0.5);
        var random = new Random(7);
        var input = Enumerable.Range(0, Rate).Select(_ => (float)(random.NextDouble() * 4 - 2)).ToArray();
        var output = new List<float>();
        foreach (var x in input)
            if (limiter.Push(x, out var y)) output.Add(y);
        for (var i = 0; i < limiter.Lookahead; i++)
            if (limiter.Push(0, out var y)) output.Add(y);

        Assert.Equal(input.Length, output.Count);
        Assert.True(output.Max(Math.Abs) <= 0.5f);
        Assert.True(limiter.LimitedSamples > 0);
    }

    [Fact]
    public void Limiter_LeavesQuietSignalExactlyAlone()
    {
        var limiter = new PeakLimiter(Rate, 0.9);
        var input = Enumerable.Range(0, 1000).Select(i => (float)(0.5 * Math.Sin(i * 0.1))).ToArray();
        var output = new List<float>();
        foreach (var x in input)
            if (limiter.Push(x, out var y)) output.Add(y);
        for (var i = 0; i < limiter.Lookahead; i++)
            if (limiter.Push(0, out var y)) output.Add(y);

        Assert.Equal(input, output);
        Assert.Equal(0, limiter.LimitedSamples);
    }

    // ---------- mix ----------
    [Fact]
    public void Mix_QuietMicNextToLoudSpeaker_IsNotBuried()
    {
        // mic alone for 2 s at -40, then the speaker alone for 2 s at -10
        var mic = Wav("m.wav", 4, t => t < 2 ? Sine(-40)(t) : 0);
        var speaker = Wav("s.wav", 4, t => t >= 2 ? Sine(-10, 700)(t) : 0);
        var dst = Path.Combine(_root, "mix.wav");

        var r = SpeechProcessor.MixBalanced(mic, speaker, dst);

        Assert.Equal(SpeechProcessor.Processed, r.Status);
        var s = Samples(dst);
        var micPart = RmsRange(s, 0.2, 1.8);
        var speakerPart = RmsRange(s, 2.2, 3.8);
        Assert.InRange(micPart, -21, -19);
        Assert.InRange(speakerPart, -24, -22);
        Assert.True(micPart >= speakerPart, $"mic {micPart:0.0} speaker {speakerPart:0.0}");
        Assert.InRange(r.MicGainDb!.Value, 19, 21);
        Assert.InRange(r.SpeakerGainDb!.Value, -14, -12);
    }

    [Fact]
    public void Mix_BothLoudAtOnce_NeverClips()
    {
        var mic = Wav("m.wav", 3, t => 0.99 * Math.Sin(2 * Math.PI * 440 * t));
        var speaker = Wav("s.wav", 3, t => 0.99 * Math.Sin(2 * Math.PI * 450 * t));
        var dst = Path.Combine(_root, "mix.wav");

        SpeechProcessor.MixBalanced(mic, speaker, dst);

        Assert.True(Samples(dst).Max(Math.Abs) <= Ceiling + 1.0 / 32768);
    }

    [Fact]
    public void Mix_DifferentLengths_KeepsTheLonger()
    {
        var mic = Wav("m.wav", 3, Sine(-30));
        var speaker = Wav("s.wav", 1, Sine(-30, 700));
        var dst = Path.Combine(_root, "mix.wav");

        SpeechProcessor.MixBalanced(mic, speaker, dst);

        var s = Samples(dst);
        Assert.Equal(3 * Rate, s.Length);
        Assert.InRange(RmsRange(s, 1.2, 2.8), -21, -19);
    }

    [Fact]
    public void Mix_SilentMic_RaisesOnlyTheSpeaker()
    {
        var mic = Wav("m.wav", 2, _ => 0);
        var speaker = Wav("s.wav", 2, Sine(-35));
        var dst = Path.Combine(_root, "mix.wav");

        var r = SpeechProcessor.MixBalanced(mic, speaker, dst);

        Assert.Equal(0, r.MicGainDb);
        Assert.Null(r.MicInputRmsDbfs);
        Assert.InRange(Rms(Samples(dst)), -24, -22);
    }

    [Fact]
    public void Mix_BothSilent_WritesNothing()
    {
        var dst = Path.Combine(_root, "mix.wav");
        var r = SpeechProcessor.MixBalanced(Wav("m.wav", 1, _ => 0), Wav("s.wav", 1, _ => 0), dst);
        Assert.Equal(SpeechProcessor.Silent, r.Status);
        Assert.False(File.Exists(dst));
    }

    // ---------- through the real save ----------
    private (string Dir, SessionManifest Manifest) Save(string name, AppSettings settings, double micAmp = 0.01, double speakerAmp = 0.5, Action<string>? prepare = null)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        prepare?.Invoke(dir);
        var clock = Stopwatch.StartNew();
        var recorders = new List<SourceRecorder>();
        if (settings.MicEnabled) recorders.Add(PartRecorder(SourceKind.Mic, dir, clock, 3, 440, micAmp));
        if (settings.SpeakerEnabled) recorders.Add(PartRecorder(SourceKind.Speaker, dir, clock, 3, 660, speakerAmp));
        var session = RecordingSession.FromParts(dir, settings, recorders.ToArray());
        session.StopAndSaveAsync().GetAwaiter().GetResult();
        return (dir, SessionManifest.Load(dir)!);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Save_On_CleansMicAndMix_LeavesRawFilesAndSpeakerCopyIdentical()
    {
        var (on, onManifest) = Save("on", new AppSettings { CleanSpeechCopies = true });
        var (off, offManifest) = Save("off", new AppSettings { CleanSpeechCopies = false });

        foreach (var raw in new[] { "mic.wav", "speaker.wav", "mixed.wav", "speaker_16k.wav" })
            Assert.Equal(Hash(Path.Combine(off, raw)), Hash(Path.Combine(on, raw)));
        foreach (var cleaned in new[] { "mic_16k.wav", "mixed_16k.wav" })
            Assert.NotEqual(Hash(Path.Combine(off, cleaned)), Hash(Path.Combine(on, cleaned)));

        Assert.Null(offManifest.SpeechProcessing);
        Assert.DoesNotContain(offManifest.Files, f => f.Description.Contains("normalized") || f.Description.Contains("balanced"));

        var info = onManifest.SpeechProcessing!;
        Assert.Equal(SpeechProcessor.HighPassHz, info.HighPassHz);
        Assert.Equal(new[] { "mic_16k.wav", "mixed_16k.wav" }, info.Files.Select(f => f.File).OrderBy(f => f).ToArray());
        Assert.All(info.Files, f => Assert.Equal(SpeechProcessor.Processed, f.Status));
        Assert.Contains(onManifest.Files, f => f.Name == "mic_16k.wav" && f.Description.Contains("normalized"));
        Assert.Contains(onManifest.Files, f => f.Name == "mixed_16k.wav" && f.Description.Contains("balanced"));
        Assert.Empty(Directory.GetFiles(on, "*.tmp"));

        // what session.json claims is what the file holds
        var mic = info.Files.Single(f => f.File == "mic_16k.wav");
        var measured = Rms(Samples(Path.Combine(on, "mic_16k.wav")));
        Assert.InRange(mic.OutputRmsDbfs!.Value, measured - 0.5, measured + 0.5);
        Assert.InRange(measured, -21, -19);

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(on, SessionManifest.FileName)));
        Assert.True(json.RootElement.TryGetProperty("speechProcessing", out _));
    }

    [Fact]
    public void Save_MicOnly_CleansMic_NoMixClaim()
    {
        var (dir, m) = Save("miconly", new AppSettings { SpeakerEnabled = false });
        Assert.Equal("mic_16k.wav", m.SpeechProcessing!.Files.Single().File);
        Assert.Null(m.MixedSpeechFile);
        Assert.InRange(Rms(Samples(Path.Combine(dir, "mic_16k.wav"))), -21, -19);
    }

    [Fact]
    public void Save_SpeakerOnly_ClaimsNothing()
    {
        var (_, m) = Save("spkonly", new AppSettings { MicEnabled = false });
        Assert.Null(m.SpeechProcessing);
    }

    [Fact]
    public void Save_NoSpeechCopies_ClaimsNothing()
    {
        var (dir, m) = Save("nocopies", new AppSettings { SaveSpeechCopy = false });
        Assert.Null(m.SpeechProcessing);
        Assert.False(File.Exists(Path.Combine(dir, "mic_16k.wav")));
    }

    [Fact]
    public void Save_CleaningFails_KeepsThePlainCopy_AndSaysSo()
    {
        // a folder where the temp file goes makes the write fail, the way a full or locked disk does
        var (dir, m) = Save("fail", new AppSettings(), prepare: d =>
        {
            Directory.CreateDirectory(Path.Combine(d, "mic_16k.wav.tmp"));
            Directory.CreateDirectory(Path.Combine(d, "mixed_16k.wav.tmp"));
        });
        var (plain, _) = Save("plain", new AppSettings { CleanSpeechCopies = false });

        Assert.All(m.SpeechProcessing!.Files, f =>
        {
            Assert.Equal(SpeechProcessor.Failed, f.Status);
            Assert.NotNull(f.Error);
            Assert.Null(f.GainDb);
            Assert.Null(f.OutputRmsDbfs);
        });
        Assert.Equal(Hash(Path.Combine(plain, "mic_16k.wav")), Hash(Path.Combine(dir, "mic_16k.wav")));
        Assert.Equal(Hash(Path.Combine(plain, "mixed_16k.wav")), Hash(Path.Combine(dir, "mixed_16k.wav")));
        Assert.Equal("mixed_16k.wav", m.MixedSpeechFile);
        Assert.Contains(m.Warnings, w => w.Contains("mic_16k.wav"));
        Assert.Contains(m.Warnings, w => w.Contains("mixed_16k.wav"));
        Assert.DoesNotContain(m.Files, f => f.Description.Contains("normalized") || f.Description.Contains("balanced"));
    }

    [Fact]
    public void Save_SilentRecording_IsSavedPlain_AndNotClaimedCleaned()
    {
        var (_, m) = Save("silent", new AppSettings(), micAmp: 0, speakerAmp: 0);
        Assert.All(m.SpeechProcessing!.Files, f => Assert.Equal(SpeechProcessor.Silent, f.Status));
        Assert.Equal("mixed_16k.wav", m.MixedSpeechFile);
        Assert.Empty(m.Warnings);
        Assert.DoesNotContain(m.Files, f => f.Description.Contains("normalized") || f.Description.Contains("balanced"));
    }

    [Fact]
    public void NewSettings_DefaultOn_AlsoForAnOldSettingsFile()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("""{ "MicEnabled": true }""")!;
        Assert.True(old.BoostMicWhileRecording);
        Assert.True(old.CleanSpeechCopies);
    }

    /// <summary>One part file of a tone, 48 kHz stereo float: the shape a real capture leaves on disk.</summary>
    private static SourceRecorder PartRecorder(SourceKind kind, string dir, Stopwatch clock, int seconds, double hz, double amp)
    {
        const int rate = 48000;
        var rec = new SourceRecorder(kind, null, dir, clock);
        var file = $"{rec.KindName}.part1.wav";
        using (var w = new WaveFileWriter(Path.Combine(dir, file), WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)))
        {
            var buf = new float[rate * seconds * 2];
            for (var i = 0; i < rate * seconds; i++)
            {
                var v = (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate));
                buf[2 * i] = v;
                buf[2 * i + 1] = v;
            }
            w.WriteSamples(buf, 0, buf.Length);
        }
        rec.Segments.Add(new SegmentInfo
        {
            Index = 1,
            Device = "test",
            DeviceId = "test",
            Reason = "start",
            DurationSeconds = seconds,
            File = file,
            SampleRate = rate,
            Channels = 2,
            Encoding = "IeeeFloat 32-bit",
        });
        return rec;
    }
}

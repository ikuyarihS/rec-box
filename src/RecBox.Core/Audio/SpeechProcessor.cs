using NAudio.Wave;
using RecBox.Core.Session;

namespace RecBox.Core.Audio;

/// <summary>
/// Makes the 16 kHz copies clear for speech models: an 80 Hz high-pass for hum, a level set from the loudness of
/// the active speech, and a limiter so nothing clips. Reads twice (measure, then write) and streams, so a long
/// recording needs no extra memory. A result that is not "processed" means the target file was not written.
/// </summary>
public static class SpeechProcessor
{
    public const double HighPassHz = 80;
    public const double TargetRmsDbfs = -20;
    /// <summary>In the mix the speaker sits this far under the mic, so the mic voice is never buried.</summary>
    public const double SpeakerOffsetDb = -3;
    public const double CeilingDbfs = -1;
    /// <summary>Never amplify more than this: past it the result is mostly room noise.</summary>
    public const double MaxGainDb = 30;
    /// <summary>A 50 ms frame under this level is not speech, whatever else it looks like.</summary>
    public const double SilenceGateDbfs = -55;
    /// <summary>In the mix, a source with no speech sits at least this far under the other side's voice.</summary>
    public const double NoiseUnderVoiceDb = 20;

    public const string Processed = "processed";
    public const string Silent = "silent";
    public const string Failed = "failed";

    private const int Rate = SessionFinalizer.SpeechRate;
    private const int BufferSamples = 16384;

    public static SpeechProcessingInfo NewInfo() => new()
    {
        HighPassHz = HighPassHz,
        TargetRmsDbfs = TargetRmsDbfs,
        SpeakerOffsetDb = SpeakerOffsetDb,
        CeilingDbfs = CeilingDbfs,
        MaxGainDb = MaxGainDb,
    };

    /// <summary>Writes a cleaned copy of source to target (source and target may be the same file). Never throws.</summary>
    public static ProcessedFile Normalize(string source, string target)
    {
        var result = new ProcessedFile { File = Path.GetFileName(target) };
        return Guarded(result, target, tmp =>
        {
            var input = Measure(source);
            result.InputRmsDbfs = Round(input.ActiveRmsDb);
            if (input.ActiveRmsDb is null)
            {
                result.Status = Silent;
                return false;
            }
            var gain = Gain(input.ActiveRmsDb.Value, TargetRmsDbfs, out var capped);
            result.GainDb = Round(gain);
            result.GainCapped = capped ? true : null;

            using var reader = new WaveFileReader(source);
            var src = Pipe.Shape(reader.ToSampleProvider(), Rate, 1);
            var hp = new HighPassFilter(Rate, HighPassHz);
            var linear = FromDb(gain);
            Write(tmp, result, buf =>
            {
                var n = src.Read(buf, 0, buf.Length);
                for (var i = 0; i < n; i++) buf[i] = (float)(hp.Process(buf[i]) * linear);
                return n;
            });
            return true;
        });
    }

    /// <summary>
    /// Writes one mono mix with each source set to its own level, so a quiet mic next to loud system audio is
    /// still heard. Both start at the same moment; the shorter one ends in silence. Never throws.
    /// </summary>
    public static ProcessedFile MixBalanced(string mic, string speaker, string target)
    {
        var result = new ProcessedFile { File = Path.GetFileName(target) };
        return Guarded(result, target, tmp =>
        {
            var micStats = Measure(mic);
            var speakerStats = Measure(speaker);
            result.MicInputRmsDbfs = Round(micStats.ActiveRmsDb);
            result.SpeakerInputRmsDbfs = Round(speakerStats.ActiveRmsDb);
            if (micStats.ActiveRmsDb is null && speakerStats.ActiveRmsDb is null)
            {
                result.Status = Silent;
                return false;
            }

            var capped = false;
            double micGain, speakerGain;
            if (micStats.ActiveRmsDb is { } micRms)
            {
                micGain = Gain(micRms, TargetRmsDbfs, out var c);
                capped |= c;
            }
            else micGain = NoiseGain(micStats, TargetRmsDbfs + SpeakerOffsetDb);
            if (speakerStats.ActiveRmsDb is { } speakerRms)
            {
                speakerGain = Gain(speakerRms, TargetRmsDbfs + SpeakerOffsetDb, out var c);
                capped |= c;
            }
            else speakerGain = NoiseGain(speakerStats, TargetRmsDbfs);
            result.MicGainDb = Round(micGain);
            result.SpeakerGainDb = Round(speakerGain);
            result.GainCapped = capped ? true : null;

            using var micReader = new WaveFileReader(mic);
            using var speakerReader = new WaveFileReader(speaker);
            var a = Pipe.Shape(micReader.ToSampleProvider(), Rate, 1);
            var b = Pipe.Shape(speakerReader.ToSampleProvider(), Rate, 1);
            var hpA = new HighPassFilter(Rate, HighPassHz);
            var hpB = new HighPassFilter(Rate, HighPassHz);
            var ga = FromDb(micGain);
            var gb = FromDb(speakerGain);
            var other = new float[BufferSamples];
            Write(tmp, result, buf =>
            {
                var na = ReadFull(a, buf);
                var nb = ReadFull(b, other);
                var n = Math.Max(na, nb);
                for (var i = 0; i < n; i++)
                {
                    var x = i < na ? hpA.Process(buf[i]) * ga : 0;
                    var y = i < nb ? hpB.Process(other[i]) * gb : 0;
                    buf[i] = (float)(x + y);
                }
                return n;
            });
            return true;
        });
    }

    /// <summary>Runs the work on a temp file and swaps it in only when it finished. Failures become the result.</summary>
    private static ProcessedFile Guarded(ProcessedFile result, string target, Func<string, bool> work)
    {
        var tmp = target + ".tmp";
        try
        {
            if (work(tmp))
            {
                File.Move(tmp, target, overwrite: true);
                result.Status = Processed;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"cleaning {result.File} failed", ex);
            result.Status = Failed;
            result.Error = ex.Message;
            result.GainDb = null;
            result.MicGainDb = null;
            result.SpeakerGainDb = null;
            result.GainCapped = null;
            result.OutputRmsDbfs = null;
            result.OutputPeakDbfs = null;
            result.Limited = null;
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch (Exception cleanup)
            {
                Log.Error($"could not remove {tmp}", cleanup);
            }
        }
        return result;
    }

    /// <summary>Pulls gained samples from next (0 ends it), limits them and writes 16-bit, recording what came out.</summary>
    private static void Write(string path, ProcessedFile result, Func<float[], int> next)
    {
        var limiter = new PeakLimiter(Rate, FromDb(CeilingDbfs));
        var stats = new LoudnessStats();
        var buf = new float[BufferSamples];
        using (var writer = new WaveFileWriter(path, new WaveFormat(Rate, 16, 1)))
        {
            void Emit(float y)
            {
                stats.Add(y);
                writer.WriteSample(y);
            }

            int n;
            while ((n = next(buf)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    if (limiter.Push(buf[i], out var y)) Emit(y);
                }
            }
            for (var i = 0; i < limiter.Lookahead; i++)
            {
                if (limiter.Push(0, out var y)) Emit(y);
            }
        }
        result.OutputRmsDbfs = Round(stats.ActiveRmsDb);
        result.OutputPeakDbfs = Round(stats.PeakDb);
        result.Limited = limiter.LimitedSamples > 0;
    }

    private static LoudnessStats Measure(string file)
    {
        using var reader = new WaveFileReader(file);
        var src = Pipe.Shape(reader.ToSampleProvider(), Rate, 1);
        var hp = new HighPassFilter(Rate, HighPassHz);
        var stats = new LoudnessStats();
        var buf = new float[BufferSamples];
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < n; i++) stats.Add(hp.Process(buf[i]));
        }
        return stats;
    }

    private static int ReadFull(ISampleProvider src, float[] buf)
    {
        var total = 0;
        int n;
        while (total < buf.Length && (n = src.Read(buf, total, buf.Length - total)) > 0) total += n;
        return total;
    }

    private static double Gain(double rmsDb, double targetDb, out bool capped)
    {
        var wanted = targetDb - rmsDb;
        capped = wanted > MaxGainDb;
        return Math.Min(wanted, MaxGainDb);
    }

    /// <summary>A source with no speech is never raised, and goes down when it would sit too close to the other side's voice.</summary>
    private static double NoiseGain(LoudnessStats noise, double otherVoiceDb) =>
        noise.AudibleRmsDb is { } db ? Math.Min(0, otherVoiceDb - NoiseUnderVoiceDb - db) : 0;

    private static double FromDb(double db) => Math.Pow(10, db / 20);
    private static double? Round(double? db) => db is { } v ? Math.Round(v, 1) : null;
}

/// <summary>A second-order Butterworth high-pass.</summary>
internal sealed class HighPassFilter
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    public HighPassFilter(int rate, double hz)
    {
        var w = 2 * Math.PI * hz / rate;
        var cos = Math.Cos(w);
        var alpha = Math.Sin(w) / Math.Sqrt(2);
        var a0 = 1 + alpha;
        _b0 = (1 + cos) / 2 / a0;
        _b1 = -(1 + cos) / a0;
        _b2 = _b0;
        _a1 = -2 * cos / a0;
        _a2 = (1 - alpha) / a0;
    }

    public double Process(double x)
    {
        var y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }
}

/// <summary>
/// A lookahead brick-wall limiter: the gain each sample needs is held over the lookahead, released slowly, then
/// averaged over the lookahead. Every sample leaves the delay line while the average window only holds gains at or
/// under what it needs, so the output stays at the ceiling without the harsh edge of plain clipping.
/// </summary>
internal sealed class PeakLimiter
{
    private readonly float _ceiling;
    private readonly float[] _delay;
    private readonly double[] _average;
    private readonly double _release;
    private readonly float[] _holdValues;
    private readonly long[] _holdIndex;
    private int _holdHead;
    private int _holdCount;
    private double _averageSum;
    private double _gain = 1;
    private long _n;

    public PeakLimiter(int rate, double ceiling)
    {
        _ceiling = (float)ceiling;
        Lookahead = Math.Max(1, rate * 5 / 1000);
        _delay = new float[Lookahead];
        _average = new double[Lookahead + 1];
        Array.Fill(_average, 1.0);
        _averageSum = _average.Length;
        _release = Math.Exp(-1.0 / (0.1 * rate));
        _holdValues = new float[Lookahead + 2];
        _holdIndex = new long[Lookahead + 2];
    }

    /// <summary>Samples of delay: push this many zeros at the end to get the last samples out.</summary>
    public int Lookahead { get; }
    public long LimitedSamples { get; private set; }

    /// <summary>False while the delay line is still filling (no output yet).</summary>
    public bool Push(float x, out float y)
    {
        var abs = Math.Abs(x);
        var need = 1f;
        if (abs > _ceiling)
        {
            need = _ceiling / abs;
            LimitedSamples++;
        }

        // sliding minimum of the needed gain over the last Lookahead + 1 samples
        var size = _holdValues.Length;
        while (_holdCount > 0 && _holdValues[(_holdHead + _holdCount - 1) % size] >= need) _holdCount--;
        var tail = (_holdHead + _holdCount) % size;
        _holdValues[tail] = need;
        _holdIndex[tail] = _n;
        _holdCount++;
        if (_holdIndex[_holdHead] <= _n - _average.Length)
        {
            _holdHead = (_holdHead + 1) % size;
            _holdCount--;
        }
        double hold = _holdValues[_holdHead];

        _gain = hold < _gain ? hold : hold + (_gain - hold) * _release;

        var slot = (int)(_n % _average.Length);
        _averageSum += _gain - _average[slot];
        _average[slot] = _gain;
        var smooth = _averageSum / _average.Length;

        var d = (int)(_n % _delay.Length);
        var delayed = _delay[d];
        _delay[d] = x;
        var ready = _n >= _delay.Length;
        _n++;

        y = Math.Clamp((float)(delayed * smooth), -_ceiling, _ceiling);
        return ready;
    }
}

/// <summary>
/// Peak and the RMS of the speech only. Level cannot tell a voice from room noise, so a 50 ms frame counts as speech
/// when it is over the silence gate, repeats at a pitch between 60 and 400 Hz, and has its energy in the speech band.
/// Noise of any level, digital silence, clicks and mains hum fail that. Of the frames left, a bump more than 15 dB over
/// the median and the loudest tenth are dropped, and only what is within 30 dB of the rest counts.
/// Keeps a level histogram, not the samples.
/// </summary>
internal sealed class LoudnessStats
{
    private const int FrameSamples = SessionFinalizer.SpeechRate / 20;
    private const int FloorDb = -120;
    private const int RelativeGateDb = 30;
    private const double TrimLoudest = 0.1;
    private const int MaxOverMedianDb = 15;
    /// <summary>Less than this much of the audible time is a beep or a bump, not someone talking.</summary>
    private const double MinSpeechShare = 0.005;
    private const int MinSpeechSamples = SessionFinalizer.SpeechRate / 4;
    /// <summary>Under this the frame's energy is low-frequency hum or rumble, which repeats too but is not a voice.</summary>
    private const double MinSpeechBandShare = 0.25;
    private const double SpeechBandHz = 200;
    /// <summary>Pitch search in samples at the half rate used for it: 400 Hz down to 60 Hz.</summary>
    private const int MinLag = 20;
    private const int MaxLag = 133;
    private const double MinPeriodicity = 0.5;

    private readonly long[] _samples = new long[-FloorDb + 1];
    private readonly double[] _energy = new double[-FloorDb + 1];
    private readonly HighPassFilter _speechBand = new(SessionFinalizer.SpeechRate, SpeechBandHz);
    private readonly float[] _band = new float[FrameSamples / 2];
    private readonly double[] _bandPower = new double[FrameSamples / 2 + 1];
    private double _frameEnergy;
    private double _bandEnergy;
    private double _halfSample;
    private int _frameCount;
    private double _peak;
    private long _measured;
    private long _audible;
    private double _audibleEnergy;
    private bool _anyFrame;

    public void Add(double v)
    {
        var abs = Math.Abs(v);
        if (abs > _peak) _peak = abs;
        _frameEnergy += v * v;
        var band = _speechBand.Process(v);
        _bandEnergy += band * band;
        if (_frameCount % 2 == 0) _halfSample = band;
        else _band[_frameCount / 2] = (float)((_halfSample + band) / 2);
        if (++_frameCount == FrameSamples) CloseFrame();
    }

    private void CloseFrame()
    {
        if (_frameCount == 0) return;
        // a last piece too short to analyze counts only when it is the whole track, and then there is nothing to tell by
        var whole = _frameCount == FrameSamples;
        if (whole || !_anyFrame)
        {
            _measured += _frameCount;
            var bin = _frameEnergy > 0 ? (int)Math.Floor(10 * Math.Log10(_frameEnergy / _frameCount)) - FloorDb : 0;
            if (bin >= SilenceBin)
            {
                _audible += _frameCount;
                _audibleEnergy += _frameEnergy;
                if (!whole || IsVoiced())
                {
                    bin = Math.Min(bin, _samples.Length - 1);
                    _samples[bin] += _frameCount;
                    _energy[bin] += _frameEnergy;
                }
            }
        }
        _anyFrame = true;
        _frameEnergy = 0;
        _bandEnergy = 0;
        _frameCount = 0;
    }

    private static int SilenceBin => (int)SpeechProcessor.SilenceGateDbfs - FloorDb;

    private bool IsVoiced()
    {
        if (_bandEnergy < _frameEnergy * MinSpeechBandShare) return false;
        for (var i = 0; i < _band.Length; i++) _bandPower[i + 1] = _bandPower[i] + _band[i] * _band[i];

        var previous = Correlation(MinLag - 1);
        var current = Correlation(MinLag);
        for (var lag = MinLag; lag <= MaxLag; lag++)
        {
            var next = Correlation(lag + 1);
            // a real peak: the slow fall of low-frequency noise from lag 0 is high too but never rises again
            if (current >= MinPeriodicity && current > previous && current >= next) return true;
            previous = current;
            current = next;
        }
        return false;
    }

    private double Correlation(int lag)
    {
        var count = _band.Length - lag;
        double sum = 0;
        for (var i = 0; i < count; i++) sum += _band[i] * _band[i + lag];
        var power = _bandPower[count] * (_bandPower[_band.Length] - _bandPower[lag]);
        return power > 0 ? sum / Math.Sqrt(power) : 0;
    }

    public double? PeakDb => _peak > 0 ? 20 * Math.Log10(_peak) : null;

    /// <summary>The RMS of everything over the silence gate, speech or not. Null when nothing is.</summary>
    public double? AudibleRmsDb
    {
        get
        {
            CloseFrame();
            return _audible > 0 ? 10 * Math.Log10(_audibleEnergy / _audible) : null;
        }
    }

    /// <summary>Null when the track holds no speech: silence, room noise, clicks, or only a moment of something tonal.</summary>
    public double? ActiveRmsDb
    {
        get
        {
            CloseFrame();
            var speech = _samples.Sum();
            if (speech == 0) return null;
            var needed = Math.Max(_audible * MinSpeechShare, Math.Min(_measured, MinSpeechSamples));
            return speech >= needed ? TrimmedRms(speech) : null;
        }
    }

    private int MedianBin(long total)
    {
        long below = 0;
        for (var bin = 0; bin < _samples.Length; bin++)
        {
            below += _samples[bin];
            if (below * 2 >= total) return bin;
        }
        return _samples.Length - 1;
    }

    private double? TrimmedRms(long speech)
    {
        var top = Math.Min(MedianBin(speech) + MaxOverMedianDb, _samples.Length - 1);
        long active = 0;
        for (var bin = 0; bin <= top; bin++) active += _samples[bin];

        var drop = active * TrimLoudest;
        var reference = -1;
        double samples = 0;
        double energy = 0;
        for (var bin = top; bin >= 0; bin--)
        {
            if (_samples[bin] == 0) continue;
            if (reference >= 0 && bin < reference - RelativeGateDb) break;
            double keep = _samples[bin];
            if (drop > 0)
            {
                var dropped = Math.Min(drop, keep);
                drop -= dropped;
                keep -= dropped;
                if (keep <= 0) continue;
            }
            if (reference < 0) reference = bin;
            samples += keep;
            energy += _energy[bin] * keep / _samples[bin];
        }
        return samples > 0 && energy > 0 ? 10 * Math.Log10(energy / samples) : null;
    }
}

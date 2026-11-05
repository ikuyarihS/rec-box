using NAudio.Wave;
using RecBox.Core.Session;

namespace RecBox.Core.Audio;

/// <summary>
/// Raises the 16 kHz copies: an 80 Hz high-pass, then one fixed gain that brings the peak to -1 dBFS. The gain is
/// between 0 and +12 dB, so a file is never turned down and never raised more than that. Nothing looks at what the
/// audio is. Reads twice (peak, then write) and streams, so a long recording needs no extra memory. A result that is
/// not "processed" means the target file was not written.
/// </summary>
public static class SpeechProcessor
{
    public const double HighPassHz = 80;
    public const double CeilingDbfs = -1;
    public const double MaxGainDb = 12;

    public const string Processed = "processed";
    public const string Silent = "silent";
    public const string Failed = "failed";

    private const int Rate = SessionFinalizer.SpeechRate;
    private const int BufferSamples = 16384;
    /// <summary>A source brought exactly to the ceiling must not count as limited because of float rounding.</summary>
    private const double RoundingRoom = 1.00001;

    public static SpeechProcessingInfo NewInfo() => new()
    {
        HighPassHz = HighPassHz,
        CeilingDbfs = CeilingDbfs,
        MaxGainDb = MaxGainDb,
    };

    /// <summary>Writes a filtered and raised copy of source to target (source and target may be the same file). Never throws.</summary>
    public static ProcessedFile Normalize(string source, string target)
    {
        var result = new ProcessedFile { File = Path.GetFileName(target) };
        return Guarded(result, target, tmp =>
        {
            var peak = FilteredPeak(source);
            result.InputPeakDbfs = PeakDb(peak);
            if (peak <= 0)
            {
                result.Status = Silent;
                return false;
            }
            var gain = Gain(peak, out var capped);
            result.GainDb = Round(gain);
            result.GainCapped = capped ? true : null;

            using var reader = new WaveFileReader(source);
            var src = Pipe.Shape(reader.ToSampleProvider(), Rate, 1);
            var hp = new HighPassFilter(Rate, HighPassHz);
            var linear = FromDb(gain);
            // a file already over the ceiling keeps its level, so this limiter only stops what the filter pushes past full scale
            Write(tmp, result, 1, buf =>
            {
                var n = src.Read(buf, 0, buf.Length);
                for (var i = 0; i < n; i++) buf[i] = (float)(hp.Process(buf[i]) * linear);
                return n;
            });
            return true;
        });
    }

    /// <summary>
    /// Writes one mono mix: each source filtered and raised on its own like <see cref="Normalize"/>, then summed, then
    /// limited at the ceiling. Both start at the same moment; the shorter one ends in silence. Never throws.
    /// </summary>
    public static ProcessedFile MixNormalized(string mic, string speaker, string target)
    {
        var result = new ProcessedFile { File = Path.GetFileName(target) };
        return Guarded(result, target, tmp =>
        {
            var micPeak = FilteredPeak(mic);
            var speakerPeak = FilteredPeak(speaker);
            result.MicInputPeakDbfs = PeakDb(micPeak);
            result.SpeakerInputPeakDbfs = PeakDb(speakerPeak);
            if (micPeak <= 0 && speakerPeak <= 0)
            {
                result.Status = Silent;
                return false;
            }

            var micGain = Gain(micPeak, out var micCapped);
            var speakerGain = Gain(speakerPeak, out var speakerCapped);
            result.MicGainDb = Round(micGain);
            result.SpeakerGainDb = Round(speakerGain);
            result.GainCapped = micCapped || speakerCapped ? true : null;

            using var micReader = new WaveFileReader(mic);
            using var speakerReader = new WaveFileReader(speaker);
            var a = Pipe.Shape(micReader.ToSampleProvider(), Rate, 1);
            var b = Pipe.Shape(speakerReader.ToSampleProvider(), Rate, 1);
            var hpA = new HighPassFilter(Rate, HighPassHz);
            var hpB = new HighPassFilter(Rate, HighPassHz);
            var ga = FromDb(micGain);
            var gb = FromDb(speakerGain);
            var other = new float[BufferSamples];
            Write(tmp, result, FromDb(CeilingDbfs) * RoundingRoom, buf =>
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
    private static void Write(string path, ProcessedFile result, double ceiling, Func<float[], int> next)
    {
        var limiter = new PeakLimiter(Rate, ceiling);
        var peak = 0.0;
        var buf = new float[BufferSamples];
        using (var writer = new WaveFileWriter(path, new WaveFormat(Rate, 16, 1)))
        {
            void Emit(float y)
            {
                peak = Math.Max(peak, Math.Abs(y));
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
        result.OutputPeakDbfs = PeakDb(peak);
        result.Limited = limiter.LimitedSamples > 0;
    }

    /// <summary>The peak after the high-pass, which is what the gain acts on. 0 means the file is silent or empty.</summary>
    private static double FilteredPeak(string file)
    {
        using var reader = new WaveFileReader(file);
        var src = Pipe.Shape(reader.ToSampleProvider(), Rate, 1);
        var hp = new HighPassFilter(Rate, HighPassHz);
        var buf = new float[BufferSamples];
        var peak = 0.0;
        int n;
        while ((n = src.Read(buf, 0, buf.Length)) > 0)
        {
            for (var i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(hp.Process(buf[i])));
        }
        return peak;
    }

    private static int ReadFull(ISampleProvider src, float[] buf)
    {
        var total = 0;
        int n;
        while (total < buf.Length && (n = src.Read(buf, total, buf.Length - total)) > 0) total += n;
        return total;
    }

    private static double Gain(double peak, out bool capped)
    {
        capped = false;
        if (peak <= 0) return 0;
        var wanted = CeilingDbfs - 20 * Math.Log10(peak);
        capped = wanted > MaxGainDb;
        return Math.Clamp(wanted, 0, MaxGainDb);
    }

    private static double FromDb(double db) => Math.Pow(10, db / 20);
    private static double? PeakDb(double peak) => peak > 0 ? Round(20 * Math.Log10(peak)) : null;
    private static double Round(double db) => Math.Round(db, 1);
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

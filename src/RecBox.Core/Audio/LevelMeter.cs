using NAudio.Wave;

namespace RecBox.Core.Audio;

public static class LevelMeter
{
    private const double MeterFloorDb = -60;

    /// <summary>Peak 0..1 of a capture buffer, in any format the recorder writes (float, 8, 16, 24 or 32-bit PCM).</summary>
    public static float Peak(byte[] buf, int count, WaveFormat fmt)
    {
        double peak = 0;
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            for (var i = 0; i + 4 <= count; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buf, i)));
        }
        else if (fmt.BitsPerSample == 16)
        {
            for (var i = 0; i + 2 <= count; i += 2) peak = Math.Max(peak, Math.Abs((double)BitConverter.ToInt16(buf, i)) / 32768);
        }
        else if (fmt.BitsPerSample == 24)
        {
            for (var i = 0; i + 3 <= count; i += 3)
            {
                var s = buf[i] | buf[i + 1] << 8 | (sbyte)buf[i + 2] << 16;
                peak = Math.Max(peak, Math.Abs((double)s) / 8388608);
            }
        }
        else if (fmt.BitsPerSample == 32)
        {
            for (var i = 0; i + 4 <= count; i += 4) peak = Math.Max(peak, Math.Abs((double)BitConverter.ToInt32(buf, i)) / 2147483648);
        }
        else if (fmt.BitsPerSample == 8)
        {
            for (var i = 0; i < count; i++) peak = Math.Max(peak, Math.Abs(buf[i] - 128) / 128.0);
        }
        return (float)Math.Clamp(peak, 0, 1);
    }

    /// <summary>A peak level as a meter position 0..1 on a -60..0 dB scale, so a quiet mic is visibly low but not invisible.</summary>
    public static double ToMeterScale(float peak)
    {
        if (peak <= 0) return 0;
        var db = 20 * Math.Log10(peak);
        return Math.Clamp((db - MeterFloorDb) / -MeterFloorDb, 0, 1);
    }
}

/// <summary>True once the level stayed near silence for the hold time; any louder reading starts it over.</summary>
public sealed class QuietDetector
{
    /// <summary>-50 dBFS: well under speech even on a quiet mic, about the noise floor of a working one.</summary>
    public const float NearSilence = 0.00316f;
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(5);

    private TimeSpan? _quietSince;

    public bool IsQuiet { get; private set; }

    public bool Update(float peak, TimeSpan now)
    {
        if (peak >= NearSilence)
        {
            _quietSince = null;
            IsQuiet = false;
            return false;
        }
        _quietSince ??= now;
        IsQuiet = now - _quietSince.Value >= Hold;
        return IsQuiet;
    }

    public void Reset()
    {
        _quietSince = null;
        IsQuiet = false;
    }
}

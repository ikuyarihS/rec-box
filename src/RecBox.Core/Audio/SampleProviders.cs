using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RecBox.Core.Audio;

/// <summary>A fixed number of silent sample frames.</summary>
internal sealed class SilenceSampleProvider : ISampleProvider
{
    private long _samplesLeft;

    public SilenceSampleProvider(WaveFormat format, double seconds)
    {
        WaveFormat = format;
        _samplesLeft = (long)Math.Round(seconds * format.SampleRate) * format.Channels;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var n = (int)Math.Min(count, _samplesLeft);
        Array.Clear(buffer, offset, n);
        _samplesLeft -= n;
        return n;
    }
}

/// <summary>Converts any channel count to 1 (average) or 2 (front pair, centre folded in).</summary>
internal sealed class ChannelMapper : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly int _in;
    private readonly int _out;
    private float[] _buf = Array.Empty<float>();

    private ChannelMapper(ISampleProvider src, int outChannels)
    {
        _src = src;
        _in = src.WaveFormat.Channels;
        _out = outChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, outChannels);
    }

    public static ISampleProvider To(ISampleProvider src, int outChannels) =>
        src.WaveFormat.Channels == outChannels ? src : new ChannelMapper(src, outChannels);

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / _out;
        var need = frames * _in;
        if (_buf.Length < need) _buf = new float[need];
        var got = _src.Read(_buf, 0, need);
        var gotFrames = got / _in;
        for (var f = 0; f < gotFrames; f++)
        {
            var b = f * _in;
            if (_out == 1)
            {
                float sum = 0;
                for (var c = 0; c < _in; c++) sum += _buf[b + c];
                buffer[offset + f] = sum / _in;
            }
            else if (_in == 1)
            {
                buffer[offset + f * 2] = _buf[b];
                buffer[offset + f * 2 + 1] = _buf[b];
            }
            else
            {
                var centre = _in >= 3 ? _buf[b + 2] * 0.707f : 0f;
                buffer[offset + f * 2] = _buf[b] + centre;
                buffer[offset + f * 2 + 1] = _buf[b + 1] + centre;
            }
        }
        return gotFrames * _out;
    }
}

internal static class Pipe
{
    public static ISampleProvider Resample(ISampleProvider src, int rate) =>
        src.WaveFormat.SampleRate == rate ? src : new WdlResamplingSampleProvider(src, rate);

    public static ISampleProvider Shape(ISampleProvider src, int rate, int channels) =>
        Resample(ChannelMapper.To(src, channels), rate);
}

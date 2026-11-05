using NAudio.Wave;
using RecBox.Core.Audio;
using Xunit;

namespace RecBox.Tests;

public sealed class LevelMeterTests
{
    private static byte[] Bytes(int bytesPerSample, IEnumerable<long> samples) =>
        samples.SelectMany(s => BitConverter.GetBytes(s).Take(bytesPerSample)).ToArray();

    [Fact]
    public void Peak_Float32()
    {
        var buf = new[] { 0.1f, -0.5f, 0.25f }.SelectMany(BitConverter.GetBytes).ToArray();
        Assert.Equal(0.5f, LevelMeter.Peak(buf, buf.Length, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)));
    }

    [Theory]
    [InlineData(16, 2, 16384, 0.5f)]
    [InlineData(16, 2, -16384, 0.5f)]
    [InlineData(24, 3, 4194304, 0.5f)]
    [InlineData(24, 3, -4194304, 0.5f)]
    [InlineData(32, 4, 1073741824, 0.5f)]
    [InlineData(32, 4, -1073741824, 0.5f)]
    public void Peak_Pcm(int bits, int bytes, long sample, float expected)
    {
        var buf = Bytes(bytes, new[] { 0L, sample, 0L });
        Assert.Equal(expected, LevelMeter.Peak(buf, buf.Length, new WaveFormat(48000, bits, 1)), 3);
    }

    [Fact]
    public void Peak_Pcm8_IsCenteredOn128()
    {
        Assert.Equal(0f, LevelMeter.Peak(new byte[] { 128, 128 }, 2, new WaveFormat(48000, 8, 1)));
        Assert.Equal(0.5f, LevelMeter.Peak(new byte[] { 128, 192 }, 2, new WaveFormat(48000, 8, 1)));
    }

    [Fact]
    public void Peak_SeesASingleSampleInAnyPosition_OnEveryChannel()
    {
        for (var at = 0; at < 8; at++)
        {
            var samples = new float[8];
            samples[at] = 0.5f;
            var buf = samples.SelectMany(BitConverter.GetBytes).ToArray();
            Assert.Equal(0.5f, LevelMeter.Peak(buf, buf.Length, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
        }
    }

    [Fact]
    public void Peak_FullScaleAndOver_StaysAtOne_AndHonoursTheCount()
    {
        Assert.Equal(1f, LevelMeter.Peak(Bytes(4, new[] { (long)int.MinValue }), 4, new WaveFormat(48000, 32, 1)));
        var over = BitConverter.GetBytes(3f);
        Assert.Equal(1f, LevelMeter.Peak(over, 4, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)));
        var buf = new[] { 0.1f, 0.9f }.SelectMany(BitConverter.GetBytes).ToArray();
        Assert.Equal(0.1f, LevelMeter.Peak(buf, 4, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 1)]
    [InlineData(0.1f, 0.6667)]
    [InlineData(0.001f, 0)]
    [InlineData(0.00316f, 0.1667)]
    public void ToMeterScale_IsDbOverSixtyDb(float peak, double expected) =>
        Assert.Equal(expected, LevelMeter.ToMeterScale(peak), 2);

    [Fact]
    public void Quiet_OnlyAfterTheFullHold()
    {
        var q = new QuietDetector();
        Assert.False(q.Update(0, TimeSpan.Zero));
        Assert.False(q.Update(0.001f, TimeSpan.FromSeconds(4.9)));
        Assert.True(q.Update(0, TimeSpan.FromSeconds(5)));
        Assert.True(q.IsQuiet);
    }

    [Fact]
    public void Quiet_AnyRealSignal_StartsItOver_AndClearsTheWarning()
    {
        var q = new QuietDetector();
        q.Update(0, TimeSpan.Zero);
        q.Update(0, TimeSpan.FromSeconds(6));
        Assert.True(q.IsQuiet);

        Assert.False(q.Update(0.2f, TimeSpan.FromSeconds(7)));
        Assert.False(q.IsQuiet);
        Assert.False(q.Update(0, TimeSpan.FromSeconds(8)));
        Assert.False(q.Update(0, TimeSpan.FromSeconds(12.9)));
        Assert.True(q.Update(0, TimeSpan.FromSeconds(13)));
    }

    [Fact]
    public void Quiet_Reset_DoesNotCarryIntoTheNextRecording()
    {
        var q = new QuietDetector();
        q.Update(0, TimeSpan.Zero);
        q.Update(0, TimeSpan.FromSeconds(6));
        q.Reset();

        Assert.False(q.IsQuiet);
        Assert.False(q.Update(0, TimeSpan.FromSeconds(6.1)));
    }
}

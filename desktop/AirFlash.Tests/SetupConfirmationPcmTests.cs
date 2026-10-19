using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class SetupConfirmationPcmTests
{
    [Theory]
    [InlineData(8000, 1, 12000)]
    [InlineData(11025, 1, 16538)]
    [InlineData(44100, 2, 132300)]
    [InlineData(48000, 2, 144000)]
    [InlineData(96000, 1, 144000)]
    [InlineData(192000, 2, 576000)]
    public void ChimeHasOneAndAHalfSecondsOfInterleavedFloatPcm(int sampleRate, int channels, int samples)
    {
        var pcm = SetupConfirmationPcm.Create(sampleRate, channels);
        Assert.Equal(samples, pcm.Length);
        Assert.Equal(0, pcm.Length % channels);
        Assert.InRange((double)pcm.Length / channels / sampleRate, 1.5, 1.5001);
        Assert.All(pcm, sample => Assert.True(float.IsFinite(sample)));
        Assert.InRange(pcm.Max(sample => Math.Abs(sample)), .10f, .18f);
    }

    [Fact]
    public void MonoAndStereoCarryTheSameDeterministicChimeWithoutChannelGain()
    {
        var mono = SetupConfirmationPcm.Create(48000, 1);
        var stereo = SetupConfirmationPcm.Create(48000, 2);
        Assert.Equal(mono, SetupConfirmationPcm.Create(48000, 1));
        for (var frame = 0; frame < mono.Length; frame++)
        {
            Assert.Equal(mono[frame], stereo[frame * 2]);
            Assert.Equal(mono[frame], stereo[frame * 2 + 1]);
        }
    }

    [Fact]
    public void BothNotesFadeFromAndToSilenceWithAnAudibleMiddleAndASilentGap()
    {
        var pcm = SetupConfirmationPcm.Create(48000, 1);
        foreach (var start in new[] { 0, 38400 })
        {
            Assert.Equal(0f, pcm[start]);
            Assert.Equal(0f, pcm[start + 33599]);
            Assert.True(Rms(pcm.AsSpan(start, 240)) < Rms(pcm.AsSpan(start + 1440, 1440)) * .1);
            Assert.True(Rms(pcm.AsSpan(start + 33360, 240)) < Rms(pcm.AsSpan(start + 30720, 1440)) * .1);
            Assert.InRange(Rms(pcm.AsSpan(start + 4800, 14400)), .04, .11);
        }
        Assert.All(pcm[33600..38400], sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void ChimeHasTwoDistinctRisingPitches()
    {
        var pcm = SetupConfirmationPcm.Create(48000, 1);
        Assert.InRange(PositiveCrossings(pcm.AsSpan(4800, 14400)), 156, 158);
        Assert.InRange(PositiveCrossings(pcm.AsSpan(43200, 14400)), 197, 199);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-1, 2)]
    [InlineData(7999, 2)]
    [InlineData(192001, 2)]
    [InlineData(int.MaxValue, 2)]
    [InlineData(48000, 0)]
    [InlineData(48000, 3)]
    public void UnsupportedFormatsAreRejectedBeforeAllocatingPcm(int sampleRate, int channels)
        => Assert.Throws<ArgumentOutOfRangeException>(() => SetupConfirmationPcm.Create(sampleRate, channels));

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var sample in samples) sum += (double)sample * sample;
        return Math.Sqrt(sum / samples.Length);
    }

    private static int PositiveCrossings(ReadOnlySpan<float> samples)
    {
        var crossings = 0;
        for (var index = 1; index < samples.Length; index++)
            if (samples[index - 1] <= 0 && samples[index] > 0) crossings++;
        return crossings;
    }
}

using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class PcmVolumeCalibrationTests
{
    private const string DriverHash = "f01344602472f1b527de5ee98f18987c03db48fea444e457d061e937f1d531d5";
    private static PcmVolumeCalibration Calibration => new(false, "44100/2/32/3", DriverHash, 0, 38);
    private static VolumeSample[] Samples(double lowRms) => [new(0,1,false,.02,.04), new(-12,.25,false,lowRms,lowRms*2),
        new(0,1,false,.02,.04), new(-12,.25,false,lowRms,lowRms*2)];

    [Fact]
    public void GainIsAppliedOnce()
    {
        Assert.Equal(1, PcmVolumeMath.Gain(Calibration with { MasterInPcm = true }, new(1,-12,.25,false,Guid.Empty)));
        Assert.Equal(.251188643150958, PcmVolumeMath.Gain(Calibration, new(1,-12,.25,false,Guid.Empty)), 12);
        Assert.Equal(1, PcmVolumeMath.Gain(Calibration, new(2,1,1,false,Guid.Empty)));
        Assert.Equal(0, PcmVolumeMath.Gain(Calibration, new(3,-12,.25,true,Guid.Empty)));
        Assert.Equal(0, PcmVolumeMath.Gain(Calibration, new(4,-96,0,false,Guid.Empty)));
    }

    [Theory]
    [InlineData(double.NaN, .5)]
    [InlineData(double.PositiveInfinity, .5)]
    [InlineData(-12, double.NaN)]
    [InlineData(-12, -0.1)]
    [InlineData(-12, 1.1)]
    public void InvalidEndpointReadingsNeverBecomeAnAudibleGain(double db, double scalar)
        => Assert.Throws<ArgumentOutOfRangeException>(() => PcmVolumeMath.Gain(Calibration, new(1,db,scalar,false,Guid.Empty)));

    [Theory]
    [InlineData(.005, true)]
    [InlineData(.02, false)]
    public void RepeatedMeasurementsSelectTheActualCapturePoint(double lowRms, bool expected)
    {
        var calibration = PcmVolumeMath.Classify(Samples(lowRms), "44100/2/32/3", DriverHash, 38);
        Assert.Equal(expected, calibration.MasterInPcm);
        Assert.Equal(0, calibration.MaxDb);
        Assert.Equal(38, calibration.ResumeDeviceVolume);
    }

    [Fact]
    public void UnstableCapturePointRejectsCalibration()
    {
        Assert.Throws<ArgumentException>(() => PcmVolumeMath.Classify(Samples(.01), Calibration.MixFormat, DriverHash, 38));
        var unstable = Samples(.005); unstable[3] = unstable[3] with { Rms = .02, Peak = .04 };
        Assert.Throws<ArgumentException>(() => PcmVolumeMath.Classify(unstable, Calibration.MixFormat, DriverHash, 38));
        Assert.Throws<ArgumentException>(() => PcmVolumeMath.Classify(Samples(.005)[..2], Calibration.MixFormat, DriverHash, 38));
        Assert.Throws<ArgumentException>(() => PcmVolumeMath.Classify(Samples(.005), Calibration.MixFormat, DriverHash, 0));
    }

    [Fact]
    public void SilenceClippingAndNonFiniteMeasurementsCannotQualify()
    {
        foreach (var changed in new[] { new VolumeSample(0,1,false,0,0), new(0,1,false,.02,.08), new(0,1,false,double.NaN,.04) })
        {
            var samples = Samples(.005); samples[0] = changed;
            Assert.Throws<ArgumentException>(() => PcmVolumeMath.Classify(samples, Calibration.MixFormat, DriverHash, 38));
        }
    }

    [Fact]
    public void ApplicationMixerGainMustAlreadyBeCapturedExactlyOnce()
    {
        PcmVolumeMath.ValidateSessionGain(.02,.01);
        Assert.Throws<ArgumentException>(() => PcmVolumeMath.ValidateSessionGain(.02,.005));
        Assert.Throws<ArgumentException>(() => PcmVolumeMath.ValidateSessionGain(.02,.02));
    }

    [Theory]
    [InlineData(16,false)]
    [InlineData(24,false)]
    [InlineData(32,false)]
    [InlineData(32,true)]
    public void PcmStatisticsReadSignedLittleEndianSamples(int bits, bool ieee)
    {
        byte[] bytes = (bits, ieee) switch {
            (16, _) => [0,32,0,192],
            (24, _) => [0,0,32,0,0,192],
            (32, false) => [0,0,0,32,0,0,0,192],
            _ => [0,0,128,62,0,0,0,191]
        };
        var meter = new PcmMeter(bits, ieee);
        meter.Add(bytes);
        var measured = meter.Snapshot();
        Assert.Equal(2, measured.Samples);
        Assert.Equal(.5, measured.Peak);
        Assert.Equal(.39528470752104744, measured.Rms, 12);
    }

    [Fact]
    public void InvalidCaptureChunksDoNotBecomeCalibrationEvidence()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmMeter(8,false));
        Assert.Throws<InvalidDataException>(() => new PcmMeter(16,false).Add([1]));
        Assert.Throws<InvalidDataException>(() => new PcmMeter(32,true).Add([0,0,192,127]));
    }
}

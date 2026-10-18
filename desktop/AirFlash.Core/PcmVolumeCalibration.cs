using System.Buffers.Binary;
namespace AirFlash.Core;

public sealed record PcmVolumeCalibration(bool MasterInPcm, string MixFormat, string DriverSha256,
    double MaxDb, int ResumeDeviceVolume);

public sealed record VolumeSample(double EndpointDb, double Scalar, bool Muted, double Rms, double Peak);
public sealed record NativeVolume(long Revision, double Db, double Scalar, bool Muted, Guid Context);

public static class PcmVolumeMath
{
    public static (int Volume, double Gain) DeviceControl(PcmVolumeCalibration calibration, NativeVolume volume)
    {
        _ = Gain(calibration, volume);
        if (calibration.MasterInPcm || calibration.ResumeDeviceVolume is < 1 or > 100)
            throw new ArgumentException("Device control requires pre-master PCM and a calibrated nonzero reference.", nameof(calibration));
        if (volume.Muted || volume.Scalar == 0) return (0, 1);
        var db = calibration.ResumeDeviceVolume * .3 - 30 + Math.Min(0, volume.Db - calibration.MaxDb);
        var target = Math.Clamp((int)Math.Ceiling((db + 30) / .3 - 1e-9), 1, calibration.ResumeDeviceVolume);
        // shortcut: below the receiver floor, residual gain follows buffered PCM; wider device dB needs hardware verification.
        return (target, Math.Clamp(Math.Pow(10, (db - (target * .3 - 30)) / 20), 0, 1));
    }
    public static double Gain(PcmVolumeCalibration calibration, NativeVolume volume)
    {
        if (!double.IsFinite(volume.Db) || !double.IsFinite(calibration.MaxDb)
            || !double.IsFinite(volume.Scalar) || volume.Scalar is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(volume), "Endpoint volume must be finite and within its scalar range.");
        if (volume.Muted || volume.Scalar == 0) return 0;
        return calibration.MasterInPcm ? 1 : Math.Clamp(Math.Pow(10, (volume.Db - calibration.MaxDb) / 20), 0, 1);
    }

    public static PcmVolumeCalibration Classify(IReadOnlyList<VolumeSample> samples, string mixFormat, string driverHash, int resumeDeviceVolume)
    {
        if (resumeDeviceVolume is < 1 or > 100 || string.IsNullOrWhiteSpace(mixFormat) || mixFormat.Length > 128
            || driverHash.Length != 64 || !driverHash.All(Uri.IsHexDigit))
            throw new ArgumentException("A verified format, driver hash and audible nonzero reference volume are required.");
        if (samples.Count < 4 || samples.Any(s => !double.IsFinite(s.EndpointDb) || !double.IsFinite(s.Scalar)
            || s.Scalar is <= 0 or > 1 || s.Muted || !double.IsFinite(s.Rms) || !double.IsFinite(s.Peak)
            || s.Rms <= 0 || s.Peak < s.Rms || s.Peak > .05001))
            throw new ArgumentException("Calibration requires repeated finite, non-silent, unclipped measurements.");
        var max = samples.Max(s => s.EndpointDb);
        var high = samples.Where(s => Math.Abs(s.EndpointDb - max) <= .01).ToArray();
        var low = samples.Where(s => Math.Abs(s.EndpointDb - (max - 12)) <= .01).ToArray();
        if (high.Length < 2 || low.Length < 2 || high.Length + low.Length != samples.Count || high.Any(s => s.Scalar < .99))
            throw new ArgumentException("The endpoint must supply maximum and maximum minus 12 dB measurements.");
        var reference = high.Average(s => s.Rms);
        if (high.Any(s => Math.Abs(20 * Math.Log10(s.Rms / reference)) > .5))
            throw new ArgumentException("The reference signal is unstable.");
        var changes = low.Select(s => 20 * Math.Log10(s.Rms / reference)).ToArray();
        var included = changes.All(db => Math.Abs(db + 12) <= 1);
        var excluded = changes.All(db => Math.Abs(db) <= .5);
        if (!included && !excluded) throw new ArgumentException("Neither single-gain capture model matches the measured samples.");
        return new(included, mixFormat, driverHash.ToLowerInvariant(), max, resumeDeviceVolume);
    }

    public static void ValidateSessionGain(double baselineRms, double halfRms)
    {
        if (!double.IsFinite(baselineRms) || !double.IsFinite(halfRms) || baselineRms <= 0
            || halfRms / baselineRms is < .45 or > .55)
            throw new ArgumentException("The Windows application mixer was not captured exactly once.");
    }
}

public sealed class PcmMeter
{
    private readonly int _bytes;
    private readonly bool _ieee;
    private double _sum, _peak;
    private long _count;
    public PcmMeter(int bits, bool ieee)
    {
        if (ieee && bits != 32 || !ieee && bits is not (16 or 24 or 32)) throw new ArgumentOutOfRangeException(nameof(bits));
        _bytes = bits / 8; _ieee = ieee;
    }
    public void Add(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % _bytes != 0) throw new InvalidDataException("Incomplete PCM sample.");
        for (var offset = 0; offset < bytes.Length; offset += _bytes)
        {
            var item = bytes[offset..];
            double sample;
            if (_ieee) sample = BinaryPrimitives.ReadSingleLittleEndian(item);
            else if (_bytes == 2) sample = BinaryPrimitives.ReadInt16LittleEndian(item) / 32768d;
            else if (_bytes == 4) sample = BinaryPrimitives.ReadInt32LittleEndian(item) / 2147483648d;
            else {
                var value = item[0] | item[1] << 8 | item[2] << 16;
                if ((value & 0x800000) != 0) value -= 0x1000000;
                sample = value / 8388608d;
            }
            if (!double.IsFinite(sample)) throw new InvalidDataException("Non-finite captured PCM sample.");
            _sum += sample * sample; _peak = Math.Max(_peak, Math.Abs(sample)); _count++;
        }
    }
    public (double Rms, double Peak, long Samples) Snapshot() => (_count == 0 ? 0 : Math.Sqrt(_sum / _count), _peak, _count);
}

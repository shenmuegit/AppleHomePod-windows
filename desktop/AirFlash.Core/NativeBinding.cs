using System.Buffers.Binary;
using System.Security.Cryptography;
namespace AirFlash.Core;

public sealed record EndpointIdentity(string EndpointId, string AdapterInstanceId, string HardwareId,
    string Provider, string DriverVersion, string DriverSha256, string PinName,
    string FriendlyName, string MixFormat, int Channels, AudioEndpointKind Kind, bool Active, string? EndpointLabel = null);

public sealed record NativeBinding(int SchemaVersion, string ReceiverId, EndpointIdentity Endpoint,
    string OriginalName, string AssignedName, PcmVolumeCalibration? Calibration, int? VerifiedBufferMs = null,
    string? VerifiedBufferProfile = null);

public static class NativeBindingPolicy
{
    public const string HardwareId = "VBAudioVACWDM";
    public const string Provider = "VB-Audio Software";
    public const string DriverVersion = "3.3.1.7";
    public const string DriverSha256 = "f01344602472f1b527de5ee98f18987c03db48fea444e457d061e937f1d531d5";

    public static (string MixFormat, int Channels) ReadMixFormat(byte[] blob)
    {
        if (blob.Length is < 18 or > 256) throw new InvalidDataException("Invalid endpoint WAVEFORMATEX size.");
        var bytes = blob.AsSpan();
        var tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        var extra = BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]);
        if (blob.Length < 18 + extra || tag == 65534 && blob.Length < 40)
            throw new InvalidDataException("Incomplete endpoint WAVEFORMATEX.");
        if (tag == 65534) tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes[24..]);
        if (channels is < 1 or > 32 || rate is < 8000 or > 192000 || tag is not (1 or 3)
            || tag == 3 && bits != 32 || tag == 1 && bits is not (16 or 24 or 32))
            throw new InvalidDataException("Unsupported endpoint sample format.");
        return ($"{rate}/{channels}/{bits}/{tag}/{Convert.ToHexString(SHA256.HashData(blob)).ToLowerInvariant()}", channels);
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    private static bool VerifiedEndpoint(EndpointIdentity? endpoint) => endpoint is not null && endpoint.Active
        && endpoint.Kind == AudioEndpointKind.Playback && endpoint.Channels is >= 2 and <= 8
        && Text(endpoint.EndpointId, 2048) && Text(endpoint.AdapterInstanceId, 2048)
        && Text(endpoint.FriendlyName, 256) && Text(endpoint.MixFormat, 128)
        && (endpoint.EndpointLabel is null || Text(endpoint.EndpointLabel, 256))
        && Same(endpoint.HardwareId, HardwareId) && Same(endpoint.Provider, Provider)
        && Same(endpoint.DriverVersion, DriverVersion) && Same(endpoint.DriverSha256, DriverSha256)
        && Same(endpoint.PinName, "CABLE Input");

    public static bool CanClaim(EndpointIdentity candidate, IReadOnlySet<string> preexistingAdapterIds)
        => VerifiedEndpoint(candidate) && !preexistingAdapterIds.Any(id => Same(id, candidate.AdapterInstanceId));

    public static NativeBinding PrepareBinding(EndpointIdentity candidate, string receiverId, string requestedName,
        NativeBinding? previous, IReadOnlySet<string> preexistingAdapterIds)
    {
        if (!CanClaim(candidate, preexistingAdapterIds)) throw new InvalidOperationException("The output is not a verified ordinary VB-CABLE playback pin.");
        receiverId = ReceiverIdentity.Normalize(receiverId);
        if (previous is not null && (previous.ReceiverId != receiverId || !Same(previous.Endpoint.EndpointId, candidate.EndpointId)
            || !Same(previous.Endpoint.AdapterInstanceId, candidate.AdapterInstanceId) || !Same(previous.Endpoint.DriverSha256, candidate.DriverSha256)))
            throw new InvalidOperationException("Remove the existing binding before replacing its receiver or adapter.");
        var desired = candidate.EndpointLabel == requestedName || candidate.FriendlyName == requestedName;
        if (previous?.Calibration is null && !desired && (candidate.EndpointLabel is null || Same(candidate.EndpointLabel, candidate.PinName)))
            throw new InvalidOperationException("First name this output in Windows sound device properties. Application property writes are not supported.");
        var name = desired ? requestedName : candidate.EndpointLabel ?? candidate.FriendlyName;
        var calibration = previous?.Endpoint.MixFormat == candidate.MixFormat ? previous.Calibration : null;
        var binding = new NativeBinding(1, receiverId, candidate, previous?.OriginalName ?? candidate.FriendlyName, name, calibration,
            calibration is null ? null : previous?.VerifiedBufferMs, calibration is null ? null : previous?.VerifiedBufferProfile);
        if (ValidateStored(binding) is { } error) throw new ArgumentException(error);
        return binding;
    }

    public static string? ValidateStored(NativeBinding? binding)
    {
        if (binding is null || binding.SchemaVersion != 1 || !Text(binding.ReceiverId, 256) || !ReceiverIdentity.IsBroadcast(binding.ReceiverId)
            || !Text(binding.OriginalName, 256) || !Text(binding.AssignedName, 256) || !VerifiedEndpoint(binding.Endpoint))
            return "Invalid or unsupported native output binding.";
        if (binding.Calibration is { } calibration && (!double.IsFinite(calibration.MaxDb)
            || calibration.ResumeDeviceVolume is < 1 or > 100
            || !Same(calibration.DriverSha256, binding.Endpoint.DriverSha256)
            || calibration.MixFormat != binding.Endpoint.MixFormat))
            return "The native output calibration does not match its driver or format.";
        if (binding.VerifiedBufferMs is < 0 or > 2000) return "The verified device buffer must be within 0..2000 ms.";
        if (binding.VerifiedBufferProfile is { } profile && (profile.Length != 64 || !profile.All(Uri.IsHexDigit)
            || binding.VerifiedBufferMs is null)) return "The verified device profile requires its SHA-256 fingerprint and buffer.";
        return null;
    }

    public static string? PlaybackQualification(NativeBinding binding) => ValidateStored(binding)
        ?? (binding.Calibration is null ? "Finish the measured Windows volume and audible reference calibration before enabling native output." : null);

    public static string? ValidateLive(NativeBinding binding, EndpointIdentity live)
    {
        if (ValidateStored(binding) is { } error) return error;
        if (binding.Calibration is null) return "The native output has not been calibrated.";
        if (!VerifiedEndpoint(live) || !Same(live.EndpointId, binding.Endpoint.EndpointId)
            || !Same(live.AdapterInstanceId, binding.Endpoint.AdapterInstanceId)
            || !Same(live.DriverSha256, binding.Calibration.DriverSha256)
            || live.MixFormat != binding.Calibration.MixFormat)
            return "The bound output is unavailable or its driver or format changed. Revalidate the binding.";
        return null;
    }
}

using System.Security.Cryptography;
using System.Text.Json;
namespace AirFlash.Core;

public sealed record NativeLatencyChoice(int Milliseconds, string Source, string ProfileKey);

public static class NativeLatencyAdaptation
{
    public static string ProfileKey(Receiver receiver, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        if (sampleRate is not (44100 or 48000)) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (receiver.Members.Length > 2 || !Valid(receiver) || receiver.Members.Any(member => !Valid(member)))
            throw new ArgumentException("Invalid receiver latency profile.", nameof(receiver));
        var data = JsonSerializer.SerializeToUtf8Bytes(new {
            version = 1, id = ReceiverIdentity.Normalize(receiver.Id), receiver.Model, receiver.SoftwareVersion,
            receiver.GroupId, receiver.StereoId, receiver.IsLeader, receiver.IsGroup,
            timing = receiver.PrefersNtpAlac ? "ntp/alac" : "ptp/receiver-codecs", sampleRate,
            members = receiver.Members.OrderBy(member => ReceiverIdentity.Normalize(member.Id), StringComparer.Ordinal)
                .Select(member => new { id = ReceiverIdentity.Normalize(member.Id), member.Model, member.SoftwareVersion,
                    member.GroupId, member.StereoId, member.IsLeader })
        });
        return Convert.ToHexStringLower(SHA256.HashData(data));
    }

    public static int? VerifiedPreference(NativeBinding binding, Receiver receiver, int sampleRate)
    {
        if (NativeBindingPolicy.ValidateStored(binding) is not null || binding.VerifiedBufferMs is null
            || sampleRate is not (44100 or 48000)
            || ReceiverIdentity.Normalize(binding.ReceiverId) != ReceiverIdentity.Normalize(receiver.Id)) return null;
        if (binding.VerifiedBufferProfile is null)
            return receiver.PrefersNtpAlac && sampleRate == 44100 ? binding.VerifiedBufferMs : null;
        return binding.VerifiedBufferProfile.Equals(ProfileKey(receiver, sampleRate), StringComparison.OrdinalIgnoreCase)
            ? binding.VerifiedBufferMs : null;
    }

    public static NativeLatencyChoice SelectUnreported(Receiver receiver, int sampleRate, int? recoveryCandidateMs = null)
    {
        var profile = ProfileKey(receiver, sampleRate);
        var model = receiver.PrefersNtpAlac && sampleRate == 44100;
        // shortcut: this model/firmware has a normal-music baseline; add profiles only after hardware qualification.
        var initial = model ? 100 : 250;
        if (recoveryCandidateMs is { } candidate && (candidate < initial || candidate > 2000))
            throw new ArgumentOutOfRangeException(nameof(recoveryCandidateMs));
        return new(recoveryCandidateMs ?? initial,
            recoveryCandidateMs is not null ? "music_recovery" : model ? "model_profile" : "compatibility_fallback", profile);
    }

    public static int? NextCandidate(int currentMs)
    {
        if (currentMs is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(currentMs));
        return currentMs == 2000 ? null : Math.Min(currentMs * 2, 2000);
    }

    // The caller owns the explicit normal-music acknowledgement; transport feedback is not audible confirmation.
    public static NativeBinding ConfirmReference(NativeBinding binding, Receiver receiver, int sampleRate, int appliedMs)
    {
        NativeSetupReferenceProbe.ValidateReceiver(binding, receiver);
        if (sampleRate != 44100) throw new InvalidOperationException("This native sample rate has not been qualified for normal-music confirmation.");
        if (appliedMs is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(appliedMs));
        if (VerifiedPreference(binding, receiver, sampleRate) is not null) return binding;
        return binding with { VerifiedBufferMs = appliedMs, VerifiedBufferProfile = ProfileKey(receiver, sampleRate) };
    }

    private static bool Valid(Receiver receiver) => !string.IsNullOrWhiteSpace(receiver.Id)
        && new[] { receiver.Id, receiver.Model, receiver.SoftwareVersion, receiver.GroupId, receiver.StereoId }
            .All(value => value is not null && value.Length <= 256 && !value.Any(char.IsControl));
}

namespace AirFlash.Core;

public sealed class NativeSetupPendingException(string message) : Exception(message);

public static class NativeMusicConfirmation
{
    public static int ConfirmableAppliedMs(NativeBinding binding, Receiver receiver, SessionSnapshot snapshot, int sampleRate)
    {
        RequireContext(binding, receiver, snapshot, sampleRate);
        var diagnostics = snapshot.Diagnostics; var transport = diagnostics?.Transport; var latency = diagnostics?.Latency;
        if (snapshot.State != PlaybackState.Streaming || snapshot.Receiver is null || snapshot.StreamRate != sampleRate
            || latency?.AppliedMs is not (>= 1 and <= 2000) || !KnownHost(latency.Host)
            || transport?.SessionUptimeMs is not { } uptime || !double.IsFinite(uptime) || uptime < 5000
            || transport.Members.Count != 1 || transport.SkippedPackets != 0
            || diagnostics?.LastFault is not null || diagnostics?.ReconnectCount != 0)
            throw Pending();
        var member = transport.Members[0];
        if (!KnownHost(member.Host) || member.NonzeroPcmPacketsSent is not > 0 || member.PacketsSent is not > 0 || member.FeedbackDelayed != false
            || member.SendErrors != 0 || member.SyncErrors != 0 || member.FeedbackFailures != 0
            || member.RetransmitSendErrors != 0 || member.RetransmitMissing != 0
            || member.RetransmitExpired != 0 || member.RetransmitQueueDrops != 0)
            throw Pending();
        return latency.AppliedMs.Value;
    }

    public static int ConfirmableAppliedMs(NativeBinding binding, Receiver receiver, SessionSnapshot current, int sampleRate, SessionSnapshot baseline)
    {
        RequireContext(binding, receiver, baseline, sampleRate); RequireContext(binding, receiver, current, sampleRate);
        var before = baseline.Diagnostics; var after = current.Diagnostics;
        var previousLatency = before?.Latency; var latency = after?.Latency;
        if (previousLatency?.AppliedMs is not null && latency?.AppliedMs is not null && previousLatency.AppliedMs != latency.AppliedMs
            || before is not null && after is not null && (before.ReconnectCount != after.ReconnectCount || before.LastFault != after.LastFault))
            throw Pending();
        var previousMember = before?.Transport?.Members.Count == 1 ? before.Transport.Members[0] : null;
        var member = after?.Transport?.Members.Count == 1 ? after.Transport.Members[0] : null;
        var previousErrors = ErrorCounters(baseline); var errors = ErrorCounters(current);
        if (previousErrors.Zip(errors).Any(pair => pair.First is >= 0 && pair.Second is >= 0 && pair.Second < pair.First)
            || previousMember?.PacketsSent is >= 0 && member?.PacketsSent < previousMember.PacketsSent
            || previousMember?.NonzeroPcmPacketsSent is >= 0 && member?.NonzeroPcmPacketsSent < previousMember.NonzeroPcmPacketsSent)
            throw Pending();
        if (!HasWindowEvidence(baseline, sampleRate))
        {
            if (previousErrors.Any(value => value is > 0) || before?.LastFault is not null || before?.ReconnectCount is > 0) throw Pending();
            return ConfirmableAppliedMs(binding, receiver, current, sampleRate);
        }
        if (!HasWindowEvidence(current, sampleRate) || latency?.AppliedMs != previousLatency?.AppliedMs
            || after!.Transport!.SessionUptimeMs < before!.Transport!.SessionUptimeMs + 5000
            || !previousErrors.SequenceEqual(errors) || member!.FeedbackDelayed != false
            || member.PacketsSent <= previousMember!.PacketsSent || member.NonzeroPcmPacketsSent <= previousMember.NonzeroPcmPacketsSent)
            throw Pending();
        return latency!.AppliedMs!.Value;
    }

    private static bool HasWindowEvidence(SessionSnapshot snapshot, int sampleRate)
    {
        if (snapshot.State != PlaybackState.Streaming || snapshot.Receiver is null || snapshot.StreamRate != sampleRate
            || snapshot.Diagnostics is not { ReconnectCount: >= 0, Latency: { AppliedMs: >= 1 and <= 2000 } latency,
                Transport: { Members.Count: 1, SessionUptimeMs: >= 0 } transport } || !KnownHost(latency.Host)
            || !double.IsFinite(transport.SessionUptimeMs!.Value)) return false;
        var member = transport.Members[0];
        return KnownHost(member.Host) && member.PacketsSent is >= 0 && member.NonzeroPcmPacketsSent is >= 0
            && ErrorCounters(snapshot).All(value => value is >= 0);
    }

    private static long?[] ErrorCounters(SessionSnapshot snapshot)
    {
        var transport = snapshot.Diagnostics?.Transport;
        var member = transport?.Members.Count == 1 ? transport.Members[0] : null;
        return [transport?.SkippedPackets, transport?.SenderLateRecoveries, member?.SendErrors, member?.SyncErrors,
            member?.FeedbackFailures, member?.RetransmitRequests, member?.RetransmitsSent, member?.RetransmitMissing,
            member?.RetransmitExpired, member?.RetransmitQueueDrops, member?.RetransmitSendErrors];
    }

    public static int? RecoveryCandidateMs(NativeBinding binding, Receiver receiver, SessionSnapshot snapshot, int attempts, int sampleRate)
    {
        if (attempts is < 0 or >= 2 || NativeLatencyAdaptation.VerifiedPreference(binding, receiver, sampleRate) is not null
            || snapshot.State != PlaybackState.Streaming || snapshot.Receiver is null || snapshot.StreamRate != sampleRate
            || snapshot.Diagnostics?.Latency is not { AppliedMs: >= 1 and <= 2000 } latency || !KnownHost(latency.Host)
            || latency.ReceiverMinSamples is > 0 || latency.ReceiverMaxSamples is > 0) return null;
        try { RequireContext(binding, receiver, snapshot, sampleRate); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException) { return null; }
        return NativeLatencyAdaptation.NextCandidate(latency.AppliedMs.Value);
    }

    private static void RequireContext(NativeBinding binding, Receiver receiver, SessionSnapshot snapshot, int sampleRate)
    {
        NativeSetupReferenceProbe.ValidateReceiver(binding, receiver);
        if (snapshot.Receiver is { } actual && (ReceiverIdentity.Normalize(actual.Id) != ReceiverIdentity.Normalize(receiver.Id)
            || actual.TransportKey != receiver.TransportKey
            || NativeLatencyAdaptation.ProfileKey(actual, sampleRate) != NativeLatencyAdaptation.ProfileKey(receiver, sampleRate))
            || snapshot.StreamRate != 0 && snapshot.StreamRate != sampleRate
            || snapshot.Diagnostics?.Latency is { } latency && (latency.SampleRate != sampleRate
                || latency.PreferredMs != NativeLatencyAdaptation.VerifiedPreference(binding, receiver, sampleRate)
                || KnownHost(latency.Host) && (System.Net.IPAddress.TryParse(receiver.Address, out var address) && latency.Host != address.ToString()
                    || snapshot.Diagnostics.Transport is { Members.Count: 1 } transport && KnownHost(transport.Members[0].Host) && transport.Members[0].Host != latency.Host)))
            throw new InvalidOperationException("The normal-music evidence belongs to a different receiver, connection or sample rate. Reconnect before confirming.");
    }

    private static NativeSetupPendingException Pending() => new("Keep playing normal music through the Windows HomePod output. Connection evidence is still incomplete; settings have not been saved.");
    private static bool KnownHost(string host) => !string.IsNullOrWhiteSpace(host) && host != "—";
}

using System.Net;
using System.Net.Sockets;
namespace AirFlash.Core;

public static class NativeSetupReferenceProbe
{
    public static void ValidateReceiver(NativeBinding binding, Receiver receiver)
    {
        if (NativeBindingPolicy.ValidateStored(binding) is { } invalid) throw new InvalidOperationException(invalid);
        if (ReceiverIdentity.Normalize(receiver.Id) != ReceiverIdentity.Normalize(binding.ReceiverId)
            || !ReceiverIdentity.IsBroadcast(receiver.Id) || !receiver.Online || !receiver.Complete || !receiver.PrefersNtpAlac
            || receiver.Port is < 1 or > 65535)
            throw new InvalidOperationException("Select the bound, online HomePod with a qualified standalone profile before calibration.");
    }

    public static async Task<int> ReadAsync(IEngineConnection connection, NativeBinding binding, Receiver receiver,
        string host, CancellationToken cancellation)
    {
        ValidateReceiver(binding, receiver);
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Resolve the selected receiver to its current IPv4 address before calibration.", nameof(host));
        host = address.ToString();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var session = Guid.NewGuid().ToString("N"); int? reference = null;
        await connection.SendAsync(session, "probe", new {
            peers = new[] { new { host, port = receiver.Port, codecs = new[] { 1 } } },
            source = "loopback", duration_ms = 1, gain = .1, timing = "ntp", sample_rate = 44100,
            handshake_only = true, latency_policy = "negotiate", latency_ms = binding.VerifiedBufferMs
        }, timeout.Token).ConfigureAwait(false);
        for (var count = 0; count < 64; count++)
        {
            var item = await connection.ReadAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new IOException("The reference handshake ended before confirming a nonzero receiver volume.");
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object || item.Integer("version") != 1)
                throw new IOException("The reference handshake returned an incompatible event.");
            if (item.Text("session_id") != session) continue;
            switch (item.Text("event"))
            {
                case "error": throw new IOException(DiagnosticText.Protect(item.Text("message", "The reference handshake failed.")));
                case "device_volume":
                    if (item.Text("host") != host || item.Boolean("available") != true || item.Text("status") != "confirmed"
                        || item.Integer("sequence") != 0 || item.Integer("volume") is not (>= 1 and <= 100))
                        throw new IOException("The receiver has no confirmed nonzero reference volume. Set a comfortable music volume before retrying.");
                    reference = (int)item.Integer("volume")!.Value;
                    break;
                case "handshake_complete":
                    if (item.Boolean("audio_sent") != false || item.Integer("members") != 1 || reference is null)
                        throw new IOException("The reference requires a single receiver handshake with no audio sent.");
                    return reference.Value;
                case "streaming":
                case "stopped": throw new IOException("The reference handshake did not remain free of audio playback.");
            }
        }
        throw new IOException("The reference handshake exceeded its event limit.");
    }
}

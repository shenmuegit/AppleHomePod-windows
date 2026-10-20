using System.Text.Json.Serialization;
namespace AirFlash.Core;

public sealed record NativeAgentStatus(int Version, bool CaptureReady = false, string ReceiverId = "",
    long Generation = 0, bool ReceiverDiscovered = false, PlaybackState State = PlaybackState.Idle,
    bool GateOpen = false, bool MediaStarted = false, string? Error = null, Guid InstanceId = default)
{
    [JsonIgnore]
    public bool TransportReady => Validate() is null && CaptureReady && ReceiverDiscovered && Generation > 0
        && State == PlaybackState.Streaming && GateOpen && MediaStarted && string.IsNullOrEmpty(Error);

    public string? Validate() => Version != 1 || Generation < 0 || !Enum.IsDefined(State)
        || ReceiverId is null || ReceiverId.Length > 256 || ReceiverId.Any(char.IsControl)
        || CaptureReady && (!ReceiverIdentity.IsBroadcast(ReceiverId) || InstanceId == Guid.Empty) || Error?.Length > 1024
        ? "Invalid or unsupported native background status." : null;

    public static async Task<NativeAgentStatus> WaitForTransportAsync(
        Func<CancellationToken, Task<NativeAgentStatus?>> query, string receiverId, CancellationToken cancellation,
        long? expectedGeneration = null, TimeSpan? timeout = null, Guid? expectedInstanceId = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!ReceiverIdentity.IsBroadcast(receiverId) || expectedGeneration is < 0)
            throw new ArgumentException("A valid HomePod identity and route generation are required.");
        cancellation.ThrowIfCancellationRequested();
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        ready.CancelAfter(timeout ?? TimeSpan.FromSeconds(12));
        try
        {
            while (true)
            {
                ready.Token.ThrowIfCancellationRequested();
                var status = await query(ready.Token).WaitAsync(ready.Token).ConfigureAwait(false);
                ready.Token.ThrowIfCancellationRequested();
                if (status is null || status.Validate() is not null)
                    throw new IOException(L.Get("The AppleHome background did not provide connection status. Reconnect, or update AppleHome if the installed background is an older version."));
                if (status.ReceiverId.Length > 0 && ReceiverIdentity.Normalize(status.ReceiverId) != ReceiverIdentity.Normalize(receiverId))
                    throw new IOException(L.Get("The HomePod target changed during audio preparation. Reconnect before continuing."));
                if (status.State == PlaybackState.Error)
                    throw new IOException(DiagnosticText.Protect(status.Error ?? "The HomePod connection failed."));
                if (status.InstanceId != Guid.Empty)
                {
                    expectedInstanceId ??= status.InstanceId;
                    if (status.InstanceId != expectedInstanceId)
                        throw new IOException(L.Get("The AppleHome background restarted during audio preparation. Reconnect before continuing."));
                }
                if (status.Generation > 0)
                {
                    expectedGeneration ??= status.Generation;
                    if (status.Generation != expectedGeneration)
                        throw new IOException(L.Get("The HomePod route changed during audio preparation. Reconnect before continuing."));
                }
                if (status.TransportReady) return status;
                await Task.Delay(50, ready.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException(L.Get("The HomePod transport did not become ready before the audio preparation deadline."), error); }
    }
}

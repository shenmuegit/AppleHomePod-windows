using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeAgentStatusTests
{
    private const string Device = "001122334455";
    private static NativeAgentStatus Ready => new(1, true, Device, 3, true, PlaybackState.Streaming, true, true,
        InstanceId: Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public void CaptureReadyDoesNotClaimCurrentRouteHasSentMedia()
    {
        Assert.True(Ready.TransportReady);
        Assert.False(Ready with { CaptureReady = false } is { TransportReady: true });
        Assert.False(Ready with { ReceiverDiscovered = false } is { TransportReady: true });
        Assert.False(Ready with { State = PlaybackState.Connecting } is { TransportReady: true });
        Assert.False(Ready with { GateOpen = false } is { TransportReady: true });
        Assert.False(Ready with { MediaStarted = false } is { TransportReady: true });
        Assert.False(Ready with { Generation = 0 } is { TransportReady: true });
    }

    [Fact]
    public void ContractRejectsUnknownVersionsUnboundedFieldsAndInvalidState()
    {
        Assert.Null(Ready.Validate());
        foreach (var invalid in new[] {
            Ready with { Version = 2 }, Ready with { Generation = -1 },
            Ready with { ReceiverId = null! }, Ready with { ReceiverId = new string('a', 257) },
            Ready with { ReceiverId = "bad\0id" }, Ready with { State = (PlaybackState)99 },
            Ready with { InstanceId = Guid.Empty },
            Ready with { Error = new string('e', 1025) } })
        {
            Assert.NotNull(invalid.Validate()); Assert.False(invalid.TransportReady);
        }
    }

    [Fact]
    public async Task AnAlreadySendingRouteCompletesWithoutAnArtificialSettleDelay()
    {
        var calls = 0;
        var task = NativeAgentStatus.WaitForTransportAsync(_ => { calls++; return Task.FromResult<NativeAgentStatus?>(Ready); }, Device, default);
        Assert.True(task.IsCompletedSuccessfully); Assert.Equal(1, calls); Assert.Equal(Ready, await task);
    }

    [Fact]
    public async Task WaitRequiresDiscoveryAnOpenGateAndTheActualFirstSend()
    {
        var states = new Queue<NativeAgentStatus>([
            Ready with { ReceiverDiscovered = false, Generation = 0, State = PlaybackState.Idle, GateOpen = false, MediaStarted = false },
            Ready with { State = PlaybackState.Connecting, GateOpen = false, MediaStarted = false },
            Ready with { GateOpen = false, MediaStarted = false }, Ready with { MediaStarted = false }, Ready]);
        var result = await NativeAgentStatus.WaitForTransportAsync(_ => Task.FromResult<NativeAgentStatus?>(states.Dequeue()), Device, default,
            timeout: TimeSpan.FromSeconds(3));
        Assert.Equal(Ready, result); Assert.Empty(states);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("generation")]
    [InlineData("instance")]
    [InlineData("protocol")]
    [InlineData("error")]
    public async Task WrongTargetChangedRouteOrFailedBackendCannotBecomeReady(string failure)
    {
        var next = failure switch {
            "target" => Ready with { ReceiverId = "aabbccddeeff" },
            "generation" => Ready with { Generation = 4 },
            "instance" => Ready with { InstanceId = Guid.NewGuid() },
            "protocol" => Ready with { Version = 2 },
            _ => Ready with { State = PlaybackState.Error, Error = "connection failed" }
        };
        var states = new Queue<NativeAgentStatus>([Ready with { MediaStarted = false }, next]);
        await Assert.ThrowsAsync<IOException>(() => NativeAgentStatus.WaitForTransportAsync(
            _ => Task.FromResult<NativeAgentStatus?>(states.Dequeue()), Device, default, timeout: TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task LegacyPeerAndNeverReadyPeerAreNotAccepted()
    {
        await Assert.ThrowsAsync<IOException>(() => NativeAgentStatus.WaitForTransportAsync(
            _ => Task.FromResult<NativeAgentStatus?>(null), Device, default));
        await Assert.ThrowsAsync<TimeoutException>(() => NativeAgentStatus.WaitForTransportAsync(
            _ => Task.FromResult<NativeAgentStatus?>(Ready with { MediaStarted = false }), Device, default, timeout: TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task ExplicitRouteGenerationAndCallerCancellationArePreserved()
    {
        await Assert.ThrowsAsync<IOException>(() => NativeAgentStatus.WaitForTransportAsync(
            _ => Task.FromResult<NativeAgentStatus?>(Ready), Device, default, expectedGeneration: 2));
        using var stop = new CancellationTokenSource(); stop.Cancel(); var queried = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeAgentStatus.WaitForTransportAsync(
            _ => { queried = true; return Task.FromResult<NativeAgentStatus?>(Ready); }, Device, stop.Token));
        Assert.False(queried);
    }

    [Fact]
    public async Task CancellationInsideSuccessfulQueryCannotReportTransportReady()
    {
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeAgentStatus.WaitForTransportAsync(
            _ => { stop.Cancel(); return Task.FromResult<NativeAgentStatus?>(Ready); }, Device, stop.Token));
    }
}

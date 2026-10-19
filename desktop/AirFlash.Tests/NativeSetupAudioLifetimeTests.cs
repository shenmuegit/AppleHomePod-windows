using AirFlash.App.Verification;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeSetupAudioLifetimeTests
{
    [Fact]
    public async Task StartupFailureIsReportedButReleasedOwnershipCanBeRetried()
    {
        using var ownership = new SemaphoreSlim(1, 1);
        var failure = new IOException("device initialization failed");
        var lifetime = new NativeSetupAudioLifetime(_ => { });
        lifetime.Run(() => { ownership.Wait(); throw failure; }, () => ownership.Release());
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => lifetime.Ready));
        await lifetime.Stopped;
        Assert.True(ownership.Wait(0));
    }

    [Fact]
    public async Task CleanupFailureRemainsDistinctFromTheOriginalStartupFailure()
    {
        var startup = new IOException("device initialization failed");
        var cleanup = new InvalidOperationException("device release failed");
        var lifetime = new NativeSetupAudioLifetime(_ => { });
        lifetime.Run(() => throw startup, () => throw cleanup);
        Assert.Same(startup, await Assert.ThrowsAsync<IOException>(() => lifetime.Ready));
        Assert.Same(cleanup, await Assert.ThrowsAsync<InvalidOperationException>(() => lifetime.Stopped));
    }

    [Fact]
    public async Task PlaybackFailureEndsPendingPcmWithoutFalselyFailingCleanup()
    {
        using var source = new SetupConfirmationPlayback(8000, 1);
        var playing = source.PlayAsync(default);
        var failure = new IOException("device disconnected");
        var lifetime = new NativeSetupAudioLifetime(source.Stop);
        lifetime.Run(() => { lifetime.MarkReady(); throw failure; }, source.Dispose);
        await lifetime.Ready; await lifetime.Stopped;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => playing));
    }
}

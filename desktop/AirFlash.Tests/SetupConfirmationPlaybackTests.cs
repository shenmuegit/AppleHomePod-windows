using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class SetupConfirmationPlaybackTests
{
    [Fact]
    public async Task PreparedSourceStaysSilentBetweenFullLengthReplays()
    {
        using var source = new SetupConfirmationPlayback(8000, 1);
        var output = Enumerable.Repeat(1f, 12001).ToArray();
        source.Read(output); Assert.All(output, value => Assert.Equal(0f, value));
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var playing = source.PlayAsync(default);
            Assert.False(playing.IsCompleted);
            source.Read(output.AsSpan(0, 1)); Assert.False(playing.IsCompleted);
            source.Read(output.AsSpan(1)); await playing;
            Assert.Equal(SetupConfirmationPcm.Create(8000, 1), output[..12000]);
            Assert.Equal(0f, output[12000]);
            Assert.NotNull(source.LastDispatchDelay);
            source.Read(output); Assert.All(output, value => Assert.Equal(0f, value));
        }
    }

    [Fact]
    public async Task CancelledReplayCannotLeakItsRemainingPcmIntoTheNextReplay()
    {
        using var source = new SetupConfirmationPlayback(8000, 1);
        using var cancellation = new CancellationTokenSource();
        var playing = source.PlayAsync(cancellation.Token);
        var output = new float[512]; source.Read(output);
        cancellation.Cancel();
        source.Read(output); Assert.All(output, value => Assert.Equal(0f, value));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playing);
        var replay = source.PlayAsync(default);
        var all = new float[12000]; source.Read(all); await replay;
        Assert.Equal(SetupConfirmationPcm.Create(8000, 1), all);
    }

    [Fact]
    public async Task ConcurrentReplayIsRejectedAndDeviceFailureEndsThePendingReplay()
    {
        using var source = new SetupConfirmationPlayback(8000, 1);
        var playing = source.PlayAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.PlayAsync(default));
        source.Stop(new IOException("render device lost"));
        var error = await Assert.ThrowsAsync<IOException>(() => playing);
        Assert.Equal("render device lost", error.Message);
        await Assert.ThrowsAsync<IOException>(() => source.PlayAsync(default));
        var output = new float[512]; source.Read(output);
        Assert.All(output, value => Assert.Equal(0f, value));
    }

    [Fact]
    public async Task SilentDiagnosticUsesTheSameFiniteDispatchWithoutEmittingTheChime()
    {
        using var source = new SetupConfirmationPlayback(8000, 1);
        var playing = source.PlayAsync(default, silent: true);
        var output = Enumerable.Repeat(1f, 12000).ToArray(); source.Read(output); await playing;
        Assert.All(output, value => Assert.Equal(0f, value));
        Assert.NotNull(source.LastDispatchDelay);
    }

    [Fact]
    public async Task UnvalidatedBindingRejectsPendingPcmWithoutPoisoningAnIdlePreparedSource()
    {
        using var source = new SetupConfirmationPlayback(8000, 1);
        source.RejectPlayback(new IOException("validation pending while idle"));
        var playing = source.PlayAsync(default);
        var output = new float[12000]; source.Read(output.AsSpan(0, 512));
        var failure = new IOException("validation pending during playback");
        source.RejectPlayback(failure); source.Read(output);
        var wasSilent = output.All(value => value == 0);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => playing));
        Assert.True(wasSilent);
    }
}

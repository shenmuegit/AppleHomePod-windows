using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AirFlash.App.Services;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeStatusPipeTests
{
    private static NativeAgentStatus Ready => new(1, true, "001122334455", 3, true, PlaybackState.Streaming, true, true,
        InstanceId: Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public async Task VersionedStatusUsesRealPipeWithoutChangingExistingReadyAndStopCommands()
    {
        var suffix = "status-test-" + Guid.NewGuid().ToString("N");
        await using var owner = new SingleInstance(suffix);
        var stopped = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        var status = Ready with { MediaStarted = false };
        owner.Listen(command => stopped.TrySetResult(command), () => true, () => Volatile.Read(ref status));
        await using var client = new SingleInstance(suffix);
        Assert.True(await client.QueryReadyAsync(default));
        Assert.False((await client.QueryStatusAsync(default))!.TransportReady);
        Volatile.Write(ref status, Ready);
        Assert.Equal(Ready, await client.QueryStatusAsync(default));
        await client.NotifyExistingAsync(2); Assert.Equal(2, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task APeerWithoutStatusDoesNotBecomeTransportReadyOrLoseItsListener()
    {
        var suffix = "status-legacy-test-" + Guid.NewGuid().ToString("N");
        await using var owner = new SingleInstance(suffix); owner.Listen(_ => { }, () => true);
        await using var client = new SingleInstance(suffix);
        Assert.Null(await client.QueryStatusAsync(default));
        Assert.True(await client.QueryReadyAsync(default));
    }

    [Theory]
    [InlineData(-1, "")]
    [InlineData(8193, "")]
    [InlineData(1, "{")]
    [InlineData(2, "{}")]
    public async Task InvalidLengthsMalformedJsonAndMissingVersionAreRejected(int length, string content)
    {
        Assert.Null(await ReadPeer(length, Encoding.UTF8.GetBytes(content)));
    }

    [Theory]
    [InlineData(2, 3, 2)]
    [InlineData(1, -1, 2)]
    [InlineData(1, 3, 99)]
    public async Task UnknownVersionGenerationAndStateAreRejected(int version, long generation, int state)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(Ready with { Version = version, Generation = generation, State = (PlaybackState)state });
        Assert.Null(await ReadPeer(payload.Length, payload));
    }

    [Fact]
    public async Task ATruncatedReplyAndCallerCancellationRemainFailures()
    {
        Assert.Null(await ReadPeer(100, Encoding.UTF8.GetBytes("{}")));
        await using var client = new SingleInstance("status-cancel-test-" + Guid.NewGuid().ToString("N"));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.QueryStatusAsync(stop.Token));
    }

    private static async Task<NativeAgentStatus?> ReadPeer(int length, byte[] payload)
    {
        var suffix = "status-wire-test-" + Guid.NewGuid().ToString("N");
        await using var owner = new SingleInstance(suffix);
        using var identity = WindowsIdentity.GetCurrent();
        var pipeName = $"{AppPaths.Name}.Wpf.{identity.User!.Value}.{suffix}";
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var peer = Task.Run(async () => {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(stop.Token);
            var input = new byte[1]; await pipe.ReadExactlyAsync(input, stop.Token); Assert.Equal(4, input[0]);
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
            await pipe.WriteAsync(header, stop.Token);
            if (payload.Length > 0) await pipe.WriteAsync(payload, stop.Token);
            await pipe.FlushAsync(stop.Token);
        }, stop.Token);
        await using var client = new SingleInstance(suffix);
        var result = await client.QueryStatusAsync(stop.Token); await peer;
        return result;
    }
}

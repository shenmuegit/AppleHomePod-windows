using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeEngineHostTests
{
    internal static NativeBinding Binding
    {
        get
        {
            var endpoint = new EndpointIdentity("endpoint-1", @"ROOT\MEDIA\0009", "VBAudioVACWDM", "VB-Audio Software", "3.3.1.7", NativeBindingPolicy.DriverSha256, "CABLE Input", "HomePod – da", "44100/2/32/3", 2, AudioEndpointKind.Playback, true, "HomePod – da");
            return new(1, "receiver-1", endpoint, "CABLE Input", "HomePod – da", new(false, endpoint.MixFormat, endpoint.DriverSha256, 0, 38));
        }
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"latency_negotiation\":false}")]
    [InlineData("{\"latency_negotiation\":\"true\"}")]
    public async Task NativeHelloRejectsAnEngineWithoutNegotiationCapability(string capability)
    {
        var wire = new WireConnection { HelloReplies = false };
        await using var host = new NativeEngineHost(wire, 42);
        var hello = EngineHandshake.HelloAsync(host);
        await Until(() => wire.Commands.Any(c => c.Command == "hello"));
        var reply = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(capability)!; reply["event"] = "hello";
        wire.Event("check", reply);
        var error = await Assert.ThrowsAsync<IOException>(() => hello);
        Assert.Contains("negotiation", error.Message, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task LeaseCancellationDoesNotCancelHalfLinePumpOrLoseNextSession()
    {
        var wire = new WireConnection { HelloReplies = false };
        await using var host = new NativeEngineHost(wire, 42);
        await host.ArmAsync(Binding, default);
        var first = host.Open();
        await first.SendAsync("first", "hello", null, default);
        var prefix = "{\"version\":1,\"session_id\":\"first\",";
        wire.Feed(prefix);
        await Until(() => wire.CharactersRead >= prefix.Length);
        using var cancel = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.ReadAsync(cancel.Token).AsTask());
        await first.DisposeAsync();
        await using var second = host.Open();
        await second.SendAsync("second", "hello", null, default);
        wire.Feed("\"event\":\"late\"}\n");
        wire.Event("second", new { @event = "streaming" });
        var resumed = (await second.ReadAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(2)))!.Value;
        Assert.Equal("streaming", resumed.Text("event"));
        Assert.Equal("second", resumed.Text("session_id"));
        Assert.Equal(0, wire.CancelledReads);
        Assert.False(wire.Disposed);
    }
    [Fact]
    public async Task LeaseDisposeWaitsForStopAckResetsEpochAndKeepsArmedHost()
    {
        var wire = new WireConnection { StopReplies = false };
        await using var host = new NativeEngineHost(wire, 42);
        await host.ArmAsync(Binding, default);
        await host.BeginCaptureAsync(default);
        await host.BeginCaptureAsync(default);
        var first = host.Open();
        await first.SendAsync("one", "start", Start(), default);
        var disposing = first.DisposeAsync().AsTask();
        await Until(() => wire.Commands.Any(c => c.Command == "stop"));
        wire.Event("one", new { @event = "stopped", cancelled = true });
        await Task.Delay(20);
        Assert.False(disposing.IsCompleted);
        wire.Event("one", new { @event = "stopped" });
        await disposing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(wire.Disposed);
        Assert.Equal("capture-instance", host.CaptureSessionInstanceId);
        wire.StopReplies = true;
        await using var second = host.Open();
        await second.SendAsync("two", "start", Start(), default);
        Assert.Equal(2, wire.Commands.Last(c => c.Command == "start").Params.Integer("capture_epoch"));
        Assert.Equal(2, wire.Commands.Count(c => c.Command == "begin_capture"));
        Assert.Single(wire.Commands, c => c.Command == "stop");
    }
    [Fact]
    public async Task HelloLeaseDisposalDoesNotCloseOrResetCapture()
    {
        var wire = new WireConnection();
        await using var host = new NativeEngineHost(wire, 42);
        await host.ArmAsync(Binding, default);
        Assert.Equal("hello", (await EngineHandshake.HelloAsync(host)).Text("event"));
        Assert.False(wire.Disposed);
        Assert.DoesNotContain(wire.Commands, c => c.Command is "stop" or "reset_capture");
    }
    [Fact]
    public async Task PreparedCaptureCanResetWhenDemandEndsBeforeTransportStarts()
    {
        var wire = new WireConnection();
        await using var host = new NativeEngineHost(wire, 42);
        await host.ArmAsync(Binding, default); await host.BeginCaptureAsync(default);
        await host.ResetCaptureAsync(default); await host.ResetCaptureAsync(default);
        Assert.Single(wire.Commands, c => c.Command == "reset_capture");
        await using var lease = host.Open(); await lease.SendAsync("next", "start", Start(), default);
        Assert.Equal(2, wire.Commands.Last(c => c.Command == "start").Params.Integer("capture_epoch"));
    }
    [Fact]
    public async Task OldEpochGateCannotOpenTheReplacementNetworkSession()
    {
        var wire = new WireConnection();
        await using var host = new NativeEngineHost(wire, 42);
        await host.ArmAsync(Binding, default);
        var first = host.Open(); await first.SendAsync("old", "start", Start(), default);
        var oldEpoch = host.CaptureEpoch; await first.DisposeAsync();
        await using var next = host.Open(); await next.SendAsync("new", "start", Start(), default);
        var before = wire.Commands.Count(c => c.Command == "set_capture_gate");
        Assert.False(await host.TrySetCaptureGateAsync(oldEpoch, true, true, default));
        Assert.Equal(before, wire.Commands.Count(c => c.Command == "set_capture_gate"));
        Assert.True(await host.TrySetCaptureGateAsync(host.CaptureEpoch, true, false, default));
        Assert.False(host.Completion.IsCompleted);
    }
    [Theory]
    [InlineData("{invalid}\n")]
    [InlineData("{\"event\":")]
    [InlineData("")]
    public async Task BrokenJsonOrEofFaultsHostAndRejectsNewLeases(string bytes)
    {
        var wire = new WireConnection();
        await using var host = new NativeEngineHost(wire, 42);
        wire.Feed(bytes); wire.End();
        await Assert.ThrowsAsync<IOException>(() => host.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Throws<IOException>(() => { host.Open(); });
        await Until(() => wire.Disposed);
    }
    [Fact]
    public async Task OversizeLineAndLeaseOverflowAreFatalAndBounded()
    {
        foreach (var longLine in new[] { false, true })
        {
            var wire = new WireConnection { HelloReplies = false };
            await using var host = new NativeEngineHost(wire, 42);
            await using var lease = host.Open();
            await lease.SendAsync("overflow", "hello", null, default);
            if (longLine) wire.Feed(new string('x', 65537));
            else for (var i = 0; i < 65; i++) wire.Event("overflow", new { @event = "progress" });
            await Assert.ThrowsAsync<IOException>(() => host.Completion.WaitAsync(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAsync<IOException>(() => lease.SendAsync("overflow", "hello", null, default));
        }
    }
    [Fact]
    public async Task LateOldEventsCannotReachNewLeaseAndDifferentEndpointIsRejected()
    {
        var wire = new WireConnection();
        await using var host = new NativeEngineHost(wire, 42);
        await host.ArmAsync(Binding, default);
        var old = host.Open(); await old.SendAsync("old", "start", Start(), default); await old.DisposeAsync();
        await using var next = host.Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.SendAsync("next", "start", new { source = "loopback", capture_endpoint = "different", sample_rate = 44100 }, default));
        await next.SendAsync("next", "start", Start(), default);
        wire.Event("old", new { @event = "error", code = "late" });
        wire.Event("next", new { @event = "streaming" });
        Assert.Equal("streaming", (await next.ReadAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(2)))!.Value.Text("event"));
        Assert.False(host.Completion.IsCompleted);
    }
    [Fact]
    public async Task MissingStopAckClosesOwnedProcessInsteadOfReusingOldStream()
    {
        var wire = new WireConnection { StopReplies = false };
        await using var host = new NativeEngineHost(wire, 42);
        await host.ArmAsync(Binding, default);
        var lease = host.Open(); await lease.SendAsync("one", "start", Start(), default);
        await Assert.ThrowsAsync<IOException>(() => lease.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)));
        await Assert.ThrowsAsync<IOException>(() => host.Completion);
        Assert.Throws<IOException>(() => { host.Open(); });
        await Until(() => wire.Disposed);
    }
    [Fact]
    public async Task ConnectingGainUpdateIsWrittenBeforeCaptureGateOpens()
    {
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wire = new WireConnection { StartHold = releaseStart, StreamReplies = true };
        await using var host = new NativeEngineHost(wire, 42);
        await host.ArmAsync(Binding, default); await host.BeginCaptureAsync(default);
        await using var session = new SessionController(host, new NativeAudio());
        await session.SetNativeGainAsync(0);
        await session.StartAsync(new("receiver-1", "da", "127.0.0.1"), new() { CaptureMode = "endpoint", CaptureEndpoint = "endpoint-1", StreamSampleRate = "44100", MuteWhileStreaming = false, ForceReconnect = false });
        await Until(() => wire.Commands.Any(c => c.Command == "start"));
        var gain = session.SetNativeGainAsync(0.3);
        releaseStart.TrySetResult();
        await gain.WaitAsync(TimeSpan.FromSeconds(2));
        await host.SetCaptureGateAsync(true, false, default);
        var commands = wire.Commands.ToArray();
        var gainIndex = Array.FindLastIndex(commands, c => c.Command == "set_gain");
        Assert.Equal(0.3, commands[gainIndex].Params.Number("gain"));
        Assert.True(gainIndex < Array.FindLastIndex(commands, c => c.Command == "set_capture_gate"));
        Assert.Equal(0, commands.Single(c => c.Command == "start").Params.Number("gain"));
    }
    private static object Start() => new { source = "loopback", duration_ms = 0, capture_endpoint = "endpoint-1", sample_rate = 44100, gain = 0.1 };
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    internal sealed record Sent(string Session, string Command, JsonElement Params);
    internal sealed class WireConnection : IEngineConnection
    {
        private readonly FragmentReader _reader = new();
        public ConcurrentQueue<Sent> Commands { get; } = new();
        public bool HelloReplies = true, StopReplies = true;
        public bool StreamReplies, VolumeReplies;
        public int? InitialVolume;
        public TaskCompletionSource? StartHold;
        public bool Disposed;
        public int CharactersRead => _reader.CharactersRead;
        public int CancelledReads => _reader.CancelledReads;
        public void Feed(string text) { foreach (var c in text) _reader.Bytes.Writer.TryWrite(c); }
        public void End() => _reader.Bytes.Writer.TryComplete();
        public void Event(string session, object payload)
        {
            var node = JsonSerializer.SerializeToNode(payload)!.AsObject(); node["version"] = 1; node["session_id"] = session;
            Feed(node.ToJsonString() + "\n");
        }
        public async Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var p = JsonSerializer.SerializeToElement(parameters ?? new { }); Commands.Enqueue(new(session, command, p));
            switch (command)
            {
                case "arm_capture": Event(session, new { @event = "capture_ready", capture_id = p.Text("capture_id"), epoch = 1, session_instance_id = "capture-instance" }); break;
                case "begin_capture": Event(session, new { @event = "capture_begun", capture_id = p.Text("capture_id"), epoch = p.Integer("epoch") }); break;
                case "reset_capture": Event(session, new { @event = "capture_reset", capture_id = p.Text("capture_id"), epoch = p.Integer("epoch") + 1 }); break;
                case "set_capture_gate": Event(session, new { @event = "capture_gate", capture_id = p.Text("capture_id"), epoch = p.Integer("epoch"), open = p.Boolean("open") }); break;
                case "hello" when HelloReplies: Event(session, new { @event = "hello", latency_negotiation = true }); break;
                case "set_device_volume" when VolumeReplies: Volume(session, (int)p.Integer("volume")!.Value, p.Integer("sequence")!.Value); break;
                case "stop" when StopReplies: Event(session, new { @event = "stopped" }); break;
                case "start":
                    if (StartHold is not null) await StartHold.Task.WaitAsync(cancellation);
                    if (StreamReplies) Event(session, new { @event = "streaming" });
                    if (InitialVolume is { } value) Volume(session, value, 0);
                    break;
            }
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            var line = await ProcessEngineConnection.ReadBoundedLineAsync(_reader, cancellation);
            if (line is null) return null;
            return JsonSerializer.Deserialize<JsonElement>(line);
        }
        public ValueTask DisposeAsync() { Disposed = true; End(); return ValueTask.CompletedTask; }
        public void Volume(string session, int value, long sequence, string host = "127.0.0.1") => Event(session, new { @event = "device_volume", host, volume = value, sequence, status = "confirmed", available = true });
    }
    private sealed class NativeAudio : IAudioService
    {
        public event Action? EndpointsChanged { add { } remove { } }
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>("endpoint-1");
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task MuteAsync(string? endpointId) => throw new InvalidOperationException("Native mode must not mute physical outputs.");
        public Task RestoreAsync() => Task.CompletedTask;
    }
    private sealed class FragmentReader : TextReader
    {
        public Channel<char> Bytes { get; } = Channel.CreateUnbounded<char>();
        public int CharactersRead, CancelledReads;
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            try { var c = await Bytes.Reader.ReadAsync(cancellationToken); buffer.Span[0] = c; Interlocked.Increment(ref CharactersRead); return 1; }
            catch (ChannelClosedException) { return 0; }
            catch (OperationCanceledException) { Interlocked.Increment(ref CancelledReads); throw; }
        }
    }
}

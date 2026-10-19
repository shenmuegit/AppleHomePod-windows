using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeSetupReferenceProbeTests
{
    private static NativeBinding Binding => NativeEngineHostTests.Binding with { ReceiverId = "aabbccddeeff" };
    private static Receiver Mini => new("AA:BB:CC:DD:EE:FF", "Living room", "homepod.local")
        { Model = "AudioAccessory5,1", SoftwareVersion = "27.0", IsLeader = true, Codecs = [1] };

    [Theory]
    [InlineData(null)]
    [InlineData(100)]
    public async Task SetupRequestsOnlyAHandshakeAndKeepsTheDeviceSpecificReferenceNullable(int? verified)
    {
        await using var wire = new ProbeWire([Volume(), Complete()]);
        var result = await NativeSetupReferenceProbe.ReadAsync(wire, Binding with { VerifiedBufferMs = verified }, Mini, "192.0.2.12", default);
        Assert.Equal(38, result);
        Assert.Equal("probe", wire.Command);
        var request = wire.Parameters;
        Assert.Equal("loopback", request.Text("source"));
        Assert.Equal(1, request.Integer("duration_ms"));
        Assert.True(request.Boolean("handshake_only"));
        Assert.Equal("ntp", request.Text("timing"));
        Assert.Equal(44100, request.Integer("sample_rate"));
        Assert.Equal(.1, request.Number("gain"));
        Assert.Equal("negotiate", request.Text("latency_policy"));
        Assert.Equal(verified, request.Integer("latency_ms"));
        Assert.False(request.TryGetProperty("latency_max_ms", out _));
        Assert.False(request.TryGetProperty("wav_path", out _));
        Assert.False(request.TryGetProperty("capture_endpoint", out _));
        var peer = Assert.Single(request.GetProperty("peers").EnumerateArray());
        Assert.Equal("192.0.2.12", peer.Text("host"));
        Assert.Equal(7000, peer.Integer("port"));
        Assert.Equal(1, Assert.Single(peer.GetProperty("codecs").EnumerateArray()).GetInt32());
    }

    [Fact]
    public async Task StaleSessionCannotSupplyTheReferenceForTheCurrentReceiver()
    {
        await using var wire = new ProbeWire([Volume(99) with { Session = "stale" }, Volume(31), Complete()]);
        Assert.Equal(31, await NativeSetupReferenceProbe.ReadAsync(wire, Binding, Mini, "192.0.2.12", default));
    }
    [Fact]
    public async Task SilentReferenceHandshakeDoesNotReuseAProfileForAnotherSampleRate()
    {
        await using var wire = new ProbeWire([Volume(), Complete()]);
        var binding = Binding with { VerifiedBufferMs = 350, VerifiedBufferProfile = NativeLatencyAdaptation.ProfileKey(Mini, 48000) };
        Assert.Equal(38, await NativeSetupReferenceProbe.ReadAsync(wire, binding, Mini, "192.0.2.12", default));
        Assert.Null(wire.Parameters.Integer("latency_ms"));
        Assert.True(wire.Parameters.Boolean("handshake_only"));
    }

    [Theory]
    [InlineData("{\"event\":\"device_volume\",\"host\":\"192.0.2.12\",\"volume\":0,\"sequence\":0,\"status\":\"confirmed\",\"available\":true}")]
    [InlineData("{\"event\":\"device_volume\",\"host\":\"192.0.2.12\",\"volume\":101,\"sequence\":0,\"status\":\"confirmed\",\"available\":true}")]
    [InlineData("{\"event\":\"device_volume\",\"host\":\"192.0.2.12\",\"volume\":38.5,\"sequence\":0,\"status\":\"confirmed\",\"available\":true}")]
    [InlineData("{\"event\":\"device_volume\",\"host\":\"192.0.2.99\",\"volume\":38,\"sequence\":0,\"status\":\"confirmed\",\"available\":true}")]
    [InlineData("{\"event\":\"device_volume\",\"host\":\"192.0.2.12\",\"volume\":38,\"sequence\":0,\"status\":\"pending\",\"available\":true}")]
    [InlineData("{\"event\":\"device_volume\",\"host\":\"192.0.2.12\",\"volume\":38,\"sequence\":0,\"status\":\"confirmed\",\"available\":false}")]
    [InlineData("{\"event\":\"device_volume\",\"host\":\"192.0.2.12\",\"volume\":38,\"sequence\":1,\"status\":\"confirmed\",\"available\":true}")]
    public async Task InvalidOrUnconfirmedVolumeCannotCalibrateNormalMusic(string payload)
    {
        await using var wire = new ProbeWire([new(payload), Complete()]);
        await Assert.ThrowsAsync<IOException>(() => NativeSetupReferenceProbe.ReadAsync(wire, Binding, Mini, "192.0.2.12", default));
    }

    [Theory]
    [InlineData("{\"event\":\"handshake_complete\",\"audio_sent\":true,\"members\":1}")]
    [InlineData("{\"event\":\"handshake_complete\",\"audio_sent\":false,\"members\":2}")]
    [InlineData("{\"event\":\"streaming\"}")]
    [InlineData("{\"event\":\"stopped\"}")]
    [InlineData("{\"event\":\"error\",\"message\":\"Receiver unavailable\"}")]
    public async Task PlaybackOrFailedHandshakeCannotCountAsAReference(string final)
    {
        await using var wire = new ProbeWire([Volume(), new(final)]);
        await Assert.ThrowsAsync<IOException>(() => NativeSetupReferenceProbe.ReadAsync(wire, Binding, Mini, "192.0.2.12", default));
    }

    [Fact]
    public async Task ReferenceIsNotAcceptedUntilTheHandshakeCompletesWithoutAudio()
    {
        await using var wire = new ProbeWire([Volume()]);
        await Assert.ThrowsAsync<IOException>(() => NativeSetupReferenceProbe.ReadAsync(wire, Binding, Mini, "192.0.2.12", default));
        await using var missing = new ProbeWire([Complete()]);
        await Assert.ThrowsAsync<IOException>(() => NativeSetupReferenceProbe.ReadAsync(missing, Binding, Mini, "192.0.2.12", default));
    }

    [Fact]
    public async Task WrongProtocolVersionDoesNotSupplyAReference()
    {
        await using var wire = new ProbeWire([Volume() with { Version = 2 }, Complete()]);
        await Assert.ThrowsAsync<IOException>(() => NativeSetupReferenceProbe.ReadAsync(wire, Binding, Mini, "192.0.2.12", default));
    }

    [Fact]
    public async Task DifferentOrUnqualifiedReceiverIsRejectedBeforeOpeningAHandshake()
    {
        foreach (var receiver in new[] { Mini with { Id = "another-id" }, Mini with { SoftwareVersion = "26.0" }, Mini with { Online = false }, Mini with { GroupId = "group" } })
        {
            await using var wire = new ProbeWire([]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => NativeSetupReferenceProbe.ReadAsync(wire, Binding, receiver, "192.0.2.12", default));
            Assert.Null(wire.Command);
        }
    }

    [Theory]
    [InlineData("homepod.local")]
    [InlineData("::1")]
    public async Task AnUnresolvedOrIpv6HostCannotBypassTheSelectedIpv4Receiver(string host)
    {
        await using var wire = new ProbeWire([]);
        await Assert.ThrowsAsync<ArgumentException>(() => NativeSetupReferenceProbe.ReadAsync(wire, Binding, Mini, host, default));
        Assert.Null(wire.Command);
    }

    private sealed record Reply(string Payload, string? Session = null, int Version = 1);
    private static Reply Volume(int volume = 38) => new(JsonSerializer.Serialize(new { @event = "device_volume", host = "192.0.2.12", volume, sequence = 0, status = "confirmed", available = true }));
    private static Reply Complete() => new("{\"event\":\"handshake_complete\",\"audio_sent\":false,\"members\":1}");
    private sealed class ProbeWire(Reply[] replies) : IEngineConnection
    {
        private readonly Queue<Reply> _replies = new(replies);
        private string? _session;
        public string? Command { get; private set; }
        public JsonElement Parameters { get; private set; }
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            _session = session; Command = command; Parameters = JsonSerializer.SerializeToElement(parameters);
            return Task.CompletedTask;
        }
        public ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!_replies.TryDequeue(out var reply)) return ValueTask.FromResult<JsonElement?>(null);
            var item = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(reply.Payload)!;
            item["version"] = reply.Version; item["session_id"] = reply.Session ?? _session;
            return ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(item));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using System.Collections.Concurrent;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeOutputControllerTests
{
    [Fact]
    public async Task MissingRecordAndBoundsUseModelCandidateWithoutInventingVerification()
    {
        await using var f = await Fixture.Create(bufferMs: null);
        f.Monitor.Select(true); f.Start();
        await Until(() => f.Session.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(100, f.Session.Snapshot.TargetLatency);
        Assert.Null(f.Host.VerifiedBufferMs);
        Assert.Null(f.Starts.First().Params.Number("latency_ms"));
        Assert.Equal("apply", f.Starts.Last().Params.Text("latency_policy"));
        Assert.Null(f.Starts.Last().Params.Number("latency_reference_ms"));
        Assert.Equal("negotiated_request", f.Session.Snapshot.Diagnostics!.Latency!.Source);
        f.Wire.Event(f.StreamId, new { @event = "transport_metrics", session_uptime_ms = 20000,
            members = new[] { new { host = "127.0.0.1", media = new { packets_sent = 2000, nonzero_pcm_packets_sent = 2000 },
                health = new { feedback_successes = 20, feedback_rtt_ms = 2, feedback_failures = 0 } } } });
        await Until(() => f.Session.Snapshot.Diagnostics?.Transport is not null);
        Assert.Equal(2, f.Starts.Count);
        Assert.Equal(100, f.Session.Snapshot.TargetLatency);
        Assert.Null(f.Host.VerifiedBufferMs);
    }
    [Fact]
    public async Task ExplicitNormalMusicRecoveryStaysUnverifiedAndUsesAFixedActualRequest()
    {
        await using var f = await Fixture.Create(bufferMs: null, fallbackMs: 400);
        f.Monitor.Select(true); f.Start();
        await Until(() => f.Session.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(400, f.Session.Snapshot.TargetLatency);
        Assert.Null(f.Starts.First().Params.Number("latency_ms"));
        Assert.Null(f.Starts.Last().Params.Number("latency_reference_ms"));
        Assert.Equal(400, f.Starts.Last().Params.Number("latency_ms"));
        Assert.Contains(f.Logs, message => message.Contains("music_recovery", StringComparison.Ordinal));
        Assert.Null(f.Host.VerifiedBufferMs);
    }
    [Fact]
    public async Task PositiveReceiverBoundsRemainAuthoritativeOverARecoveryHint()
    {
        await using var f = await Fixture.Create(bufferMs: null, fallbackMs: 400); f.Wire.StreamReplies = false;
        f.Monitor.Select(true); f.Start(); await Until(() => f.Starts.Count == 1);
        var item = Negotiation(null, 200); var data = item["latency_negotiation"]!.AsObject();
        data["receiver_min_samples"] = 8820; data["receiver_max_samples"] = 13230;
        f.Wire.Event(f.StreamId, item); await Until(() => f.Starts.Count == 2);
        Assert.Equal(200, f.Starts.Last().Params.Number("latency_ms"));
        Assert.Null(f.Starts.Last().Params.Number("latency_reference_ms"));
        Assert.DoesNotContain(f.Logs, message => message.Contains("music_recovery", StringComparison.Ordinal));
    }
    [Fact]
    public async Task StaleProfileRecordDoesNotBecomeTheReferenceForANewContext()
    {
        await using var f = await Fixture.Create(bufferMs: 350, verifiedProfile: new string('a', 64));
        f.Monitor.Select(true); f.Start();
        await Until(() => f.Session.Snapshot.State == PlaybackState.Streaming);
        Assert.Null(f.Starts.First().Params.Number("latency_ms"));
        Assert.Null(f.Starts.Last().Params.Number("latency_reference_ms"));
        Assert.Equal(100, f.Session.Snapshot.TargetLatency);
        Assert.Equal(350, f.Host.VerifiedBufferMs);
    }
    [Theory]
    [InlineData(null)]
    [InlineData(75)]
    [InlineData(100)]
    [InlineData(350)]
    public async Task NativeStartsWithOnlyItsOwnNullableTimingPreference(int? preference)
    {
        await using var f = await Fixture.Create(bufferMs: preference);
        f.Monitor.Select(true); f.Start();
        await Until(() => f.Session.Snapshot.State == PlaybackState.Streaming);
        var start = f.Starts.First().Params;
        Assert.Equal("negotiate", start.Text("latency_policy"));
        Assert.Equal(preference, start.Number("latency_ms"));
        Assert.Equal(preference ?? 100, f.Session.Snapshot.TargetLatency);
        Assert.Equal(preference is null ? 2 : 1, f.Starts.Count);
    }
    [Fact]
    public async Task LegacySettingsCannotOverrideTheArmedNativePreference()
    {
        await using var f = await Fixture.Create(bufferMs: 75);
        await f.Session.StartAsync(f.Receiver, new() { CaptureMode = "endpoint", CaptureEndpoint = "endpoint-1", StreamSampleRate = "44100",
            LatencyMode = "custom", CustomBufferMs = 2000, ForceReconnect = false });
        await Until(() => f.Session.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(75, f.Starts.Single().Params.Number("latency_ms"));
    }
    private static System.Text.Json.Nodes.JsonObject Negotiation(int? preference = 100, int selected = 100) =>
        System.Text.Json.JsonSerializer.SerializeToNode(new { @event = "negotiated", host = "127.0.0.1", sample_rate = 44100,
            latency_negotiation = new { policy = "negotiate", preferred_ms = preference, offered_min_ms = preference ?? 250, offered_max_ms = preference ?? 2000,
                receiver_min_samples = (uint?)2205, receiver_max_samples = (uint?)22050, selected_ms = selected, applied_ms = preference == selected ? (int?)selected : null,
                source = "receiver_window", fallback_reason = (string?)null, arrival_to_render_ms = (double?)70 } })!.AsObject();
    [Fact]
    public async Task CandidateRequiresOneFixedApplyBeforeAnyVolumeOrCaptureRelease()
    {
        await using var f = await Fixture.Create(bufferMs: null); f.Wire.StreamReplies = false;
        var observations = new ConcurrentQueue<SessionSnapshot>(); f.Session.Changed += observations.Enqueue;
        f.Monitor.Select(true); f.Start(); await Until(() => f.Starts.Count == 1);
        var first = f.StreamId; var item = Negotiation(null, 250); var metadata = item["latency_negotiation"]!.AsObject();
        metadata["offered_min_ms"] = 250; metadata["offered_max_ms"] = 2000; metadata["applied_ms"] = null;
        metadata["receiver_min_samples"] = null; metadata["receiver_max_samples"] = null; metadata["source"] = "compatibility_fallback";
        f.Wire.Event(first, item); f.Wire.Event(first, new { @event = "streaming" }); f.Wire.Volume(first, 38, 0);
        await Until(() => f.Starts.Count == 2);
        Assert.Null(f.Session.Snapshot.TargetLatency);
        Assert.DoesNotContain(f.Gates, c => c.Params.Boolean("open") == true);
        Assert.DoesNotContain(f.Wire.Commands, c => c.Command == "set_device_volume");
        var apply = f.Starts.Last().Params;
        Assert.Equal("apply", apply.Text("latency_policy")); Assert.Equal(100, apply.Number("latency_ms"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, apply.GetProperty("latency_reference_ms").ValueKind);
        Assert.Equal(2, apply.Integer("capture_epoch"));
        Assert.Equal(f.Starts.First().Params.Text("capture_id"), apply.Text("capture_id"));
        Assert.Equal(f.Starts.First().Params.GetProperty("peers").GetRawText(), apply.GetProperty("peers").GetRawText());
        Assert.Equal(44100, apply.Integer("sample_rate"));
        Assert.Contains(observations, s => s.Diagnostics?.Latency?.SelectedMs == 250 && s.Diagnostics.Latency.AppliedMs is null && s.TargetLatency is null);
        metadata["policy"] = "apply"; metadata["offered_min_ms"] = 100; metadata["offered_max_ms"] = 100;
        metadata["selected_ms"] = 100; metadata["applied_ms"] = 100; metadata["source"] = "negotiated_request";
        f.Wire.Event(f.StreamId, item); f.Wire.Event(f.StreamId, new { @event = "streaming" }); f.Wire.Volume(f.StreamId, 38, 0);
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        Assert.Equal(100, f.Session.Snapshot.TargetLatency); Assert.Equal(0, f.Session.Snapshot.Diagnostics!.ReconnectCount);
        Assert.Equal(2, f.Starts.Count); Assert.Null(f.Host.VerifiedBufferMs);
        f.Wire.StreamReplies = true; f.Wire.Event(f.StreamId, new { @event = "error", message = "network disconnected", retryable = true });
        await Until(() => f.Starts.Count == 4 && f.Session.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal("negotiate", f.Starts[2].Params.Text("latency_policy"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, f.Starts[2].Params.GetProperty("latency_ms").ValueKind);
        Assert.False(f.Starts[2].Params.TryGetProperty("latency_reference_ms", out _));
    }
    [Fact]
    public async Task UnrequestedFixedOfferCannotBeReportedAsApplied()
    {
        await using var f = await Fixture.Create(); f.Wire.StreamReplies = false; f.Monitor.Select(true); f.Start();
        await Until(() => f.Starts.Count == 1);
        var forged = Negotiation(selected: 175); var metadata = forged["latency_negotiation"]!.AsObject();
        metadata["offered_min_ms"] = 175; metadata["offered_max_ms"] = 175; metadata["applied_ms"] = 175;
        f.Wire.Event(f.StreamId, forged); f.Wire.Event(f.StreamId, new { @event = "streaming" });
        f.Wire.Event(f.StreamId, Negotiation()); f.Wire.Event(f.StreamId, new { @event = "streaming" });
        await Until(() => f.Session.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(100, f.Session.Snapshot.TargetLatency); Assert.Single(f.Starts);
    }
    [Fact]
    public async Task ChangedReceiverReplyDuringApplyIsTerminalWithoutAThirdStart()
    {
        await using var f = await Fixture.Create(); f.Wire.StreamReplies = false;
        f.Monitor.Select(true); f.Start(); await Until(() => f.Starts.Count == 1);
        var item = Negotiation(selected: 200); var metadata = item["latency_negotiation"]!.AsObject();
        metadata["offered_min_ms"] = 100; metadata["offered_max_ms"] = 100; metadata["applied_ms"] = null;
        metadata["receiver_min_samples"] = 8820; metadata["receiver_max_samples"] = 35280;
        f.Wire.Event(f.StreamId, item); await Until(() => f.Starts.Count == 2);
        Assert.Equal(100, f.Starts.Last().Params.Number("latency_reference_ms"));
        f.Wire.Event(f.StreamId, new { @event = "error", code = "latency_contract_changed", message = "Receiver window changed", retryable = false });
        await Until(() => f.Session.Snapshot.State == PlaybackState.Error);
        Assert.Equal(2, f.Starts.Count); Assert.Null(f.Session.Snapshot.TargetLatency);
        Assert.DoesNotContain(f.Gates, c => c.Params.Boolean("open") == true);
    }
    [Fact]
    public async Task ReceiverWindowCanOverrideTheRequestedKnownReference()
    {
        await using var f = await Fixture.Create(); f.Wire.StreamReplies = false; f.Monitor.Select(true); f.Start();
        await Until(() => f.Starts.Count == 1);
        var item = Negotiation(selected: 200); var metadata = item["latency_negotiation"]!.AsObject();
        metadata["offered_min_ms"] = 100; metadata["offered_max_ms"] = 100;
        metadata["receiver_min_samples"] = 8820; metadata["receiver_max_samples"] = 35280;
        f.Wire.StreamReplies = true; f.Wire.Event(f.StreamId, item);
        await Until(() => f.Session.Snapshot.TargetLatency == 200);
        Assert.Equal(70, f.Session.Snapshot.Diagnostics!.Latency!.ArrivalToRenderMs);
        Assert.Equal(100, f.Host.VerifiedBufferMs);
    }
    [Fact]
    public async Task NegotiatedTargetIsSeparateFromPreferenceAndHealthyRenderLatency()
    {
        await using var f = await Fixture.Create(); f.Monitor.Select(true); f.Start();
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        f.Wire.Event(f.StreamId, Negotiation());
        await Until(() => f.Session.Snapshot.TargetLatency == 100);
        var latency = f.Session.Snapshot.Diagnostics!.Latency!;
        Assert.Equal(100, latency.PreferredMs); Assert.Equal(70, latency.ArrivalToRenderMs);
        Assert.Equal(100, f.Host.VerifiedBufferMs);
        f.Wire.Event(f.StreamId, new { @event = "transport_metrics", members = new[] { new { host = "127.0.0.1", media = new { receiver_latency_ms = 999 } } } });
        await Until(() => f.Session.Snapshot.Diagnostics?.Transport is not null);
        Assert.Equal(100, f.Session.Snapshot.TargetLatency);
        f.Monitor.Select(false); await Until(() => f.Wire.Commands.Any(c => c.Command == "reset_capture"));
        f.Monitor.Select(true); await Until(() => f.Starts.Count == 2);
        Assert.Equal(100, f.Starts.Last().Params.Number("latency_ms"));
        await Until(() => f.Session.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(100, f.Session.Snapshot.TargetLatency);
    }
    [Theory]
    [InlineData("host", "wrong")]
    [InlineData("sample_rate", "different_stream")]
    [InlineData("policy", "fixed")]
    [InlineData("preferred_ms", "stale")]
    [InlineData("selected_ms", "negative")]
    [InlineData("selected_ms", "large")]
    [InlineData("selected_ms", "string")]
    [InlineData("selected_ms", "outside_offer")]
    [InlineData("offered_min_ms", "inverted")]
    [InlineData("receiver_min_samples", "negative")]
    [InlineData("receiver_max_samples", "overflow")]
    [InlineData("source", "unknown")]
    [InlineData("arrival_to_render_ms", "negative")]
    [InlineData("fallback_reason", "number")]
    [InlineData("selected_ms", "missing")]
    public async Task InvalidNegotiationCannotChangeCurrentTarget(string field, string invalid)
    {
        await using var f = await Fixture.Create(); f.Monitor.Select(true); f.Start();
        await Until(() => f.Session.Snapshot.State == PlaybackState.Streaming);
        var item = Negotiation(); var metadata = item["latency_negotiation"]!.AsObject();
        if (field == "host") item[field] = "127.0.0.2";
        else if (field == "sample_rate") item[field] = 48000;
        else if (invalid == "missing") metadata.Remove(field);
        else metadata[field] = invalid switch { "fixed" => System.Text.Json.Nodes.JsonValue.Create("fixed"),
            "unknown" => System.Text.Json.Nodes.JsonValue.Create("invented"), "string" => System.Text.Json.Nodes.JsonValue.Create("125"),
            "stale" => System.Text.Json.Nodes.JsonValue.Create(350), "large" => System.Text.Json.Nodes.JsonValue.Create(2001),
            "outside_offer" => System.Text.Json.Nodes.JsonValue.Create(750), "inverted" => System.Text.Json.Nodes.JsonValue.Create(501),
            "overflow" => System.Text.Json.Nodes.JsonValue.Create(4294967296L), "number" => System.Text.Json.Nodes.JsonValue.Create(1),
            _ => System.Text.Json.Nodes.JsonValue.Create(-1) };
        f.Wire.Event(f.StreamId, item);
        f.Wire.Event(f.StreamId, new { @event = "capture_metrics", metrics = new { underrun_packets = 22 } });
        await Until(() => f.Session.Snapshot.Metrics?.Underruns == 22);
        Assert.Equal(100, f.Session.Snapshot.TargetLatency);
        Assert.Equal(PlaybackState.Streaming, f.Session.Snapshot.State);
    }
    [Fact]
    public async Task ReconnectClearsNegotiationAndIgnoresOldSessionMetadata()
    {
        await using var f = await Fixture.Create(); f.Monitor.Select(true); f.Start();
        var observations = new ConcurrentQueue<SessionSnapshot>(); f.Session.Changed += observations.Enqueue;
        await Until(() => f.Session.Snapshot.State == PlaybackState.Streaming);
        var old = f.StreamId; f.Wire.Event(old, Negotiation());
        await Until(() => f.Session.Snapshot.TargetLatency == 100);
        f.Wire.Event(old, new { @event = "error", message = "network disconnected", retryable = true });
        await Until(() => f.Starts.Count == 2 && f.Session.Snapshot.State == PlaybackState.Streaming);
        Assert.Contains(observations, s => s.State == PlaybackState.Connecting && s.TargetLatency is null && s.Diagnostics?.Latency is null);
        Assert.Equal(100, f.Session.Snapshot.TargetLatency);
        f.Wire.Event(old, Negotiation(selected: 200));
        f.Wire.Event(f.StreamId, new { @event = "capture_metrics", metrics = new { underrun_packets = 23 } });
        await Until(() => f.Session.Snapshot.Metrics?.Underruns == 23);
        Assert.Equal(100, f.Session.Snapshot.TargetLatency);
        f.Wire.Event(f.StreamId, Negotiation(selected: 175));
        Assert.Equal(100, f.Session.Snapshot.TargetLatency);
    }
    [Theory]
    [InlineData(null, 100, "compatibility_fallback")]
    [InlineData(100, 100, "verified_reference")]
    public async Task ReceiverWithoutBoundsReportsFallbackOrItsVerifiedReference(int? preference, int selected, string source)
    {
        await using var f = await Fixture.Create(bufferMs: preference); f.Monitor.Select(true); f.Start();
        await Until(() => f.Session.Snapshot.State == PlaybackState.Streaming);
        var item = Negotiation(preference, selected); var metadata = item["latency_negotiation"]!.AsObject();
        metadata["offered_min_ms"] = selected; metadata["offered_max_ms"] = preference is null ? 2000 : selected;
        metadata["receiver_min_samples"] = null; metadata["receiver_max_samples"] = null;
        metadata["source"] = source; metadata["fallback_reason"] = "receiver_bounds_missing";
        metadata["arrival_to_render_ms"] = null;
        f.Wire.Event(f.StreamId, item);
        await Until(() => f.Session.Snapshot.TargetLatency == selected);
        Assert.Equal(preference is null ? "negotiated_request" : source, f.Session.Snapshot.Diagnostics!.Latency!.Source);
        Assert.Equal(preference, f.Host.VerifiedBufferMs);
    }
    [Fact]
    public async Task ChangedNativeIdentityAtTheSameAddressCannotKeepThePreviousTarget()
    {
        await using var f = await Fixture.Create();
        var settings = new AppSettings { CaptureMode = "endpoint", CaptureEndpoint = "endpoint-1", StreamSampleRate = "44100", ForceReconnect = false };
        await f.Session.StartAsync(f.Receiver, settings);
        await Until(() => f.Session.Snapshot.State == PlaybackState.Streaming);
        f.Wire.Event(f.StreamId, Negotiation()); await Until(() => f.Session.Snapshot.TargetLatency == 100);
        await f.Session.UpdateReceiverAsync(f.Receiver with { Id = "replacement-receiver" }, settings);
        await Until(() => f.Starts.Count == 3 && f.Session.Snapshot.State == PlaybackState.Streaming);
        Assert.Null(f.Starts[1].Params.Number("latency_ms"));
        Assert.Null(f.Starts[2].Params.Number("latency_reference_ms"));
        Assert.Null(f.Session.Snapshot.Diagnostics!.Latency!.PreferredMs);
    }
    [Fact]
    public async Task NativeMasterWritesReceiverWithoutRepeatingWindowsAttenuationInPcm()
    {
        await using var f = await Fixture.Create(initialVolume: 100, referenceVolume: 100);
        f.Monitor.Select(true); f.Start();
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        Assert.Contains(f.Wire.Commands, c => c.Command == "set_device_volume" && c.Params.Number("volume") == 60);
        Assert.Equal(1, f.Wire.Commands.Last(c => c.Command == "set_gain").Params.Number("gain"));
    }
    [Fact]
    public async Task NativeMuteAndUnmuteUseDeviceControlWhilePcmKeepsFlowing()
    {
        await using var f = await Fixture.Create(initialVolume: 100, referenceVolume: 100);
        f.Monitor.Select(true); f.Start();
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        f.Monitor.Volume(new(1, -12, .25, true, Guid.NewGuid()));
        await Until(() => f.Session.Snapshot.DeviceVolume?.Actual == 0);
        Assert.Equal(1, f.Wire.Commands.Last(c => c.Command == "set_gain").Params.Number("gain"));
        Assert.Contains(f.Gates, c => c.Params.Boolean("open") == true && c.Params.Boolean("clear_pending") == true);
        f.Time.Advance(TimeSpan.FromSeconds(15));
        f.Monitor.Volume(new(2, -24, .1, false, Guid.NewGuid()));
        await Until(() => f.Session.Snapshot.DeviceVolume?.Actual == 20);
        Assert.Single(f.Starts); Assert.Equal(0, f.Monitor.PhysicalMuteCalls);
        Assert.Equal(1, f.Wire.Commands.Last(c => c.Command == "set_gain").Params.Number("gain"));
    }
    [Theory]
    [InlineData(-18, .125, false, 40)]
    [InlineData(-6, .5, true, 0)]
    [InlineData(-96, 0, false, 0)]
    public async Task NewWindowsVolumeSupersedesPendingDeviceWriteWithoutWaitingForOldReadback(double db, double scalar, bool muted, int target)
    {
        await using var f = await Fixture.Create(initialVolume: 100, referenceVolume: 100);
        f.Monitor.Select(true); f.Start();
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        f.Wire.VolumeReplies = false;
        f.Monitor.Volume(new(1, -6, .5, false, Guid.NewGuid()));
        await Until(() => f.Wire.Commands.Any(c => c.Command == "set_device_volume" && c.Params.Number("volume") == 80));
        var old = f.Wire.Commands.Last(c => c.Command == "set_device_volume");
        f.Monitor.Volume(new(2, db, scalar, muted, Guid.NewGuid()));
        await Until(() => f.Wire.Commands.Any(c => c.Command == "set_device_volume" && c.Params.Number("volume") == target));
        var latest = f.Wire.Commands.Last(c => c.Command == "set_device_volume");
        f.Wire.Volume(f.StreamId, 80, old.Params.Integer("sequence")!.Value);
        await Task.Delay(20); Assert.Equal(target, f.Session.Snapshot.DeviceVolume!.Target);
        f.Wire.Volume(f.StreamId, target, latest.Params.Integer("sequence")!.Value);
        await Until(() => f.Session.Snapshot.DeviceVolume?.Target is null);
        Assert.False(f.Run!.IsCompleted); Assert.Single(f.Starts);
    }
    [Fact]
    public async Task UnconfirmedDeviceWriteStopsInsteadOfReleasingAudioAtTheOldLouderLevel()
    {
        await using var f = await Fixture.Create(initialVolume: 100, referenceVolume: 100);
        f.Wire.VolumeReplies = false; f.Monitor.Select(true); f.Start();
        await Until(() => f.Wire.Commands.Any(c => c.Command == "set_device_volume"));
        var command = f.Wire.Commands.Last(c => c.Command == "set_device_volume");
        f.Wire.Event(f.StreamId, new { @event = "device_volume", host = "127.0.0.1", volume = 100, sequence = command.Params.Integer("sequence")!.Value, status = "unconfirmed", available = true });
        await Assert.ThrowsAsync<IOException>(() => f.Run!.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.DoesNotContain(f.Gates, c => c.Params.Boolean("open") == true);
        Assert.Equal(0, f.Wire.Commands.Last(c => c.Command == "set_gain").Params.Number("gain"));
    }
    [Fact]
    public async Task ExternalZeroIsNotUndoneByAWindowsChangeObservedBeforeThatZero()
    {
        await using var f = await Fixture.Create(initialVolume: 100, referenceVolume: 100);
        f.Monitor.Select(true); f.Start();
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        var before = f.Wire.Commands.Count;
        f.Monitor.SetLatestWithoutNotification(new(1, -6, .5, false, Guid.NewGuid()));
        f.Wire.Volume(f.StreamId, 0, f.Session.Snapshot.DeviceVolume!.Sequence);
        await Until(() => f.Monitor.Snapshot.Volume.Muted || f.Wire.Commands.Skip(before).Any(c => c.Command == "set_device_volume"));
        Assert.True(f.Monitor.Snapshot.Volume.Muted);
        Assert.DoesNotContain(f.Wire.Commands.Skip(before), c => c.Command == "set_device_volume" && c.Params.Number("volume") > 0);
    }
    [Fact]
    public async Task CancelledMuteDoesNotClearNewFirstSoundWhenUnmuteReadbackOpensGate()
    {
        await using var f = await Fixture.Create(initialVolume: 100, referenceVolume: 100);
        f.Wire.VolumeReplies = false; f.Monitor.Select(true); f.Start();
        await Until(() => f.Wire.Commands.Any(c => c.Command == "set_device_volume"));
        f.Monitor.Volume(new(1, -12, .25, true, Guid.NewGuid()));
        await Until(() => f.Wire.Commands.Any(c => c.Command == "set_device_volume" && c.Params.Number("volume") == 0));
        f.Monitor.Volume(new(2, -12, .25, false, Guid.NewGuid()));
        await Until(() => f.Wire.Commands.Count(c => c.Command == "set_device_volume" && c.Params.Number("volume") == 60) == 2);
        // Opening must retain any short sound captured after this unmute.
        var current = f.Wire.Commands.Last(c => c.Command == "set_device_volume");
        f.Wire.Volume(f.StreamId, 60, current.Params.Integer("sequence")!.Value);
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        Assert.DoesNotContain(f.Gates, c => c.Params.Boolean("open") == true && c.Params.Boolean("clear_pending") == true);
    }
    [Theory]
    [InlineData(0, 75)]
    [InlineData(1, 125)]
    [InlineData(2, 350)]
    [InlineData(3, 500)]
    public async Task AnyRenderRoleOrExternalSessionStartsOneRoute(int demand, int bufferMs)
    {
        await using var f = await Fixture.Create(bufferMs: bufferMs);
        var ids = new string?[] { null, "other", null };
        if (demand < 3) ids[demand] = "endpoint-1";
        f.Monitor.Publish(f.Monitor.Snapshot with { DefaultRenderIds = ids, Sessions = demand == 3 ? [new("endpoint-1", "external", 12345, true)] : [] });
        f.Start(); await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        for (var i = 0; i < 4; i++) f.Monitor.Publish(f.Monitor.Snapshot);
        await f.FlushVolume();
        Assert.Single(f.Starts); Assert.Equal(0, f.Monitor.PhysicalMuteCalls);
        var start = f.Starts.Single().Params;
        Assert.Equal("ntp", start.Text("timing")); Assert.Equal(bufferMs, start.Number("latency_ms"));
        Assert.Equal(bufferMs, f.Session.Snapshot.TargetLatency);
        Assert.Equal(44100, start.Number("sample_rate")); Assert.False(start.GetProperty("equalizer").GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain(f.Wire.Commands, c => c.Command == "set_equalizer");
        Assert.Contains(f.Wire.Commands, c => c.Command == "set_device_volume" && c.Params.Number("volume") == 1);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OwnCaptureNeverKeepsRouteAlive(int identity)
    {
        await using var f = await Fixture.Create();
        var own = new NativeAudioSession("endpoint-1", identity == 2 ? "capture-instance" : "owned", identity == 0 ? Environment.ProcessId : identity == 1 ? 42 : 12345, true);
        f.Monitor.Publish(f.Monitor.Snapshot with { DefaultRenderIds = [null, null, null], Sessions = [own] });
        f.Start(); Assert.Empty(f.Starts);
        f.Monitor.Select(true); await Until(() => f.Starts.Count == 1);
        f.Monitor.Publish(f.Monitor.Snapshot with { Generation = 2, DefaultRenderIds = [null, "other", null] });
        await Until(() => f.Wire.Commands.Any(c => c.Command == "reset_capture"));
        Assert.Single(f.Starts); Assert.Equal(0, f.Monitor.PhysicalMuteCalls);
    }
    [Fact]
    public async Task OldGenerationCannotReopenRouteAfterSwitchAway()
    {
        await using var f = await Fixture.Create(); f.Monitor.Select(true); f.Start();
        await Until(() => f.Starts.Count == 1);
        var old = f.Monitor.Snapshot;
        f.Monitor.Publish(old with { Generation = 2, DefaultRenderIds = [null, null, null] });
        await Until(() => f.Wire.Commands.Any(c => c.Command == "reset_capture"));
        f.Monitor.EmitOld(old); f.Time.Advance(TimeSpan.FromSeconds(60));
        f.Monitor.Select(true); await Until(() => f.Starts.Count == 2);
        Assert.Equal(2, f.Starts.Last().Params.Integer("capture_epoch"));
    }
    [Fact]
    public async Task FifteenSecondPauseResumesAndThirtySecondIdleStopsUsingMonotonicTime()
    {
        await using var f = await Fixture.Create();
        f.Monitor.Publish(f.Monitor.Snapshot with { Sessions = [new("endpoint-1", "music", 12345, true)] });
        f.Start(); await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        f.Monitor.Publish(f.Monitor.Snapshot with { Sessions = [new("endpoint-1", "music", 12345, false)] });
        await f.FlushVolume();
        f.Time.JumpCalendar(TimeSpan.FromDays(30)); f.Time.Advance(TimeSpan.FromSeconds(15)); await f.FlushVolume();
        Assert.DoesNotContain(f.Wire.Commands, c => c.Command == "stop");
        f.Monitor.Publish(f.Monitor.Snapshot with { Sessions = [new("endpoint-1", "music", 12345, true)] }); await f.FlushVolume();
        Assert.Single(f.Starts);
        f.Monitor.Publish(f.Monitor.Snapshot with { Sessions = [new("endpoint-1", "music", 12345, false)] });
        await f.FlushVolume();
        f.Time.Advance(TimeSpan.FromSeconds(29)); await f.FlushVolume(); Assert.DoesNotContain(f.Wire.Commands, c => c.Command == "stop");
        f.Time.Advance(TimeSpan.FromSeconds(1)); await Until(() => f.Wire.Commands.Any(c => c.Command == "stop"));
    }
    [Fact]
    public async Task ContinuouslyActiveApplicationKeepsRoutePastThirtySeconds()
    {
        await using var f = await Fixture.Create();
        f.Monitor.Publish(f.Monitor.Snapshot with { Sessions = [new("endpoint-1", "music", 12345, true)] });
        f.Start(); await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        f.Time.Advance(TimeSpan.FromMinutes(30)); await f.FlushVolume();
        Assert.Single(f.Starts); Assert.DoesNotContain(f.Wire.Commands, c => c.Command == "stop");
    }
    [Fact]
    public async Task SessionCreatedDuringInitialEnumerationIsRetainedAndShortActiveEdgeSurvivesCoalescing()
    {
        var sessions = new NativeSessionTracker();
        sessions.Update(new("endpoint-1", "new", 12345, true));
        sessions.Update(new("endpoint-1", "new", 12345, false));
        var snapshot = sessions.TakeSnapshot();
        Assert.Single(snapshot); Assert.True(snapshot[0].ObservedActive); Assert.False(snapshot[0].Active);
        Assert.False(sessions.TakeSnapshot()[0].ObservedActive);
        await using var f = await Fixture.Create();
        f.Monitor.Publish(f.Monitor.Snapshot with { Sessions = snapshot }); f.Start();
        await Until(() => f.Starts.Count == 1);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialRemoteZeroWithoutNewIntentMutesOnlyVirtualWithoutRaisingReceiver(bool masterInPcm)
    {
        await using var f = await Fixture.Create(initialVolume: 0, masterInPcm: masterInPcm); f.Monitor.Select(true); f.Start();
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        Assert.True(f.Monitor.Snapshot.Volume.Muted); Assert.Equal(f.Controller.OwnVolumeContext, f.Monitor.Mutes.Last());
        Assert.Equal(masterInPcm ? 0 : 1, f.Wire.Commands.Last(c => c.Command == "set_gain").Params.Number("gain"));
        Assert.Equal(0, f.Session.Snapshot.DeviceVolume!.Actual);
        Assert.DoesNotContain(f.Wire.Commands, c => c.Command == "set_device_volume");
        f.Time.Advance(TimeSpan.FromSeconds(15)); Assert.Single(f.Starts); Assert.Equal(0, f.Monitor.PhysicalMuteCalls);
    }
    [Fact]
    public async Task LatestUserRevisionWinsInitialRemoteZeroAndGateWaitsForMatchingReadback()
    {
        await using var f = await Fixture.Create(initialVolume: null, referenceVolume: 100); f.Wire.VolumeReplies = false; f.Monitor.Select(true); f.Start();
        await Until(() => f.Session.Snapshot.State == PlaybackState.Streaming);
        f.Monitor.SetLatestWithoutNotification(new(1, -6, .5, false, Guid.NewGuid()));
        f.Wire.Volume(f.StreamId, 0, 0);
        await Until(() => f.Wire.Commands.Any(c => c.Command == "set_device_volume"));
        var restore = f.Wire.Commands.Last(c => c.Command == "set_device_volume");
        Assert.Equal(80, restore.Params.Number("volume")); Assert.DoesNotContain(f.Gates, c => c.Params.Boolean("open") == true);
        f.Monitor.Volume(new(2, -18, .125, false, Guid.NewGuid()));
        await Until(() => f.Wire.Commands.Any(c => c.Command == "set_device_volume" && c.Params.Number("volume") == 40));
        var latest = f.Wire.Commands.Last(c => c.Command == "set_device_volume");
        f.Wire.Volume(f.StreamId, 80, restore.Params.Integer("sequence")!.Value); // Stale old read must not release PCM.
        await Task.Delay(20); Assert.DoesNotContain(f.Gates, c => c.Params.Boolean("open") == true);
        f.Wire.Volume(f.StreamId, 40, latest.Params.Integer("sequence")!.Value);
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        Assert.Equal(1, f.Wire.Commands.Last(c => c.Command == "set_gain").Params.Number("gain")!.Value, 8);
        Assert.DoesNotContain(f.Monitor.Mutes, context => context != f.Controller.OwnVolumeContext);
    }
    [Fact]
    public async Task ExplicitMuteClearsPrerollAndUnmuteUsesRuntimeReferenceWithoutRestart()
    {
        await using var f = await Fixture.Create(initialVolume: 24, masterInPcm: true); f.Monitor.Select(true); f.Start();
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        f.Monitor.Volume(new(1, -12, .25, true, Guid.NewGuid()));
        await Until(() => f.Wire.Commands.Last(c => c.Command == "set_gain").Params.Number("gain") == 0
            && f.Gates.Any(c => c.Params.Boolean("open") == true && c.Params.Boolean("clear_pending") == true));
        Assert.Contains(f.Gates, c => c.Params.Boolean("open") == true && c.Params.Boolean("clear_pending") == true);
        f.Time.Advance(TimeSpan.FromSeconds(15)); Assert.Single(f.Starts);
        f.Wire.Volume(f.StreamId, 0, 0); await Until(() => f.Session.Snapshot.DeviceVolume?.Actual == 0);
        f.Monitor.Volume(new(2, -18, .125, false, Guid.NewGuid()));
        await Until(() => f.Wire.Commands.Any(c => c.Command == "set_device_volume"));
        var restore = f.Wire.Commands.Last(c => c.Command == "set_device_volume"); Assert.Equal(24, restore.Params.Number("volume"));
        f.Wire.Volume(f.StreamId, 24, restore.Params.Integer("sequence")!.Value);
        await Until(() => f.Wire.Commands.Last(c => c.Command == "set_gain").Params.Number("gain") == 1);
        Assert.Single(f.Starts); Assert.Equal(0, f.Monitor.PhysicalMuteCalls);
    }
    [Fact]
    public async Task BindingInvalidationAndUnsupportedProfileStopWithoutPhysicalFallback()
    {
        await using var f = await Fixture.Create(); f.Monitor.Select(true); f.Start(); await Until(() => f.Starts.Count == 1);
        f.Monitor.Publish(f.Monitor.Snapshot with { Generation = 2, BindingValid = false });
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Run!);
        Assert.Contains(f.Wire.Commands, c => c.Command == "stop"); Assert.Equal(0, f.Monitor.PhysicalMuteCalls);
        await using var unsupported = await Fixture.Create(); unsupported.Receiver = unsupported.Receiver with { SoftwareVersion = "27.1" }; unsupported.Monitor.Select(true); unsupported.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => unsupported.Run!); Assert.Empty(unsupported.Starts);
    }
    [Fact]
    public async Task OwnVolumeCallbacksDoNotInventNewUserIntentOrRaiseRemoteZero()
    {
        await using var f = await Fixture.Create(initialVolume: 0); f.Monitor.Select(true); f.Start();
        await Until(() => f.Monitor.Snapshot.Volume.Muted);
        f.Monitor.Volume(new(999, -12, .25, false, f.Controller.OwnVolumeContext));
        await Until(() => f.Monitor.Snapshot.Volume.Muted);
        Assert.DoesNotContain(f.Wire.Commands, c => c.Command == "set_device_volume");
    }
    [Fact]
    public async Task LowerPostMasterVolumeDuringInitialHoldClearsOlderLouderFrames()
    {
        await using var f = await Fixture.Create(initialVolume: null, masterInPcm: true); f.Monitor.Select(true); f.Start();
        await Until(() => f.Session.Snapshot.State == PlaybackState.Streaming);
        f.Monitor.Volume(new(1, -24, .0625, false, Guid.NewGuid()));
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == false && c.Params.Boolean("clear_pending") == true));
        f.Wire.Volume(f.StreamId, 38, 0);
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        Assert.False(f.Gates.Last(c => c.Params.Boolean("open") == true).Params.Boolean("clear_pending"));
        Assert.Equal(1, f.Wire.Commands.Last(c => c.Command == "set_gain").Params.Number("gain"));
    }
    [Fact]
    public async Task ReceiverNameAndIpChangesKeepBoundIdentityButRebuildNetworkLease()
    {
        await using var f = await Fixture.Create(); f.Monitor.Select(true); f.Start();
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        f.Receiver = f.Receiver with { Name = "renamed", Address = "127.0.0.2" }; f.Time.Advance(TimeSpan.FromSeconds(1));
        await Until(() => f.Starts.Count == 2);
        Assert.Equal("127.0.0.2", f.Starts.Last().Params.GetProperty("peers")[0].Text("host"));
        Assert.Equal(2, f.Starts.Last().Params.Integer("capture_epoch"));
        f.Receiver = f.Receiver with { SoftwareVersion = "27.1" }; f.Time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Run!);
        Assert.Equal(2, f.Starts.Count);
    }
    [Fact]
    public async Task IpChangeWithPendingVolumeClearCannotReleaseNewEpochBeforeItsReadback()
    {
        await using var f = await Fixture.Create(masterInPcm: true); f.Monitor.Select(true); f.Start();
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        f.Wire.StreamReplies = false; f.Wire.InitialVolume = null;
        f.Receiver = f.Receiver with { Address = "127.0.0.2" };
        f.Monitor.Volume(new(1, -24, .0625, false, Guid.NewGuid()));
        await Until(() => f.Starts.Count == 2 || f.Run!.IsCompleted);
        Assert.False(f.Run!.IsCompleted, "Transport refresh must not apply the previous lease's streaming state.");
        var next = f.Starts.Last().Session;
        var negotiated = Negotiation(); negotiated["host"] = "127.0.0.2"; f.Wire.Event(next, negotiated);
        f.Wire.Event(next, new { @event = "streaming" });
        await Until(() => f.Wire.Commands.Any(c => c.Session == next && c.Command == "set_gain" && c.Params.Number("gain") == 0) || f.Run.IsCompleted);
        Assert.False(f.Run.IsCompleted);
        Assert.DoesNotContain(f.Gates, c => c.Params.Integer("epoch") == 2 && c.Params.Boolean("open") == true);
        f.Wire.Volume(next, 0, 0, "127.0.0.2");
        await Until(() => f.Monitor.Snapshot.Volume.Muted);
        Assert.DoesNotContain(f.Wire.Commands, c => c.Session == next && c.Command == "set_device_volume");
    }
    [Theory]
    [InlineData("firmware")]
    [InlineData("model")]
    [InlineData("leader")]
    public async Task PartialDiscoveryWaitsForQualificationMetadataWithinStartupBound(string missing)
    {
        await using var f = await Fixture.Create(); var complete = f.Receiver;
        f.Receiver = missing switch { "firmware" => complete with { SoftwareVersion = "" }, "model" => complete with { Model = "" }, _ => complete with { IsLeader = false } };
        f.Monitor.Select(true); f.Start(); await Until(() => f.Resolutions > 0);
        using (var pending = new CancellationTokenSource(50))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Run!.WaitAsync(pending.Token));
        Assert.Empty(f.Starts);
        f.Receiver = complete; f.Time.Advance(TimeSpan.FromMilliseconds(250));
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        Assert.Single(f.Starts);
    }
    [Fact]
    public async Task CaptureTimingReportsInitialAndBoundedSteadySamples()
    {
        await using var f = await Fixture.Create(); f.Monitor.Select(true); f.Start();
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        f.Wire.Event(f.StreamId, new { @event = "capture_metrics", metrics = new { capture_to_send_p95_ms = 1250, max_queue_age_ms = 1300, underrun_packets = 0, dropped_frames = 0, input_rate = 48000 } });
        await Until(() => f.Session.Snapshot.Metrics is not null);
        await f.FlushVolume();
        var entry = Assert.Single(f.Logs, s => s.StartsWith("Native capture timing: ", StringComparison.Ordinal));
        using var document = System.Text.Json.JsonDocument.Parse(entry["Native capture timing: ".Length..]);
        var timing = document.RootElement;
        Assert.Equal(1250, timing.GetProperty("CaptureToSendP95").GetDouble());
        Assert.Equal(1300, timing.GetProperty("MaxQueueAge").GetDouble());
        f.Wire.Event(f.StreamId, new { @event = "capture_metrics", metrics = new { capture_to_send_p95_ms = 20, max_queue_age_ms = 1300, underrun_packets = 0, dropped_frames = 0, input_rate = 48000 } });
        await Until(() => f.Session.Snapshot.Metrics?.CaptureToSendP95 == 20);
        await f.FlushVolume();
        Assert.Single(f.Logs, s => s.StartsWith("Native capture timing: ", StringComparison.Ordinal));
        f.Time.Advance(TimeSpan.FromSeconds(15));
        f.Wire.Event(f.StreamId, new { @event = "capture_metrics", metrics = new { capture_to_send_p95_ms = 25, max_queue_age_ms = 1300, underrun_packets = 0, dropped_frames = 0, input_rate = 48000 } });
        await Until(() => f.Session.Snapshot.Metrics?.CaptureToSendP95 == 25);
        await f.FlushVolume();
        Assert.Equal(2, f.Logs.Count(s => s.StartsWith("Native capture timing: ", StringComparison.Ordinal)));
    }
    [Fact]
    public async Task FullControllerQueueFailsInsteadOfSilentlyDroppingWindowsIntent()
    {
        await using var f = await Fixture.Create(); f.Start();
        for (var i = 0; i < 10000; i++) f.Monitor.Publish(f.Monitor.Snapshot);
        await Assert.ThrowsAsync<IOException>(() => f.Run!.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly NativeEngineHostTests.WireConnection Wire = new() { StreamReplies = true };
        public readonly Monitor Monitor = new();
        public readonly ConcurrentQueue<string> Logs = new();
        public readonly ManualTime Time = new();
        public readonly CancellationTokenSource Stop = new();
        public NativeEngineHost Host = null!;
        public SessionController Session = null!;
        public NativeOutputController Controller = null!;
        private PcmVolumeCalibration Calibration = null!;
        public Receiver Receiver = new("receiver-1", "da", "127.0.0.1") { Model = "AudioAccessory5,1", SoftwareVersion = "27.0", IsLeader = true, Codecs = [1] };
        public Task? Run;
        public int Resolutions;
        public List<NativeEngineHostTests.Sent> Starts => Wire.Commands.Where(c => c.Command == "start").ToList();
        public List<NativeEngineHostTests.Sent> Gates => Wire.Commands.Where(c => c.Command == "set_capture_gate").ToList();
        public string StreamId => Starts.Last().Session;
        public static async Task<Fixture> Create(int? initialVolume = 38, bool masterInPcm = false, int referenceVolume = 38, int? bufferMs = 100,
            int? fallbackMs = null, string? verifiedProfile = null)
        {
            var f = new Fixture(); f.Wire.InitialVolume = initialVolume; f.Wire.VolumeReplies = true;
            var binding = NativeEngineHostTests.Binding;
            binding = binding with { Calibration = binding.Calibration! with { MasterInPcm = masterInPcm, ResumeDeviceVolume = referenceVolume } };
            var document = System.Text.Json.JsonSerializer.SerializeToNode(binding, AppSettings.JsonOptions)!.AsObject();
            document["verified_buffer_ms"] = bufferMs;
            document["verified_buffer_profile"] = verifiedProfile;
            binding = System.Text.Json.JsonSerializer.Deserialize<NativeBinding>(document, AppSettings.JsonOptions)!;
            f.Calibration = binding.Calibration!;
            f.Host = new(f.Wire, 42, fallbackMs); await f.Host.ArmAsync(binding, default);
            f.Session = new(f.Host, f.Monitor, f.Logs.Enqueue);
            f.Controller = new(binding, f.Monitor, f.Host, f.Session, f.Time, f.Logs.Enqueue);
            return f;
        }
        public void Start() => Run = Controller.RunAsync(_ => { Interlocked.Increment(ref Resolutions); return Receiver; }, Stop.Token);
        public async Task FlushVolume()
        {
            var db = Monitor.Snapshot.Volume.Db - .1;
            Monitor.Volume(Monitor.Snapshot.Volume with { Revision = Monitor.Snapshot.Volume.Revision + 1, Db = db, Context = Guid.NewGuid() });
            var gain = Calibration.MasterInPcm ? 1 : PcmVolumeMath.DeviceControl(Calibration, Monitor.Snapshot.Volume).Gain;
            await Until(() => Wire.Commands.Any(c => c.Command == "set_gain" && Math.Abs(c.Params.Number("gain")!.Value - gain) < 1e-8));
        }
        public async ValueTask DisposeAsync()
        {
            Stop.Cancel();
            if (Run is not null) try { await Run; } catch (Exception) { /* The test asserts any expected run failure. */ }
            await Session.DisposeAsync(); await Host.DisposeAsync(); Stop.Dispose();
        }
    }
    private sealed class Monitor : INativeOutputMonitor
    {
        public NativeOutputSnapshot Snapshot { get; private set; } = new(1, [null, "other", null], [], new(0, -12, .25, false, Guid.Empty), true);
        public ConcurrentQueue<Guid> Mutes { get; } = new();
        public int PhysicalMuteCalls;
        public event Action<NativeOutputSnapshot>? Changed;
        public event Action? EndpointsChanged { add { } remove { } }
        public void Publish(NativeOutputSnapshot value) { Snapshot = value; Changed?.Invoke(value); }
        public void EmitOld(NativeOutputSnapshot value) => Changed?.Invoke(value);
        public void Select(bool selected) => Publish(Snapshot with { DefaultRenderIds = [selected ? "endpoint-1" : "other", null, null] });
        public void Volume(NativeVolume volume) => Publish(Snapshot with { Volume = volume });
        public void SetLatestWithoutNotification(NativeVolume volume) => Snapshot = Snapshot with { Volume = volume };
        public Task SetMutedAsync(bool muted, Guid context, CancellationToken cancellation)
        { Mutes.Enqueue(context); Volume(Snapshot.Volume with { Muted = muted, Context = context }); return Task.CompletedTask; }
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult(Snapshot.DefaultRenderIds[0]);
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task MuteAsync(string? endpointId) { PhysicalMuteCalls++; throw new InvalidOperationException("Physical mute forbidden."); }
        public Task RestoreAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        private DateTimeOffset _utc = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        private TimerCallback? _callback;
        private object? _state;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan span) { _ticks += span.Ticks; _utc += span; _callback?.Invoke(_state); }
        public void JumpCalendar(TimeSpan span) => _utc += span;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) { _callback = callback; _state = state; return new Timer(this); }
        private sealed class Timer(ManualTime owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => owner._callback = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

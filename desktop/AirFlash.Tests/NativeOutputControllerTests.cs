using System.Collections.Concurrent;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeOutputControllerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AnyRenderRoleOrExternalSessionStartsOneRoute(int demand)
    {
        await using var f = await Fixture.Create();
        var ids = new string?[] { null, "other", null };
        if (demand < 3) ids[demand] = "endpoint-1";
        f.Monitor.Publish(f.Monitor.Snapshot with { DefaultRenderIds = ids, Sessions = demand == 3 ? [new("endpoint-1", "external", 12345, true)] : [] });
        f.Start(); await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        for (var i = 0; i < 4; i++) f.Monitor.Publish(f.Monitor.Snapshot);
        await f.FlushVolume();
        Assert.Single(f.Starts); Assert.Equal(0, f.Monitor.PhysicalMuteCalls);
        var start = f.Starts.Single().Params;
        Assert.Equal("ntp", start.Text("timing")); Assert.Equal(500, start.Number("latency_ms"));
        Assert.Equal(500, f.Session.Snapshot.TargetLatency);
        Assert.Equal(44100, start.Number("sample_rate")); Assert.False(start.GetProperty("equalizer").GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain(f.Wire.Commands, c => c.Command is "set_equalizer" or "set_device_volume");
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
    [Fact]
    public async Task InitialRemoteZeroWithoutNewIntentMutesOnlyVirtualAndDrainsAtZero()
    {
        await using var f = await Fixture.Create(initialVolume: 0); f.Monitor.Select(true); f.Start();
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        Assert.True(f.Monitor.Snapshot.Volume.Muted); Assert.Equal(f.Controller.OwnVolumeContext, f.Monitor.Mutes.Last());
        Assert.Equal(0, f.Wire.Commands.Last(c => c.Command == "set_gain").Params.Number("gain"));
        Assert.Contains(f.Gates, c => c.Params.Boolean("clear_pending") == true);
        Assert.DoesNotContain(f.Wire.Commands, c => c.Command == "set_device_volume");
        f.Time.Advance(TimeSpan.FromSeconds(15)); Assert.Single(f.Starts); Assert.Equal(0, f.Monitor.PhysicalMuteCalls);
    }
    [Fact]
    public async Task LatestUserRevisionWinsInitialRemoteZeroAndGateWaitsForMatchingReadback()
    {
        await using var f = await Fixture.Create(initialVolume: null); f.Monitor.Select(true); f.Start();
        await Until(() => f.Session.Snapshot.State == PlaybackState.Streaming);
        f.Monitor.SetLatestWithoutNotification(new(1, -6, .5, false, Guid.NewGuid()));
        f.Wire.Volume(f.StreamId, 0, 0);
        await Until(() => f.Wire.Commands.Any(c => c.Command == "set_device_volume"));
        var restore = f.Wire.Commands.Last(c => c.Command == "set_device_volume");
        Assert.Equal(38, restore.Params.Number("volume")); Assert.DoesNotContain(f.Gates, c => c.Params.Boolean("open") == true);
        f.Monitor.Volume(new(2, -18, .125, false, Guid.NewGuid()));
        f.Wire.Volume(f.StreamId, 38, 0); // Stale old read must not release PCM.
        await Task.Delay(20); Assert.DoesNotContain(f.Gates, c => c.Params.Boolean("open") == true);
        f.Wire.Volume(f.StreamId, 38, restore.Params.Integer("sequence")!.Value);
        await Until(() => f.Gates.Any(c => c.Params.Boolean("open") == true));
        Assert.Equal(Math.Pow(10, -18d / 20), f.Wire.Commands.Last(c => c.Command == "set_gain").Params.Number("gain")!.Value, 8);
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
    public async Task FirstCaptureTimingIsLoggedOncePerConnection()
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
        public Receiver Receiver = new("receiver-1", "da", "127.0.0.1") { Model = "AudioAccessory5,1", SoftwareVersion = "27.0", IsLeader = true, Codecs = [1] };
        public Task? Run;
        public int Resolutions;
        public List<NativeEngineHostTests.Sent> Starts => Wire.Commands.Where(c => c.Command == "start").ToList();
        public List<NativeEngineHostTests.Sent> Gates => Wire.Commands.Where(c => c.Command == "set_capture_gate").ToList();
        public string StreamId => Starts.Last().Session;
        public static async Task<Fixture> Create(int? initialVolume = 38, bool masterInPcm = false)
        {
            var f = new Fixture(); f.Wire.InitialVolume = initialVolume;
            var binding = NativeEngineHostTests.Binding;
            binding = binding with { Calibration = binding.Calibration! with { MasterInPcm = masterInPcm } };
            f.Host = new(f.Wire, 42); await f.Host.ArmAsync(binding, default);
            f.Session = new(f.Host, f.Monitor);
            f.Controller = new(binding, f.Monitor, f.Host, f.Session, f.Time, f.Logs.Enqueue);
            return f;
        }
        public void Start() => Run = Controller.RunAsync(_ => { Interlocked.Increment(ref Resolutions); return Receiver; }, Stop.Token);
        public async Task FlushVolume()
        {
            var db = Monitor.Snapshot.Volume.Db - .1;
            Monitor.Volume(Monitor.Snapshot.Volume with { Revision = Monitor.Snapshot.Volume.Revision + 1, Db = db, Context = Guid.NewGuid() });
            await Until(() => Wire.Commands.Any(c => c.Command == "set_gain" && Math.Abs(c.Params.Number("gain")!.Value - Math.Pow(10, db / 20)) < 1e-8));
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

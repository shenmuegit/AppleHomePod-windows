using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class NativeEngineSupervisorTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public async Task OnlyWindowsPlaybackOutputSelectionStartsAnEngine(int role, bool expected)
    {
        var monitor = new Monitor(); var ids = new string?[3];
        if (role < 3) ids[role] = "endpoint-1";
        monitor.Publish(monitor.Snapshot with { DefaultRenderIds = ids, Sessions = [new("endpoint-1", "music", 123, true)] });
        using var stop = new CancellationTokenSource(); var calls = 0;
        var run = new NativeEngineSupervisor(monitor, "endpoint-1").RunAsync(async token => {
            Interlocked.Increment(ref calls); await Task.Delay(Timeout.Infinite, token);
        }, stop.Token);
        try { await Task.Delay(30); Assert.Equal(expected ? 1 : 0, calls); Assert.False(run.IsCompleted); }
        finally { stop.Cancel(); await Cancelled(run); }
    }

    [Fact]
    public async Task SwitchAwayDisposesBeforeReselectionStartsAFreshEngine()
    {
        var monitor = new Monitor(); using var stop = new CancellationTokenSource();
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0; var disposed = 0; var disposedBeforeRestart = -1;
        var run = new NativeEngineSupervisor(monitor, "endpoint-1").RunAsync(async token => {
            var call = Interlocked.Increment(ref calls);
            if (call == 2) disposedBeforeRestart = disposed;
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { retired.TrySetResult(); await release.Task; Interlocked.Increment(ref disposed); }
        }, stop.Token);
        try
        {
            await Task.Delay(20); Assert.Equal(0, calls);
            monitor.Select(true); await Until(() => calls == 1);
            monitor.Select(false); await retired.Task.WaitAsync(TimeSpan.FromSeconds(2));
            monitor.Select(true); await Task.Delay(20); Assert.Equal(1, calls);
            release.TrySetResult(); await Until(() => calls == 2 && disposedBeforeRestart >= 0);
            Assert.Equal(1, disposedBeforeRestart);
            monitor.Select(false); await Until(() => disposed == 2);
            Assert.False(run.IsCompleted);
        }
        finally { release.TrySetResult(); stop.Cancel(); await Cancelled(run); }
    }

    [Fact]
    public async Task FailedStopRetiresThePoisonedHostBeforeStartingAFreshOne()
    {
        var monitor = new Monitor(); monitor.Select(true);
        var time = new ManualTime(); using var stop = new CancellationTokenSource();
        var calls = 0; NativeEngineHostTests.WireConnection? old = null;
        var run = new NativeEngineSupervisor(monitor, "endpoint-1", time).RunAsync(async token => {
            var first = Interlocked.Increment(ref calls) == 1;
            var wire = new NativeEngineHostTests.WireConnection { StopReplies = !first };
            await using var host = new NativeEngineHost(wire, first ? 41 : 42);
            await host.ArmAsync(NativeEngineHostTests.Binding, token);
            if (first)
            {
                old = wire;
                var lease = host.Open();
                await lease.SendAsync("old", "start", new { capture_endpoint = "endpoint-1", sample_rate = 44100 }, token);
                await lease.DisposeAsync();
            }
            else { await Task.Delay(Timeout.Infinite, token); }
        }, stop.Token);
        try
        {
            await Until(() => time.Pending > 0, 5000);
            Assert.True(old!.Disposed); Assert.False(run.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(31)); await Until(() => calls == 2); Assert.True(old.Disposed);
        }
        finally { stop.Cancel(); await Cancelled(run); }
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("protocol")]
    [InlineData("binding")]
    public async Task RepeatedFailuresKeepTheWatcherAliveWithoutAdmittingInvalidAudio(string reason)
    {
        var monitor = new Monitor(); monitor.Select(true); var time = new ManualTime();
        using var stop = new CancellationTokenSource(); var calls = 0;
        var run = new NativeEngineSupervisor(monitor, "endpoint-1", time).RunAsync(_ => {
            Interlocked.Increment(ref calls); throw new IOException(reason);
        }, stop.Token);
        try
        {
            for (var i = 1; i <= 12; i++)
            {
                await Until(() => calls == i && time.Pending > 0); Assert.False(run.IsCompleted);
                time.Advance(TimeSpan.FromDays(365));
            }
            await Until(() => calls == 13 && time.Pending > 0);
            monitor.Select(false); await Until(() => time.Pending == 0);
            time.Advance(TimeSpan.FromDays(365)); Assert.Equal(13, calls);
            monitor.Select(true); await Until(() => calls == 14);
        }
        finally { stop.Cancel(); await Cancelled(run); }
    }

    [Fact]
    public async Task SilenceMuteVolumeAndElapsedTimeNeverReleaseASelectedEngine()
    {
        var monitor = new Monitor(); monitor.Select(true); var time = new ManualTime();
        using var stop = new CancellationTokenSource(); var calls = 0; var disposed = 0;
        var run = new NativeEngineSupervisor(monitor, "endpoint-1", time).RunAsync(async token => {
            Interlocked.Increment(ref calls);
            try { await Task.Delay(Timeout.Infinite, token); } finally { Interlocked.Increment(ref disposed); }
        }, stop.Token);
        try
        {
            await Until(() => calls == 1);
            foreach (var muted in new[] { true, false })
            {
                monitor.Publish(monitor.Snapshot with { Volume = monitor.Snapshot.Volume with { Muted = muted, Scalar = muted ? 0 : .5 }, Sessions = [] });
                time.Advance(TimeSpan.FromDays(36500));
            }
            await Task.Delay(30); Assert.Equal(1, calls); Assert.Equal(0, disposed); Assert.False(run.IsCompleted);
        }
        finally { stop.Cancel(); await Cancelled(run); }
    }

    [Fact]
    public async Task ExplicitShutdownDuringRecoveryDoesNotLaunchAnotherEngine()
    {
        var monitor = new Monitor(); monitor.Select(true); var time = new ManualTime();
        using var stop = new CancellationTokenSource(); var calls = 0;
        var run = new NativeEngineSupervisor(monitor, "endpoint-1", time).RunAsync(_ => {
            Interlocked.Increment(ref calls); throw new IOException("owned engine exited");
        }, stop.Token);
        try { await Until(() => time.Pending > 0); stop.Cancel(); await Cancelled(run); time.Advance(TimeSpan.FromDays(1)); Assert.Equal(1, calls); }
        finally { stop.Cancel(); await Cancelled(run); }
    }

    private static async Task Cancelled(Task run) { try { await run; } catch (OperationCanceledException) { } }
    private static async Task Until(Func<bool> condition, int milliseconds = 2000)
    { using var timeout = new CancellationTokenSource(milliseconds); while (!condition()) await Task.Delay(5, timeout.Token); }
    private sealed class Monitor : INativeOutputMonitor
    {
        private NativeOutputSnapshot _snapshot = new(1, ["other", "other", null], [], new(0, -6, .5, false, Guid.Empty), true);
        public NativeOutputSnapshot Snapshot => Volatile.Read(ref _snapshot);
        public event Action<NativeOutputSnapshot>? Changed;
        public event Action? EndpointsChanged { add { } remove { } }
        public void Publish(NativeOutputSnapshot value) { Volatile.Write(ref _snapshot, value); Changed?.Invoke(value); }
        public void Select(bool selected) => Publish(Snapshot with { Generation = Snapshot.Generation + 1, DefaultRenderIds = [selected ? "endpoint-1" : "other", selected ? "endpoint-1" : "other", null] });
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult(Snapshot.DefaultRenderIds[0]);
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task SetMutedAsync(bool muted, Guid context, CancellationToken cancellation) => throw new InvalidOperationException();
        public Task MuteAsync(string? endpointId) => throw new InvalidOperationException();
        public Task RestoreAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        private readonly List<Timer> _timers = [];
        public int Pending { get { lock (_timers) return _timers.Count(t => !t.Disposed); } }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan span)
        {
            Interlocked.Add(ref _ticks, span.Ticks); Timer[] due;
            lock (_timers) { due = _timers.Where(t => !t.Disposed && t.Due <= _ticks).ToArray(); foreach (var t in due) t.Disposed = true; }
            foreach (var t in due) t.Callback(t.State);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(this, callback, state, GetTimestamp() + dueTime.Ticks); lock (_timers) _timers.Add(timer); return timer; }
        private sealed class Timer(ManualTime owner, TimerCallback callback, object? state, long due) : ITimer
        {
            public readonly TimerCallback Callback = callback;
            public readonly object? State = state;
            public long Due = due;
            public bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (owner._timers) { Due = owner.GetTimestamp() + dueTime.Ticks; return !Disposed; } }
            public void Dispose() { lock (owner._timers) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

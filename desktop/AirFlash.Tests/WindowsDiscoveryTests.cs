using System.ComponentModel;
using System.Runtime.InteropServices;
using AirFlash.App.Services;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class WindowsDiscoveryTests
{
    [Fact]
    public void DiscoveryFailureNamesHomePodWithoutUnsupportedManualSetup()
    {
        var api = new FakeDns { FailBrowse = true };
        using var discovery = new WindowsDiscovery(api, () => []);
        var failures = new List<string>(); discovery.Failed += failures.Add;
        discovery.Start();
        Assert.NotEmpty(failures);
        Assert.All(failures, message => {
            Assert.Contains("HomePod", message);
            Assert.DoesNotContain("manually", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("接收器", message);
        });
    }

    [Theory]
    [InlineData("start")]
    [InlineData("browse")]
    [InlineData("resolve")]
    [InlineData("worker")]
    [InlineData("capability")]
    public void DiscoveryFaultRecoversWithoutAnotherStartOrNetworkChange(string fault)
    {
        var time = new ManualTime(); var api = new FakeDns { FailBrowse = fault == "start" };
        var available = fault != "capability";
        using var discovery = new WindowsDiscovery(api, () => [], time: time, dnsAvailable: () => available);
        IReadOnlyList<Receiver> last = []; discovery.Changed += values => last = values;
        discovery.Start();
        if (fault is "browse" or "resolve" or "worker")
        {
            if (fault == "browse") api.Browsers[0].Callback(1222, []);
            else if (fault == "worker") api.Browsers[0].Failed("discovery worker stopped");
            else { api.Browsers[0].Callback(0, [new(Name, 120)]); api.Resolvers[0].Callback(1222, null); }
        }
        var old = api.Operations.ToArray(); var attempts = api.BrowseAttempts;
        api.FailBrowse = false; available = true;
        time.Advance(TimeSpan.FromMilliseconds(999)); Assert.Equal(attempts, api.BrowseAttempts);
        time.Advance(TimeSpan.FromMilliseconds(1)); Assert.Equal(attempts + 2, api.BrowseAttempts);
        Assert.All(old, operation => Assert.Equal(1, operation.Stops));
        api.Browsers[^2].Callback(0, [new(Name, 120)]); api.Resolvers[^1].Callback(0, Record());
        Assert.Equal("192.0.2.10", Assert.Single(last).Address);
        if (old.OfType<ResolveOperation>().FirstOrDefault() is { } stale) stale.Callback(0, Record("192.0.2.99"));
        foreach (var staleBrowser in old.OfType<BrowseOperation>()) staleBrowser.Failed("obsolete query failure");
        Assert.Equal("192.0.2.10", Assert.Single(last).Address);
        attempts = api.BrowseAttempts; time.Advance(TimeSpan.FromSeconds(30)); Assert.Equal(attempts, api.BrowseAttempts);
    }

    [Fact]
    public void DiscoveryRecoveryBacksOffAndStopCancelsPendingRetries()
    {
        var time = new ManualTime(); var api = new FakeDns { FailBrowse = true };
        using var discovery = new WindowsDiscovery(api, () => [], time: time);
        discovery.Start();
        foreach (var seconds in new[] { 1, 2, 4, 8, 16, 30, 30 })
        {
            var attempts = api.BrowseAttempts;
            time.Advance(TimeSpan.FromMilliseconds(seconds * 1000 - 1)); Assert.Equal(attempts, api.BrowseAttempts);
            time.Advance(TimeSpan.FromMilliseconds(1)); Assert.Equal(attempts + 2, api.BrowseAttempts);
        }
        api.FailBrowse = false; time.Advance(TimeSpan.FromSeconds(30));
        api.Browsers[^2].Callback(0, [new(Name, 120)]); api.Resolvers[^1].Callback(0, Record());
        time.Advance(TimeSpan.FromSeconds(10)); api.Browsers[^2].Failed("new failure after recovery");
        var recovered = api.BrowseAttempts;
        time.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(recovered + 2, api.BrowseAttempts);
        api.Browsers[^2].Failed("pending retry before stop");
        discovery.Stop(); var stopped = api.BrowseAttempts;
        time.Advance(TimeSpan.FromHours(8)); Assert.Equal(stopped, api.BrowseAttempts);
    }

    [Fact]
    public void SilentDiscoveryAndExpiredRecordsAreRefreshedWithoutAHotLoop()
    {
        var time = new ManualTime(); var api = new FakeDns();
        using var discovery = new WindowsDiscovery(api, () => [], time: time);
        discovery.Start(); time.Advance(TimeSpan.FromSeconds(29)); Assert.Equal(2, api.BrowseAttempts);
        time.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(4, api.BrowseAttempts);
        api.Browsers[^2].Callback(0, [new(Name, 120)]); api.Resolvers[^1].Callback(0, Record());
        time.Advance(TimeSpan.FromSeconds(119)); Assert.Equal(4, api.BrowseAttempts);
        time.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(4, api.BrowseAttempts);
        time.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(6, api.BrowseAttempts);
        discovery.Dispose(); time.Advance(TimeSpan.FromMinutes(5)); Assert.Equal(6, api.BrowseAttempts);
    }

    [Fact]
    public void AHealthyDeviceDoesNotHideAnotherUnresolvedDiscoveryQuery()
    {
        var time = new ManualTime(); var api = new FakeDns();
        using var discovery = new WindowsDiscovery(api, () => [], time: time);
        discovery.Start(); api.Browsers[0].Callback(0, [new(Name, 120)]); api.Resolvers[0].Callback(0, Record());
        time.Advance(TimeSpan.FromSeconds(5));
        api.Browsers[0].Callback(0, [new("Other._airplay._tcp.local", 120)]);
        time.Advance(TimeSpan.FromSeconds(29)); Assert.Equal(2, api.BrowseAttempts);
        time.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(4, api.BrowseAttempts);
    }

    [Fact]
    public void SilentWindowsQueriesRecoverThroughTheExistingManagedProvider()
    {
        var time = new ManualTime(); var native = new FakeNative(); var fallback = new FakeDns();
        var api = new WindowsDnsSdApi(native, fallback);
        using var discovery = new WindowsDiscovery(api, () => [new("lan", "LAN", "LAN", "192.0.2.1", true, 17)], time: time);
        discovery.SetInterface("lan");
        IReadOnlyList<Receiver> last = []; discovery.Changed += values => last = values;
        discovery.Start(); Assert.Equal(2, native.Starts); Assert.Empty(fallback.Browsers);
        time.Advance(TimeSpan.FromSeconds(29)); Assert.Empty(fallback.Browsers);
        time.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(2, fallback.Browsers.Count);
        Assert.Equal(2, native.Starts); Assert.Equal(2, native.Cancels);
        fallback.Browsers[0].Callback(0, [new(Name, 120)]); fallback.Resolvers[0].Callback(0, Record());
        Assert.Equal("192.0.2.10", Assert.Single(last).Address);
        using var unaffected = api.Browse(Type, 18, (_, _) => { }, _ => { });
        Assert.Equal(3, native.Starts);
        time.Advance(TimeSpan.FromSeconds(30)); Assert.Equal(2, fallback.Browsers.Count);
    }

    [Fact]
    public void ExpiredWindowsDiscoverySwitchesProviderAfterAnInitiallyGoodResult()
    {
        var time = new ManualTime(); var native = new FakeNative(); var fallback = new FakeDns();
        using var discovery = new WindowsDiscovery(new WindowsDnsSdApi(native, fallback), () => [], time: time);
        IReadOnlyList<Receiver> last = []; discovery.Changed += values => last = values;
        discovery.Start(); native.EmitAt(0); native.EmitAt(2); Assert.Single(last);
        time.Advance(TimeSpan.FromSeconds(120)); Assert.Empty(last);
        time.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(2, fallback.Browsers.Count);
        fallback.Browsers[0].Callback(0, [new(Name, 120)]); fallback.Resolvers[0].Callback(0, Record());
        Assert.Single(last);
    }

    [Theory]
    [InlineData(5u)]
    [InlineData(87u)]
    public void ExplicitNativePermissionAndRequestFailuresCannotTriggerSilentFallback(uint error)
    {
        var time = new ManualTime(); var native = new FakeNative { StartStatus = error }; var fallback = new FakeDns();
        using var discovery = new WindowsDiscovery(new WindowsDnsSdApi(native, fallback), () => [], time: time);
        discovery.Start();
        foreach (var seconds in new[] { 1, 2, 4, 8, 16, 30, 30 }) time.Advance(TimeSpan.FromSeconds(seconds));
        Assert.Empty(fallback.Browsers);
    }

    [Fact]
    public async Task InFlightPublicationCannotFinishAfterTheNewInterfaceSnapshot()
    {
        var api = new FakeDns(); using var discovery = new WindowsDiscovery(api, () => [new("nic", "LAN", "LAN", "192.0.2.1", true, 17)]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        IReadOnlyList<Receiver> last = [];
        var blocked = 0;
        discovery.Changed += items =>
        {
            if (items.Any(r => r.Address == "192.0.2.10") && Interlocked.Exchange(ref blocked, 1) == 0)
            { entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException(); }
        };
        discovery.Changed += items => last = items;
        discovery.Start(); api.Browsers[0].Callback(0, [new(Name, 120)]);
        var old = Task.Run(() => api.Resolvers[0].Callback(0, Record()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var newer = Task.Run(() =>
            {
                discovery.SetInterface("nic"); api.Browsers[2].Callback(0, [new(Name, 120)]);
                api.Resolvers[1].Callback(0, Record("192.0.2.20"));
            });
            await newer.WaitAsync(TimeSpan.FromSeconds(2));
            release.Set(); await old;
            Assert.Equal("192.0.2.20", Assert.Single(last).Address);
        }
        finally { release.Set(); await old; }
    }
    private const string Type = "_airplay._tcp.local", Name = "Office._airplay._tcp.local";
    private static ServiceRecord Record(string address = "192.0.2.10") => new(Name, Type, address, 7000, new Dictionary<string, string> { ["deviceid"] = "aabbcc" });
    [Fact]
    public void QueuedSnapshotCanBeCheckedAfterInterfaceChanges()
    {
        var api = new FakeDns(); using var discovery = new WindowsDiscovery(api, () => [new("nic", "LAN", "LAN", "192.0.2.1", true, 17)]);
        IReadOnlyList<Receiver> last = []; discovery.Changed += items => last = items;
        discovery.Start(); api.Browsers[0].Callback(0, [new(Name, 120)]); api.Resolvers[0].Callback(0, Record());
        var old = last; Assert.True(((IDiscoveryService)discovery).IsCurrentSnapshot(old));
        discovery.SetInterface("nic");
        Assert.False(((IDiscoveryService)discovery).IsCurrentSnapshot(old));
        Assert.True(((IDiscoveryService)discovery).IsCurrentSnapshot(last));
    }
    [Fact]
    public void MissingDnsEntryPointsReportsFailureWithoutOpeningQueries()
    {
        var api = new FakeDns(); using var discovery = new WindowsDiscovery(api, () => [], dnsAvailable: () => false);
        string? failure = null; discovery.Failed += value => failure = value;
        discovery.Start(); Assert.NotNull(failure); Assert.Empty(api.Operations);
    }
    [Fact]
    public void CallbackErrorStatusIsReportedAndDoesNotDiscardShellState()
    {
        var api = new FakeDns(); using var discovery = new WindowsDiscovery(api, () => []);
        var failures = 0; discovery.Failed += _ => failures++;
        discovery.Start(); api.Browsers[0].Callback(5, []);
        api.Browsers[0].Callback(0, [new(Name, 120)]);
        api.Resolvers[0].Callback(5, null);
        Assert.Equal(2, failures);
    }
    [Fact]
    public void FailedBrowsersReportPausedAndHiddenInterfaceChangesNeverStartQueries()
    {
        var api = new FakeDns { FailBrowse = true }; using var discovery = new WindowsDiscovery(api, () => []);
        var running = true; var failures = 0;
        discovery.RunningChanged += value => running = value; discovery.Failed += _ => failures++;
        discovery.Start(); Assert.False(running); Assert.Equal(2, failures);
        discovery.Stop(); api.FailBrowse = false; discovery.SetInterface("missing");
        Assert.Empty(api.Operations);
        discovery.SetInterface(""); discovery.Start(); Assert.Equal(2, api.Browsers.Count);
    }
    [Fact]
    public void RepeatedBrowseAndResultsKeepOneResolverUntilStop()
    {
        var api = new FakeDns(); using var discovery = new WindowsDiscovery(api, () => []);
        IReadOnlyList<Receiver> last = []; discovery.Changed += items => last = items;
        discovery.Start(); discovery.Start(); Assert.Equal(2, api.Browsers.Count);
        for (var i = 0; i < 100; i++) api.Browsers[0].Callback(0, [new(Name, 120)]);
        Assert.Single(api.Resolvers);
        api.Resolvers[0].Callback(0, Record()); api.Resolvers[0].Callback(0, Record("192.0.2.11"));
        Assert.Equal("192.0.2.11", Assert.Single(last).Address);
        Assert.Equal(0, api.Resolvers[0].Stops);
        var changes = 0; discovery.Changed += _ => changes++;
        discovery.Stop(); discovery.Stop(); Assert.Equal(0, changes);
        Assert.All(api.Operations, operation => Assert.Equal(1, operation.Stops));
        api.Resolvers[0].Callback(0, Record("192.0.2.99")); Assert.Equal(0, changes);
        discovery.Start(); Assert.Equal(4, api.Browsers.Count);
        api.Browsers[2].Callback(0, [new(Name, 120)]); api.Resolvers[1].Callback(0, Record());
        Assert.Single(last);
    }
    [Fact]
    public void RemovalCancelsOutsideCacheLockAndRejectsLateResults()
    {
        var api = new FakeDns(); using var discovery = new WindowsDiscovery(api, () => []);
        IReadOnlyList<Receiver> last = []; discovery.Changed += items => last = items;
        discovery.Start(); api.Browsers[0].Callback(0, [new(Name, 120)]); api.Resolvers[0].Callback(0, Record());
        api.Resolvers[0].DuringStop = () =>
        {
            var callback = Task.Run(() => api.Resolvers[0].Callback(0, Record()));
            Assert.True(callback.Wait(TimeSpan.FromSeconds(2)), "Cancellation held the cache lock.");
        };
        api.Browsers[0].Callback(0, [new(Name, 0)]);
        Assert.Equal(1, api.Resolvers[0].Stops); Assert.Empty(last);
    }
    [Fact]
    public void EarlyCallbackCanStopBeforeHandleIsAttached()
    {
        var api = new FakeDns { EarlyBrowse = true, EarlyResolve = true };
        using var discovery = new WindowsDiscovery(api, () => []);
        discovery.Changed += items => { if (items.Count > 0) discovery.Stop(); };
        discovery.Start(); Assert.All(api.Operations, operation => Assert.Equal(1, operation.Stops));
    }
    [Fact]
    public void FailedQueryCanRetryAndSwitchingInterfaceCancelsOldEpoch()
    {
        var api = new FakeDns { FailResolve = true }; using var discovery = new WindowsDiscovery(api, () => [new("nic", "LAN", "LAN", "192.0.2.1", true, 17)]);
        var failures = 0; discovery.Failed += _ => failures++;
        IReadOnlyList<Receiver> last = []; discovery.Changed += items => last = items;
        discovery.Start(); api.Browsers[0].Callback(0, [new(Name, 120)]); Assert.Equal(1, failures);
        api.FailResolve = false; api.Browsers[0].Callback(0, [new(Name, 120)]); Assert.Single(api.Resolvers);
        var old = api.Operations.ToArray();
        discovery.SetInterface("nic"); Assert.All(old, operation => Assert.Equal(1, operation.Stops));
        api.Resolvers[0].Callback(0, Record()); Assert.Empty(last);
        Assert.All(api.Browsers.Skip(2), operation => Assert.Equal(17u, operation.Index));
        discovery.Dispose(); Assert.All(api.Operations, operation => Assert.Equal(1, operation.Stops));
    }
    [Fact]
    public void TtlExpiresOnlyWhileScanningAndResumedCacheGetsGracePeriod()
    {
        var time = new ManualTime(); var api = new FakeDns(); using var discovery = new WindowsDiscovery(api, () => [], time: time);
        IReadOnlyList<Receiver> last = []; discovery.Changed += items => last = items;
        discovery.Start(); api.Browsers[0].Callback(0, [new(Name, 120)]); api.Resolvers[0].Callback(0, Record());
        discovery.Stop(); time.Advance(TimeSpan.FromMinutes(5)); Assert.Single(last);
        discovery.Start(); time.Advance(TimeSpan.FromSeconds(9)); Assert.Single(last);
        time.Advance(TimeSpan.FromSeconds(1)); Assert.Empty(last);
        api.Browsers[2].Callback(0, [new(Name, 30)]); api.Resolvers[1].Callback(0, Record());
        time.Advance(TimeSpan.FromSeconds(30)); Assert.Empty(last); Assert.Equal(1, api.Resolvers[1].Stops);
    }
    [Fact]
    public void RepeatedSessionsLeaveNoActiveOperations()
    {
        var api = new FakeDns(); using var discovery = new WindowsDiscovery(api, () => []);
        for (var i = 0; i < 100; i++)
        {
            discovery.Start(); api.Browsers[^2].Callback(0, [new(Name, 120)]); api.Resolvers[^1].Callback(0, Record()); discovery.Stop();
            Assert.All(api.Operations, operation => Assert.Equal(1, operation.Stops));
        }
    }
    [Fact]
    public void NativeResultDoesNotReleaseCancellationHandleAndEveryResultIsFreed()
    {
        var native = new FakeNative(); var callbacks = 0;
        var operation = new WindowsDnsSdApi.Operation(native, Name, 0, true, (_, _) => callbacks++, _ => { });
        operation.Start(); native.Emit(); native.Emit(); Assert.Equal(2, callbacks); Assert.Equal(2, native.Frees); Assert.False(operation.IsReleased);
        native.CallbackOnCancel = true; operation.Dispose(); operation.Dispose();
        Assert.Equal(1, native.Cancels); Assert.Equal(3, native.Frees); Assert.Equal(2, callbacks); Assert.True(operation.IsReleased);
    }
    [Fact]
    public void NativeEarlyCallbackDefersCancellationUntilStartReturns()
    {
        var native = new FakeNative { CallbackOnStart = true }; WindowsDnsSdApi.Operation? operation = null;
        operation = new(native, Name, 0, true, (_, _) => operation!.Dispose(), _ => { });
        operation.Start(); Assert.Equal(1, native.Cancels); Assert.True(operation.IsReleased); Assert.Equal(1, native.Frees);
    }
    [Fact]
    public void NativeStartFailureAndCancellationRetryRetainCorrectOwnership()
    {
        var native = new FakeNative { StartStatus = 87 };
        var failed = new WindowsDnsSdApi.Operation(native, Name, 0, true, (_, _) => { }, _ => { });
        Assert.Throws<Win32Exception>(failed.Start); Assert.True(failed.IsReleased); Assert.Equal(0, native.Cancels);
        native.StartStatus = 9506; native.CancelStatus = 5;
        var operation = new WindowsDnsSdApi.Operation(native, Name, 0, true, (_, _) => { }, _ => { });
        operation.Start(); Assert.Throws<Win32Exception>(operation.Dispose); Assert.False(operation.IsReleased);
        native.CancelStatus = 0; operation.Dispose(); Assert.True(operation.IsReleased); Assert.Equal(2, native.Cancels);
    }
    private sealed class FakeDns : IDnsSdApi
    {
        public List<BrowseOperation> Browsers { get; } = [];
        public List<ResolveOperation> Resolvers { get; } = [];
        public IEnumerable<FakeOperation> Operations => Browsers.Cast<FakeOperation>().Concat(Resolvers);
        public bool EarlyBrowse, EarlyResolve, FailResolve, FailBrowse;
        public int BrowseAttempts;
        public IDisposable Browse(string type, uint index, Action<uint, IReadOnlyList<DnsSdService>> callback, Action<string> failed)
        {
            BrowseAttempts++;
            if (FailBrowse) throw new Win32Exception(87);
            var operation = new BrowseOperation(index, callback, failed); Browsers.Add(operation);
            if (EarlyBrowse) callback(0, [new(Name, 120)]); return operation;
        }
        public IDisposable Resolve(string instance, string type, uint index, Action<uint, ServiceRecord?> callback, Action<string> failed)
        {
            if (FailResolve) throw new Win32Exception(87);
            var operation = new ResolveOperation(index, callback); Resolvers.Add(operation);
            if (EarlyResolve) callback(0, Record()); return operation;
        }
    }
    private class FakeOperation(uint index) : IDisposable
    {
        public uint Index { get; } = index;
        public int Stops { get; private set; }
        public Action? DuringStop;
        public void Dispose() { Stops++; DuringStop?.Invoke(); }
    }
    private sealed class BrowseOperation(uint index, Action<uint, IReadOnlyList<DnsSdService>> callback, Action<string> failed) : FakeOperation(index)
    {
        public Action<uint, IReadOnlyList<DnsSdService>> Callback { get; } = callback;
        public Action<string> Failed { get; } = failed;
    }
    private sealed class ResolveOperation(uint index, Action<uint, ServiceRecord?> callback) : FakeOperation(index) { public Action<uint, ServiceRecord?> Callback { get; } = callback; }
    private sealed class FakeNative : IWindowsDnsApi
    {
        private DnsSdRequest _request;
        private readonly List<DnsSdRequest> _requests = [];
        public int Starts, Cancels, Frees;
        public uint StartStatus = 9506, CancelStatus;
        public bool CallbackOnStart, CallbackOnCancel;
        public uint Start(ref DnsSdRequest request, IntPtr cancel, bool resolve)
        {
            _request = request; _requests.Add(request); Starts++; Marshal.WriteIntPtr(cancel, new(123)); if (CallbackOnStart) Emit(); return StartStatus;
        }
        public uint Cancel(IntPtr cancel, bool resolve)
        {
            Assert.Equal(new IntPtr(123), Marshal.ReadIntPtr(cancel)); Cancels++;
            if (CallbackOnCancel) Assert.True(Task.Run(Emit).Wait(TimeSpan.FromSeconds(2)), "Cancellation held the operation lock.");
            return CancelStatus;
        }
        public void Emit() => Marshal.GetDelegateForFunctionPointer<DnsSdCallback>(_request.Callback)(0, _request.Context, new(1));
        public void EmitAt(int index) => Marshal.GetDelegateForFunctionPointer<DnsSdCallback>(_requests[index].Callback)(0, _requests[index].Context, new(1));
        public IReadOnlyList<DnsSdService> ReadBrowse(IntPtr records) => [new(Name, 120)];
        public ServiceRecord? ReadResolve(string name, string type, IntPtr result) => Record();
        public void FreeResult(IntPtr result, bool resolve) => Frees++;
    }
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private ManualTimer? _timer;
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => _timer = new(this, callback, state);
        public void Advance(TimeSpan span) { _now += span; _timer?.Fire(); }
        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            public bool Change(TimeSpan dueTime, TimeSpan period) { _due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime; return true; }
            public void Fire() { if (_due <= owner.GetUtcNow()) { _due = null; callback(state); } }
            public void Dispose() => _due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

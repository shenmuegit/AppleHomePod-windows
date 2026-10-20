using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AirFlash.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
namespace AirFlash.App.Services;

public sealed class WindowsNativeOutput : INativeOutputMonitor, IMMNotificationClient
{
    private readonly NativeBinding _binding;
    private readonly NativeBindingValidation _bindingValidation = new();
    private readonly BlockingCollection<Action> _queue = new(64);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, (AudioSessionControl Control, SessionEvents Events, NativeAudioSession Value)> _sessions = [];
    private readonly NativeSessionTracker _tracker = new();
    private readonly HashSet<Guid> _ownContexts = [];
    private readonly System.Threading.Timer _publish;
    private readonly Task _initialized;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private AudioSessionManager? _manager;
    private NativeOutputSnapshot _snapshot = new(0, [null, null, null], [], new(0, 0, 0, true, Guid.Empty), false);
    private NativeVolume _volume = new(0, 0, 0, true, Guid.Empty);
    private Exception? _callbackFailure;
    private long _revision, _generation = 1;
    private bool _disposed, _bindingValid, _revalidate;
    private int _scheduled;
    public NativeOutputSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool IsBindingCurrent => Snapshot.BindingValid && _bindingValidation.IsCurrent;
    public event Action<NativeOutputSnapshot>? Changed;
    public event Action? EndpointsChanged;
    public WindowsNativeOutput(NativeBinding binding)
    {
        if (NativeBindingPolicy.ValidateStored(binding) is { } invalid) throw new InvalidDataException(invalid);
        _binding = binding;
        _publish = new(_ => Post(Publish), null, Timeout.Infinite, Timeout.Infinite);
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var action in _queue.GetConsumingEnumerable())
                {
                    action();
                    if (Interlocked.Exchange(ref _callbackFailure, null) is { } failure) Invalidate(failure);
                }
            }
            finally
            {
                try { Cleanup(); _stopped.TrySetResult(); }
                catch (Exception error) { _stopped.TrySetException(error); }
                _queue.Dispose();
            }
        }) { IsBackground = true, Name = "AppleHome native audio monitor" };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        _initialized = InvokeAsync(() =>
        {
            _enumerator = new(); _enumerator.RegisterEndpointNotificationCallback(this);
            _device = _enumerator.GetDevice(_binding.Endpoint.EndpointId);
            if (_device.DataFlow != DataFlow.Render) throw new InvalidDataException("Native output must be a playback endpoint.");
            _device.AudioEndpointVolume.OnVolumeNotification += VolumeNotification;
            _manager = _device.AudioSessionManager;
            // Subscribe before GetCount enables delivery; keep the collection before enumeration.
            _manager.OnSessionCreated += SessionCreated;
            var count = _manager.Sessions.Count;
            for (var i = 0; i < count; i++) Retain(_manager.Sessions[i], false);
            _volume = ReadVolume(Guid.Empty); _revalidate = true; Publish(); return true;
        });
    }
    private Task<T> InvokeAsync<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queue)
        {
            if (_disposed) return Task.FromException<T>(new ObjectDisposedException(nameof(WindowsNativeOutput)));
            if (!_queue.TryAdd(() => { try { result.SetResult(action()); } catch (Exception error) { result.SetException(error); } }))
                result.TrySetException(new IOException("Native monitor operation queue exceeded its bound."));
        }
        return result.Task;
    }
    private async Task<T> RunAsync<T>(Func<T> action)
    { await _initialized.ConfigureAwait(false); return await InvokeAsync(action).ConfigureAwait(false); }
    private void Post(Action action)
    {
        lock (_queue)
        {
            if (_disposed) return;
            if (!_queue.TryAdd(() => { try { action(); } catch (Exception error) { Invalidate(error); } }))
                Interlocked.CompareExchange(ref _callbackFailure, new IOException("Native monitor notification queue exceeded its bound."), null);
        }
    }
    private void Schedule()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 0) _publish.Change(50, Timeout.Infinite);
    }
    private void SessionCreated(object sender, IAudioSessionControl control) => Post(() => { Retain(new(control), true); Schedule(); });
    private void Retain(AudioSessionControl control, bool created)
    {
        var retained = false;
        try
        {
            var id = control.GetSessionInstanceIdentifier;
            if (_sessions.ContainsKey(id)) return;
            var events = new SessionEvents(this, id);
            control.RegisterEventClient(events);
            var value = new NativeAudioSession(_binding.Endpoint.EndpointId, id, checked((int)control.GetProcessID), control.State == AudioSessionState.AudioSessionStateActive, created);
            _sessions.Add(id, (control, events, value)); _tracker.Update(value); retained = true;
        }
        finally { if (!retained) control.Dispose(); }
    }
    private void StateChanged(string id, AudioSessionState state)
    {
        if (!_sessions.TryGetValue(id, out var session)) return;
        var value = session.Value with { Active = state == AudioSessionState.AudioSessionStateActive, ObservedActive = false };
        _sessions[id] = (session.Control, session.Events, value); _tracker.Update(value);
        // Keep the final inactive descriptor until publication so a short active edge survives.
        if (state == AudioSessionState.AudioSessionStateExpired) ReleaseSession(id);
        Schedule();
    }
    private void ReleaseSession(string id)
    {
        if (_sessions.Remove(id, out var session)) session.Control.Dispose();
    }
    private void VolumeNotification(AudioVolumeNotificationData data) => Post(() =>
    {
        if (!_ownContexts.Contains(data.EventContext)) _revision = checked(_revision + 1);
        _volume = ReadVolume(data.EventContext); Schedule();
    });
    private NativeVolume ReadVolume(Guid context)
    {
        var endpoint = _device!.AudioEndpointVolume;
        return new(_revision, endpoint.MasterVolumeLevel, endpoint.MasterVolumeLevelScalar, endpoint.Mute, context);
    }
    private void Publish()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        long? validatedVersion = null;
        if (_revalidate)
        {
            _revalidate = false;
            validatedVersion = _bindingValidation.Version;
            var endpoints = new WindowsNativeBinding().SnapshotAsync(default).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            var current = endpoints.SingleOrDefault(e => e.EndpointId == _binding.Endpoint.EndpointId);
            var valid = current is not null && NativeBindingPolicy.ValidateLive(_binding, current) is null;
            if (_bindingValid && !valid) _generation = checked(_generation + 1);
            _bindingValid = valid;
        }
        var defaults = new[] { Role.Console, Role.Multimedia, Role.Communications }.Select(role =>
        {
            try { using var device = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, role); return device.ID; }
            catch (COMException) { return null; }
        }).ToArray();
        var value = new NativeOutputSnapshot(_generation, defaults, _tracker.TakeSnapshot(), _volume, _bindingValid);
        Volatile.Write(ref _snapshot, value);
        if (validatedVersion is { } version) _bindingValidation.CompleteValidation(version);
        Changed?.Invoke(value); EndpointsChanged?.Invoke();
        foreach (var expired in value.Sessions.Where(s => !_sessions.ContainsKey(s.InstanceId))) _tracker.Remove(expired.InstanceId);
    }
    private void Invalidate(Exception error)
    {
        _bindingValidation.NotifyChanged();
        AppPaths.Log($"Native output monitor failed: {error}"); _bindingValid = false; _generation = checked(_generation + 1);
        var value = Snapshot with { Generation = _generation, BindingValid = false };
        Volatile.Write(ref _snapshot, value); Changed?.Invoke(value);
    }
    public Task SetMutedAsync(bool muted, Guid context, CancellationToken cancellation)
    {
        if (context == Guid.Empty) throw new ArgumentException("An owned volume context is required.", nameof(context));
        var expected = Snapshot.Volume;
        return RunAsync(() =>
        {
            cancellation.ThrowIfCancellationRequested();
            var current = ReadVolume(Guid.Empty);
            if (current.Revision != expected.Revision || current.Db != expected.Db || current.Scalar != expected.Scalar || current.Muted != expected.Muted)
            {
                // Preserve a newer Windows operation that raced this synchronization request.
                _revision = checked(_revision + 1); _volume = ReadVolume(Guid.Empty); Publish(); return true;
            }
            _ownContexts.Add(context); _device!.AudioEndpointVolume.NotificationGuid = context;
            _device.AudioEndpointVolume.Mute = muted; _volume = ReadVolume(context); Publish(); return true;
        });
    }
    public Task<string?> GetDefaultEndpointIdAsync() => RunAsync(() => Snapshot.DefaultRenderIds[0]);
    internal Task<ApartmentState> ReadApartmentAsync() => RunAsync(() => Thread.CurrentThread.GetApartmentState());
    public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => RunAsync<IReadOnlyList<AudioEndpoint>>(() => [new(_binding.Endpoint.EndpointId, _device!.FriendlyName)]);
    public Task MuteAsync(string? endpointId) => throw new InvalidOperationException("Native output cannot mute a physical playback endpoint.");
    public Task RestoreAsync() => Task.CompletedTask;
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { if (flow == DataFlow.Render) Post(Schedule); }
    public void OnDeviceStateChanged(string deviceId, DeviceState state) => DeviceChanged(deviceId);
    public void OnDeviceAdded(string deviceId) => DeviceChanged(deviceId);
    public void OnDeviceRemoved(string deviceId) => DeviceChanged(deviceId);
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) => DeviceChanged(deviceId);
    private void DeviceChanged(string id)
    {
        if (!string.Equals(id, _binding.Endpoint.EndpointId, StringComparison.OrdinalIgnoreCase)) return;
        _bindingValidation.NotifyChanged();
        Post(() => { _revalidate = true; Schedule(); });
    }
    public async ValueTask DisposeAsync()
    {
        lock (_queue)
        {
            if (!_disposed)
            {
                _bindingValidation.NotifyChanged();
                _disposed = true; _publish.Dispose(); _queue.CompleteAdding();
            }
        }
        await _stopped.Task.ConfigureAwait(false);
    }
    private void Cleanup()
    {
        var failures = new List<Exception>();
        void Release(Action action) { try { action(); } catch (Exception error) { failures.Add(error); } }
        if (_manager is not null) Release(() => { _manager.OnSessionCreated -= SessionCreated; _manager.Dispose(); });
        foreach (var id in _sessions.Keys.ToArray()) Release(() => ReleaseSession(id));
        if (_device is not null) Release(() => { _device.AudioEndpointVolume.OnVolumeNotification -= VolumeNotification; _device.Dispose(); });
        if (_enumerator is not null) Release(() => { _enumerator.UnregisterEndpointNotificationCallback(this); _enumerator.Dispose(); });
        if (failures.Count > 0) throw new AggregateException("Native monitor cleanup failed.", failures);
    }
    private sealed class SessionEvents(WindowsNativeOutput owner, string id) : IAudioSessionEventsHandler
    {
        public void OnStateChanged(AudioSessionState state) => owner.Post(() => owner.StateChanged(id, state));
        public void OnSessionDisconnected(AudioSessionDisconnectReason reason) => owner.Post(() => owner.StateChanged(id, AudioSessionState.AudioSessionStateExpired));
        public void OnVolumeChanged(float volume, bool muted) { }
        public void OnDisplayNameChanged(string name) { }
        public void OnIconPathChanged(string path) { }
        public void OnChannelVolumeChanged(uint channels, IntPtr volumes, uint channel) { }
        public void OnGroupingParamChanged(ref Guid grouping) { }
    }
}

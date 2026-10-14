using System.Collections.Concurrent;
using AirFlash.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
namespace AirFlash.App.Services;

public sealed class AudioService : IAudioService, IAsyncDisposable, IMMNotificationClient
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, (MMDevice Device, AudioEndpointVolumeNotificationDelegate Callback)> _owned = [];
    private readonly Guid _muteContext = Guid.NewGuid();
    private readonly string _recoveryPath;
    private MuteRecoveryJournal? _journal;
    private MuteRecoveryJournal Journal => _journal ??= new(_recoveryPath);
    private readonly Task _initialized;
    private MMDeviceEnumerator? _notifications;
    private bool _disposed;
    public event Action? EndpointsChanged;

    public AudioService(string recoveryPath)
    {
        _recoveryPath = recoveryPath;
        var thread = new Thread(() =>
        {
            try { foreach (var operation in _queue.GetConsumingEnumerable()) operation(); }
            finally { _queue.Dispose(); _stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "AppleHome audio devices" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        _initialized = InvokeAsync(() =>
        {
            _notifications = new MMDeviceEnumerator();
            _notifications.RegisterEndpointNotificationCallback(this);
            return true;
        });
    }
    private Task<T> InvokeAsync<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queue)
        {
            if (_disposed) return Task.FromException<T>(new ObjectDisposedException(nameof(AudioService)));
            _queue.Add(() => { try { result.SetResult(action()); } catch (Exception error) { result.SetException(error); } });
        }
        return result.Task;
    }
    private async Task<T> RunAsync<T>(Func<T> action)
    {
        await _initialized.ConfigureAwait(false);
        return await InvokeAsync(action).ConfigureAwait(false);
    }
    public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => RunAsync<IReadOnlyList<AudioEndpoint>>(() =>
    {
        UiPerformance.Count("audio.enumerate");
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = new List<AudioEndpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
            using (device) endpoints.Add(new(device.ID, device.FriendlyName, device.DataFlow == DataFlow.Capture ? AudioEndpointKind.Recording : AudioEndpointKind.Playback));
        return endpoints;
    });
    public Task<string?> GetDefaultEndpointIdAsync() => RunAsync(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console); return device.ID; }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    });
    public Task MuteAsync(string? endpointId) => RunAsync(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = endpointId is null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console) : enumerator.GetDevice(endpointId);
        var id = device.ID;
        try
        {
            // Recording endpoint mute affects the captured signal, not local speakers.
            if (device.DataFlow != DataFlow.Render || _owned.ContainsKey(id) || device.AudioEndpointVolume.Mute) return true;
            Journal.Remember(id, false);
            device.AudioEndpointVolume.NotificationGuid = _muteContext;
            AudioEndpointVolumeNotificationDelegate callback = data =>
            {
                if (data.EventContext != _muteContext && !data.Muted) _ = ReleaseExternalMuteAsync(id, device);
            };
            device.AudioEndpointVolume.OnVolumeNotification += callback;
            _owned[id] = (device, callback);
            device.AudioEndpointVolume.Mute = true;
            return true;
        }
        finally { if (!_owned.TryGetValue(id, out var owned) || !ReferenceEquals(owned.Device, device)) device.Dispose(); }
    });
    private async Task ReleaseExternalMuteAsync(string id, MMDevice device)
    {
        try
        {
            await InvokeAsync(() =>
            {
                if (_owned.TryGetValue(id, out var owned) && ReferenceEquals(owned.Device, device))
                { Journal.Forget(id); Release(id); }
                return true;
            }).ConfigureAwait(false);
        }
        catch (Exception error) { AppPaths.Log("Could not release local mute ownership: " + error.Message); }
    }
    private void Release(string id)
    {
        if (!_owned.Remove(id, out var owned)) return;
        try { owned.Device.AudioEndpointVolume.OnVolumeNotification -= owned.Callback; }
        finally { owned.Device.Dispose(); }
    }
    public Task RecoverPendingMuteAsync() => RunAsync(() => { _ = Journal; Restore(); return true; });
    public Task RestoreAsync() => RunAsync(() => { Restore(); return true; });
    private void Restore()
    {
        if (_journal is null || _journal.Entries.Count == 0) return;
        using var enumerator = new MMDeviceEnumerator();
        foreach (var id in _journal.Entries.Keys.ToArray())
        {
            MMDevice? device = null;
            var held = _owned.TryGetValue(id, out var owned);
            try
            {
                device = held ? owned.Device : enumerator.GetDevice(id);
                var current = device.AudioEndpointVolume.Mute;
                if (_journal.RestoreValue(id, current) is { } previous && current != previous)
                { device.AudioEndpointVolume.NotificationGuid = _muteContext; device.AudioEndpointVolume.Mute = previous; }
                _journal.Forget(id);
                Release(id);
            }
            catch (System.Runtime.InteropServices.COMException) { Release(id); /* Journal retries when the endpoint returns. */ }
            finally { if (!held) device?.Dispose(); }
        }
    }
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { if (flow == DataFlow.Render && role == Role.Console) EndpointsChanged?.Invoke(); }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => EndpointsChanged?.Invoke();
    public void OnDeviceAdded(string deviceId) => EndpointsChanged?.Invoke();
    public void OnDeviceRemoved(string deviceId) => EndpointsChanged?.Invoke();
    public void OnPropertyValueChanged(string deviceId, PropertyKey key) => EndpointsChanged?.Invoke();
    public async ValueTask DisposeAsync()
    {
        Task cleanup;
        lock (_queue)
        {
            if (_disposed) { cleanup = _stopped.Task; }
            else
            {
                cleanup = InvokeAsync(() =>
                {
                    try { Restore(); }
                    finally
                    {
                        foreach (var id in _owned.Keys.ToArray()) Release(id);
                        if (_notifications is not null) { _notifications.UnregisterEndpointNotificationCallback(this); _notifications.Dispose(); }
                    }
                    return true;
                });
                _disposed = true;
                _queue.CompleteAdding();
            }
        }
        try { await cleanup.ConfigureAwait(false); }
        finally { await _stopped.Task.ConfigureAwait(false); }
    }
}

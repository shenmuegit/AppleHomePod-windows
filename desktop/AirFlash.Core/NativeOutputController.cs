using System.Threading.Channels;
namespace AirFlash.Core;

public sealed record NativeAudioSession(string EndpointId, string InstanceId, int ProcessId, bool Active, bool ObservedActive = false);
public sealed record NativeOutputSnapshot(long Generation, IReadOnlyList<string?> DefaultRenderIds,
    IReadOnlyList<NativeAudioSession> Sessions, NativeVolume Volume, bool BindingValid);
public interface INativeOutputMonitor : IAudioService, IAsyncDisposable
{
    NativeOutputSnapshot Snapshot { get; }
    event Action<NativeOutputSnapshot>? Changed;
    Task SetMutedAsync(bool muted, Guid context, CancellationToken cancellation);
}

// Preserve active edges until a snapshot is delivered, even if a short sound has ended.
public sealed class NativeSessionTracker
{
    private readonly Dictionary<string, NativeAudioSession> _sessions = [];
    public void Update(NativeAudioSession session)
    {
        var seen = session.Active || session.ObservedActive || _sessions.GetValueOrDefault(session.InstanceId)?.ObservedActive == true;
        _sessions[session.InstanceId] = session with { ObservedActive = seen };
    }
    public void Remove(string instanceId) => _sessions.Remove(instanceId);
    public IReadOnlyList<NativeAudioSession> TakeSnapshot()
    {
        var result = _sessions.Values.ToArray();
        foreach (var session in result) _sessions[session.InstanceId] = session with { ObservedActive = false };
        return result;
    }
}

public sealed class NativeOutputController
{
    private readonly NativeBinding _binding;
    private readonly INativeOutputMonitor _monitor;
    private readonly NativeEngineHost _host;
    private readonly SessionController _session;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly Channel<Update> _events = Channel.CreateBounded<Update>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private sealed record Update(NativeOutputSnapshot? Windows = null, SessionSnapshot? Session = null, long RouteEpoch = 0, ulong CaptureEpoch = 0, long UserRevision = 0);
    private NativeOutputSnapshot? _windows;
    private NativeVolume? _volume;
    private SessionSnapshot? _stream;
    private Receiver? _receiver;
    private long? _externalAt, _receiverWaitAt, _errorAt, _metricsAt;
    private long _routeEpoch, _connectRevision, _zeroRevision, _remoteObservedRevision;
    private ulong _streamCaptureEpoch;
    private int? _lastNonZero, _remoteActual, _restoreTarget;
    private long? _restoreSequence;
    private long _deviceRevision = -1;
    private double _devicePcmGain = 1;
    private double? _lastGain;
    private bool _active, _begun, _gateOpen, _clearPending, _externalActive;
    private int _run;
    public Guid OwnVolumeContext { get; } = Guid.NewGuid();
    public NativeOutputController(NativeBinding binding, INativeOutputMonitor monitor, NativeEngineHost host, SessionController session,
        TimeProvider? timeProvider = null, Action<string>? log = null)
    {
        if (NativeBindingPolicy.ValidateStored(binding) is { } invalid) throw new InvalidDataException(invalid);
        if (binding.Calibration is null) throw new InvalidDataException("Native output requires measured calibration.");
        _binding = binding; _monitor = monitor; _host = host; _session = session; _time = timeProvider ?? TimeProvider.System; _log = log;
    }
    private void Enqueue(Update value)
    {
        if (!_events.Writer.TryWrite(value)) _events.Writer.TryComplete(new IOException("Native output exceeded 64 pending events; user intent cannot be discarded."));
    }
    public async Task RunAsync(Func<string, Receiver?> resolveReceiver, CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _run, 1) != 0) throw new InvalidOperationException("Native output controller runs once.");
        void WindowsChanged(NativeOutputSnapshot value) => Enqueue(new(Windows: value));
        void SessionChanged(SessionSnapshot value) => Enqueue(new(Session: value, RouteEpoch: Interlocked.Read(ref _routeEpoch), CaptureEpoch: _host.CaptureEpoch, UserRevision: _monitor.Snapshot.Volume.Revision));
        _monitor.Changed += WindowsChanged; _session.Changed += SessionChanged;
        using var timer = _time.CreateTimer(_ => Enqueue(new()), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        _ = _host.Completion.ContinueWith(task => _events.Writer.TryComplete(task.IsFaulted ? task.Exception!.GetBaseException() : null), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await _monitor.GetDefaultEndpointIdAsync().ConfigureAwait(false); // Wait for the MTA listener's initial snapshot.
            Enqueue(new(Windows: _monitor.Snapshot));
            await foreach (var update in _events.Reader.ReadAllAsync(cancellation).ConfigureAwait(false))
            {
                if (update.Windows is { } windows) AcceptSnapshot(windows);
                if (_windows is null) continue;
                // A receiver reply may be queued ahead of the latest Windows operation.
                var latest = _monitor.Snapshot;
                if (!latest.BindingValid) throw new InvalidDataException("The native endpoint, driver or mix format changed; maintenance is required.");
                if (latest.Generation > _windows.Generation || latest.Generation == _windows.Generation && latest.Volume.Revision >= (_volume?.Revision ?? 0))
                    AcceptVolume(latest.Volume);
                if (update.Session is { } stream && update.RouteEpoch == _routeEpoch && _active)
                {
                    if (stream.State == PlaybackState.Connecting && _stream?.State is not PlaybackState.Connecting)
                    {
                        if (_stream is not null) _connectRevision = update.UserRevision;
                        _remoteActual = null; _restoreSequence = null; _restoreTarget = null; _gateOpen = false; _lastGain = null; _deviceRevision = -1; _metricsAt = null;
                    }
                    if (stream.Metrics is { } metrics && !ReferenceEquals(metrics, _stream?.Metrics)
                        && (_metricsAt is null || _time.GetElapsedTime(_metricsAt.Value) >= TimeSpan.FromSeconds(15)))
                    {
                        _log?.Invoke("Native capture timing: " + System.Text.Json.JsonSerializer.Serialize(metrics)); _metricsAt = _time.GetTimestamp();
                        _log?.Invoke("Native transport timing: " + System.Text.Json.JsonSerializer.Serialize(stream.Diagnostics?.Transport));
                    }
                    _stream = stream;
                    _streamCaptureEpoch = update.CaptureEpoch;
                    _remoteObservedRevision = update.UserRevision;
                    if (stream.State == PlaybackState.Error) _errorAt ??= _time.GetTimestamp();
                    else _errorAt = null;
                }
                await ReconcileAsync(resolveReceiver, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            _monitor.Changed -= WindowsChanged; _session.Changed -= SessionChanged; _events.Writer.TryComplete();
            await StopRouteAsync().ConfigureAwait(false);
        }
    }
    private void AcceptSnapshot(NativeOutputSnapshot value)
    {
        if (_windows is not null && value.Generation < _windows.Generation) return;
        if (value.Generation < 0 || value.DefaultRenderIds.Count != 3 || value.Sessions.Count > 4096
            || value.Sessions.Any(s => string.IsNullOrEmpty(s.InstanceId) || s.ProcessId < 0)) throw new InvalidDataException("Invalid native output snapshot.");
        if (!value.BindingValid) throw new InvalidDataException("The native endpoint, driver or mix format changed; maintenance is required.");
        _windows = value; AcceptVolume(value.Volume);
        var external = value.Sessions.Where(s => s.EndpointId == _binding.Endpoint.EndpointId
            && s.ProcessId != Environment.ProcessId && s.ProcessId != _host.ProcessId && s.InstanceId != _host.CaptureSessionInstanceId).ToArray();
        var active = external.Any(s => s.Active);
        if (_externalActive && !active || external.Any(s => s.Active || s.ObservedActive))
            _externalAt = _time.GetTimestamp();
        _externalActive = active;
    }
    private void AcceptVolume(NativeVolume value)
    {
        if (value.Revision < 0) throw new InvalidDataException("Invalid native volume revision.");
        _ = PcmVolumeMath.Gain(_binding.Calibration!, value);
        if (value.Context == OwnVolumeContext) value = value with { Revision = _volume?.Revision ?? value.Revision };
        if (_volume is not null && value.Revision < _volume.Revision) return;
        if (_volume is { } old && (old.Db != value.Db || old.Scalar != value.Scalar || old.Muted != value.Muted))
            _clearPending |= _binding.Calibration!.MasterInPcm || value.Muted || value.Scalar == 0;
        if (!_binding.Calibration!.MasterInPcm && !value.Muted && value.Scalar > 0) _clearPending = false;
        _volume = value;
    }
    private AppSettings Settings() => new()
    {
        CaptureMode = "endpoint", CaptureEndpoint = _binding.Endpoint.EndpointId, StreamSampleRate = "44100",
        LatencyMode = "custom", CustomBufferMs = 100, Equalizer = new(), MasterVolume = 100,
        MuteWhileStreaming = false, StandbyEnabled = false, AutoConnectOnDiscover = false, ForceReconnect = true, MaxReconnectAttempts = 5
    };
    private async Task ReconcileAsync(Func<string, Receiver?> resolve, CancellationToken cancellation)
    {
        var selected = _windows!.DefaultRenderIds.Any(id => id == _binding.Endpoint.EndpointId);
        var held = _externalActive || _externalAt is { } at && _time.GetElapsedTime(at) < TimeSpan.FromSeconds(30);
        if (!selected && !held) { await StopRouteAsync().ConfigureAwait(false); return; }
        if (!_begun)
        {
            _connectRevision = _volume!.Revision;
            await _host.BeginCaptureAsync(cancellation).ConfigureAwait(false); _begun = true; _receiverWaitAt = _time.GetTimestamp();
        }
        var receiver = resolve(_binding.ReceiverId);
        var metadataPending = receiver is not null && ReceiverIdentity.Normalize(receiver.Id) == _binding.ReceiverId && receiver.Online && receiver.Complete
            && !receiver.IsGroup && receiver.StereoId.Length == 0 && receiver.GroupId.Length == 0
            && receiver.Model is "" or "AudioAccessory5,1" && receiver.SoftwareVersion is "" or "27.0"
            && (receiver.Codecs.Length == 0 || receiver.Codecs.Contains((byte)1))
            && (receiver.Model.Length == 0 || receiver.SoftwareVersion.Length == 0 || !receiver.IsLeader);
        if (metadataPending)
        {
            _receiverWaitAt ??= _time.GetTimestamp();
            if (_time.GetElapsedTime(_receiverWaitAt.Value) >= TimeSpan.FromSeconds(2)) throw new IOException("The bound HomePod's discovery metadata did not become complete within the startup bound.");
            if (_active) { await GainAsync(0).ConfigureAwait(false); await GateAsync(false, false, cancellation).ConfigureAwait(false); }
            return;
        }
        if (receiver is not null && (ReceiverIdentity.Normalize(receiver.Id) != _binding.ReceiverId || !receiver.Online || !receiver.Complete || !receiver.PrefersNtpAlac))
            throw new InvalidOperationException("This receiver identity, firmware or group has not been qualified for native output.");
        if (!_active)
        {
            if (receiver is null)
            {
                if (_time.GetElapsedTime(_receiverWaitAt!.Value) >= TimeSpan.FromSeconds(2)) throw new IOException("The bound HomePod is unavailable; startup sound cannot be retained indefinitely.");
                return;
            }
            Interlocked.Increment(ref _routeEpoch); _active = true; _receiver = receiver; _stream = null; _remoteActual = null; _gateOpen = false; _lastGain = null; _deviceRevision = -1; _metricsAt = null;
            _receiverWaitAt = null;
            await _session.SetNativeGainAsync(0).ConfigureAwait(false);
            await _session.StartAsync(receiver, Settings()).ConfigureAwait(false);
            _log?.Invoke("Native route started for the verified receiver.");
        }
        else if (receiver is not null && receiver != _receiver)
        {
            _receiver = receiver; await _session.UpdateReceiverAsync(receiver, Settings()).ConfigureAwait(false);
            return; // Consume the new connection/volume snapshot before touching its capture gate.
        }
        if (receiver is not null) _receiverWaitAt = null;
        if (_errorAt is { } failed && _session.Snapshot.State == PlaybackState.Error && _time.GetElapsedTime(failed) >= TimeSpan.FromMilliseconds(100))
            throw new IOException(_session.Snapshot.Message);
        if (_stream?.State is not (PlaybackState.Streaming or PlaybackState.Standby)) return;
        await ApplyVolumeAsync(cancellation).ConfigureAwait(false);
    }
    private async Task ApplyVolumeAsync(CancellationToken cancellation)
    {
        if (!IsCurrentStream()) return;
        if (!_binding.Calibration!.MasterInPcm) { await ApplyDeviceVolumeAsync(cancellation).ConfigureAwait(false); return; }
        var volume = _volume!;
        if (_clearPending && !_gateOpen && !await GateAsync(false, true, cancellation).ConfigureAwait(false)) return;
        var remote = _stream!.DeviceVolume;
        if (remote?.Available != true || remote.Actual is not { } actual)
        {
            await GainAsync(0).ConfigureAwait(false); await GateAsync(false, _clearPending, cancellation).ConfigureAwait(false); return;
        }
        var changed = _remoteActual != actual;
        if (changed && actual == 0) _zeroRevision = _remoteActual is null ? _connectRevision : _remoteObservedRevision;
        _remoteActual = actual;
        if (actual > 0) _lastNonZero = actual;
        if (_restoreSequence is { } sequence)
        {
            if (remote.Sequence != sequence) return;
            if (remote.Target is not null || remote.Status == "pending")
            {
                if (volume.Muted || volume.Scalar == 0) { await GainAsync(0).ConfigureAwait(false); await GateAsync(true, true, cancellation).ConfigureAwait(false); }
                return;
            }
            if (remote.Status != "confirmed" || actual != _restoreTarget) throw new IOException("HomePod did not confirm the requested reference volume.");
            _restoreSequence = null; _restoreTarget = null;
        }
        if (actual == 0 && !volume.Muted && volume.Scalar > 0 && volume.Revision > _zeroRevision)
        {
            if (!await GateAsync(false, true, cancellation).ConfigureAwait(false)) return;
            await GainAsync(0).ConfigureAwait(false);
            if (!IsCurrentStream()) return;
            var target = _lastNonZero ?? _binding.Calibration!.ResumeDeviceVolume;
            if (target is < 1 or > 100) throw new InvalidDataException("No measured nonzero reference volume is available.");
            RequestDeviceVolume(target);
            return;
        }
        if (actual == 0)
        {
            if (!volume.Muted)
            {
                if (!IsCurrentStream()) return;
                await _monitor.SetMutedAsync(true, OwnVolumeContext, cancellation).ConfigureAwait(false);
                AcceptVolume(_monitor.Snapshot.Volume); volume = _volume!;
                if (!volume.Muted && volume.Revision > _zeroRevision) return;
            }
            await GainAsync(0).ConfigureAwait(false); await GateAsync(true, changed || _clearPending, cancellation).ConfigureAwait(false); return;
        }
        await GainAsync(PcmVolumeMath.Gain(_binding.Calibration!, volume)).ConfigureAwait(false);
        await GateAsync(true, _clearPending, cancellation).ConfigureAwait(false);
    }
    private void RequestDeviceVolume(int target)
    {
        var write = _session.SetDeviceVolumeAsync(_binding.ReceiverId, target);
        _restoreSequence = _session.Snapshot.DeviceVolume!.Sequence; _restoreTarget = target;
        _log?.Invoke($"Native receiver volume requested: target={target}; sequence={_restoreSequence}");
        _ = write.ContinueWith(task => { if (task.IsFaulted) _events.Writer.TryComplete(task.Exception!.GetBaseException()); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private async Task ApplyDeviceVolumeAsync(CancellationToken cancellation)
    {
        var volume = _volume!;
        var remote = _stream!.DeviceVolume;
        if (remote?.Available != true || remote.Actual is not { } actual)
        {
            await GainAsync(0).ConfigureAwait(false); await GateAsync(false, false, cancellation).ConfigureAwait(false); return;
        }
        if (_remoteActual != actual && actual == 0) _zeroRevision = _remoteActual is null ? _connectRevision : _remoteObservedRevision;
        _remoteActual = actual;
        if (_restoreSequence is null && actual == 0 && !volume.Muted && volume.Scalar > 0
            && volume.Revision <= _zeroRevision)
        {
            await _monitor.SetMutedAsync(true, OwnVolumeContext, cancellation).ConfigureAwait(false);
            AcceptVolume(_monitor.Snapshot.Volume); volume = _volume!;
        }
        var mapped = PcmVolumeMath.DeviceControl(_binding.Calibration!, volume);
        if (mapped.Volume != 0) _devicePcmGain = mapped.Gain;
        var newer = volume.Revision > _deviceRevision;
        if (_restoreSequence is { } sequence)
        {
            if (newer && mapped.Volume != _restoreTarget)
            {
                _deviceRevision = volume.Revision; RequestDeviceVolume(mapped.Volume); return;
            }
            if (newer) _deviceRevision = volume.Revision;
            if (remote.Sequence != sequence || remote.Target is not null || remote.Status == "pending") return;
            if (remote.Status != "confirmed" || actual != _restoreTarget)
            {
                await GainAsync(0).ConfigureAwait(false); await GateAsync(false, true, cancellation).ConfigureAwait(false);
                throw new IOException("HomePod did not confirm the requested device volume.");
            }
            _restoreSequence = null; _restoreTarget = null;
            newer = false;
        }
        if ((newer || mapped.Volume == 0) && actual != mapped.Volume)
        {
            _deviceRevision = volume.Revision; RequestDeviceVolume(mapped.Volume); return;
        }
        _deviceRevision = volume.Revision;
        await GainAsync(_devicePcmGain).ConfigureAwait(false);
        await GateAsync(true, _clearPending, cancellation).ConfigureAwait(false);
    }
    private async Task GainAsync(double value)
    {
        if (_lastGain == value) return;
        await _session.SetNativeGainAsync(value).ConfigureAwait(false); _lastGain = value;
    }
    private bool IsCurrentStream() => _stream is not null && _streamCaptureEpoch == _host.CaptureEpoch && ReferenceEquals(_stream, _session.Snapshot);
    private async Task<bool> GateAsync(bool open, bool clear, CancellationToken cancellation)
    {
        if (!IsCurrentStream()) return false;
        if (_gateOpen == open && !clear) return true;
        if (!await _host.TrySetCaptureGateAsync(_streamCaptureEpoch, open, clear, cancellation).ConfigureAwait(false)) return false;
        _gateOpen = open; _clearPending = false;
        return true;
    }
    private async Task StopRouteAsync()
    {
        if (!_active && !_begun) return;
        Interlocked.Increment(ref _routeEpoch); _active = false;
        try { await _session.StopAsync().ConfigureAwait(false); await _host.ResetCaptureAsync(default).ConfigureAwait(false); }
        finally
        {
            _begun = false; _gateOpen = false; _receiver = null; _stream = null; _restoreSequence = null; _restoreTarget = null; _remoteActual = null; _lastGain = null; _errorAt = null;
        }
    }
}

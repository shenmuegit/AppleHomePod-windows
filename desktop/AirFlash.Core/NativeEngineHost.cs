using System.Text.Json;
using System.Threading.Channels;
namespace AirFlash.Core;

// Owns one process/reader. Stream leases own only their bounded event queues.
public sealed class NativeEngineHost : IEngineFactory, IAsyncDisposable
{
    private readonly IEngineConnection _connection;
    private readonly object _sync = new();
    private readonly Dictionary<string, Lease> _sessions = [];
    private readonly HashSet<Lease> _leases = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _captureSerial = new(1, 1);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lazy<Task> _close;
    private readonly Task _pump;
    private Task? _dispose;
    private IOException? _failure;
    private bool _disposed, _begun;
    private NativeBinding? _binding;
    private string? _captureId;
    private ulong _epoch;
    private Lease? _transport;
    public int ProcessId { get; }
    public string? CaptureSessionInstanceId { get; private set; }
    public Task Completion => _completion.Task;
    public NativeEngineHost(IEngineConnection connection, int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);
        _connection = connection; ProcessId = processId;
        _close = new(() => _connection.DisposeAsync().AsTask());
        _pump = Task.Run(PumpAsync);
    }
    private void EnsureHealthy()
    {
        lock (_sync)
        {
            if (_failure is not null) throw new IOException(_failure.Message, _failure);
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }
    public IEngineConnection Open()
    {
        lock (_sync) { EnsureHealthy(); var lease = new Lease(this); _leases.Add(lease); return lease; }
    }
    private void Register(Lease lease, string session)
    {
        lock (_sync)
        {
            EnsureHealthy();
            ObjectDisposedException.ThrowIf(lease.Closing, lease);
            if (string.IsNullOrEmpty(session) || session.Length > 128) throw new ArgumentException("Invalid engine session ID.", nameof(session));
            if (lease.Session is not null && lease.Session != session) throw new InvalidOperationException("A lease cannot change session ID.");
            if (_sessions.TryGetValue(session, out var owner) && owner != lease) throw new InvalidOperationException("Session ID already leased.");
            lease.Session = session; _sessions[session] = lease;
        }
    }
    private async Task PumpAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var item = await _connection.ReadAsync(_stop.Token).ConfigureAwait(false) ?? throw new IOException("The armed engine closed stdout.");
                if (item.ValueKind != JsonValueKind.Object || item.Integer("version") != 1 || string.IsNullOrEmpty(item.Text("session_id")) || string.IsNullOrEmpty(item.Text("event")))
                    throw new IOException("Invalid armed-engine event envelope.");
                lock (_sync)
                {
                    if (!_sessions.TryGetValue(item.Text("session_id"), out var lease)) continue;
                    if (item.Text("event") == "stopped" && !item.TryGetProperty("cancelled", out _)) lease.StopAck?.TrySetResult();
                    if (!lease.Closing && !lease.Events.Writer.TryWrite(item)) throw new IOException("Armed-engine lease exceeded 64 pending events.");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
    }
    private void Fail(Exception error)
    {
        lock (_sync)
        {
            if (_disposed || _failure is not null) return;
            _failure = error as IOException ?? new IOException("Armed-engine protocol failed.", error);
            foreach (var lease in _leases) { lease.Events.Writer.TryComplete(_failure); lease.StopAck?.TrySetException(_failure); }
            _completion.TrySetException(_failure);
        }
        _stop.Cancel();
        _ = CloseAfterFailureAsync();
    }
    private async Task CloseAfterFailureAsync()
    {
        try { await _close.Value.ConfigureAwait(false); }
        catch (Exception) { /* The original fatal protocol fault remains the completion reason. */ }
    }
    public async Task ArmAsync(NativeBinding binding, CancellationToken cancellation)
    {
        if (NativeBindingPolicy.ValidateStored(binding) is { } invalid) throw new InvalidOperationException(invalid);
        if (binding.Calibration is null) throw new InvalidOperationException("Native output is not calibrated.");
        await _captureSerial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            EnsureHealthy();
            if (_binding is not null) throw new InvalidOperationException("Capture is already armed.");
            var id = Guid.NewGuid().ToString("D");
            var ready = await ControlAsync("arm_capture", new { capture_id = id, endpoint_id = binding.Endpoint.EndpointId, sample_rate = 44100 }, "capture_ready", TimeSpan.FromSeconds(3), cancellation).ConfigureAwait(false);
            if (ready.Text("capture_id") != id || ready.Integer("epoch") != 1 || string.IsNullOrEmpty(ready.Text("session_instance_id")))
                throw new IOException("Invalid capture-ready identity.");
            _binding = binding; _captureId = id; _epoch = 1; CaptureSessionInstanceId = ready.Text("session_instance_id");
        }
        catch (IOException error) { Fail(error); throw; }
        finally { _captureSerial.Release(); }
    }
    public async Task BeginCaptureAsync(CancellationToken cancellation)
    {
        await _captureSerial.WaitAsync(cancellation).ConfigureAwait(false);
        try { await BeginLockedAsync(cancellation).ConfigureAwait(false); }
        finally { _captureSerial.Release(); }
    }
    private async Task BeginLockedAsync(CancellationToken cancellation)
    {
        EnsureHealthy();
        if (_binding is null) throw new InvalidOperationException("Capture is not armed.");
        if (_begun) return;
        var begun = await ControlAsync("begin_capture", new { capture_id = _captureId, epoch = _epoch }, "capture_begun", TimeSpan.FromSeconds(2), cancellation).ConfigureAwait(false);
        ValidateCaptureAck(begun, _epoch); _begun = true;
    }
    public async Task SetCaptureGateAsync(bool open, bool clearPending, CancellationToken cancellation)
    {
        await _captureSerial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            EnsureHealthy();
            if (!_begun) throw new InvalidOperationException("Capture has not begun.");
            var ack = await ControlAsync("set_capture_gate", new { capture_id = _captureId, epoch = _epoch, open, clear_pending = clearPending }, "capture_gate", TimeSpan.FromSeconds(2), cancellation).ConfigureAwait(false);
            ValidateCaptureAck(ack, _epoch);
            if (ack.Boolean("open") != open) throw new IOException("Capture gate acknowledgment mismatch.");
        }
        catch (IOException error) { Fail(error); throw; }
        finally { _captureSerial.Release(); }
    }
    private void ValidateCaptureAck(JsonElement ack, ulong epoch)
    {
        if (ack.Text("capture_id") != _captureId || !ack.TryGetProperty("epoch", out var value) || !value.TryGetUInt64(out var actual) || actual != epoch)
        {
            var error = new IOException("Capture acknowledgment has a stale identity."); Fail(error); throw error;
        }
    }
    private async Task<JsonElement> ControlAsync(string command, object parameters, string expected, TimeSpan limit, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _stop.Token);
        timeout.CancelAfter(limit);
        await using var lease = Open();
        try
        {
            await lease.SendAsync(Guid.NewGuid().ToString("N"), command, parameters, timeout.Token).ConfigureAwait(false);
            var ack = await lease.ReadAsync(timeout.Token).ConfigureAwait(false) ?? throw new IOException("Capture control ended without acknowledgment.");
            if (ack.Text("event") != expected) throw new IOException(ack.Text("message", "Unexpected capture control response."));
            return ack;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            var failure = new IOException($"Capture command {command} did not complete safely.", error); Fail(failure); throw failure;
        }
    }
    private async Task StartAsync(Lease lease, object? parameters, CancellationToken cancellation)
    {
        var data = JsonSerializer.SerializeToNode(parameters) as System.Text.Json.Nodes.JsonObject ?? throw new InvalidOperationException("Start parameters required.");
        await _captureSerial.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            EnsureHealthy();
            if (_binding is null || data["capture_endpoint"]?.GetValue<string>() != _binding.Endpoint.EndpointId || data["sample_rate"]?.GetValue<int>() != 44100)
                throw new InvalidOperationException("Start must use the armed endpoint and 44100 Hz.");
            if (data.ContainsKey("capture_id") || data.ContainsKey("capture_epoch") || data.ContainsKey("await_volume_gate")) throw new InvalidOperationException("Capture keys are owned by the host.");
            if (_transport is not null) throw new InvalidOperationException("Stop the previous transport before starting another.");
            await BeginLockedAsync(cancellation).ConfigureAwait(false);
            data["capture_id"] = _captureId; data["capture_epoch"] = _epoch; data["await_volume_gate"] = true;
            _transport = lease; lease.Started = true;
            await WriteAsync(lease.Session!, "start", data, cancellation).ConfigureAwait(false);
        }
        finally { _captureSerial.Release(); }
    }
    private async Task WriteAsync(string session, string command, object? parameters, CancellationToken cancellation)
    {
        EnsureHealthy();
        try { await _connection.SendAsync(session, command, parameters, _stop.Token).WaitAsync(cancellation).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { Fail(error); throw new IOException("Armed-engine write failed.", error); }
    }
    private Task StopLeaseAsync(Lease lease)
    {
        lock (_sync)
        {
            EnsureHealthy();
            lease.StopAck ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return lease.StopTask ??= StopCoreAsync(lease);
        }
    }
    private async Task StopCoreAsync(Lease lease)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await WriteAsync(lease.Session!, "stop", null, timeout.Token).ConfigureAwait(false);
            await lease.StopAck!.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            var failure = new IOException("Transport stop was not acknowledged; armed process cannot be reused.", error); Fail(failure); throw failure;
        }
    }
    private async Task ReleaseAsync(Lease lease)
    {
        try
        {
            if (lease.Started)
            {
                await StopLeaseAsync(lease).ConfigureAwait(false);
                await _captureSerial.WaitAsync().ConfigureAwait(false);
                try
                {
                    var next = checked(_epoch + 1);
                    var ack = await ControlAsync("reset_capture", new { capture_id = _captureId, epoch = _epoch }, "capture_reset", TimeSpan.FromSeconds(2), default).ConfigureAwait(false);
                    ValidateCaptureAck(ack, next); _epoch = next; _begun = false; _transport = null;
                }
                finally { _captureSerial.Release(); }
            }
        }
        finally
        {
            lock (_sync) { _leases.Remove(lease); if (lease.Session is { } id) _sessions.Remove(id); lease.Events.Writer.TryComplete(_failure); }
        }
    }
    private sealed class Lease(NativeEngineHost host) : IEngineConnection
    {
        internal readonly Channel<JsonElement> Events = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = true });
        internal string? Session;
        internal bool Started, Closing;
        internal TaskCompletionSource? StopAck;
        internal Task? StopTask;
        private Task? _dispose;
        public async Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            host.Register(this, session);
            if (command == "start") await host.StartAsync(this, parameters, cancellation).ConfigureAwait(false);
            else if (command == "stop" && Started) await host.StopLeaseAsync(this).WaitAsync(cancellation).ConfigureAwait(false);
            else await host.WriteAsync(session, command, parameters, cancellation).ConfigureAwait(false);
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            try { return await Events.Reader.ReadAsync(cancellation).ConfigureAwait(false); }
            catch (ChannelClosedException error) { if (error.InnerException is not null) throw new IOException("Armed engine lease failed.", error.InnerException); return null; }
        }
        public ValueTask DisposeAsync()
        {
            lock (host._sync) { Closing = true; return new(_dispose ??= host.ReleaseAsync(this)); }
        }
    }
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposed = true;
            return new(_dispose ??= DisposeCoreAsync());
        }
    }
    private async Task DisposeCoreAsync()
    {
        _stop.Cancel();
        lock (_sync) foreach (var lease in _leases) lease.Events.Writer.TryComplete(_failure);
        try { await _close.Value.ConfigureAwait(false); await _pump.ConfigureAwait(false); }
        finally { _completion.TrySetResult(); }
    }
}

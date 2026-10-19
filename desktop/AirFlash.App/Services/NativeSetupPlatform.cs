using AirFlash.App.Verification;
using AirFlash.Core;
namespace AirFlash.App.Services;

internal sealed class NativeSetupPlatform : INativeSetupPlatform
{
    private readonly WindowsNativeBinding _windows = new();
    private readonly NativeBindingStore _store = new(WindowsNativeBinding.BindingPath);
    private SingleInstance? _legacyGuard;
    private WindowsDiscovery? _discovery;
    private IReadOnlyList<Receiver> _receivers = [];
    private IDisposable? _maintenance;
    private WindowsNativeOutput? _monitor;
    private NativeEngineHost? _host;
    private SessionController? _session;
    private CancellationTokenSource? _previewLifetime;
    private Task? _previewWork;
    private NativeBinding? _baseline, _provisional;
    private Receiver? _previewReceiver;
    private int _adaptations;
    private bool _resumeAgent, _usingExistingAgent, _completed, _cleanupFailed, _newBinding;

    public async Task<NativeSetupInspection> InspectAsync(CancellationToken cancellation)
    {
        var snapshot = await _windows.PreflightAsync(cancellation).ConfigureAwait(false);
        var binding = _store.Load();
        var task = await NativeAutostart.InspectAsync(NativeAutostart.InstalledExecutable, cancellation).ConfigureAwait(false);
        var ready = await NativeAutostart.QueryReadyAsync(cancellation).ConfigureAwait(false);
        var installed = snapshot.CableAdapterIds.Length > 0;
        var qualified = snapshot.Endpoints.Where(e => NativeBindingPolicy.CanClaim(e, new HashSet<string>())).ToArray();
        var endpoint = qualified.Length == 1 ? qualified[0] : null;
        var rebooted = File.Exists(WindowsNativeBinding.PreflightPath) && WindowsNativeBinding.RebootedSincePreinstall();
        var legacyRunning = false;
        if (_legacyGuard is null)
        {
            await using var legacy = new SingleInstance();
            legacyRunning = !legacy.IsOwner;
        }
        string? blocked = null;
        if (legacyRunning)
            blocked = "Close the legacy AppleHome panel before setup so local calibration cannot be forwarded to HomePod.";
        else if (task.Exists && !task.Owned || ready && !task.Owned)
            blocked = "The existing AppleHome startup task or running agent has no matching ownership proof; it was preserved.";
        else if (installed && !File.Exists(WindowsNativeBinding.PreflightPath))
            blocked = "VB-CABLE was already installed before this setup. Its output cannot be adopted without the original pre-install proof.";
        else if (installed && (snapshot.CableAdapterIds.Length != 1 || qualified.Length != 1))
            blocked = "The ordinary VB-CABLE playback output is missing, unverified or ambiguous. Existing drivers were preserved.";
        else if (binding is not null && (endpoint is null || endpoint.EndpointId != binding.Endpoint.EndpointId
            || endpoint.AdapterInstanceId != binding.Endpoint.AdapterInstanceId || endpoint.MixFormat != binding.Endpoint.MixFormat
            || endpoint.DriverSha256 != binding.Endpoint.DriverSha256))
            blocked = "The bound output or its driver format changed. Existing device settings were preserved.";
        if (blocked is null && binding?.Calibration is not null && rebooted)
            _ = await NativeAutostart.ReadQualifiedBindingAsync(cancellation).ConfigureAwait(false);
        return new(installed, rebooted, endpoint, binding, ready, blocked);
    }

    public async Task<IReadOnlyList<Receiver>> DiscoverAsync(CancellationToken cancellation)
    {
        if (_previewWork is not null || _usingExistingAgent) throw new InvalidOperationException("Stop the music preview before refreshing devices.");
        _discovery?.Dispose();
        var discovery = _discovery = new WindowsDiscovery(log: AppPaths.Log);
        var found = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _receivers, []);
        discovery.Changed += values => {
            if (!ReferenceEquals(_discovery, discovery) || !discovery.IsCurrentSnapshot(values)) return;
            var current = ReceiverCatalog.Reconcile(values, new()).Receivers
                .Where(r => r.Online && ReceiverIdentity.IsBroadcast(r.Id)).ToArray();
            Volatile.Write(ref _receivers, current);
            if (current.Any(r => r.PrefersNtpAlac)) found.TrySetResult();
        };
        discovery.Failed += message => found.TrySetException(new IOException(message));
        discovery.Start();
        try { await found.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellation).ConfigureAwait(false); }
        catch (TimeoutException) { /* Return the bounded fresh snapshot so the user can refresh discovery. */ }
        return Volatile.Read(ref _receivers);
    }

    public async Task StartPreviewAsync(Receiver receiver, CancellationToken cancellation)
    {
        if (_cleanupFailed) throw new IOException("Preview resources were not released safely. Check the AppleHome log before starting another preview.");
        if (_baseline is not null || _completed) throw new InvalidOperationException("Stop the previous music preview before starting another.");
        var current = Volatile.Read(ref _receivers).SingleOrDefault(r => ReceiverIdentity.Normalize(r.Id) == ReceiverIdentity.Normalize(receiver.Id))
            ?? throw new InvalidOperationException("The selected HomePod is no longer in the current discovery snapshot. Refresh devices and retry.");
        var inspection = await InspectAsync(cancellation).ConfigureAwait(false);
        if (inspection.BlockedReason is { } blocked) throw new InvalidOperationException(blocked);
        if (!inspection.DriverInstalled || !inspection.RebootVerified || inspection.Endpoint is null)
            throw new InvalidOperationException("Finish the independent driver installation, required reboot and Windows output naming before music preview.");
        if (inspection.Binding is { } existing) NativeSetupReferenceProbe.ValidateReceiver(existing, current);
        else if (!current.PrefersNtpAlac || !current.Online)
            throw new InvalidOperationException("This HomePod model, firmware or group has not been qualified for native output.");
        try
        {
            await AcquireLegacyGuardAsync().ConfigureAwait(false);
            _previewReceiver = current; _adaptations = 0;
            if (inspection.Binding is { Calibration: not null } ready && inspection.AgentReady
                && NativeLatencyAdaptation.VerifiedPreference(ready, current, 44100) is not null)
            {
                _baseline = _provisional = ready; _usingExistingAgent = true;
                return;
            }
            await StopOwnedAgentAsync(cancellation).ConfigureAwait(false);
            _maintenance = NativeVolumeCalibration.AcquireMaintenance();
            _baseline = inspection.Binding ?? await _windows.BindAsync(current.Id, inspection.Endpoint.EndpointId,
                NativeSetupFlow.OutputName(current), cancellation).ConfigureAwait(false);
            _newBinding = inspection.Binding is null;
            _provisional = _baseline;
            if (_baseline.Calibration is null)
            {
                var proposal = await NativeVolumeCalibration.MeasureForSetupAsync(_baseline, current, cancellation).ConfigureAwait(false);
                _provisional = _baseline with { Calibration = proposal.Calibration };
            }
            await OpenPreviewAsync(current, null, cancellation).ConfigureAwait(false);
        }
        catch
        {
            await StopPreviewAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> TryAdaptMusicAsync(CancellationToken cancellation)
    {
        if (_usingExistingAgent || _baseline is not { } baseline || _provisional is not { } provisional
            || _session is null || _previewReceiver is null) return false;
        var current = CurrentPreviewReceiver();
        var next = NativeMusicConfirmation.RecoveryCandidateMs(provisional, current, _session.Snapshot, _adaptations, 44100);
        if (next is null) return false;
        if (_store.Load() != baseline) throw new IOException("Device settings changed during music playback; their current contents were preserved.");
        _adaptations++;
        try
        {
            await ClosePreviewResourcesAsync().ConfigureAwait(false);
            await AcquireLegacyGuardAsync().ConfigureAwait(false);
            _maintenance = NativeVolumeCalibration.AcquireMaintenance();
            if (_store.Load() != baseline) throw new IOException("Device settings changed while reconnecting; their current contents were preserved.");
            var endpoints = await _windows.SnapshotAsync(cancellation).ConfigureAwait(false);
            var live = endpoints.SingleOrDefault(endpoint => endpoint.EndpointId == baseline.Endpoint.EndpointId);
            if (live is null || NativeBindingPolicy.ValidateLive(provisional, live) is { })
                throw new IOException("The Windows output changed while reconnecting. Recheck the output before continuing.");
            await OpenPreviewAsync(CurrentPreviewReceiver(), next, cancellation).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await StopPreviewAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task CompleteAsync(CancellationToken cancellation)
    {
        if (_completed) return;
        var baseline = _baseline ?? throw new InvalidOperationException("Preview normal music before completing setup.");
        var provisional = _provisional ?? throw new InvalidOperationException("The measured calibration is unavailable.");
        var executable = Environment.ProcessPath ?? throw new IOException("The published setup executable is unavailable.");
        if (File.Exists(Path.ChangeExtension(executable, ".dll")))
            throw new InvalidOperationException("Complete setup from the published application, not a development apphost.");
        if (_usingExistingAgent)
        {
            if (!await NativeAutostart.QueryReadyAsync(cancellation).ConfigureAwait(false))
                throw new IOException("The existing native output agent is no longer ready. Retry the normal music preview.");
        }
        else
        {
            if (_previewWork?.IsCompleted == true) await _previewWork.ConfigureAwait(false);
            var current = CurrentPreviewReceiver();
            var snapshot = _session?.Snapshot ?? throw new NativeSetupPendingException("请在 Windows 选择 HomePod 输出并持续播放正常音乐，再确认听感。");
            if (NativeLatencyAdaptation.VerifiedPreference(provisional, current, 44100) is null)
            {
                int applied;
                try { applied = NativeMusicConfirmation.ConfirmableAppliedMs(provisional, current, snapshot, 44100); }
                catch (NativeSetupPendingException)
                {
                    var session = _session; var host = _host;
                    if (session is null || host is null) throw;
                    var epoch = host.CaptureEpoch;
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false);
                    if (!ReferenceEquals(session, _session) || !ReferenceEquals(host, _host) || epoch != host.CaptureEpoch)
                        throw new NativeSetupPendingException("音乐连接在确认期间重新准备，请继续播放正常音乐片刻后再完成。");
                    current = CurrentPreviewReceiver();
                    applied = NativeMusicConfirmation.ConfirmableAppliedMs(provisional, current, session.Snapshot, 44100, snapshot);
                }
                provisional = NativeLatencyAdaptation.ConfirmReference(provisional, current, 44100, applied);
            }
            else if (snapshot.State != PlaybackState.Streaming)
                throw new NativeSetupPendingException("请在 Windows 选择 HomePod 输出并持续播放正常音乐，再确认听感。");
        }
        await ClosePreviewResourcesAsync().ConfigureAwait(false);
        if (_store.Load() != baseline) throw new IOException("Device settings changed during the music preview; their current contents were preserved.");
        if (provisional != baseline)
        {
            await _store.SaveAsync(provisional, cancellation).ConfigureAwait(false);
            _baseline = provisional;
        }
        try
        {
            await NativeAutostart.InstallForSetupAsync(executable, cancellation).ConfigureAwait(false);
            _completed = true; _resumeAgent = false; _usingExistingAgent = false; _newBinding = false;
        }
        catch
        {
            await ResumeAgentAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopPreviewAsync(CancellationToken cancellation)
    {
        await ClosePreviewResourcesAsync(discardUnconfirmed: true).ConfigureAwait(false);
        _baseline = _provisional = null; _usingExistingAgent = false; _newBinding = false;
        _previewReceiver = null; _adaptations = 0;
        await ResumeAgentAsync().ConfigureAwait(false);
    }

    private Receiver CurrentPreviewReceiver()
    {
        var previous = _previewReceiver ?? throw new InvalidOperationException("Start normal music preview before confirming or reconnecting.");
        var current = Volatile.Read(ref _receivers).SingleOrDefault(receiver => ReceiverIdentity.Normalize(receiver.Id) == ReceiverIdentity.Normalize(previous.Id));
        if (current is null || NativeLatencyAdaptation.ProfileKey(current, 44100) != NativeLatencyAdaptation.ProfileKey(previous, 44100))
            throw new InvalidOperationException("The selected HomePod or its profile changed. Refresh devices and reconnect.");
        NativeSetupReferenceProbe.ValidateReceiver(_provisional!, current);
        return current;
    }

    private async Task AcquireLegacyGuardAsync()
    {
        _legacyGuard = new SingleInstance();
        if (_legacyGuard.IsOwner) return;
        await _legacyGuard.DisposeAsync().ConfigureAwait(false); _legacyGuard = null;
        throw new InvalidOperationException("Close the legacy AppleHome panel before setup so local calibration cannot be forwarded to HomePod.");
    }

    private async Task OpenPreviewAsync(Receiver receiver, int? recoveryCandidateMs, CancellationToken cancellation)
    {
        var provisional = _provisional ?? throw new InvalidOperationException("The measured calibration is unavailable.");
        _monitor = new WindowsNativeOutput(provisional);
        await _monitor.GetDefaultEndpointIdAsync().WaitAsync(cancellation).ConfigureAwait(false);
        if (!_monitor.Snapshot.BindingValid) throw new InvalidDataException("The Windows output changed before the music preview.");
        var process = new ProcessEngineConnection(AppPaths.ExtractEngine(), AppPaths.Log);
        _host = new NativeEngineHost(process, process.ProcessId, recoveryCandidateMs);
        var hello = await EngineHandshake.HelloAsync(_host, cancellation, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        if (!hello.TryGetProperty("commands", out var commands) || !commands.EnumerateArray().Any(command => command.GetString() == "arm_capture"))
            throw new IOException("The packaged engine does not support armed capture.");
        await _host.ArmAsync(provisional, cancellation).ConfigureAwait(false);
        _session = new SessionController(_host, _monitor, AppPaths.Log);
        _previewReceiver = receiver;
        _previewLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _previewWork = new NativeOutputController(provisional, _monitor, _host, _session, log: AppPaths.Log)
            .RunAsync(id => Volatile.Read(ref _receivers).SingleOrDefault(value => ReceiverIdentity.Normalize(value.Id) == id), _previewLifetime.Token);
    }

    private async Task StopOwnedAgentAsync(CancellationToken cancellation)
    {
        var task = await NativeAutostart.InspectAsync(NativeAutostart.InstalledExecutable, cancellation).ConfigureAwait(false);
        await using (var instance = new SingleInstance("Native"))
        {
            if (instance.IsOwner) return;
            if (!task.Owned) throw new InvalidOperationException("The running native agent has no matching ownership proof; it was preserved.");
            _resumeAgent = true;
            await instance.NotifyExistingAsync(2).WaitAsync(cancellation).ConfigureAwait(false);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            await using (var instance = new SingleInstance("Native"))
                if (instance.IsOwner) return;
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task ResumeAgentAsync()
    {
        if (!_resumeAgent || _completed) return;
        if (_cleanupFailed) throw new IOException("Preview resources were not released safely. Check the AppleHome log before restarting native output.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await NativeAutostart.InstallForSetupAsync(NativeAutostart.InstalledExecutable, timeout.Token).ConfigureAwait(false);
        _resumeAgent = false;
    }

    private async Task ClosePreviewResourcesAsync(bool discardUnconfirmed = false)
    {
        var errors = new List<Exception>();
        if (_previewLifetime is not null) await _previewLifetime.CancelAsync().ConfigureAwait(false);
        if (_previewWork is not null)
        {
            try { await _previewWork.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_previewLifetime?.IsCancellationRequested == true) { }
            catch (Exception error) { AppPaths.Log("Native setup music preview stopped: " + error); }
        }
        if (_session is not null) await Cleanup(async () => await _session.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        if (_host is not null) await Cleanup(async () => await _host.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        if (_monitor is not null) await Cleanup(async () => await _monitor.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        _previewLifetime?.Dispose(); _previewLifetime = null; _previewWork = null; _session = null; _host = null; _monitor = null;
        if (errors.Count == 0 && discardUnconfirmed && _newBinding && _baseline is { Calibration: null } temporary)
        {
            try { _store.DiscardUnconfirmed(temporary); }
            catch (Exception error) { errors.Add(error); }
        }
        _maintenance?.Dispose(); _maintenance = null;
        _cleanupFailed |= errors.Count > 0;
        if (!_cleanupFailed && _legacyGuard is not null)
        {
            await Cleanup(async () => await _legacyGuard.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
            _cleanupFailed |= errors.Count > 0;
            if (!_cleanupFailed) _legacyGuard = null;
        }
        if (_cleanupFailed) throw new AggregateException("The music preview did not stop cleanly; startup was not resumed.", errors);
        async Task Cleanup(Func<Task> action) { try { await action().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); } }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopPreviewAsync(CancellationToken.None).ConfigureAwait(false); }
        finally { _discovery?.Dispose(); _discovery = null; }
    }
}

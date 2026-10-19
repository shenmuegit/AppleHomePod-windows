using AirFlash.App.Verification;
using AirFlash.Core;
using System.Diagnostics;
using System.Text.Json;
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
    private NativeSetupAudioPlayer? _confirmation;
    private NativeSetupInspection? _previewInspection;
    private long _previewGeneration;
    private byte[]? _previewReceipt;
    private NativeEngineHost? _host;
    private SessionController? _session;
    private CancellationTokenSource? _previewLifetime;
    private Task? _previewWork;
    private NativeBinding? _baseline, _provisional;
    private Receiver? _previewReceiver;
    private NativeSetupOutput? _outputSetup;
    private byte[]? _outputJournal;
    private bool _recoveredOutput;
    private sealed record OutputJournal(int Version, EndpointIdentity Endpoint, NativeSetupOutputState State);
    private static string OutputJournalPath => Path.Combine(AppPaths.DataDirectory, "native-setup-output.json");
    private int _adaptations;
    private bool _resumeAgent, _usingExistingAgent, _completed, _cleanupFailed, _newBinding;

    public async Task<NativeDriverInstallOutcome> InstallDriverAsync(CancellationToken cancellation)
    {
        var inspection = await InspectAsync(cancellation).ConfigureAwait(false);
        if (inspection.DriverInstalled) return NativeDriverInstallOutcome.VerificationRequired;
        if (inspection.BlockedReason is { } blocked) throw new InvalidOperationException(blocked);
        var result = await NativeDriverInstaller.InstallAsync(cancellation).ConfigureAwait(false);
        for (var attempt = 0; result != NativeDriverInstallOutcome.RestartRequired && attempt < 20; attempt++)
        {
            if ((await _windows.PreflightAsync(cancellation).ConfigureAwait(false)).CableAdapterIds.Length > 0) break;
            await Task.Delay(250, cancellation).ConfigureAwait(false);
        }
        return result;
    }

    public async Task PrepareOutputAsync(Receiver receiver, CancellationToken cancellation)
    {
        var inspection = await InspectAsync(cancellation).ConfigureAwait(false);
        if (inspection.BlockedReason is { } blocked) throw new InvalidOperationException(blocked);
        var endpoint = inspection.Endpoint ?? throw new IOException("The verified playback endpoint is unavailable.");
        if (!inspection.DriverInstalled || !inspection.RebootVerified) throw new IOException("Finish the driver's required Windows restart before connecting.");
        if (_outputSetup is not null) throw new InvalidOperationException("Finish the previous setup attempt first.");
        _outputSetup = new NativeSetupOutput(new WindowsNativeSetupOutput(endpoint), endpoint.EndpointId, endpoint.PinName,
            state => SaveOutputJournal(endpoint, state));
        await WindowsNativeBinding.OnMtaAsync(() => { _outputSetup.PrepareName(NativeSetupFlow.OutputName(receiver)); return true; }, cancellation).ConfigureAwait(false);
    }

    public async Task<NativeSetupInspection> InspectAsync(CancellationToken cancellation)
    {
        if (!_recoveredOutput && _outputSetup is null)
        {
            await WindowsNativeBinding.OnMtaAsync(() => { RecoverOutputJournal(); return true; }, cancellation).ConfigureAwait(false);
            _recoveredOutput = true;
        }
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

    private void RecoverOutputJournal()
    {
        if (!File.Exists(OutputJournalPath)) return;
        using (var file = File.OpenRead(OutputJournalPath))
        {
            if (file.Length > 65536) throw new InvalidDataException("The interrupted setup record exceeds its size limit.");
            _outputJournal = new byte[file.Length]; file.ReadExactly(_outputJournal);
        }
        var saved = JsonSerializer.Deserialize<OutputJournal>(_outputJournal,
            new JsonSerializerOptions(AppSettings.JsonOptions) { RespectRequiredConstructorParameters = true })
            ?? throw new InvalidDataException("The interrupted setup record is invalid; it was preserved.");
        if (saved.Version != 1 || saved.Endpoint is null || saved.State is null
            || !NativeBindingPolicy.CanClaim(saved.Endpoint, new HashSet<string>())
            || saved.State.EndpointId != saved.Endpoint.EndpointId || saved.State.PinName != saved.Endpoint.PinName)
            throw new InvalidDataException("The interrupted setup record does not describe a verified output; it was preserved.");
        NativeSetupOutput.Recover(new WindowsNativeSetupOutput(saved.Endpoint), saved.State, state => SaveOutputJournal(saved.Endpoint, state));
    }

    private void SaveOutputJournal(EndpointIdentity endpoint, NativeSetupOutputState? state)
    {
        var current = File.Exists(OutputJournalPath) ? File.ReadAllBytes(OutputJournalPath) : null;
        if ((current is null) != (_outputJournal is null) || current is not null && !_outputJournal!.SequenceEqual(current))
            throw new IOException("The setup recovery record was changed externally; its current contents were preserved.");
        if (state is null)
        {
            if (current is not null) File.Delete(OutputJournalPath);
            _outputJournal = null; return;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new OutputJournal(1, endpoint, state), AppSettings.JsonOptions);
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var temporary = OutputJournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            File.Move(temporary, OutputJournalPath, true); _outputJournal = bytes;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<IReadOnlyList<Receiver>> DiscoverAsync(CancellationToken cancellation)
    {
        if (_previewWork is not null || _usingExistingAgent) throw new InvalidOperationException("Stop the music preview before refreshing devices.");
        _discovery?.Dispose();
        var discovery = _discovery = new WindowsDiscovery(log: AppPaths.Log);
        var found = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundId = _store.Load()?.ReceiverId;
        var browse = Stopwatch.StartNew();
        string? discoveryError = null;
        Volatile.Write(ref _receivers, []);
        discovery.Changed += values => {
            if (!ReferenceEquals(_discovery, discovery) || !discovery.IsCurrentSnapshot(values)) return;
            var current = ReceiverCatalog.Reconcile(values, new()).Receivers
                .Where(r => r.Online && ReceiverIdentity.IsBroadcast(r.Id)).ToArray();
            Volatile.Write(ref _receivers, current);
            if (current.Any(r => r.PrefersNtpAlac && (boundId is null || ReceiverIdentity.Normalize(r.Id) == boundId))) found.TrySetResult();
        };
        discovery.Failed += message => { Volatile.Write(ref discoveryError, message); found.TrySetException(new IOException(message)); };
        discovery.Start();
        try { await found.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellation).ConfigureAwait(false); }
        catch (TimeoutException) { /* Return the bounded fresh snapshot so the user can refresh discovery. */ }
        // An unbound first result is not evidence of uniqueness: allow other mDNS resolvers the full browse window.
        if (boundId is null && browse.Elapsed < TimeSpan.FromSeconds(8))
            await Task.Delay(TimeSpan.FromSeconds(8) - browse.Elapsed, cancellation).ConfigureAwait(false);
        if (Volatile.Read(ref discoveryError) is { } error) throw new IOException(error);
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
            throw new InvalidOperationException("Finish the verified driver installation and its required restart before connecting.");
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
                _monitor = new WindowsNativeOutput(ready);
                await _monitor.GetDefaultEndpointIdAsync().WaitAsync(cancellation).ConfigureAwait(false);
                _previewReceipt = ReadStartupReceipt();
            }
            else
            {
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
            if (!_monitor!.Snapshot.BindingValid) throw new InvalidDataException("The Windows output changed before audio verification.");
            _previewGeneration = _monitor.Snapshot.Generation;
            _previewInspection = inspection with { Binding = _baseline };
        }
        catch
        {
            await StopPreviewAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<NativeSetupInspection> InspectPreviewAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var inspection = _previewInspection ?? throw new InvalidOperationException("Start audio verification before replaying it.");
        var monitor = _monitor ?? throw new IOException("The native output monitor is unavailable.");
        var snapshot = monitor.Snapshot;
        if (!monitor.IsBindingCurrent || snapshot.Generation != _previewGeneration)
            throw new IOException("The monitored output, driver or mix format changed. Reconnect before replaying.");
        if (_store.Load() != _baseline) throw new IOException("Device settings changed during audio verification; their current contents were preserved.");
        _ = CurrentPreviewReceiver();
        if (_usingExistingAgent)
        {
            if (_previewReceipt is null || !ReadStartupReceipt().AsSpan().SequenceEqual(_previewReceipt)
                || !await NativeAutostart.QueryReadyAsync(cancellation).ConfigureAwait(false))
                throw new IOException("The verified native output background is no longer ready. Reconnect before replaying.");
        }
        else if (_previewWork is null || _previewWork.IsCompleted)
        {
            if (_previewWork is not null) await _previewWork.ConfigureAwait(false);
            throw new IOException("The native output preview stopped. Reconnect before replaying.");
        }
        return inspection;
    }

    private static byte[] ReadStartupReceipt()
    {
        using var file = File.OpenRead(Path.Combine(AppPaths.DataDirectory, "native-startup.json"));
        if (file.Length > 65536) throw new InvalidDataException("The native startup ownership proof exceeds its size limit.");
        var bytes = new byte[file.Length]; file.ReadExactly(bytes); return bytes;
    }

    public async Task PrepareConfirmationAsync(CancellationToken cancellation)
    {
        if (_confirmation is not null) return;
        try
        {
            var output = _outputSetup ?? throw new InvalidOperationException("Prepare the Windows playback output before verification.");
            var monitor = _monitor ?? throw new IOException("The native output monitor is unavailable.");
            await WaitForCurrentBindingAsync(monitor, cancellation).ConfigureAwait(false);
            await WindowsNativeBinding.OnMtaAsync(() => { output.SelectForConfirmation(); return true; }, cancellation).ConfigureAwait(false);
            await WaitForCurrentBindingAsync(monitor, cancellation).ConfigureAwait(false);
            var generation = _previewGeneration;
            _confirmation = new NativeSetupAudioPlayer(_provisional ?? throw new InvalidOperationException("The measured calibration is unavailable."),
                () => monitor.IsBindingCurrent && monitor.Snapshot.Generation == generation);
            await _confirmation.Ready.WaitAsync(TimeSpan.FromSeconds(6), cancellation).ConfigureAwait(false);
            if (_usingExistingAgent)
            {
                if (!await NativeAutostart.QueryReadyAsync(cancellation).ConfigureAwait(false)) throw new IOException("The native output background is unavailable.");
                // shortcut: installed-agent readiness omits transport status; retain this first-route settle until its IPC reports it.
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation).ConfigureAwait(false);
            }
            else
            {
                await WaitForPreviewStreamAsync(cancellation).ConfigureAwait(false);
                // Let the native controller apply the receiver's volume before the first finite confirmation clip.
                await Task.Delay(500, cancellation).ConfigureAwait(false);
            }
            await WaitForCurrentBindingAsync(monitor, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        { throw new IOException("自动音频准备未在限定时间内就绪，尚未完成安装。请重新检查 HomePod 连接。", error); }
        catch (TimeoutException error)
        { throw new IOException("自动音频准备超时，尚未完成安装。请重新检查 HomePod 连接。", error); }
    }

    public async Task PlayConfirmationAsync(CancellationToken cancellation)
    {
        if (_confirmation is null) throw new InvalidOperationException("Prepare the setup confirmation before playing it.");
        try
        {
            var monitor = _monitor ?? throw new IOException("The native output monitor is unavailable.");
            if (!monitor.IsBindingCurrent || monitor.Snapshot.Generation != _previewGeneration)
                throw new IOException("The monitored output changed before confirmation playback. Reconnect before continuing.");
            _ = CurrentPreviewReceiver();
            if (!_usingExistingAgent) await WaitForPreviewStreamAsync(cancellation).ConfigureAwait(false);
            await _confirmation.PlayAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(6), cancellation).ConfigureAwait(false);
            AppPaths.Log($"Setup confirmation enqueue_to_provider_ms={_confirmation.LastDispatchDelay?.TotalMilliseconds:F1}; clip_ms=1500; acoustic_latency=unmeasured");
            if (!_usingExistingAgent)
            {
                using var evidence = CancellationTokenSource.CreateLinkedTokenSource(cancellation); evidence.CancelAfter(TimeSpan.FromSeconds(7));
                while (true)
                {
                    try
                    {
                        _ = NativeMusicConfirmation.ConfirmableAppliedMs(_provisional!, CurrentPreviewReceiver(), _session!.Snapshot, 44100);
                        break;
                    }
                    catch (NativeSetupPendingException) { await Task.Delay(100, evidence.Token).ConfigureAwait(false); }
                }
            }
        }
        catch (OperationCanceledException error) when (!cancellation.IsCancellationRequested)
        { throw new IOException("自动音频验证未在限定时间内就绪，尚未完成安装。请重新检查 HomePod 连接。", error); }
        catch (TimeoutException error)
        { throw new IOException("自动音频验证超时，尚未完成安装。请重新检查 HomePod 连接。", error); }
    }

    private async Task WaitForCurrentBindingAsync(WindowsNativeOutput monitor, CancellationToken cancellation)
    {
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellation); ready.CancelAfter(TimeSpan.FromSeconds(6));
        while (!monitor.IsBindingCurrent)
        {
            if (!monitor.Snapshot.BindingValid || monitor.Snapshot.Generation != _previewGeneration)
                throw new IOException("The Windows output changed during audio preparation. Reconnect before continuing.");
            await Task.Delay(25, ready.Token).ConfigureAwait(false);
        }
        if (monitor.Snapshot.Generation != _previewGeneration)
            throw new IOException("The Windows output changed during audio preparation. Reconnect before continuing.");
    }

    private async Task WaitForPreviewStreamAsync(CancellationToken cancellation)
    {
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(cancellation); ready.CancelAfter(TimeSpan.FromSeconds(12));
        while (_session?.Snapshot.State != PlaybackState.Streaming)
        {
            if (_previewWork?.IsCompleted == true) { await _previewWork.ConfigureAwait(false); throw new IOException("The native output preview stopped."); }
            if (_session?.Snapshot.State == PlaybackState.Error) throw new IOException(_session.Snapshot.Message);
            await Task.Delay(50, ready.Token).ConfigureAwait(false);
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
            _previewGeneration = _monitor!.Snapshot.Generation;
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
            _outputSetup?.Commit(); _outputSetup?.Dispose(); _outputSetup = null;
            _completed = true; _resumeAgent = false; _usingExistingAgent = false; _newBinding = false;
            AppPaths.Log("Automatic setup confirmed audible; native output ready.");
        }
        catch
        {
            await ResumeAgentAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopPreviewAsync(CancellationToken cancellation)
    {
        try { await ClosePreviewResourcesAsync(discardUnconfirmed: true).ConfigureAwait(false); }
        finally
        {
            if (_outputSetup is { } output)
            {
                _outputSetup = null;
                try { await WindowsNativeBinding.OnMtaAsync(() => { output.Dispose(); return true; }, CancellationToken.None).ConfigureAwait(false); }
                finally { _recoveredOutput = false; }
            }
        }
        _baseline = _provisional = null; _usingExistingAgent = false; _newBinding = false;
        _previewInspection = null; _previewReceipt = null;
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
        if (_confirmation is not null) await Cleanup(async () => await _confirmation.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        _confirmation = null;
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

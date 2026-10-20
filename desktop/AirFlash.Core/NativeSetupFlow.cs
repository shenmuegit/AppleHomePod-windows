namespace AirFlash.Core;

public enum NativeSetupStep { Checking, Driver, Restart, Receiver, Configuring, Testing, Listening, Ready, Blocked, DiscoveryUnavailable }
public sealed record NativeSetupInspection(bool DriverInstalled, bool RebootVerified, EndpointIdentity? Endpoint,
    NativeBinding? Binding, bool AgentReady, string? BlockedReason = null);
public interface INativeSetupPlatform : IAsyncDisposable
{
    Task<NativeSetupInspection> InspectAsync(CancellationToken cancellation);
    Task<NativeSetupInspection> InspectPreviewAsync(CancellationToken cancellation) => InspectAsync(cancellation);
    Task<IReadOnlyList<Receiver>> DiscoverAsync(CancellationToken cancellation);
    Task<NativeDriverInstallOutcome> InstallDriverAsync(CancellationToken cancellation);
    Task PrepareOutputAsync(Receiver receiver, CancellationToken cancellation);
    Task StartPreviewAsync(Receiver receiver, CancellationToken cancellation);
    Task PrepareConfirmationAsync(CancellationToken cancellation) => Task.CompletedTask;
    Task PlayConfirmationAsync(CancellationToken cancellation);
    Task<bool> TryAdaptMusicAsync(CancellationToken cancellation) => Task.FromResult(false);
    Task CompleteAsync(CancellationToken cancellation);
    Task StopPreviewAsync(CancellationToken cancellation);
}

public sealed class NativeSetupFlow(INativeSetupPlatform platform) : IAsyncDisposable
{
    private readonly SemaphoreSlim _action = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private EndpointIdentity? _previewEndpoint;
    private bool _preview, _disposed, _automatic;
    private NativeSetupStep _step = NativeSetupStep.Checking;
    public event Action? Changed;
    public NativeSetupStep Step { get => _step; private set { _step = value; Changed?.Invoke(); } }
    public NativeSetupInspection? Inspection { get; private set; }
    public IReadOnlyList<Receiver> Receivers { get; private set; } = [];
    public Receiver? SelectedReceiver { get; private set; }
    public string? Error { get; private set; }
    public Task StartAutomaticAsync(CancellationToken cancellation)
    { _automatic = true; return RefreshAsync(cancellation); }
    public Task RefreshAsync(CancellationToken cancellation) => RunAsync(async token => {
        await StopPreviewAsync(token).ConfigureAwait(false);
        Step = NativeSetupStep.Checking;
        var inspection = await platform.InspectAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        if (_automatic && !inspection.DriverInstalled && string.IsNullOrWhiteSpace(inspection.BlockedReason))
        {
            Step = NativeSetupStep.Driver;
            var outcome = await platform.InstallDriverAsync(token).ConfigureAwait(false);
            inspection = await platform.InspectAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            if (outcome == NativeDriverInstallOutcome.RestartRequired) { Inspection = inspection; Step = NativeSetupStep.Restart; return; }
            if (!inspection.DriverInstalled) throw new IOException("驱动安装后未通过 Windows 设备核验。请重新运行安装程序。");
        }
        if (!CheckInspection(inspection)) return;
        if (inspection.Binding is { Calibration: not null, VerifiedBufferMs: not null, VerifiedBufferProfile: null } && inspection.AgentReady)
        { Step = NativeSetupStep.Ready; return; }
        IReadOnlyList<Receiver> receivers;
        try { receivers = await platform.DiscoverAsync(token).ConfigureAwait(false); }
        catch (IOException)
        {
            Receivers = []; Step = NativeSetupStep.DiscoveryUnavailable; return;
        }
        token.ThrowIfCancellationRequested();
        Receivers = receivers.ToArray();
        if (inspection.Binding is { Calibration: not null } binding && inspection.AgentReady)
        {
            var bound = Receivers.SingleOrDefault(receiver => SameReceiver(receiver.Id, binding.ReceiverId));
            if (bound is not null && NativeLatencyAdaptation.VerifiedPreference(binding, bound, 44100) is not null)
            { SelectedReceiver = bound; Step = NativeSetupStep.Ready; return; }
        }
        if (SelectedReceiver is { } previous)
            SelectedReceiver = Receivers.FirstOrDefault(receiver => SameReceiver(receiver.Id, previous.Id));
        Step = NativeSetupStep.Receiver;
        if (_automatic)
        {
            var candidates = Receivers.Where(Qualified).Where(receiver => inspection.Binding is not { } bound
                || SameReceiver(receiver.Id, bound.ReceiverId)).ToArray();
            if (candidates.Length == 1) await ConnectCoreAsync(candidates[0], token).ConfigureAwait(false);
        }
    }, cancellation);

    public Task ConnectAsync(Receiver receiver, CancellationToken cancellation) => RunAsync(token => ConnectCoreAsync(receiver, token), cancellation);
    private async Task ConnectCoreAsync(Receiver receiver, CancellationToken token)
    {
        await StopPreviewAsync(token).ConfigureAwait(false);
        var inspection = await platform.InspectAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        if (!CheckInspection(inspection)) return;
        var selected = Receivers.FirstOrDefault(candidate => SameReceiver(candidate.Id, receiver.Id));
        if (selected is null || !Qualified(selected))
            throw new InvalidOperationException("Select a discovered, online HomePod with a qualified standalone profile.");
        SelectedReceiver = selected;
        if (inspection.Binding is { } binding && !SameReceiver(binding.ReceiverId, selected.Id))
            throw new InvalidOperationException("An existing output is bound to another HomePod. Remove that binding before replacing it.");
        var endpoint = inspection.Endpoint!;
        _preview = true; _previewEndpoint = endpoint;
        Step = NativeSetupStep.Configuring;
        await platform.PrepareOutputAsync(selected, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        await platform.StartPreviewAsync(selected, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        await platform.PrepareConfirmationAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        Step = NativeSetupStep.Testing;
        await platform.PlayConfirmationAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        Step = NativeSetupStep.Listening;
    }

    public Task ReplayConfirmationAsync(CancellationToken cancellation) => RunAsync(async token => {
        if (!await CheckPreviewAsync(token, replay: true).ConfigureAwait(false)) return;
        Step = NativeSetupStep.Testing;
        await platform.PlayConfirmationAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        Step = NativeSetupStep.Listening;
    }, cancellation);

    public Task ConfirmMusicAsync(CancellationToken cancellation) => RunAsync(async token => {
        if (!await CheckPreviewAsync(token).ConfigureAwait(false)) return;
        await platform.CompleteAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        _preview = false; _previewEndpoint = null; Step = NativeSetupStep.Ready;
    }, cancellation);

    private async Task<bool> CheckPreviewAsync(CancellationToken token, bool replay = false)
    {
        if (Step != NativeSetupStep.Listening || !_preview || SelectedReceiver is null) return false;
        var inspection = await (replay ? platform.InspectPreviewAsync(token) : platform.InspectAsync(token)).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        if (!CheckInspection(inspection, publishReceiverStep: false)) { await StopPreviewAsync(token).ConfigureAwait(false); return false; }
        if (!SameEndpoint(inspection.Endpoint!, _previewEndpoint!) || inspection.Binding is { } binding
            && !SameReceiver(binding.ReceiverId, SelectedReceiver.Id))
            throw new InvalidOperationException("The selected output or HomePod changed during audio verification. Reconnect before continuing.");
        return true;
    }

    public Task RejectMusicAsync(CancellationToken cancellation) => RunAsync(async token => {
        if (Step != NativeSetupStep.Listening) return;
        if (await platform.TryAdaptMusicAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested(); Step = NativeSetupStep.Configuring;
            await platform.PrepareConfirmationAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            Step = NativeSetupStep.Testing;
            await platform.PlayConfirmationAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
            Step = NativeSetupStep.Listening; return;
        }
        await StopPreviewAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        Block("自动检查与重试后仍未确认出声，尚未完成安装。请检查 HomePod 已通电且未被其他设备占用，再重新检查。");
    }, cancellation);

    public static string OutputName(Receiver receiver)
    {
        var name = new string((receiver.Name ?? "").Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (name.Length == 0) return "HomePod";
        name = "HomePod – " + name;
        if (name.Length > 256) name = name[..256];
        return char.IsHighSurrogate(name[^1]) ? name[..^1] : name;
    }

    private bool CheckInspection(NativeSetupInspection inspection, bool publishReceiverStep = true)
    {
        Inspection = inspection;
        if (!inspection.DriverInstalled) { Step = NativeSetupStep.Driver; return false; }
        if (!string.IsNullOrWhiteSpace(inspection.BlockedReason)) return Block(inspection.BlockedReason);
        if (!inspection.RebootVerified) { Step = NativeSetupStep.Restart; return false; }
        if (inspection.Endpoint is not { } endpoint || !NativeBindingPolicy.CanClaim(endpoint, new HashSet<string>()))
            return Block("No verified ordinary VB-CABLE playback output is available. Check the driver and Windows sound settings.");
        if (inspection.Binding is { } binding)
        {
            if (NativeBindingPolicy.ValidateStored(binding) is { } invalid) return Block(invalid);
            if (!SameEndpoint(binding.Endpoint, endpoint)) return Block("The bound output or its format changed. Revalidate the binding before continuing.");
            if (binding.Calibration is not null && NativeBindingPolicy.ValidateLive(binding, endpoint) is { } changed) return Block(changed);
        }
        if (publishReceiverStep) Step = NativeSetupStep.Receiver;
        return true;
    }
    private bool Block(string error) { Error = DiagnosticText.Protect(error); Step = NativeSetupStep.Blocked; return false; }
    private static bool SameReceiver(string left, string right) => ReceiverIdentity.Normalize(left) == ReceiverIdentity.Normalize(right);
    private static bool Qualified(Receiver receiver) => ReceiverIdentity.IsBroadcast(receiver.Id) && receiver.Online
        && receiver.Complete && receiver.PrefersNtpAlac && receiver.Port is >= 1 and <= 65535;
    private static bool SameEndpoint(EndpointIdentity left, EndpointIdentity right)
        => string.Equals(left.EndpointId, right.EndpointId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.AdapterInstanceId, right.AdapterInstanceId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.DriverSha256, right.DriverSha256, StringComparison.OrdinalIgnoreCase) && left.MixFormat == right.MixFormat;

    private async Task StopPreviewAsync(CancellationToken cancellation)
    {
        if (!_preview) return;
        await platform.StopPreviewAsync(cancellation).ConfigureAwait(false);
        _preview = false; _previewEndpoint = null;
    }
    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); cancellation.ThrowIfCancellationRequested();
        if (!await _action.WaitAsync(0, cancellation).ConfigureAwait(false)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested(); Error = null;
            await action(linked.Token).ConfigureAwait(false); linked.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            Step = NativeSetupStep.Receiver;
            try { await StopPreviewAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { Block(error.Message); }
            throw;
        }
        catch (NativeSetupPendingException error) when (_preview)
        {
            Error = DiagnosticText.Protect(error.Message); Step = NativeSetupStep.Listening;
        }
        catch (Exception error)
        {
            var message = error.Message;
            try { await StopPreviewAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { message += " " + cleanup.Message; }
            Block(message);
        }
        finally { _action.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        await _action.WaitAsync().ConfigureAwait(false);
        try { await StopPreviewAsync(CancellationToken.None).ConfigureAwait(false); }
        finally { await platform.DisposeAsync().ConfigureAwait(false); _action.Release(); _lifetime.Dispose(); _action.Dispose(); }
    }
}

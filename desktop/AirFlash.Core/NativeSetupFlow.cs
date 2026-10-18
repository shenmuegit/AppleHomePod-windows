namespace AirFlash.Core;

public enum NativeSetupStep { Checking, Driver, Restart, Receiver, NameOutput, Listening, Ready, Blocked }
public sealed record NativeSetupInspection(bool DriverInstalled, bool RebootVerified, EndpointIdentity? Endpoint,
    NativeBinding? Binding, bool AgentReady, string? BlockedReason = null);
public interface INativeSetupPlatform : IAsyncDisposable
{
    Task<NativeSetupInspection> InspectAsync(CancellationToken cancellation);
    Task<IReadOnlyList<Receiver>> DiscoverAsync(CancellationToken cancellation);
    Task StartPreviewAsync(Receiver receiver, CancellationToken cancellation);
    Task CompleteAsync(CancellationToken cancellation);
    Task StopPreviewAsync(CancellationToken cancellation);
}

public sealed class NativeSetupFlow(INativeSetupPlatform platform) : IAsyncDisposable
{
    private readonly SemaphoreSlim _action = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private EndpointIdentity? _previewEndpoint;
    private bool _preview, _disposed;
    public NativeSetupStep Step { get; private set; } = NativeSetupStep.Checking;
    public NativeSetupInspection? Inspection { get; private set; }
    public IReadOnlyList<Receiver> Receivers { get; private set; } = [];
    public Receiver? SelectedReceiver { get; private set; }
    public string? Error { get; private set; }
    public Task RefreshAsync(CancellationToken cancellation) => RunAsync(async token => {
        await StopPreviewAsync(token).ConfigureAwait(false);
        var inspection = await platform.InspectAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        if (!CheckInspection(inspection)) return;
        if (inspection.Binding?.Calibration is not null && inspection.AgentReady) { Step = NativeSetupStep.Ready; return; }
        var receivers = await platform.DiscoverAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        Receivers = receivers.ToArray();
        if (SelectedReceiver is { } previous)
            SelectedReceiver = Receivers.FirstOrDefault(receiver => SameReceiver(receiver.Id, previous.Id));
        Step = NativeSetupStep.Receiver;
    }, cancellation);

    public Task ConnectAsync(Receiver receiver, CancellationToken cancellation) => RunAsync(async token => {
        await StopPreviewAsync(token).ConfigureAwait(false);
        var inspection = await platform.InspectAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        if (!CheckInspection(inspection)) return;
        var selected = Receivers.FirstOrDefault(candidate => SameReceiver(candidate.Id, receiver.Id));
        if (selected is null || !ReceiverIdentity.IsBroadcast(selected.Id) || !selected.Online || !selected.Complete
            || !selected.PrefersNtpAlac || selected.Port is < 1 or > 65535)
            throw new InvalidOperationException("Select a discovered, online HomePod with a qualified standalone profile.");
        SelectedReceiver = selected;
        if (inspection.Binding is { } binding && !SameReceiver(binding.ReceiverId, selected.Id))
            throw new InvalidOperationException("An existing output is bound to another HomePod. Remove that binding before replacing it.");
        var endpoint = inspection.Endpoint!; var name = OutputName(selected);
        if (inspection.Binding?.Calibration is null && endpoint.EndpointLabel != name && endpoint.FriendlyName != name
            && (endpoint.EndpointLabel is null || string.Equals(endpoint.EndpointLabel, endpoint.PinName, StringComparison.OrdinalIgnoreCase)))
        { Step = NativeSetupStep.NameOutput; return; }
        _preview = true; _previewEndpoint = endpoint;
        await platform.StartPreviewAsync(selected, token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        Step = NativeSetupStep.Listening;
    }, cancellation);

    public Task ConfirmMusicAsync(CancellationToken cancellation) => RunAsync(async token => {
        if (Step != NativeSetupStep.Listening || !_preview || SelectedReceiver is null) return;
        var inspection = await platform.InspectAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        if (!CheckInspection(inspection)) { await StopPreviewAsync(token).ConfigureAwait(false); return; }
        if (!SameEndpoint(inspection.Endpoint!, _previewEndpoint!) || inspection.Binding is { } binding
            && !SameReceiver(binding.ReceiverId, SelectedReceiver.Id))
            throw new InvalidOperationException("The selected output or HomePod changed during music playback. Refresh and connect again.");
        await platform.CompleteAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        _preview = false; _previewEndpoint = null; Step = NativeSetupStep.Ready;
    }, cancellation);

    public Task RejectMusicAsync(CancellationToken cancellation) => RunAsync(async token => {
        if (Step != NativeSetupStep.Listening) return;
        await StopPreviewAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
        Step = NativeSetupStep.Receiver;
    }, cancellation);

    public static string OutputName(Receiver receiver)
    {
        var name = new string((receiver.Name ?? "").Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (name.Length == 0) return "HomePod";
        name = "HomePod – " + name;
        if (name.Length > 256) name = name[..256];
        return char.IsHighSurrogate(name[^1]) ? name[..^1] : name;
    }

    private bool CheckInspection(NativeSetupInspection inspection)
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
        Step = NativeSetupStep.Receiver; return true;
    }
    private bool Block(string error) { Error = DiagnosticText.Protect(error); Step = NativeSetupStep.Blocked; return false; }
    private static bool SameReceiver(string left, string right) => ReceiverIdentity.Normalize(left) == ReceiverIdentity.Normalize(right);
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

using System.Diagnostics;
using AirFlash.Core;
namespace AirFlash.App.Services;

internal static class NativeOutputAgent
{
    internal static Task<int> RunAsync(CancellationToken cancellation) => NativeStartupPolicy.RunHeadlessAsync(RunCoreAsync, async error =>
    {
        AppPaths.Log($"Native agent failed: {error}");
        await NativeNotification.ShowAsync("HomePod 输出尚未就绪。请完成首次安装与校准，或查看 AppleHome 日志后重试。", CancellationToken.None).ConfigureAwait(false);
    }, cancellation);
    private static async Task RunCoreAsync(CancellationToken cancellation)
    {
        var started = Stopwatch.GetTimestamp();
        await using var instance = new SingleInstance("Native");
        if (!instance.IsOwner) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var ready = false;
        NativeOutputController? controller = null;
        instance.Listen(command => { if (command == 2) { Volatile.Write(ref ready, false); lifetime.Cancel(); } },
            () => Volatile.Read(ref ready), () => {
                var captureReady = Volatile.Read(ref ready);
                var status = Volatile.Read(ref controller)?.Status ?? new NativeAgentStatus(1);
                return captureReady ? status : status with { CaptureReady = false, GateOpen = false, MediaStarted = false };
            });
        try
        {
            var binding = await NativeAutostart.ReadQualifiedBindingAsync(lifetime.Token).ConfigureAwait(false);
            await using var monitor = new WindowsNativeOutput(binding);
            await monitor.GetDefaultEndpointIdAsync().ConfigureAwait(false);
            Volatile.Write(ref ready, true);
            AppPaths.Log("Native agent watching Windows output; engine lifetime follows output selection.");
            await new NativeEngineSupervisor(monitor, binding.Endpoint.EndpointId, log: AppPaths.Log).RunAsync(async cycle =>
            {
                if (!monitor.Snapshot.BindingValid) throw new InvalidDataException("The native endpoint no longer matches its calibration.");
                var process = new ProcessEngineConnection(AppPaths.ExtractEngine(), AppPaths.Log);
                await using var host = new NativeEngineHost(process, process.ProcessId);
                var hello = await EngineHandshake.HelloAsync(host, cycle, TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                if (!hello.TryGetProperty("commands", out var commands) || !commands.EnumerateArray().Any(c => c.GetString() == "arm_capture")) throw new IOException("The packaged engine does not support armed capture.");
                await host.ArmAsync(binding, cycle).ConfigureAwait(false);
                await using var session = new SessionController(host, monitor, AppPaths.Log);
                using var discovery = new WindowsDiscovery(log: AppPaths.Log);
                IReadOnlyList<Receiver> receivers = [];
                discovery.Changed += values => Volatile.Write(ref receivers, ReceiverCatalog.Reconcile(values, new()).Receivers);
                discovery.Failed += message => AppPaths.Log("Native receiver discovery: " + message);
                var discoveryStarted = false;
                Receiver? Resolve(string id)
                {
                    if (!discoveryStarted) { discoveryStarted = true; discovery.Start(); }
                    return Volatile.Read(ref receivers).SingleOrDefault(r => ReceiverIdentity.Normalize(r.Id) == id);
                }
                var current = new NativeOutputController(binding, monitor, host, session, log: AppPaths.Log);
                Volatile.Write(ref controller, current);
                try
                {
                    AppPaths.Log($"Native agent capture_ready cold_start_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}; engine_pid={process.ProcessId}; daily_windows=0 daily_tray=0");
                    await current.RunAsync(Resolve, cycle).ConfigureAwait(false);
                }
                finally { Volatile.Write(ref controller, null); }
            }, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { Volatile.Write(ref ready, false); }
    }
}

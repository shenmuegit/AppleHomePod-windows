using System.Threading.Channels;
namespace AirFlash.Core;

public sealed class NativeEngineSupervisor
{
    private readonly INativeOutputMonitor _monitor;
    private readonly string _endpointId;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    public NativeEngineSupervisor(INativeOutputMonitor monitor, string endpointId, TimeProvider? timeProvider = null, Action<string>? log = null)
    { _monitor = monitor; _endpointId = endpointId; _time = timeProvider ?? TimeProvider.System; _log = log; }

    // The Windows output picker controls playback roles; a communications-only route is independent.
    public static bool IsSelected(NativeOutputSnapshot snapshot, string endpointId)
        => snapshot.DefaultRenderIds.Count == 3 && snapshot.DefaultRenderIds.Take(2).Any(id => id == endpointId);

    public async Task RunAsync(Func<CancellationToken, Task> run, CancellationToken cancellation)
    {
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var sync = new object(); CancellationTokenSource? active = null;
        void Changed(NativeOutputSnapshot _)
        {
            lock (sync) if (!IsSelected(_monitor.Snapshot, _endpointId)) active?.Cancel();
            changes.Writer.TryWrite(true);
        }
        _monitor.Changed += Changed;
        var retrySeconds = 1;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (!IsSelected(_monitor.Snapshot, _endpointId))
                {
                    retrySeconds = 1;
                    await changes.Reader.ReadAsync(cancellation).ConfigureAwait(false);
                    continue;
                }
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                lock (sync)
                {
                    if (!IsSelected(_monitor.Snapshot, _endpointId)) continue;
                    active = cycle;
                }
                var started = _time.GetTimestamp();
                try
                {
                    try { await run(cycle.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cycle.IsCancellationRequested) { }
                    catch (Exception error) { _log?.Invoke("Native engine cycle retired: " + DiagnosticText.Protect(error.ToString())); }
                    // Only a selection change or explicit application shutdown ends demand. A failed attempt never does.
                    if (!cycle.IsCancellationRequested)
                    {
                        if (_time.GetElapsedTime(started) >= TimeSpan.FromSeconds(10)) retrySeconds = 1;
                        _log?.Invoke($"Native output remains selected; rebuilding engine in {retrySeconds} s.");
                        await Task.Delay(TimeSpan.FromSeconds(retrySeconds), _time, cycle.Token).ConfigureAwait(false);
                        retrySeconds = Math.Min(retrySeconds * 2, 30);
                    }
                }
                catch (OperationCanceledException) when (cycle.IsCancellationRequested) { }
                finally { lock (sync) active = null; }
            }
        }
        finally { _monitor.Changed -= Changed; }
    }
}

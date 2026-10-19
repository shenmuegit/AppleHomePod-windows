namespace AirFlash.App.Verification;

internal sealed class NativeSetupAudioLifetime(Action<Exception> failed)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Ready => _ready.Task;
    internal Task Stopped => _stopped.Task;
    internal void MarkReady() => _ready.TrySetResult();
    internal void CancelReady() => _ready.TrySetCanceled();
    internal void ReportFailure(Exception error) { _ready.TrySetException(error); failed(error); }
    internal void Run(Action operation, Action cleanup)
    {
        try { operation(); }
        catch (Exception error) { ReportFailure(error); }
        finally
        {
            try { cleanup(); _stopped.TrySetResult(); }
            catch (Exception error) { ReportFailure(error); _stopped.TrySetException(error); }
        }
    }
}

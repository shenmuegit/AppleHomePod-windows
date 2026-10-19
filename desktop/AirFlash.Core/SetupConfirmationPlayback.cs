using System.Diagnostics;
namespace AirFlash.Core;

public sealed class SetupConfirmationPlayback : IDisposable
{
    private readonly float[] _samples;
    private readonly object _sync = new();
    private TaskCompletionSource? _playing;
    private CancellationToken _cancellation;
    private Exception? _failure;
    private int _position;
    private bool _silent;
    private long _queuedAt;
    private TimeSpan? _dispatchDelay;
    public SetupConfirmationPlayback(int sampleRate, int channels) => _samples = SetupConfirmationPcm.Create(sampleRate, channels);
    public TimeSpan? LastDispatchDelay { get { lock (_sync) return _dispatchDelay; } }
    public async Task PlayAsync(CancellationToken cancellation, bool silent = false)
    {
        cancellation.ThrowIfCancellationRequested();
        TaskCompletionSource playing;
        lock (_sync)
        {
            if (_failure is not null) throw _failure;
            if (_playing is not null) throw new InvalidOperationException("The setup confirmation is already playing.");
            _position = 0; _silent = silent; _cancellation = cancellation;
            _dispatchDelay = null; _queuedAt = Stopwatch.GetTimestamp();
            _playing = playing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try { await playing.Task.WaitAsync(cancellation).ConfigureAwait(false); }
        finally { lock (_sync) if (ReferenceEquals(_playing, playing)) _playing = null; }
    }
    public void Read(Span<float> output)
    {
        output.Clear();
        lock (_sync)
        {
            if (_playing is null || _failure is not null || output.IsEmpty || _position == _samples.Length) return;
            if (_cancellation.IsCancellationRequested) { _playing.TrySetCanceled(_cancellation); return; }
            _dispatchDelay ??= Stopwatch.GetElapsedTime(_queuedAt);
            var count = Math.Min(output.Length, _samples.Length - _position);
            if (!_silent) _samples.AsSpan(_position, count).CopyTo(output);
            _position += count;
            if (_position == _samples.Length) _playing.TrySetResult();
        }
    }
    public void Stop(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_sync) { _failure ??= error; _playing?.TrySetException(_failure); }
    }
    public void RejectPlayback(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_sync)
        {
            if (_playing is null) return;
            _position = _samples.Length; _playing.TrySetException(error);
        }
    }
    public void Dispose() => Stop(new ObjectDisposedException(nameof(SetupConfirmationPlayback)));
}

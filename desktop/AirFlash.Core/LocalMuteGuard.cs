namespace AirFlash.Core;

public enum LocalMuteAction { None, Mute, RestoreAndWarn }
public sealed class LocalMuteGuard
{
    private long _lastAudio;
    private int _stalled;
    private bool _disabled;
    public LocalMuteAction Observe(long lastAudioQpcNs)
    {
        if (_disabled || lastAudioQpcNs <= 0) return LocalMuteAction.None;
        if (_lastAudio == 0) { _lastAudio = lastAudioQpcNs; return LocalMuteAction.Mute; }
        if (lastAudioQpcNs > _lastAudio) { _lastAudio = lastAudioQpcNs; _stalled = 0; return LocalMuteAction.None; }
        if (++_stalled < 2) return LocalMuteAction.None;
        _disabled = true;
        return LocalMuteAction.RestoreAndWarn;
    }
}

namespace AirFlash.Core;

public sealed class NativeBindingValidation
{
    private long _version = 1, _validated;
    public long Version => Volatile.Read(ref _version);
    public bool IsCurrent => Volatile.Read(ref _validated) == Version;
    public void NotifyChanged() => Interlocked.Increment(ref _version);
    public void CompleteValidation(long version)
    { if (version == Version) Volatile.Write(ref _validated, version); }
}

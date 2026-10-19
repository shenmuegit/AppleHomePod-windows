using System.Collections.ObjectModel;
namespace AirFlash.Core;

public sealed record NativeSetupOutputVolume(float Scalar, bool Muted);
public sealed record NativeSetupOutputState(string EndpointId, string PinName, string? OriginalName, string? AssignedName,
    IReadOnlyDictionary<int, string> OriginalDefaults, NativeSetupOutputVolume? OriginalVolume, NativeSetupOutputVolume? AssignedVolume);

public interface INativeSetupOutput
{
    string ReadName();
    void WriteName(string name);
    string? ReadDefault(int role);
    void WriteDefault(int role, string endpointId);
    (float Scalar, bool Muted) ReadVolume();
    void WriteVolume(float scalar, bool muted);
}

public sealed class NativeSetupOutput(INativeSetupOutput output, string endpointId, string pinName,
    Action<NativeSetupOutputState?>? save = null) : IDisposable
{
    private NativeSetupOutputState _state = new(endpointId, pinName, null, null, new Dictionary<int, string>(), null, null);
    private bool _committed, _disposed;

    public void PrepareName(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.Any(char.IsControl)) throw new ArgumentException("Invalid output name.");
        var current = output.ReadName();
        if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, pinName, StringComparison.OrdinalIgnoreCase)) return;
        Persist(_state with { OriginalName = _state.OriginalName ?? current, AssignedName = name });
        output.WriteName(name);
        if (output.ReadName() != name) throw new IOException("Windows did not confirm the automatic output name. Retry setup.");
    }

    public void SelectForConfirmation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Only playback Console and Multimedia change; Communications and capture devices are untouched.
        foreach (var role in new[] { 0, 1 })
        {
            var previous = output.ReadDefault(role);
            if (previous == endpointId || _state.OriginalDefaults.ContainsKey(role)) continue;
            if (previous is null) throw new IOException("Windows has no playback output to restore if setup is cancelled.");
            var defaults = _state.OriginalDefaults.ToDictionary();
            defaults.Add(role, previous);
            Persist(_state with { OriginalDefaults = defaults });
            output.WriteDefault(role, endpointId);
            if (output.ReadDefault(role) != endpointId) throw new IOException("Windows did not confirm the automatic playback selection. Retry setup.");
        }
        if (_state.OriginalVolume is not null) return;
        var volume = output.ReadVolume();
        if (!float.IsFinite(volume.Scalar) || volume.Scalar is < 0 or > 1) throw new InvalidDataException("Invalid Windows output volume.");
        var confirmation = (Math.Clamp(volume.Scalar, .3f, .6f), false);
        if (volume == confirmation) return;
        Persist(_state with { OriginalVolume = new(volume.Scalar, volume.Muted), AssignedVolume = new(confirmation.Item1, confirmation.Item2) });
        output.WriteVolume(confirmation.Item1, confirmation.Item2);
        if (!SameVolume(output.ReadVolume(), confirmation)) throw new IOException("Windows did not confirm the test audio volume.");
    }

    public static void Recover(INativeSetupOutput output, NativeSetupOutputState state, Action<NativeSetupOutputState?> save)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(save);
        var validated = ValidatedCopy(state);
        var setup = new NativeSetupOutput(output, validated.EndpointId, validated.PinName, save) { _state = validated };
        setup.Dispose();
    }

    private void Persist(NativeSetupOutputState state)
    {
        var next = ValidatedCopy(state);
        save?.Invoke(next);
        _state = next;
    }

    private static NativeSetupOutputState ValidatedCopy(NativeSetupOutputState state)
    {
        if (state is null || !Text(state.EndpointId, 2048) || !Text(state.PinName, 256)
            || state.OriginalDefaults is null || state.OriginalDefaults.Count > 2
            || (state.OriginalName is null) != (state.AssignedName is null)
            || state.OriginalName is not null && (!Text(state.OriginalName, 256, allowEmpty: true) || !Text(state.AssignedName, 256))
            || (state.OriginalVolume is null) != (state.AssignedVolume is null)
            || state.OriginalVolume is not null && (!Volume(state.OriginalVolume) || !Volume(state.AssignedVolume!)))
            throw new InvalidDataException("Invalid or incomplete setup output recovery state.");
        var defaults = new Dictionary<int, string>();
        foreach (var (role, original) in state.OriginalDefaults)
        {
            if (role is not (0 or 1) || !Text(original, 2048))
                throw new InvalidDataException("The recovery state can restore only valid Console and Multimedia playback outputs.");
            defaults.Add(role, original);
        }
        return state with { OriginalDefaults = new ReadOnlyDictionary<int, string>(defaults) };

        static bool Text(string? value, int max, bool allowEmpty = false)
            => value is not null && value.Length <= max && (allowEmpty || !string.IsNullOrWhiteSpace(value)) && !value.Any(char.IsControl);
        static bool Volume(NativeSetupOutputVolume volume) => float.IsFinite(volume.Scalar) && volume.Scalar is >= 0 and <= 1;
    }

    public void Commit() { ObjectDisposedException.ThrowIf(_disposed, this); save?.Invoke(null); _committed = true; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_committed) return;
        var errors = new List<Exception>();
        Restore(() => {
            if (_state.OriginalVolume is { } original && _state.AssignedVolume is { } assigned)
            {
                var current = output.ReadVolume();
                if (SameVolume(OwnedTarget(current), current)) return;
                save?.Invoke(_state);
                current = output.ReadVolume();
                var target = OwnedTarget(current);
                if (SameVolume(target, current)) return;
                output.WriteVolume(target.Scalar, target.Muted);
                if (!SameVolume(output.ReadVolume(), target)) throw new IOException("Windows did not confirm the restored output volume.");

                (float Scalar, bool Muted) OwnedTarget((float Scalar, bool Muted) value)
                    => (Math.Abs(value.Scalar - assigned.Scalar) <= .00001f ? original.Scalar : value.Scalar,
                        value.Muted == assigned.Muted ? original.Muted : value.Muted);
            }
        });
        foreach (var role in new[] { 1, 0 })
        {
            if (!_state.OriginalDefaults.TryGetValue(role, out var original)) continue;
            Restore(() => {
                if (output.ReadDefault(role) != endpointId) return;
                save?.Invoke(_state);
                if (output.ReadDefault(role) != endpointId) return;
                output.WriteDefault(role, original);
                if (output.ReadDefault(role) != original) throw new IOException("Windows did not confirm the restored playback output.");
            });
        }
        Restore(() => {
            if (_state.OriginalName is not { } original || output.ReadName() != _state.AssignedName) return;
            save?.Invoke(_state);
            if (output.ReadName() != _state.AssignedName) return;
            output.WriteName(original);
            if (output.ReadName() != original) throw new IOException("Windows did not confirm the restored output name.");
        });
        if (errors.Count == 0) Restore(() => save?.Invoke(null));
        if (errors.Count > 0) throw new AggregateException("Windows could not restore all setup-owned audio changes. Reopen AppleHome to check the output.", errors);
        void Restore(Action action) { try { action(); } catch (Exception error) { errors.Add(error); } }
    }
    private static bool SameVolume((float Scalar, bool Muted) left, (float Scalar, bool Muted) right)
        => Math.Abs(left.Scalar - right.Scalar) <= .00001f && left.Muted == right.Muted;
}

using System.Text.Json;
namespace AirFlash.Core;

public sealed class MuteRecoveryJournal
{
    private const int MaxBytes = 65536;
    private readonly string _path;
    private Dictionary<string, bool> _entries = [];
    public IReadOnlyDictionary<string, bool> Entries => _entries;
    private sealed record Stored(int SchemaVersion, Dictionary<string, bool>? Entries);
    public MuteRecoveryJournal(string path)
    {
        _path = Path.GetFullPath(path);
        if (!File.Exists(_path)) return;
        using var stream = File.OpenRead(_path);
        if (stream.Length > MaxBytes) throw new InvalidDataException(L.Get("The local mute recovery file is too large. Local playback remains enabled."));
        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(stream, AppSettings.JsonOptions);
            if (stored is not { SchemaVersion: 1, Entries: not null } || stored.Entries.Keys.Any(string.IsNullOrWhiteSpace))
                throw new JsonException("Invalid mute recovery schema.");
            _entries = stored.Entries;
        }
        catch (JsonException error) { throw new InvalidDataException(L.Get("Could not read local mute recovery. Local playback remains enabled."), error); }
    }
    public void Remember(string endpointId, bool originalMute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        if (_entries.ContainsKey(endpointId)) return;
        var next = new Dictionary<string, bool>(_entries) { [endpointId] = originalMute };
        Save(next);
    }
    public void Forget(string endpointId)
    {
        if (!_entries.ContainsKey(endpointId)) return;
        var next = new Dictionary<string, bool>(_entries); next.Remove(endpointId);
        Save(next);
    }
    public bool? RestoreValue(string endpointId, bool currentMute)
    {
        // shortcut: after a crash, same-value external mute writes cannot be identified; use a virtual endpoint if independent routing is required.
        return currentMute && _entries.TryGetValue(endpointId, out var previous) ? previous : null;
    }
    private void Save(Dictionary<string, bool> next)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Stored(1, next), AppSettings.JsonOptions);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Local mute recovery exceeds its size limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            if (File.Exists(_path)) File.Replace(temp, _path, null); else File.Move(temp, _path);
            _entries = next;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

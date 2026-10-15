using System.Text;
using System.Text.Json;
namespace AirFlash.Core;

public sealed class NativeBindingStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(AppSettings.JsonOptions) { RespectRequiredConstructorParameters = true };
    public NativeBinding? Load()
    {
        if (!File.Exists(path)) return null;
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        var buffer = new char[65537];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count > 65536) throw new InvalidDataException("The native output binding exceeds its size limit.");
        var binding = JsonSerializer.Deserialize<NativeBinding>(buffer.AsSpan(0, count), JsonOptions);
        if (NativeBindingPolicy.ValidateStored(binding) is { } error) throw new InvalidDataException(error);
        return binding;
    }

    public async Task SaveAsync(NativeBinding binding, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (NativeBindingPolicy.ValidateStored(binding) is { } error) throw new ArgumentException(error, nameof(binding));
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(binding, AppSettings.JsonOptions),
                new UTF8Encoding(false), cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(fullPath)) File.Replace(temp, fullPath, null);
            else File.Move(temp, fullPath);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

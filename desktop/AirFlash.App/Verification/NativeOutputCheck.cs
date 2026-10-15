using System.Text.Json;
using System.Windows;
using AirFlash.App.Services;
using AirFlash.Core;
namespace AirFlash.App.Verification;

internal static class NativeOutputCheck
{
    private static readonly string[] Commands = ["--native-preflight-check", "--native-bind", "--native-unbind"];
    internal static bool IsCommand(string[] args) => args.Any(Commands.Contains);

    internal static async Task<int> RunAsync(string[] args)
    {
        string? output = null;
        try
        {
            if (args.Count(Commands.Contains) != 1) throw new ArgumentException("Specify one native output command.");
            output = OutputPath(args);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var windows = new WindowsNativeBinding();
            if (args.Contains("--native-preflight-check"))
            {
                var before = await windows.PreflightAsync(timeout.Token).ConfigureAwait(false);
                var after = await windows.PreflightAsync(timeout.Token).ConfigureAwait(false);
                if (!before.Defaults.SequenceEqual(after.Defaults))
                    throw new InvalidOperationException("The Windows default output changed during inspection. Retry after routing is stable.");
                var binding = new NativeBindingStore(WindowsNativeBinding.BindingPath).Load();
                var unmanaged = before.CableAdapterIds.Any(id => binding?.Endpoint.AdapterInstanceId != id);
                Write(output, new { ok = !unmanaged, stage = "native-preflight", version = AppPaths.Version,
                    wpf_initialized = Application.Current is not null, driver_installed = before.CableAdapterIds.Length > 0,
                    unmanaged_driver = unmanaged, preinstall_snapshot_saved = File.Exists(WindowsNativeBinding.PreflightPath),
                    endpoints = before.Endpoints,
                    adapters = before.CableAdapterIds.Select(id => new { adapter_instance_id = id }),
                    defaults = before.Defaults.Select((id, index) => new { role = new[] { "console", "multimedia", "communications" }[index], endpoint_id = id }),
                    note = "Windows audio inspection only; no stream, playback, mute, default-output, driver-install or startup-task changes." });
                return unmanaged ? 1 : 0;
            }
            if (args.Contains("--native-bind"))
            {
                var binding = await windows.BindAsync(Argument(args, "--receiver-id"), Argument(args, "--endpoint-id"), "HomePod – da", timeout.Token).ConfigureAwait(false);
                Write(output, new { ok = true, stage = "native-bound", binding, calibrated = binding.Calibration is not null,
                    note = "Naming must survive a reboot and calibration must pass before enabling the agent." });
                return 0;
            }
            var store = new NativeBindingStore(WindowsNativeBinding.BindingPath);
            var existing = store.Load();
            if (existing is not null)
            {
                await windows.RestoreAsync(existing, timeout.Token).ConfigureAwait(false);
                if (store.Load() != existing) throw new IOException("The binding changed during removal; its current contents were preserved.");
                File.Delete(WindowsNativeBinding.BindingPath);
            }
            Write(output, new { ok = true, stage = "native-unbound", driver_uninstalled = false });
            return 0;
        }
        catch (Exception error)
        {
            AppPaths.Log($"Native output setup failed: {error}");
            if (output is not null)
                try { Write(output, new { ok = false, stage = "native-setup", error = DiagnosticText.Protect(error.Message), hresult = $"0x{error.HResult:X8}" }); }
                catch (Exception reportError) { AppPaths.Log($"Native output report failed: {reportError.Message}"); }
            return 1;
        }
    }

    internal static string Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)
            || args.Count(a => a == name) != 1) throw new ArgumentException($"Specify one {name} value.");
        return args[index + 1];
    }

    private static string OutputPath(string[] args)
    {
        var output = Path.GetFullPath(Argument(args, "--output"));
        if (!Path.GetExtension(output).Equals(".json", StringComparison.OrdinalIgnoreCase)
            || output.Equals(Path.GetFullPath(WindowsNativeBinding.BindingPath), StringComparison.OrdinalIgnoreCase)
            || output.Equals(Path.GetFullPath(WindowsNativeBinding.PreflightPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a separate .json diagnostic output file, not a binding or pre-install snapshot.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        return output;
    }

    internal static void Write(string output, object value)
        => File.WriteAllText(output, JsonSerializer.Serialize(value,
            new JsonSerializerOptions(AppSettings.JsonOptions) { IgnoreReadOnlyProperties = false }));
}

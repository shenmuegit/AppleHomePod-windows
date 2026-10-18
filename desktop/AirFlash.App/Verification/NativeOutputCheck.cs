using System.Text.Json;
using System.Windows;
using AirFlash.App.Services;
using AirFlash.Core;
namespace AirFlash.App.Verification;

internal static class NativeOutputCheck
{
    private static readonly string[] Commands = ["--native-check", "--native-enable", "--native-disable", "--native-preflight-check", "--native-monitor-check", "--native-bind", "--native-unbind", "--native-calibrate"];
    internal static bool IsCommand(string[] args) => args.Any(Commands.Contains);

    internal static async Task<int> RunAsync(string[] args)
    {
        string? output = null;
        try
        {
            if (args.Count(Commands.Contains) != 1) throw new ArgumentException("Specify one native output command.");
            var startupMaintenance = args.Contains("--native-enable") || args.Contains("--native-disable");
            output = startupMaintenance && !args.Contains("--output") ? null : OutputPath(args);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(args.Contains("--native-calibrate") || args.Contains("--native-enable") ?60:30));
            var windows = new WindowsNativeBinding();
            if (startupMaintenance)
            {
                if (args.Contains("--native-enable")) await NativeAutostart.InstallAsync(Environment.ProcessPath!, timeout.Token).ConfigureAwait(false);
                else await NativeAutostart.RemoveOwnedAsync(NativeAutostart.InstalledExecutable, timeout.Token).ConfigureAwait(false);
                if (output is not null) Write(output, new { ok = true, stage = args.Contains("--native-enable") ? "native-enabled" : "native-disabled", wpf_initialized = Application.Current is not null });
                return 0;
            }
            if (args.Contains("--native-check"))
            {
                var binding = new NativeBindingStore(WindowsNativeBinding.BindingPath).Load();
                string? gate = null;
                try { _ = await NativeAutostart.ReadQualifiedBindingAsync(timeout.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or InvalidOperationException) { gate = DiagnosticText.Protect(error.Message); }
                var schema = await NativeAutostart.ValidateSchemaAsync(timeout.Token).ConfigureAwait(false);
                var task = await NativeAutostart.InspectAsync(NativeAutostart.InstalledExecutable, timeout.Token).ConfigureAwait(false);
                Write(output!, new { ok = true, stage = "native-check", headless_dispatch = NativeStartupPolicy.IsHeadless(["--native-agent"]),
                    wpf_initialized = Application.Current is not null, normal_window_count = Application.Current?.Windows.Count ?? 0, daily_tray_registered = false,
                    binding_present = binding is not null, verified_buffer_ms = binding?.VerifiedBufferMs, can_enable = gate is null, qualification_error = gate, task_schema_valid = schema,
                    reboot_verified = binding is not null && File.Exists(WindowsNativeBinding.PreflightPath) && WindowsNativeBinding.RebootedSincePreinstall(),
                    task_exists = task.Exists, task_owned = task.Owned, ready = await NativeAutostart.QueryReadyAsync(timeout.Token).ConfigureAwait(false),
                    engine_started = false, capture_started = false, uses_service = false, stores_password = false,
                    note = "Read-only startup inspection and in-memory Task Scheduler schema validation; no task registration, application installation or audio activation." });
                return 0;
            }
            if (args.Contains("--native-monitor-check"))
            {
                var binding = new NativeBindingStore(WindowsNativeBinding.BindingPath).Load()
                    ?? throw new InvalidDataException("Bind a verified virtual output before monitoring.");
                await using var monitor = new WindowsNativeOutput(binding);
                var count = 0;
                monitor.Changed += _ => Interlocked.Increment(ref count);
                await monitor.GetDefaultEndpointIdAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
                var apartment = await monitor.ReadApartmentAsync().ConfigureAwait(false);
                await Task.Delay(750, timeout.Token).ConfigureAwait(false);
                Write(output!, new { ok = true, stage = "native-monitor", listening_only = true, binding_valid = monitor.Snapshot.BindingValid,
                    wpf_initialized = Application.Current is not null, engine_started = false, capture_started = false,
                    listener_apartment = apartment.ToString(), notifications = count, snapshot = monitor.Snapshot,
                    note = "Read-only endpoint/session notifications; no stream, PCM, mute, volume, default-output or startup-task writes. Binding validity remains a separate gate." });
                return 0;
            }
            if (args.Contains("--native-calibrate"))
            {
                using var maintenance=NativeVolumeCalibration.AcquireMaintenance();
                int? confirmed=null;
                if (args.Contains("--confirm-reference-volume"))
                {
                    if (!int.TryParse(Argument(args,"--confirm-reference-volume"),out var value) || value is <1 or >100)
                        throw new ArgumentException("Confirm only the measured nonzero receiver reference volume (1–100).");
                    confirmed=value;
                }
                var binding=await NativeVolumeCalibration.ReadBoundAsync(timeout.Token).ConfigureAwait(false);
                var proposed=await NativeVolumeCalibration.MeasureAsync(binding,timeout.Token).ConfigureAwait(false);
                if (confirmed is not null)
                {
                    if (confirmed!=proposed.Calibration.ResumeDeviceVolume)
                        throw new InvalidOperationException("The receiver's actual volume changed since listening confirmation. No calibration was saved.");
                    var current=await NativeVolumeCalibration.ReadBoundAsync(timeout.Token).ConfigureAwait(false);
                    if (current!=binding) throw new IOException("The binding changed during calibration; its contents were preserved.");
                    await new NativeBindingStore(WindowsNativeBinding.BindingPath).SaveAsync(binding with { Calibration=proposed.Calibration,
                        VerifiedBufferMs=binding.VerifiedBufferMs ?? NativeVolumeCalibration.ReferenceBufferMs },timeout.Token).ConfigureAwait(false);
                }
                Write(output!,new { ok=true,stage="native-calibration",calibrated=confirmed is not null,
                    requires_listening_confirmation=confirmed is null,reference_device_volume=proposed.Calibration.ResumeDeviceVolume,
                    reference_buffer_ms=NativeVolumeCalibration.ReferenceBufferMs,proposal=proposed });
                return 0;
            }
            if (args.Contains("--native-preflight-check"))
            {
                var before = await windows.PreflightAsync(timeout.Token).ConfigureAwait(false);
                var after = await windows.PreflightAsync(timeout.Token).ConfigureAwait(false);
                if (!before.Defaults.SequenceEqual(after.Defaults))
                    throw new InvalidOperationException("The Windows default output changed during inspection. Retry after routing is stable.");
                var binding = new NativeBindingStore(WindowsNativeBinding.BindingPath).Load();
                var unmanaged = before.CableAdapterIds.Any(id => binding?.Endpoint.AdapterInstanceId != id);
                Write(output!, new { ok = !unmanaged, stage = "native-preflight", version = AppPaths.Version,
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
                using var maintenance=NativeVolumeCalibration.AcquireMaintenance();
                var binding = await windows.BindAsync(Argument(args, "--receiver-id"), Argument(args, "--endpoint-id"), "HomePod – da", timeout.Token).ConfigureAwait(false);
                Write(output!, new { ok = true, stage = "native-bound", binding, calibrated = binding.Calibration is not null,
                    note = "Naming must survive a reboot and calibration must pass before enabling the agent." });
                return 0;
            }
            var store = new NativeBindingStore(WindowsNativeBinding.BindingPath);
            await NativeAutostart.RemoveOwnedAsync(NativeAutostart.InstalledExecutable, timeout.Token).ConfigureAwait(false);
            using var removal=NativeVolumeCalibration.AcquireMaintenance();
            var existing = store.Load();
            if (existing is not null)
            {
                await windows.RestoreAsync(existing, timeout.Token).ConfigureAwait(false);
                if (store.Load() != existing) throw new IOException("The binding changed during removal; its current contents were preserved.");
                File.Delete(WindowsNativeBinding.BindingPath);
            }
            Write(output!, new { ok = true, stage = "native-unbound", driver_uninstalled = false });
            return 0;
        }
        catch (Exception error)
        {
            AppPaths.Log($"Native output setup failed: {error}");
            if (output is not null)
                try { Write(output, new { ok = false, stage = "native-setup", error = DiagnosticText.Protect(error.Message), hresult = $"0x{error.HResult:X8}" }); }
                catch (Exception reportError) { AppPaths.Log($"Native output report failed: {reportError.Message}"); }
            if (output is null && (args.Contains("--native-enable") || args.Contains("--native-disable")))
                await NativeNotification.ShowAsync("HomePod 输出设置未完成，请查看 AppleHome 日志并按首次设置说明处理。", CancellationToken.None).ConfigureAwait(false);
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
        if (!Path.GetExtension(output).Equals(".json", StringComparison.OrdinalIgnoreCase) || Directory.Exists(output)
            || output.StartsWith(Path.GetFullPath(AppPaths.DataDirectory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a separate .json diagnostic file outside the application's private configuration directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var writable=new FileStream(output,FileMode.OpenOrCreate,FileAccess.Write,FileShare.Read);
        return output;
    }

    internal static void Write(string output, object value)
        => File.WriteAllText(output, JsonSerializer.Serialize(value,
            new JsonSerializerOptions(AppSettings.JsonOptions) { IgnoreReadOnlyProperties = false }));
}

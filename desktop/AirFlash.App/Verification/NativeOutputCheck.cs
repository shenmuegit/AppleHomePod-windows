using System.Windows;
using AirFlash.App.Services;
using AirFlash.Core;
using static AirFlash.App.Services.NativeOutputMaintenance;
namespace AirFlash.App.Verification;

internal static class NativeOutputCheck
{
    private static readonly string[] Commands = ["--native-check", "--native-preflight-check", "--native-monitor-check"];
    internal static bool IsCommand(string[] args) => args.Any(Commands.Contains);

    internal static async Task<int> RunAsync(string[] args)
    {
        string? output = null;
        try
        {
            ValidateCommand(args);
            if (!IsCommand(args)) throw new ArgumentException("Specify one native output inspection command.");
            output = OutputPath(args);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var windows = new WindowsNativeBinding();
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
            var before = await windows.PreflightAsync(timeout.Token).ConfigureAwait(false);
            var after = await windows.PreflightAsync(timeout.Token).ConfigureAwait(false);
            if (!before.Defaults.SequenceEqual(after.Defaults))
                throw new InvalidOperationException("The Windows default output changed during inspection. Retry after routing is stable.");
            var preflightBinding = new NativeBindingStore(WindowsNativeBinding.BindingPath).Load();
            var unmanaged = before.CableAdapterIds.Any(id => preflightBinding?.Endpoint.AdapterInstanceId != id);
            Write(output!, new { ok = !unmanaged, stage = "native-preflight", version = AppPaths.Version,
                wpf_initialized = Application.Current is not null, driver_installed = before.CableAdapterIds.Length > 0,
                unmanaged_driver = unmanaged, preinstall_snapshot_saved = File.Exists(WindowsNativeBinding.PreflightPath),
                endpoints = before.Endpoints,
                adapters = before.CableAdapterIds.Select(id => new { adapter_instance_id = id }),
                defaults = before.Defaults.Select((id, index) => new { role = new[] { "console", "multimedia", "communications" }[index], endpoint_id = id }),
                note = "Windows audio inspection only; no stream, playback, mute, default-output, driver-install or startup-task changes." });
            return unmanaged ? 1 : 0;
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
}

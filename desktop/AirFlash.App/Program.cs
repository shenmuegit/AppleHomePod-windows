using System.Runtime.InteropServices;
using AirFlash.App.Services;
using AirFlash.App.Services.Setup;
using AirFlash.App.Verification;
using AirFlash.Core;
namespace AirFlash.App;

internal static class Program
{
    internal static bool IsDiagnostic(string[] args) => args.Any(a => a.EndsWith("-check", StringComparison.Ordinal)
        || a is "--ui-smoke" or "--tray-smoke" or "--ui-perf" or "--discovery-perf" or "--setup-smoke")
        || NativeOutputCheck.IsCommand(args) || NativeOutputMaintenance.IsCommand(args)
        || NativeVolumeCalibration.IsSourceCommand(args) || NativeSetupAudioSource.IsSourceCommand(args);
    [STAThread]
    public static int Main(string[] args)
    {
        var headless = NativeStartupPolicy.IsHeadless(args);
        // Also works when the WPF resource dictionary or localization cannot initialize.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppPaths.Log($"Fatal stage=unhandled {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => AppPaths.Log($"Background task failed {e.Exception}");
        var stage = "platform";
        try
        {
            if (args.Contains("--native-check") && args.Contains("--data-directory"))
                AppPaths.DataDirectory = Path.GetFullPath(NativeOutputMaintenance.Argument(args, "--data-directory"));
            // The collector gets isolated logs, including failures before WPF initialization.
            if (args.Contains("--compat-check"))
            {
                var index = Array.IndexOf(args, "--output");
                if (index >= 0 && index + 1 < args.Length)
                    AppPaths.DataDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[index + 1]))!, "compat-logs");
            }
            AppPaths.Log($"Startup stage={stage} version={AppPaths.Version} os={Environment.OSVersion.Version} architecture={RuntimeInformation.ProcessArchitecture}");
            if (!WindowsCapabilities.Current.Supported) throw new PlatformNotSupportedException(WindowsCompatibility.Requirement);
            if (NativeVolumeCalibration.IsSourceCommand(args))
                return NativeVolumeCalibration.RunSourceAsync(args).GetAwaiter().GetResult();
            if (NativeSetupAudioSource.IsSourceCommand(args))
                return NativeSetupAudioSource.RunSourceAsync(args).GetAwaiter().GetResult();
            if (NativeOutputMaintenance.IsCommand(args))
            {
                stage = "native-setup";
                return NativeOutputMaintenance.RunAsync(args).GetAwaiter().GetResult();
            }
            if (NativeOutputCheck.IsCommand(args))
            {
                stage = "native-setup";
                return NativeOutputCheck.RunAsync(args).GetAwaiter().GetResult();
            }
            if (headless)
            {
                stage = "native-agent";
                return NativeOutputAgent.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            stage = "wpf-resources";
            var app = new App();
            app.InitializeComponent();
            AppPaths.Log($"Startup stage={stage} ready");
            return app.Run();
        }
        catch (Exception error)
        {
            AppPaths.Log($"Startup stage={stage} failed {error}");
            try { App.WriteOutput(args, new { ok = false, stage, error = error.ToString(), hresult = $"0x{error.HResult:X8}" }); }
            catch (Exception reportError) { AppPaths.Log($"Diagnostic output failed: {reportError.Message}"); }
            if (headless && !IsDiagnostic(args))
                NativeNotification.ShowAsync("HomePod 输出尚未就绪，请查看 AppleHome 日志与首次设置说明。", CancellationToken.None).GetAwaiter().GetResult();
            else if (!headless && !IsDiagnostic(args))
                MessageBox(IntPtr.Zero, $"{error.Message}\n\nLogs: {AppPaths.LogDirectory}", $"{AppPaths.Name} startup failed", 0x10);
            return 1;
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string message, string caption, uint type);
}

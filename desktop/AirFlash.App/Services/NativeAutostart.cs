using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using AirFlash.App.Verification;
using AirFlash.Core;
using Microsoft.Win32;
namespace AirFlash.App.Services;

internal static class NativeAutostart
{
    internal static string InstalledExecutable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.Name, "app", "AppleHome.exe");
    private static string ReceiptPath => Path.Combine(AppPaths.DataDirectory, "native-startup.json");
    private static string Sid { get { using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value ?? throw new InvalidOperationException("Current Windows SID is unavailable."); } }
    private static string TaskName(string sid) => "AppleHome.NativeOutput." + sid;
    internal static async Task<NativeBinding> ReadQualifiedBindingAsync(CancellationToken cancellation)
    {
        var binding = await NativeVolumeCalibration.ReadBoundAsync(cancellation).ConfigureAwait(false);
        if (binding.Calibration is null) throw new InvalidDataException("Finish the measured Windows volume and audible reference calibration before enabling native output.");
        if (!WindowsNativeBinding.RebootedSincePreinstall()) throw new InvalidOperationException("The independent driver's required reboot has not been verified.");
        return binding;
    }
    private static NativeTaskReceipt? ReadReceipt()
    {
        if (!File.Exists(ReceiptPath)) return null;
        try
        {
            using var file = File.OpenRead(ReceiptPath); if (file.Length > 65536) return null;
            return JsonSerializer.Deserialize<NativeTaskReceipt>(file, new JsonSerializerOptions(AppSettings.JsonOptions) { RespectRequiredConstructorParameters = true });
        }
        catch (Exception error) when (error is JsonException or InvalidDataException) { AppPaths.Log("Native startup ownership proof is invalid: " + error.Message); return null; }
    }
    private static async Task SaveReceiptAsync(NativeTaskReceipt receipt, CancellationToken cancellation)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var temp = ReceiptPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(receipt, AppSettings.JsonOptions), cancellation).ConfigureAwait(false); cancellation.ThrowIfCancellationRequested(); File.Move(temp, ReceiptPath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static string Hash(string executable)
    { using var file = File.OpenRead(executable); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    private static async Task<bool> AgentPresentAsync(CancellationToken cancellation)
    {
        await using var instance = new SingleInstance("Native");
        cancellation.ThrowIfCancellationRequested(); return !instance.IsOwner;
    }
    internal static async Task<bool> QueryReadyAsync(CancellationToken cancellation)
    {
        await using var instance = new SingleInstance("Native");
        return !instance.IsOwner && await instance.QueryReadyAsync(cancellation).ConfigureAwait(false);
    }
    internal static Task InstallAsync(string executable, CancellationToken cancellation) => InstallCoreAsync(Path.GetFullPath(executable), cancellation);
    private static async Task InstallCoreAsync(string source, CancellationToken cancellation)
    {
        _ = await ReadQualifiedBindingAsync(cancellation).ConfigureAwait(false);
        if (File.Exists(Path.ChangeExtension(source, ".dll"))) throw new InvalidOperationException("Enable only the published self-contained application, not a development apphost.");
        await using (var legacy = new SingleInstance())
            if (!legacy.IsOwner) throw new InvalidOperationException("Exit the stopped legacy AppleHome panel before enabling native output.");
        var sid = Sid; var target = InstalledExecutable; var sourceHash = Hash(source); var receipt = ReadReceipt();
        var legacyRun = ReadKnownLegacyRun(source);
        var state = await InspectAsync(target, cancellation).ConfigureAwait(false);
        if (state.Exists && !state.Owned) throw new InvalidOperationException("The startup task was changed by the user; it was preserved.");
        var directory = Path.GetDirectoryName(target)!; Directory.CreateDirectory(directory);
        using var installLock = new FileStream(Path.Combine(directory, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(target) && Hash(target) != sourceHash)
        {
            if (await AgentPresentAsync(cancellation).ConfigureAwait(false)) throw new InvalidOperationException("Disable the running native agent before updating its executable.");
            if (!NativeStartupPolicy.OwnsInstalledBinary(receipt, sid, target, Hash(target))) throw new InvalidOperationException("The installed executable was changed or has no ownership proof; it was preserved.");
        }
        if (!File.Exists(target) || Hash(target) != sourceHash)
        {
            var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(source, temp); if (Hash(temp) != sourceHash) throw new IOException("The copied application hash does not match its source.");
                cancellation.ThrowIfCancellationRequested(); File.Move(temp, target, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        await using (var recovery = new AudioService(Path.Combine(AppPaths.DataDirectory, "local-mute.json")))
            await recovery.RecoverPendingMuteAsync().ConfigureAwait(false);
        var registered = await OnSchedulerAsync(folder =>
        {
            dynamic? existing = FindTask(folder, TaskName(sid));
            if (existing is not null)
            {
                try
                {
                    var xml = (string)existing.Xml;
                    if (!NativeStartupPolicy.OwnsTask(xml, receipt, sid, target)) throw new InvalidOperationException("Startup task changed during enable; its current contents were preserved.");
                    return xml;
                }
                finally { ReleaseCom(existing); }
            }
            dynamic task = folder.RegisterTask(TaskName(sid), NativeStartupPolicy.TaskXml(sid, target), 6, sid, null, 3, null);
            try { return (string)task.Xml; }
            finally { ReleaseCom(task); }
        }, cancellation).ConfigureAwait(false);
        try
        {
            await SaveReceiptAsync(NativeStartupPolicy.Receipt(registered, sid, target, sourceHash), cancellation).ConfigureAwait(false);
            if (legacyRun is not null)
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key?.GetValue(AppPaths.Name) is { } current && !Equals(current, legacyRun)) throw new InvalidOperationException("The legacy login value changed during enable; it was preserved.");
                key?.DeleteValue(AppPaths.Name, false);
            }
        }
        catch
        {
            if (!state.Exists) await OnSchedulerAsync(folder => { dynamic? created = FindTask(folder, TaskName(sid)); try { if (created is not null && (string)created.Xml == registered) folder.DeleteTask(TaskName(sid), 0); } finally { ReleaseCom(created); } return true; }, default).ConfigureAwait(false);
            throw;
        }
        await NativeStartupPolicy.EnsureRunningAsync(QueryReadyAsync, async () =>
        {
            await OnSchedulerAsync(folder =>
            {
                dynamic task = FindTask(folder, TaskName(sid)) ?? throw new IOException("The owned startup task disappeared.");
                try
                {
                    if (!NativeStartupPolicy.OwnsTask((string)task.Xml, ReadReceipt(), sid, target)) throw new InvalidOperationException("The startup task changed before run; it was preserved.");
                    dynamic running = task.Run(null); ReleaseCom(running); return true;
                }
                finally { ReleaseCom(task); }
            }, cancellation).ConfigureAwait(false);
        }, cancellation).ConfigureAwait(false);
    }
    private static string? ReadKnownLegacyRun(string source)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
        if (key?.GetValue(AppPaths.Name) is not string value) return null;
        var known = NativeStartupPolicy.OwnsLegacyRun(value, source);
        const string baselineHash = "992c36249a4b91c014c71b78959ba82d561bb4f958c8eefed49998d995e44ca5";
        if (!known && value.StartsWith('"') && value.EndsWith("\" --startup", StringComparison.Ordinal))
        {
            var path = value[1..^11];
            known = File.Exists(path) && Hash(path) == baselineHash && NativeStartupPolicy.OwnsLegacyRun(value, path);
        }
        if (!known) throw new InvalidOperationException("The legacy login value was changed; it was preserved. Review it before enabling a second startup path.");
        return value;
    }
    internal static async Task RemoveOwnedAsync(string executable, CancellationToken cancellation)
    {
        var target = Path.GetFullPath(executable); var sid = Sid; var receipt = ReadReceipt();
        await OnSchedulerAsync(folder =>
        {
            dynamic? task = FindTask(folder, TaskName(sid));
            if (task is null) return true;
            try
            {
                if (!NativeStartupPolicy.OwnsTask((string)task.Xml, receipt, sid, target)) throw new InvalidOperationException("The startup task was changed by the user; it was preserved.");
                folder.DeleteTask(TaskName(sid), 0); return true;
            }
            finally { ReleaseCom(task); }
        }, cancellation).ConfigureAwait(false);
        await using (var instance = new SingleInstance("Native"))
            if (!instance.IsOwner) await instance.NotifyExistingAsync(2).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { while (await AgentPresentAsync(timeout.Token).ConfigureAwait(false)) await Task.Delay(100, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException("Native agent did not release its resources after stop."); }
        // Keep the receipt as ownership proof for the installed file; no user data is removed.
    }
    internal static Task<(bool Exists, bool Owned)> InspectAsync(string executable, CancellationToken cancellation)
    {
        var sid = Sid; var receipt = ReadReceipt();
        return OnSchedulerAsync(folder =>
        {
            dynamic? task = FindTask(folder, TaskName(sid));
            try { return task is null ? (false, false) : (true, NativeStartupPolicy.OwnsTask((string)task.Xml, receipt, sid, executable)); }
            finally { ReleaseCom(task); }
        }, cancellation);
    }
    internal static Task<bool> ValidateSchemaAsync(CancellationToken cancellation) => WindowsNativeBinding.OnMtaAsync(() =>
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new PlatformNotSupportedException("Windows Task Scheduler is unavailable."))!;
        dynamic? task = null, principal = null, settings = null;
        try { service.Connect(); task = service.NewTask(0); task.XmlText = NativeStartupPolicy.TaskXml(Sid, InstalledExecutable); principal = task.Principal; settings = task.Settings; return (int)principal.RunLevel == 0 && (int)settings.RestartCount == 3; }
        finally { ReleaseCom(settings); ReleaseCom(principal); ReleaseCom(task); ReleaseCom(service); }
    }, cancellation);
    private static Task<T> OnSchedulerAsync<T>(Func<dynamic, T> action, CancellationToken cancellation) => WindowsNativeBinding.OnMtaAsync<T>(() =>
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new PlatformNotSupportedException("Windows Task Scheduler is unavailable."))!;
        dynamic? folder = null;
        try { service.Connect(); folder = service.GetFolder(@"\"); return action(folder); }
        finally { ReleaseCom(folder); ReleaseCom(service); }
    }, cancellation);
    private static dynamic? FindTask(dynamic folder, string name)
    {
        try { return folder.GetTask(name); }
        catch (COMException error) when (unchecked((uint)error.HResult) == 0x80070002) { return null; }
        catch (FileNotFoundException) { return null; }
    }
    private static void ReleaseCom(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}

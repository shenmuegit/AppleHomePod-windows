using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirFlash.Core;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
namespace AirFlash.App.Services;

public sealed class WindowsNativeBinding
{
    internal static string BindingPath => Path.Combine(AppPaths.DataDirectory, "native-output.json");
    internal static string PreflightPath => Path.Combine(AppPaths.DataDirectory, "native-output-preflight.json");
    private sealed record Adapter(string Id, string[] HardwareIds, string Provider, string Version, string Hash);
    private sealed record InstallSnapshot(string Machine, DateTimeOffset CreatedUtc, string[] AdapterIds);
    internal static bool RebootedSincePreinstall()
    {
        using var file = File.OpenRead(PreflightPath);
        if (file.Length > 65536) throw new InvalidDataException("The pre-install snapshot exceeds its size limit.");
        var snapshot = JsonSerializer.Deserialize<InstallSnapshot>(file, new JsonSerializerOptions(AppSettings.JsonOptions) { RespectRequiredConstructorParameters = true })
            ?? throw new InvalidDataException("The pre-install proof is missing.");
        if (snapshot.Machine != MachineStamp() || snapshot.AdapterIds is null || snapshot.AdapterIds.Length != 0) return false;
        // shortcut: boot UTC uses uptime; revalidate the proof after a major system-clock correction.
        var boot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        return NativeStartupPolicy.RebootedAfter(snapshot.CreatedUtc, boot);
    }

    public Task<IReadOnlyList<EndpointIdentity>> SnapshotAsync(CancellationToken cancellation)
        => OnMtaAsync<IReadOnlyList<EndpointIdentity>>(() => ReadEndpoints(ReadAdapters(), cancellation), cancellation);

    internal Task<(IReadOnlyList<EndpointIdentity> Endpoints, string[] CableAdapterIds, string?[] Defaults)> PreflightAsync(CancellationToken cancellation)
        => OnMtaAsync(() => {
            var adapters = ReadAdapters();
            var endpoints = ReadEndpoints(adapters, cancellation);
            var cableIds = adapters.Where(a => a.HardwareIds.Contains(NativeBindingPolicy.HardwareId, StringComparer.OrdinalIgnoreCase)).Select(a => a.Id).ToArray();
            using var enumerator = new MMDeviceEnumerator();
            var defaults = new[] { Role.Console, Role.Multimedia, Role.Communications }.Select(role => {
                try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role); return device.ID; }
                catch (COMException) { return null; }
            }).ToArray();
            if (cableIds.Length == 0 && !File.Exists(PreflightPath))
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                var snapshot = new InstallSnapshot(MachineStamp(), DateTimeOffset.UtcNow, cableIds);
                // Never replace the pre-install baseline with a post-install snapshot.
                using var file = new FileStream(PreflightPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, snapshot, AppSettings.JsonOptions);
            }
            return ((IReadOnlyList<EndpointIdentity>)endpoints, cableIds, defaults);
        }, cancellation);

    public Task<NativeBinding> BindAsync(string receiverId, string endpointId, string assignedName, CancellationToken cancellation)
        => OnMtaAsync(() => {
            using var snapshotFile = File.OpenRead(PreflightPath);
            if (snapshotFile.Length > 65536) throw new InvalidDataException("The pre-install snapshot exceeds its size limit.");
            var snapshot = JsonSerializer.Deserialize<InstallSnapshot>(snapshotFile,
                new JsonSerializerOptions(AppSettings.JsonOptions) { RespectRequiredConstructorParameters = true })
                ?? throw new InvalidDataException("A pre-install snapshot is required.");
            if (snapshot.Machine != MachineStamp() || snapshot.AdapterIds is null || snapshot.AdapterIds.Length != 0)
                throw new InvalidDataException("The pre-install snapshot is not for this machine or already contained VB-CABLE.");
            var endpoints = ReadEndpoints(ReadAdapters(), cancellation);
            var candidate = endpoints.SingleOrDefault(e => e.EndpointId == endpointId)
                ?? throw new InvalidOperationException("The selected endpoint is unavailable.");
            if (!NativeBindingPolicy.CanClaim(candidate, new HashSet<string>(snapshot.AdapterIds, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The endpoint is not a new, verified ordinary VB-CABLE playback output.");
            if (endpoints.Count(e => NativeBindingPolicy.CanClaim(e, new HashSet<string>())) != 1)
                throw new InvalidOperationException("The verified output is ambiguous; do not guess a device.");
            var store = new NativeBindingStore(BindingPath);
            var binding = NativeBindingPolicy.PrepareBinding(candidate, receiverId, assignedName, store.Load(),
                new HashSet<string>(snapshot.AdapterIds, StringComparer.OrdinalIgnoreCase));
            store.SaveAsync(binding, cancellation).GetAwaiter().GetResult();
            return binding;
        }, cancellation);

    public Task RestoreAsync(NativeBinding binding, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (NativeBindingPolicy.ValidateStored(binding) is { } error) throw new InvalidDataException(error);
        // System labels are chosen by the user; unbinding never rewrites them.
        return Task.CompletedTask;
    }

    internal static Task<T> OnMtaAsync<T>(Func<T> operation, CancellationToken cancellation)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try { cancellation.ThrowIfCancellationRequested(); result.SetResult(operation()); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { result.SetCanceled(cancellation); }
            catch (Exception error) { result.SetException(error); }
        }) { IsBackground = true, Name = "AppleHome native output setup" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return result.Task;
    }

    private static List<EndpointIdentity> ReadEndpoints(IReadOnlyList<Adapter> adapters, CancellationToken cancellation)
    {
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = new List<EndpointIdentity>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
        using (device)
        {
            cancellation.ThrowIfCancellationRequested();
            var name = ReadProperty(device, PropertyKeys.PKEY_Device_FriendlyName) as string ?? "Unknown";
            var label = ReadProperty(device, PropertyKeys.PKEY_Device_DeviceDesc) as string;
            var pinName = "";
            var instanceId = ReadProperty(device, PropertyKeys.PKEY_Device_InstanceId) as string ?? "";
            var controller = ReadProperty(device, PropertyKeys.PKEY_Device_ControllerDeviceId) as string ?? "";
            var adapter = FindAdapter(instanceId, controller, adapters);
            try
            {
                if (device.State == DeviceState.Active && (adapter is null
                    || adapter.HardwareIds.Contains(NativeBindingPolicy.HardwareId, StringComparer.OrdinalIgnoreCase))
                    && device.DeviceTopology.ConnectorCount > 0)
                {
                    var connector = device.DeviceTopology.GetConnector(0);
                    if (connector.IsConnected)
                    {
                        pinName = connector.ConnectedTo.Part.Name ?? "";
                        if (adapter is null)
                        {
                            using var connected = enumerator.GetDevice(connector.ConnectedToDeviceId);
                            adapter = FindAdapter(ReadProperty(connected, PropertyKeys.PKEY_Device_InstanceId) as string ?? "", "", adapters);
                        }
                    }
                }
            }
            catch (Exception error) when (error is COMException or FileNotFoundException)
            { /* An unavailable topology cannot qualify a pin; PnP still detects the adapter. */ }
            var format = (MixFormat: "", Channels: 0);
            if (ReadProperty(device, PropertyKeys.PKEY_AudioEngine_DeviceFormat) is byte[] blob)
                try { format = NativeBindingPolicy.ReadMixFormat(blob); }
                catch (InvalidDataException) { /* Unsupported formats remain visible but cannot be claimed. */ }
            var hardware = adapter?.HardwareIds.FirstOrDefault(id => id.Equals(NativeBindingPolicy.HardwareId, StringComparison.OrdinalIgnoreCase))
                ?? adapter?.HardwareIds.FirstOrDefault() ?? "";
            endpoints.Add(new(device.ID, adapter?.Id ?? "", hardware, adapter?.Provider ?? "", adapter?.Version ?? "", adapter?.Hash ?? "",
                pinName, name, format.MixFormat, format.Channels, device.DataFlow == DataFlow.Render ? AudioEndpointKind.Playback : AudioEndpointKind.Recording,
                device.State == DeviceState.Active, label));
        }
        if (endpoints.Select(e => e.EndpointId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != endpoints.Count)
            throw new InvalidDataException("The endpoint enumeration returned duplicate device identities.");
        return endpoints;
    }

    private static Adapter? FindAdapter(string instanceId, string controller, IReadOnlyList<Adapter> adapters)
    {
        var direct = adapters.FirstOrDefault(a => a.Id.Equals(controller, StringComparison.OrdinalIgnoreCase)
            || a.Id.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
        if (direct is not null) return direct;
        if (CM_Locate_DevNode(out var node, instanceId, 0) != 0) return null;
        for (var depth = 0; depth < 8 && CM_Get_Parent(out node, node, 0) == 0; depth++)
        {
            var id = new StringBuilder(4096);
            if (CM_Get_Device_ID(node, id, id.Capacity, 0) != 0) break;
            var parent = adapters.FirstOrDefault(a => a.Id.Equals(id.ToString(), StringComparison.OrdinalIgnoreCase));
            if (parent is not null) return parent;
        }
        return null;
    }

    private static object? ReadProperty(MMDevice device, PropertyKey key)
    {
        var store = device.Properties;
        for (var i = 0; i < store.Count; i++)
        {
            var current = store.Get(i);
            if (current.formatId != key.formatId || current.propertyId != key.propertyId) continue;
            var value = store.GetValue(i);
            try { return value.Value; }
            finally { _ = PropVariantClear(ref value); }
        }
        return null;
    }

    private static List<Adapter> ReadAdapters()
    {
        var mediaClass = new Guid("4d36e96c-e325-11ce-bfc1-08002be10318");
        var set = SetupDiGetClassDevs(ref mediaClass, null, IntPtr.Zero, 0);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var adapters = new List<Adapter>();
            for (uint index = 0; ; index++)
            {
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error);
                }
                var id = new StringBuilder(4096);
                if (!SetupDiGetDeviceInstanceId(set, ref info, id, id.Capacity, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                var hardware = RegistryProperty(set, ref info, 1).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                var driverKey = RegistryProperty(set, ref info, 9).TrimEnd('\0');
                var service = RegistryProperty(set, ref info, 4).TrimEnd('\0');
                using var driver = driverKey.Length == 0 ? null : Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey, false);
                using var serviceKey = service.Length == 0 ? null : Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service, false);
                var hash = DriverHash(serviceKey?.GetValue("ImagePath") as string);
                adapters.Add(new(id.ToString(), hardware, driver?.GetValue("ProviderName") as string ?? "",
                    driver?.GetValue("DriverVersion") as string ?? "", hash));
            }
            return adapters;
        }
        finally { _ = SetupDiDestroyDeviceInfoList(set); }
    }

    private static string RegistryProperty(IntPtr set, ref DeviceInfo info, uint property)
    {
        var buffer = new byte[32768];
        if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out _, buffer, buffer.Length, out var size))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 13) return "";
            throw new Win32Exception(error);
        }
        if (size > buffer.Length || size % 2 != 0) throw new InvalidDataException("Invalid PnP property size.");
        return Encoding.Unicode.GetString(buffer, 0, size);
    }

    private static string DriverHash(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return "";
        var path = Environment.ExpandEnvironmentVariables(imagePath.Trim('"'));
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(windows, path[12..]);
        if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        if (!Path.IsPathRooted(path)) path = Path.Combine(windows, path);
        path = Path.GetFullPath(path);
        if (!Path.GetExtension(path).Equals(".sys", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return "";
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    private static string MachineStamp()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false);
        var machineId = key?.GetValue("MachineGuid") as string ?? throw new InvalidOperationException("The current machine identity is unavailable.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(machineId))).ToLowerInvariant();
    }

    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref DeviceInfo info, StringBuilder id, int size, out int required);
    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceRegistryPropertyW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceInfo info, uint property, out uint kind, byte[] data, int size, out int required);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNode(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_ID(uint node, StringBuilder id, int size, uint flags);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}

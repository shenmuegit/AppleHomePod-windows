using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
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
            RequireAdministrator();
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
            var previous = store.Load();
            if (previous is not null && (previous.ReceiverId != receiverId || previous.Endpoint.EndpointId != endpointId
                || previous.AssignedName != assignedName || previous.Endpoint.AdapterInstanceId != candidate.AdapterInstanceId
                || previous.Endpoint.DriverSha256 != candidate.DriverSha256))
                throw new InvalidOperationException("An existing binding must be explicitly removed before it can be replaced.");
            var binding = previous ?? new NativeBinding(1, receiverId, candidate, candidate.FriendlyName, assignedName, null);
            if (candidate.FriendlyName != binding.OriginalName && candidate.FriendlyName != binding.AssignedName)
                throw new InvalidOperationException("The output was renamed by the user. Its name has been preserved.");
            store.SaveAsync(binding, cancellation).GetAwaiter().GetResult();
            cancellation.ThrowIfCancellationRequested();
            try { SetName(candidate.EndpointId, binding.AssignedName); }
            catch (Exception nameError)
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    using var fresh = enumerator.GetDevice(candidate.EndpointId);
                    if (NativeBindingPolicy.MayRestoreName(ReadProperty(fresh, PropertyKeys.PKEY_Device_FriendlyName) as string ?? "", binding.AssignedName))
                        SetName(candidate.EndpointId, binding.OriginalName);
                }
                catch (Exception recoveryError) { throw new AggregateException("Endpoint naming failed; recovery is pending in the saved binding.", nameError, recoveryError); }
                throw;
            }
            return binding;
        }, cancellation);

    public Task RestoreAsync(NativeBinding binding, CancellationToken cancellation)
        => OnMtaAsync(() => {
            RequireAdministrator();
            if (NativeBindingPolicy.ValidateStored(binding) is { } error) throw new InvalidDataException(error);
            var live = ReadEndpoints(ReadAdapters(), cancellation).SingleOrDefault(e => e.EndpointId == binding.Endpoint.EndpointId);
            if (live is null || !NativeBindingPolicy.CanClaim(live, new HashSet<string>())
                || live.AdapterInstanceId != binding.Endpoint.AdapterInstanceId)
                throw new InvalidOperationException("The original output cannot be verified. No other endpoint was changed.");
            if (NativeBindingPolicy.MayRestoreName(live.FriendlyName, binding.AssignedName))
                SetName(live.EndpointId, binding.OriginalName);
            return true;
        }, cancellation);

    private static void SetName(string endpointId, string name)
    {
        using (var enumerator = new MMDeviceEnumerator())
        using (var device = enumerator.GetDevice(endpointId))
        {
            device.GetPropertyInformation(StorageAccessMode.ReadWrite);
            var value = new PropVariant { vt = (short)VarEnum.VT_LPWSTR, pointerValue = Marshal.StringToCoTaskMemUni(name) };
            try { device.Properties.SetValue(PropertyKeys.PKEY_Device_FriendlyName, value); device.Properties.Commit(); }
            finally { Marshal.FreeCoTaskMem(value.pointerValue); }
        }
        using var freshEnumerator = new MMDeviceEnumerator();
        using var fresh = freshEnumerator.GetDevice(endpointId);
        if (!string.Equals(ReadProperty(fresh, PropertyKeys.PKEY_Device_FriendlyName) as string, name, StringComparison.Ordinal))
            throw new IOException("Windows did not retain the endpoint name. The pending binding keeps its original name for recovery.");
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
            var pinName = ReadProperty(device, PropertyKeys.PKEY_Device_DeviceDesc) as string ?? "";
            var instanceId = ReadProperty(device, PropertyKeys.PKEY_Device_InstanceId) as string ?? "";
            var controller = ReadProperty(device, PropertyKeys.PKEY_Device_ControllerDeviceId) as string ?? "";
            var adapter = FindAdapter(instanceId, controller, adapters);
            if (adapter is null)
            {
                try {
                    var connectedId = device.DeviceTopology.GetConnector(0).ConnectedToDeviceId;
                    using var connected = enumerator.GetDevice(connectedId);
                    adapter = FindAdapter(ReadProperty(connected, PropertyKeys.PKEY_Device_InstanceId) as string ?? "", "", adapters);
                }
                catch (COMException) { /* A disabled endpoint may have no active topology. PnP inventory still detects its adapter. */ }
            }
            var format = (MixFormat: "", Channels: 0);
            if (ReadProperty(device, PropertyKeys.PKEY_AudioEngine_DeviceFormat) is byte[] blob)
                try { format = NativeBindingPolicy.ReadMixFormat(blob); }
                catch (InvalidDataException) { /* Unsupported formats remain visible but cannot be claimed. */ }
            var hardware = adapter?.HardwareIds.FirstOrDefault(id => id.Equals(NativeBindingPolicy.HardwareId, StringComparison.OrdinalIgnoreCase))
                ?? adapter?.HardwareIds.FirstOrDefault() ?? "";
            endpoints.Add(new(device.ID, adapter?.Id ?? "", hardware, adapter?.Provider ?? "", adapter?.Version ?? "", adapter?.Hash ?? "",
                pinName, name, format.MixFormat, format.Channels, device.DataFlow == DataFlow.Render ? AudioEndpointKind.Playback : AudioEndpointKind.Recording,
                device.State == DeviceState.Active));
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

    private static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Run only the explicit endpoint naming maintenance command as administrator. The background agent stays unprivileged.");
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

using System.Runtime.InteropServices;
using AirFlash.Core;
using NAudio.CoreAudioApi;
namespace AirFlash.App.Services;

internal sealed class WindowsNativeSetupOutput(EndpointIdentity endpoint) : INativeSetupOutput
{
    private static readonly Guid NameSet = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    public string ReadName() => Property(null);
    public void WriteName(string name) { Validate(); _ = Property(name); }
    public string? ReadDefault(int role)
    {
        CheckRole(role);
        using var enumerator = new MMDeviceEnumerator();
        try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, (Role)role); return device.ID; }
        catch (COMException) { return null; }
    }
    public void WriteDefault(int role, string endpointId)
    {
        CheckRole(role);
        if (string.Equals(endpointId, endpoint.EndpointId, StringComparison.OrdinalIgnoreCase)) Validate();
        using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(endpointId);
        if (device.DataFlow != DataFlow.Render || device.State != DeviceState.Active)
            throw new InvalidOperationException("Only an active playback endpoint can be selected or restored.");
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try { Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(endpointId, role)); }
        finally { Marshal.FinalReleaseComObject(policy); }
    }
    public (float Scalar, bool Muted) ReadVolume()
    {
        using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(endpoint.EndpointId);
        return (device.AudioEndpointVolume.MasterVolumeLevelScalar, device.AudioEndpointVolume.Mute);
    }
    public void WriteVolume(float scalar, bool muted)
    {
        Validate();
        if (!float.IsFinite(scalar) || scalar is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(scalar));
        using var enumerator = new MMDeviceEnumerator(); using var device = enumerator.GetDevice(endpoint.EndpointId);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = scalar; device.AudioEndpointVolume.Mute = muted;
    }
    private void Validate()
    {
        var live = new WindowsNativeBinding().SnapshotAsync(CancellationToken.None).GetAwaiter().GetResult()
            .SingleOrDefault(value => value.EndpointId == endpoint.EndpointId);
        if (live is null || !NativeBindingPolicy.CanClaim(live, new HashSet<string>())
            || live.AdapterInstanceId != endpoint.AdapterInstanceId || live.DriverSha256 != endpoint.DriverSha256
            || live.PinName != endpoint.PinName || live.MixFormat != endpoint.MixFormat)
            throw new IOException("The verified ordinary playback output changed during automatic setup.");
    }
    private static void CheckRole(int role)
    { if (role is not (0 or 1)) throw new ArgumentOutOfRangeException(nameof(role)); }
    private string Property(string? text)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        var key = Marshal.AllocCoTaskMem(20); var value = Marshal.AllocCoTaskMem(24);
        Marshal.StructureToPtr(new PolicyKey { Set = NameSet, Pid = 2 }, key, false);
        Marshal.StructureToPtr(new PolicyValue { Type = text is null ? (ushort)0 : (ushort)31,
            Text = text is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUni(text) }, value, false);
        try
        {
            // shortcut: Windows' internal policy API is qualified on Win10; recheck ABI before qualifying another Windows build.
            var hr = text is null ? policy.GetPropertyValue(endpoint.EndpointId, false, key, value)
                : policy.SetPropertyValue(endpoint.EndpointId, false, key, value);
            Marshal.ThrowExceptionForHR(hr);
            var result = Marshal.PtrToStructure<PolicyValue>(value);
            if (result.Type != 31) throw new InvalidDataException("Windows returned an invalid output label.");
            return Marshal.PtrToStringUni(result.Text) ?? "";
        }
        finally
        {
            _ = PropVariantClear(value); Marshal.FreeCoTaskMem(value); Marshal.FreeCoTaskMem(key);
            Marshal.FinalReleaseComObject(policy);
        }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct PolicyKey { public Guid Set; public int Pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PolicyValue
    {
        [FieldOffset(0)] public ushort Type; [FieldOffset(2)] public ushort Reserved1;
        [FieldOffset(4)] public ushort Reserved2; [FieldOffset(6)] public ushort Reserved3;
        [FieldOffset(8)] public IntPtr Text; [FieldOffset(16)] public ulong Extra;
    }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(IntPtr value);
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] private class PolicyConfigClient { }
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, bool def, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a, IntPtr b);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, bool def, IntPtr a, IntPtr b);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr a);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, bool visible);
    }
}

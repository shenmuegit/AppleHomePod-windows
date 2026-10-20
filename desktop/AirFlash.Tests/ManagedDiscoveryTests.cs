using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using AirFlash.App.Services;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class ManagedDiscoveryTests
{
    private const string Type = "_airplay._tcp.local", Name = "Office._airplay._tcp.local";
    private static readonly ManagedService Service = new(new(Name, Type, "192.0.2.10", 7000,
        new Dictionary<string, string> { ["deviceid"] = "AA:BB:CC:DD:EE:FF", ["model"] = "AudioAccessory5,1", ["osvers"] = "27.0", ["igl"] = "1" }), 120);
    [Fact]
    public void NativeBrowseIgnoresAdditionalPointersForAnotherServiceType()
    {
        var native = new Native(9506) { Records = [new(Name, 120), new("AABBCCDDEEFF@Office._raop._tcp.local", 120)] };
        var api = new WindowsDnsSdApi(native, new Fallback());
        IReadOnlyList<DnsSdService> result = [];
        using var browse = api.Browse(Type, 17, (_, records) => result = records, _ => { });
        native.Emit(); Assert.Equal(Name, Assert.Single(result).Instance);
    }
    [Fact]
    public void NoNetworkFromWindowsAutomaticallyUsesManagedBrowseAndResolve()
    {
        var native = new Native(1222); var fallback = new Fallback();
        var api = new WindowsDnsSdApi(native, fallback);
        using var browse = api.Browse(Type, 17, (_, _) => { }, _ => { });
        using var resolve = api.Resolve(Name, Type, 17, (_, _) => { }, _ => { });
        using var second = api.Browse("_raop._tcp.local", 17, (_, _) => { }, _ => { });
        Assert.Equal(1, native.Starts);
        Assert.Equal(["browse:17", "resolve:17", "browse:17"], fallback.Calls);
    }
    [Theory]
    [InlineData(5)]
    [InlineData(87)]
    public void PermissionOrInvalidRequestErrorsAreNotRetriedWithAnotherProvider(uint error)
    {
        var fallback = new Fallback(); var api = new WindowsDnsSdApi(new Native(error), fallback);
        Assert.Throws<Win32Exception>(() => api.Browse(Type, 17, (_, _) => { }, _ => { })); Assert.Empty(fallback.Calls);
    }
    [Fact]
    public void WorkingWindowsDiscoveryKeepsItsNativeLifecycle()
    {
        var native = new Native(9506); var fallback = new Fallback(); var api = new WindowsDnsSdApi(native, fallback);
        using (api.Browse(Type, 17, (_, _) => { }, _ => { })) Assert.Equal(0, native.Cancels);
        Assert.Equal(1, native.Cancels); Assert.Empty(fallback.Calls);
    }
    [Fact]
    public void PacketCodecPreservesIdentityFirmwareAndGoodbye()
    {
        var wire = Packet(Type, "192.0.2.10", 7000, 120); var original = (byte[])wire.Clone();
        var result = Assert.Single(MdnsPacket.Read(wire, Type));
        Assert.Equal(original, wire); Assert.Equal((uint)120, result.Ttl); Assert.Equal(Name, result.Record.Instance);
        var receiver = Assert.Single(ReceiverAggregator.Build([result.Record]));
        Assert.Equal("aabbccddeeff", ReceiverIdentity.Normalize(receiver.Id)); Assert.True(receiver.PrefersNtpAlac);
        Assert.Equal((uint)0, Assert.Single(MdnsPacket.Read(Packet(Type, "192.0.2.10", 7000, 0, details: false), Type)).Ttl);
    }
    [Theory]
    [InlineData("_other._tcp.local", "192.0.2.10", 7000)]
    [InlineData("_airplay._tcp.local", "::1", 7000)]
    [InlineData("_airplay._tcp.local", "192.0.2.10", 0)]
    public void InvalidOrUnrelatedPacketsAreNotPublished(string type, string ip, int port)
        => Assert.Empty(MdnsPacket.Read(Packet(type, ip, (ushort)port, 120), Type));
    [Fact]
    public void MalformedNamesCountsAndTruncatedPacketsCannotEnterDiscovery()
    {
        byte[] cycle = [0, 0, 128, 0, 0, 0, 0, 1, 0, 0, 0, 0, 192, 12, 0, 12, 0, 1, 0, 0, 0, 1, 0, 2, 192, 12];
        Assert.Empty(MdnsPacket.Read(cycle, Type)); Assert.Empty(MdnsPacket.Read(new byte[9001], Type));
        var excessive = (byte[])cycle.Clone(); excessive[6] = 255; excessive[7] = 255;
        Assert.Empty(MdnsPacket.Read(excessive, Type));
        Assert.Empty(MdnsPacket.Read(Packet(Type, "192.0.2.10", 7000, 120)[..20], Type));
        Assert.Equal((byte)128, MdnsPacket.Question(Type)[^2]);
    }
    [Fact]
    public async Task ResolveUsesBrowseMetadataWithoutAnotherScanAndTracksChanges()
    {
        Action<ManagedService>? deliver = null; var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ManagedDnsSdApi(async (_, _, callback, token) => { calls++; deliver = callback; callback(Service); entered.SetResult(); await Task.Delay(Timeout.Infinite, token); });
        using var browse = api.Browse(Type, 17, (_, _) => { }, _ => { }); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var first = new TaskCompletionSource<ServiceRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource<ServiceRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resolve = api.Resolve(Name, Type, 17, (_, value) => { if (value!.Txt.ContainsKey("tsid")) changed.TrySetResult(value); else first.TrySetResult(value); }, _ => { });
        Assert.Equal(Service.Record, await first.Task.WaitAsync(TimeSpan.FromSeconds(2))); Assert.Equal(1, calls);
        deliver!(Service with { Record = Service.Record with { Txt = new Dictionary<string, string>(Service.Record.Txt) { ["tsid"] = "pair" } } });
        Assert.False(Assert.Single(ReceiverAggregator.Build([await changed.Task.WaitAsync(TimeSpan.FromSeconds(2))])).PrefersNtpAlac);
    }
    [Fact]
    public async Task ResolveCanStartItsSourceWhenOnlyNativeResolutionFailed()
    {
        var calls = 0;
        var found = new TaskCompletionSource<ServiceRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ManagedDnsSdApi(async (_, index, callback, token) => {
            Assert.Equal((uint)17, index); Interlocked.Increment(ref calls); callback(Service);
            await Task.Delay(Timeout.Infinite, token);
        });
        using var resolve = api.Resolve(Name, Type, 17, (_, value) => found.TrySetResult(value!), _ => { });
        Assert.Equal(Service.Record, await found.Task.WaitAsync(TimeSpan.FromSeconds(1))); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task TransientScanFailureCanRecoverOnTheSameBrowse()
    {
        var calls = 0; var failures = 0; var found = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ManagedDnsSdApi((_, _, callback, _) => {
            if (Interlocked.Increment(ref calls) == 1) throw new IOException("temporary");
            callback(Service); return Task.CompletedTask;
        });
        using var browse = api.Browse(Type, 17, (_, _) => found.TrySetResult(), _ => failures++);
        await found.Task.WaitAsync(TimeSpan.FromSeconds(4)); Assert.Equal(0, failures); Assert.True(calls >= 2);
    }
    [Fact]
    public async Task DisposingManagedBrowseCancelsScanAndSuppressesLateCallbacks()
    {
        Action<ManagedService>? deliver = null;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new ManagedDnsSdApi(async (_, index, callback, cancellation) => {
            Assert.Equal((uint)17, index); deliver = callback; entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, cancellation); } finally { stopped.SetResult(); }
        });
        var replies = 0; var failures = 0;
        var browse = api.Browse(Type, 17, (_, _) => replies++, _ => failures++);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); deliver!(Service); Assert.Equal(1, replies);
        browse.Dispose(); await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2)); deliver(Service);
        browse.Dispose(); Assert.Equal(1, replies); Assert.Equal(0, failures);
    }
    private static byte[] Packet(string type, string ip, ushort port, uint ttl, bool details = true)
    {
        var name = "Office." + type; var bytes = new List<byte>([0, 0, 132, 0, 0, 0, 0, (byte)(details ? 4 : 1), 0, 0, 0, 0]);
        byte[] U16(ushort value) { var data = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(data, value); return data; }
        byte[] DnsName(string value) => value.Split('.').SelectMany(label => new[] { (byte)Encoding.UTF8.GetByteCount(label) }.Concat(Encoding.UTF8.GetBytes(label))).Append((byte)0).ToArray();
        void Record(string owner, ushort kind, byte[] data)
        {
            bytes.AddRange(DnsName(owner)); bytes.AddRange(U16(kind)); bytes.AddRange(U16(1));
            var time = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(time, ttl); bytes.AddRange(time);
            bytes.AddRange(U16((ushort)data.Length)); bytes.AddRange(data);
        }
        Record(type, 12, DnsName(name));
        if (details)
        {
            Record(name, 33, new byte[4].Concat(U16(port)).Concat(DnsName("speaker.local")).ToArray());
            Record(name, 16, Service.Record.Txt.SelectMany(pair => { var value = Encoding.UTF8.GetBytes(pair.Key + "=" + pair.Value); return new[] { (byte)value.Length }.Concat(value); }).ToArray());
            var address = IPAddress.Parse(ip); Record("speaker.local", (ushort)(address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 1 : 28), address.GetAddressBytes());
        }
        return bytes.ToArray();
    }
    private sealed class Native(uint status) : IWindowsDnsApi
    {
        public int Starts, Cancels; public IReadOnlyList<DnsSdService> Records = []; private DnsSdRequest _request;
        public uint Start(ref DnsSdRequest request, IntPtr cancel, bool resolve) { _request = request; Starts++; Marshal.WriteIntPtr(cancel, new(123)); return status; }
        public void Emit() => Marshal.GetDelegateForFunctionPointer<DnsSdCallback>(_request.Callback)(0, _request.Context, new(1));
        public uint Cancel(IntPtr cancel, bool resolve) { Cancels++; return 0; }
        public IReadOnlyList<DnsSdService> ReadBrowse(IntPtr result) => Records;
        public ServiceRecord? ReadResolve(string name, string type, IntPtr result) => null;
        public void FreeResult(IntPtr result, bool resolve) { }
    }
    private sealed class Fallback : IDnsSdApi
    {
        public readonly List<string> Calls = [];
        public IDisposable Browse(string type, uint index, Action<uint, IReadOnlyList<DnsSdService>> callback, Action<string> failed) { Calls.Add("browse:" + index); return new CancellationTokenSource(); }
        public IDisposable Resolve(string name, string type, uint index, Action<uint, ServiceRecord?> callback, Action<string> failed) { Calls.Add("resolve:" + index); return new CancellationTokenSource(); }
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AirFlash.Core;
namespace AirFlash.App.Services;

internal sealed record ManagedService(ServiceRecord Record, uint Ttl);

// Full DNS-SD answers are reused by resolvers; they do not send a second query.
internal sealed class ManagedDnsSdApi : IDnsSdApi
{
    private sealed record Cached(long Version, ServiceRecord Record, DateTimeOffset Expires);
    private readonly ConcurrentDictionary<string, Cached> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, uint, Action<ManagedService>, CancellationToken, Task> _scan;
    private long _version;
    internal ManagedDnsSdApi(Func<string, uint, Action<ManagedService>, CancellationToken, Task>? scan = null) => _scan = scan ?? ScanAsync;
    private static string Key(uint index, string type, string instance) => index + "|" + type + "|" + instance;
    public IDisposable Browse(string type, uint index, Action<uint, IReadOnlyList<DnsSdService>> callback, Action<string> failed)
    {
        var sourceKey = Key(index, type, "");
        _sources.AddOrUpdate(sourceKey, 1, (_, count) => count + 1);
        return new Query(token => _scan(type, index, service => {
            if (token.IsCancellationRequested) return;
            var key = Key(index, type, service.Record.Instance);
            if (service.Ttl == 0) _records.TryRemove(key, out _);
            else
            {
                foreach (var stale in _records.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow)) _records.TryRemove(stale.Key, out _);
                if (_records.Count >= 256 && !_records.ContainsKey(key)) return;
                var value = new Cached(Interlocked.Increment(ref _version), service.Record, DateTimeOffset.UtcNow.AddSeconds(Math.Min(service.Ttl, 86400)));
                _records.AddOrUpdate(key, value, (_, old) => old.Version > value.Version ? old : value);
            }
            callback(0, [new(service.Record.Instance, service.Ttl)]);
        }, token), failed, 2000, () => _sources.AddOrUpdate(sourceKey, 0, (_, count) => Math.Max(0, count - 1)));
    }
    public IDisposable Resolve(string instance, string type, uint index, Action<uint, ServiceRecord?> callback, Action<string> failed)
    {
        var key = Key(index, type, instance);
        var live = _sources.GetValueOrDefault(Key(index, type, "")) > 0
            && _records.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow;
        long version = live ? 0 : Volatile.Read(ref _version);
        var source = live ? null : Browse(type, index, (_, _) => { }, failed);
        return new Query(token => {
            if (!token.IsCancellationRequested && _records.TryGetValue(key, out var value)
                && value.Expires > DateTimeOffset.UtcNow && value.Version > version)
            { version = value.Version; callback(0, value.Record); }
            return Task.CompletedTask;
        }, failed, 100, source is null ? null : source.Dispose);
    }
    private static async Task ScanAsync(string type, uint index, Action<ManagedService> callback, CancellationToken cancellation)
    {
        var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Where(nic =>
            nic.OperationalStatus == OperationalStatus.Up && nic.SupportsMulticast && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
            && nic.GetIPProperties().UnicastAddresses.Any(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            && (index == 0 || nic.GetIPProperties().GetIPv4Properties()?.Index == index)).ToArray();
        if (interfaces.Length == 0) throw new IOException("No connected IPv4 interface is available for HomePod discovery.");
        var errors = new ConcurrentQueue<Exception>(); var completed = 0;
        await Task.WhenAll(interfaces.Select(async nic => {
            try
            {
                var address = nic.GetIPProperties().UnicastAddresses.First(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Address;
                using var socket = new UdpClient(AddressFamily.InterNetwork);
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                socket.Client.Bind(new IPEndPoint(address, 0));
                using var scan = CancellationTokenSource.CreateLinkedTokenSource(cancellation); scan.CancelAfter(TimeSpan.FromSeconds(2));
                await socket.SendAsync(MdnsPacket.Question(type), new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), scan.Token).ConfigureAwait(false);
                try
                {
                    while (true)
                    {
                        var response = await socket.ReceiveAsync(scan.Token).ConfigureAwait(false);
                        foreach (var service in MdnsPacket.Read(response.Buffer, type)) callback(service);
                    }
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && scan.IsCancellationRequested) { }
                Interlocked.Increment(ref completed);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception error) { errors.Enqueue(error); }
        })).ConfigureAwait(false);
        if (completed == 0) throw new IOException("HomePod discovery could not start on any connected interface.", errors.FirstOrDefault());
    }
    private sealed class Query : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _work;
        private readonly Action? _release;
        private int _closed;
        public Query(Func<CancellationToken, Task> scan, Action<string> failed, int interval, Action? release = null)
        {
            _release = release;
            _work = Task.Run(async () => {
                var token = _stop.Token; var failures = 0;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        try { await scan(token).ConfigureAwait(false); failures = 0; }
                        catch (Exception error) when (error is IOException or SocketException or NetworkInformationException)
                        { if (!token.IsCancellationRequested && ++failures == 3) failed(error.Message); }
                        await Task.Delay(failures > 0 ? 200 : interval, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception error) { if (!token.IsCancellationRequested) failed(error.Message); }
            });
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _stop.Cancel(); }
            finally
            {
                try { _release?.Invoke(); }
                finally { _ = _work.ContinueWith(task => { _ = task.Exception; _stop.Dispose(); }, TaskScheduler.Default); }
            }
        }
    }
}

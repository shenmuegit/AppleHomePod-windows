using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using AirFlash.Core;
namespace AirFlash.App.Services;

// dnsapi's packet codec is independent of the failing DNS-SD browsing service.
internal static class MdnsPacket
{
    private sealed record Record(string Name, ushort Type, uint Ttl, string Target, int Port, string Address, IReadOnlyDictionary<string, string> Txt);
    internal static byte[] Question(string type)
    {
        var packet = new List<byte>([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]);
        foreach (var label in type.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("Invalid DNS-SD service type.", nameof(type));
            packet.Add((byte)bytes.Length); packet.AddRange(bytes);
        }
        packet.AddRange([0, 0, 12, 128, 1]); // PTR, requesting a unicast response.
        return packet.ToArray();
    }
    internal static IReadOnlyList<ManagedService> Read(byte[] packet, string type)
    {
        if (packet.Length is < 12 or > 9000 || (packet[2] & 0x80) == 0) return [];
        var data = (byte[])packet.Clone();
        var count = 0;
        for (var offset = 4; offset <= 10; offset += 2)
        {
            var value = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2)); count += value;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
        }
        if (count > 512) return [];
        IntPtr head = IntPtr.Zero;
        try
        {
            if (DnsExtractRecordsFromMessage(data, (ushort)data.Length, out head) != 0) return [];
            var records = new List<Record>(); var visited = new HashSet<IntPtr>();
            for (var item = head; item != IntPtr.Zero && records.Count < 512 && visited.Add(item); item = Marshal.ReadIntPtr(item))
            {
                var name = Text(Marshal.ReadIntPtr(item, 8));
                var kind = unchecked((ushort)Marshal.ReadInt16(item, 16));
                var ttl = unchecked((uint)Marshal.ReadInt32(item, 24));
                var target = ""; var address = ""; var port = 0; var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (kind is 12 or 33) target = Text(Marshal.ReadIntPtr(item, 32));
                if (kind == 33) port = unchecked((ushort)Marshal.ReadInt16(item, 44));
                if (kind == 1)
                {
                    var bytes = new byte[4]; Marshal.Copy(item + 32, bytes, 0, 4); address = new IPAddress(bytes).ToString();
                }
                if (kind == 16)
                {
                    var strings = Marshal.ReadInt32(item, 32);
                    if (strings is < 0 or > 256) continue;
                    for (var i = 0; i < strings; i++)
                    {
                        var value = Text(Marshal.ReadIntPtr(item, 40 + i * IntPtr.Size), trimDot: false);
                        var split = value.IndexOf('=');
                        if (split > 0) txt[value[..split]] = value[(split + 1)..];
                    }
                }
                records.Add(new(name, kind, ttl, target, port, address, txt));
            }
            var result = new List<ManagedService>();
            foreach (var pointer in records.Where(r => r.Type == 12 && r.Name.Equals(type, StringComparison.OrdinalIgnoreCase)
                && r.Target.EndsWith("." + type, StringComparison.OrdinalIgnoreCase)))
            {
                if (pointer.Ttl == 0) { result.Add(new(new(pointer.Target, type, "", 0, new Dictionary<string, string>()), 0)); continue; }
                var server = records.FirstOrDefault(r => r.Type == 33 && r.Name.Equals(pointer.Target, StringComparison.OrdinalIgnoreCase) && r.Port is > 0 and <= 65535);
                var text = records.FirstOrDefault(r => r.Type == 16 && r.Name.Equals(pointer.Target, StringComparison.OrdinalIgnoreCase));
                var ipv4 = server is null ? null : records.FirstOrDefault(r => r.Type == 1 && r.Name.Equals(server.Target, StringComparison.OrdinalIgnoreCase)
                    && IPAddress.TryParse(r.Address, out var ip) && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any));
                // shortcut: qualified HomePods return complete legacy-unicast answers; fragmented responders need a tested record assembler.
                if (server is null || text is null || ipv4 is null) continue;
                var ttl = Math.Min(pointer.Ttl, Math.Min(server.Ttl, Math.Min(text.Ttl, ipv4.Ttl)));
                result.Add(new(new(pointer.Target, type, ipv4.Address, server.Port, text.Txt), ttl));
            }
            return result;
        }
        finally { if (head != IntPtr.Zero) DnsRecordListFree(head, 1); }
    }
    private static string Text(IntPtr value, bool trimDot = true)
    {
        if (value == IntPtr.Zero) return "";
        var text = Marshal.PtrToStringUni(value) ?? "";
        if (text.Length > 4096) return "";
        return trimDot ? text.TrimEnd('.') : text;
    }
    [DllImport("dnsapi.dll", EntryPoint = "DnsExtractRecordsFromMessage_W", ExactSpelling = true)]
    private static extern uint DnsExtractRecordsFromMessage([In, Out] byte[] buffer, ushort length, out IntPtr records);
    [DllImport("dnsapi.dll")] private static extern void DnsRecordListFree(IntPtr records, int freeType);
}

// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from the author's Zeus-PowerStation (Discovery/ShellyMdns.cs @ c9c984d),
// which passed Zeus catalog review. Generalised from Shelly to any DNS-SD
// service type, and SRV ports are now read.
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace MeshMessenger.Discovery;

/// <summary>A service instance that answered an mDNS query.</summary>
public sealed record MdnsHit(
    IPAddress Address,
    int? Port,
    string Service,
    string InstanceName,
    IReadOnlyDictionary<string, string> Txt);

/// <summary>
/// A deliberately small mDNS (RFC 6762) client that only asks "which
/// instances of these services are on this network?" It sends PTR queries and
/// reads back PTR, SRV, TXT and A records. It never answers queries or
/// advertises anything.
///
/// mDNS doesn't cross routers, so this only finds nodes on the Zeus
/// computer's own networks; other VLANs are covered by the TCP scan. Every
/// hit is confirmed by speaking the node's protocol before it is shown.
/// </summary>
public static class Mdns
{
    public const int Port = 5353;
    public static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");

    /// <summary>Meshtastic firmware advertises its TCP API (port 4403) with TXT <c>id</c>, <c>shortname</c>, <c>pio_env</c>.</summary>
    public const string MeshtasticService = "_meshtastic._tcp.local";

    private const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33;
    private const ushort ClassIn = 1, UnicastResponse = 0x8000;

    // ------------------------------------------------------------ query

    /// <summary>
    /// Asks every local IPv4 network for <paramref name="services"/> and
    /// collects answers for <paramref name="listen"/>. Best effort: a network
    /// where multicast is blocked simply returns nothing.
    /// </summary>
    public static async Task<IReadOnlyList<MdnsHit>> DiscoverAsync(
        IReadOnlyList<string> services, TimeSpan listen, CancellationToken ct)
    {
        var query = BuildQuery(services);
        var hits = new List<MdnsHit>();
        var tasks = LocalIPv4Addresses()
            .Select(local => QueryAsync(query, services, new IPEndPoint(Group, Port), local, listen, ct))
            .ToList();
        // A second socket on 5353 catches nodes that answer by multicast even
        // though we asked for a unicast reply.
        tasks.Add(ListenOnMdnsPortAsync(services, listen, ct));
        foreach (var result in await Task.WhenAll(tasks).ConfigureAwait(false)) hits.AddRange(result);
        return Deduplicate(hits);
    }

    internal static async Task<IReadOnlyList<MdnsHit>> QueryAsync(
        byte[] query, IReadOnlyList<string> services, IPEndPoint target, IPAddress? local, TimeSpan listen, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(local ?? IPAddress.Any, 0));
            if (local is not null && IsMulticast(target.Address))
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            }
            await socket.SendToAsync(query, SocketFlags.None, target, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
        return await ReceiveAsync(socket, services, listen, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<MdnsHit>> ListenOnMdnsPortAsync(
        IReadOnlyList<string> services, TimeSpan listen, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, Port));
            foreach (var local in LocalIPv4Addresses())
            {
                try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, local)); }
                catch (SocketException) { }
            }
        }
        catch (SocketException)
        {
            return []; // Port held exclusively by the OS responder: fine, unicast replies still arrive.
        }
        return await ReceiveAsync(socket, services, listen, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<MdnsHit>> ReceiveAsync(
        Socket socket, IReadOnlyList<string> services, TimeSpan listen, CancellationToken ct)
    {
        var hits = new List<MdnsHit>();
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(listen);
        var buffer = new byte[9000];
        while (!window.IsCancellationRequested)
        {
            try
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, from, window.Token).ConfigureAwait(false);
                var sender = ((IPEndPoint)result.RemoteEndPoint).Address;
                hits.AddRange(ParseResponse(buffer.AsSpan(0, result.ReceivedBytes), sender, services));
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
        }
        return hits;
    }

    internal static IReadOnlyList<IPAddress> LocalIPv4Addresses()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                bool multicast;
                try { multicast = nic.SupportsMulticast; } catch (PlatformNotSupportedException) { multicast = true; }
                if (!multicast) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) list.Add(ua.Address);
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    private static bool IsMulticast(IPAddress a) =>
        a.AddressFamily == AddressFamily.InterNetwork && (a.GetAddressBytes()[0] & 0xF0) == 0xE0;

    // ------------------------------------------------------------ wire format

    /// <summary>One packet asking PTR for every service, requesting unicast replies (QU).</summary>
    internal static byte[] BuildQuery(IReadOnlyList<string> services)
    {
        var bytes = new List<byte>(64);
        bytes.AddRange([0, 0, 0, 0]);                                  // id 0, flags 0 (standard query)
        bytes.AddRange([0, (byte)services.Count, 0, 0, 0, 0, 0, 0]);   // qd, an, ns, ar
        foreach (var service in services)
        {
            foreach (var label in service.Split('.'))
            {
                var b = Encoding.ASCII.GetBytes(label);
                bytes.Add((byte)b.Length);
                bytes.AddRange(b);
            }
            bytes.Add(0);
            bytes.AddRange([0, (byte)TypePtr]);
            var cls = (ushort)(ClassIn | UnicastResponse);
            bytes.AddRange([(byte)(cls >> 8), (byte)cls]);
        }
        return bytes.ToArray();
    }

    /// <summary>
    /// Pulls instances of <paramref name="services"/> out of one mDNS response.
    /// Returns nothing (never throws) for malformed packets or other services.
    /// </summary>
    internal static IReadOnlyList<MdnsHit> ParseResponse(ReadOnlySpan<byte> packet, IPAddress sender, IReadOnlyList<string> services)
    {
        try { return ParseCore(packet, sender, services); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException) { return []; }
    }

    private static IReadOnlyList<MdnsHit> ParseCore(ReadOnlySpan<byte> p, IPAddress sender, IReadOnlyList<string> services)
    {
        if (p.Length < 12) return [];
        var flags = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
        if ((flags & 0x8000) == 0) return []; // a query, not a response
        int qd = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
        int records = BinaryPrimitives.ReadUInt16BigEndian(p[6..]) +
                      BinaryPrimitives.ReadUInt16BigEndian(p[8..]) +
                      BinaryPrimitives.ReadUInt16BigEndian(p[10..]);
        var pos = 12;
        for (var i = 0; i < qd; i++) { ReadName(p, ref pos); pos += 4; }

        var instances = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // instance -> service
        var srv = new Dictionary<string, (string Target, int Port)>(StringComparer.OrdinalIgnoreCase);
        var txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var aRecords = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < records && pos < p.Length; i++)
        {
            var name = ReadName(p, ref pos);
            var type = BinaryPrimitives.ReadUInt16BigEndian(p[pos..]);
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(p[(pos + 8)..]);
            pos += 10;
            var rdStart = pos;
            if (rdStart + rdLength > p.Length) break;
            switch (type)
            {
                case TypePtr:
                {
                    var service = services.FirstOrDefault(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
                    if (service is null) break;
                    var at = rdStart;
                    instances[ReadName(p, ref at)] = service;
                    break;
                }
                case TypeSrv when rdLength >= 7:
                {
                    var port = BinaryPrimitives.ReadUInt16BigEndian(p[(rdStart + 4)..]);
                    var at = rdStart + 6;
                    srv[name] = (ReadName(p, ref at), port);
                    break;
                }
                case TypeTxt:
                    txt[name] = ParseTxt(p.Slice(rdStart, rdLength));
                    break;
                case TypeA when rdLength == 4:
                    aRecords[name] = new IPAddress(p.Slice(rdStart, 4));
                    break;
            }
            pos = rdStart + rdLength;
        }

        var hits = new List<MdnsHit>();
        foreach (var (instance, service) in instances)
        {
            txt.TryGetValue(instance, out var t);
            t ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int? port = null;
            var address = sender;
            if (srv.TryGetValue(instance, out var s))
            {
                port = s.Port;
                if (aRecords.TryGetValue(s.Target, out var a)) address = a;
            }
            // An answer can name any address in its A record. Only local-network
            // addresses are used; anything else is dropped here.
            if (!HostValidator.IsLocal(address)) continue;
            hits.Add(new MdnsHit(address, port, service, instance.Split('.')[0], t));
        }
        return hits;
    }

    private static string ReadName(ReadOnlySpan<byte> p, ref int pos)
    {
        var labels = new List<string>();
        var at = pos;
        var jumped = false;
        for (var hops = 0; hops < 32; hops++)
        {
            int len = p[at];
            if (len == 0)
            {
                if (!jumped) pos = at + 1;
                return string.Join('.', labels);
            }
            if ((len & 0xC0) == 0xC0)
            {
                var pointer = ((len & 0x3F) << 8) | p[at + 1];
                if (!jumped) pos = at + 2;
                jumped = true;
                at = pointer;
                continue;
            }
            if (len > 63) throw new FormatException("bad label");
            labels.Add(Encoding.UTF8.GetString(p.Slice(at + 1, len)));
            at += 1 + len;
        }
        throw new FormatException("name too long or loops");
    }

    private static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < data.Length)
        {
            int len = data[i++];
            if (i + len > data.Length) break;
            var entry = Encoding.UTF8.GetString(data.Slice(i, len));
            i += len;
            var eq = entry.IndexOf('=');
            if (eq > 0) result[entry[..eq]] = entry[(eq + 1)..];
            else if (entry.Length > 0) result[entry] = "";
        }
        return result;
    }

    // Every Meshtastic node advertises the same instance name ("Meshtastic"),
    // so de-duplicate by address and keep the richest answer.
    private static IReadOnlyList<MdnsHit> Deduplicate(List<MdnsHit> hits) =>
        hits.GroupBy(h => (h.Address, h.Service)).Select(g => g.OrderByDescending(h => h.Txt.Count).First()).ToArray();
}

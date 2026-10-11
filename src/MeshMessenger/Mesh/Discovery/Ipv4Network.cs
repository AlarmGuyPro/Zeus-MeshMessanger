// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from the author's Zeus-PowerStation (Discovery/NetworkScanner.cs @ c9c984d),
// which passed Zeus catalog review.
using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MeshMessenger.Discovery;

/// <summary>An IPv4 range to scan, written as CIDR (e.g. 192.168.30.0/24).</summary>
public readonly record struct Ipv4Network(uint Network, int Prefix)
{
    /// <summary>Largest single range scanned (/20 = 4,094 hosts), same limit as PowerStation.</summary>
    public const int SmallestPrefix = 20;

    public uint Mask => Prefix == 0 ? 0 : uint.MaxValue << (32 - Prefix);

    /// <summary>Usable host addresses (network and broadcast excluded for /30 and larger).</summary>
    public int HostCount => Prefix >= 31 ? 1 << (32 - Prefix) : (1 << (32 - Prefix)) - 2;

    public override string ToString() => $"{ToAddress(Network)}/{Prefix}";

    public IEnumerable<IPAddress> Hosts()
    {
        var size = 1u << (32 - Prefix);
        var (first, last) = Prefix >= 31 ? (Network, Network + size - 1) : (Network + 1, Network + size - 2);
        for (var a = first; a <= last && a >= first; a++) yield return ToAddress(a);
    }

    public bool Contains(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && (ToUInt(address) & Mask) == Network;

    public static bool TryParse(string? text, out Ipv4Network network, out string? error)
    {
        network = default;
        error = null;
        var s = text?.Trim() ?? "";
        if (s.Length == 0) { error = "Enter a network such as 192.168.30.0/24."; return false; }
        var slash = s.IndexOf('/');
        var prefix = 24;
        if (slash >= 0 && !int.TryParse(s[(slash + 1)..], out prefix))
        {
            error = $"\"{s}\" isn't a valid network. Use the form 192.168.30.0/24.";
            return false;
        }
        var addrText = slash >= 0 ? s[..slash] : s;
        if (!IPAddress.TryParse(addrText, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork ||
            addrText.Count(ch => ch == '.') != 3)
        {
            error = $"\"{s}\" isn't a valid IPv4 network. Use the form 192.168.30.0/24.";
            return false;
        }
        if (prefix is < SmallestPrefix or > 32)
        {
            error = $"{s} is too large to scan. Use /{SmallestPrefix} or smaller (for example /24).";
            return false;
        }
        var mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        network = new Ipv4Network(ToUInt(addr) & mask, prefix);
        if (!HostValidator.IsLocal(ToAddress(network.Network)))
        {
            error = $"{network} isn't a local network. Mesh Messenger only scans private LAN addresses.";
            return false;
        }
        return true;
    }

    internal static uint ToUInt(IPAddress a) => BinaryPrimitives.ReadUInt32BigEndian(a.GetAddressBytes());

    internal static IPAddress ToAddress(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return new IPAddress(b);
    }

    /// <summary>The /24 containing an address, used to look for a node near where it was.</summary>
    public static Ipv4Network Around(IPAddress address) => new(ToUInt(address) & 0xFFFFFF00u, 24);

    /// <summary>
    /// The private IPv4 networks this computer is attached to. Networks larger
    /// than a /22 are narrowed to the /24 around this computer's address so a
    /// default scan stays quick.
    /// </summary>
    public static IReadOnlyList<Ipv4Network> LocalNetworks()
    {
        var list = new List<Ipv4Network>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || !HostValidator.IsLocal(ua.Address)) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // no DHCP lease; not a real LAN
                    var prefix = ua.PrefixLength is >= 22 and <= 30 ? ua.PrefixLength : 24;
                    var mask = uint.MaxValue << (32 - prefix);
                    var net = new Ipv4Network(ToUInt(ua.Address) & mask, prefix);
                    if (!list.Contains(net)) list.Add(net);
                }
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }
}

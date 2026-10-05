// SPDX-License-Identifier: GPL-3.0-or-later
// Adapted from the author's Zeus-PowerStation (Shelly/HostValidator.cs @ c9c984d),
// which passed Zeus catalog review.
using System.Net;
using System.Net.Sockets;

namespace MeshMessenger.Discovery;

/// <summary>
/// Mesh Messenger only talks to nodes on the local network. An address must
/// be a bare host or host:port (no scheme, path, credentials or query), and
/// every address it resolves to must be private, link-local or CGNAT-range.
/// This keeps the plugin from being usable to reach Internet hosts, or
/// services on the Zeus computer itself.
/// </summary>
public static class HostValidator
{
    /// <summary>Loopback is refused. Only the test suite turns this on, to reach simulated nodes.</summary>
    internal static bool AllowLoopback { get; set; }

    /// <summary>An address found by discovery: an IP (optionally with port) on the local network.</summary>
    public static bool IsLocalHost(string host) =>
        Uri.TryCreate("http://" + host, UriKind.Absolute, out var uri) &&
        IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) &&
        IsLocal(address);

    /// <summary>Normalises operator input to <c>host</c> or <c>host:port</c>.</summary>
    public static bool TryNormalize(string? input, out string host, out string? error)
    {
        host = "";
        error = null;
        var text = input?.Trim() ?? "";
        if (text.Length == 0) { error = "Enter an IP address or host name."; return false; }
        if (text.Length > 253) { error = "Address is too long."; return false; }
        if (text.Contains("://", StringComparison.Ordinal)) { error = "Enter just the address, without a scheme."; return false; }
        if (text.IndexOfAny(['/', '\\', '@', '?', '#', ' ']) >= 0) { error = "Enter just an IP address or host name."; return false; }
        if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out var uri) ||
            uri.PathAndQuery != "/" || !string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "That doesn't look like a valid address.";
            return false;
        }
        // "http://" is only a parsing aid; Uri.Host keeps IPv6 brackets so host:port stays unambiguous.
        host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return true;
    }

    /// <summary>Resolves a host and returns an error if any address isn't local; null when fine.</summary>
    public static async Task<string?> CheckLocalAsync(string host, CancellationToken ct)
    {
        var name = Uri.TryCreate("http://" + host, UriKind.Absolute, out var uri) ? uri.IdnHost : host;
        IPAddress[] addresses;
        if (IPAddress.TryParse(name.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try { addresses = await Dns.GetHostAddressesAsync(name, ct).ConfigureAwait(false); }
            catch (SocketException) { return $"Couldn't resolve \"{name}\". Try Scan, or the node's IP address."; }
        }
        if (addresses.Length == 0) return $"Couldn't resolve \"{name}\".";
        foreach (var address in addresses)
        {
            if (!IsLocal(address))
                return $"{address} isn't a local-network address. Mesh Messenger only talks to nodes on your LAN.";
        }
        return null;
    }

    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return AllowLoopback;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254) ||
                   (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // CGNAT / Tailscale-style overlays
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal) return true;
        }
        return false;
    }
}

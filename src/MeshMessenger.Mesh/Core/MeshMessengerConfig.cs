// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshMessenger.Core;

/// <summary>
/// Persisted through the plugin-scoped settings store. Plain classes with
/// setters so the host's document mapper can round-trip them.
/// </summary>
public sealed class MeshMessengerConfig
{
    public List<NodeConfig> Nodes { get; set; } = [];

    /// <summary>
    /// Extra private address ranges to scan for nodes (other VLANs), as CIDR
    /// (<c>192.168.30.0/24</c>) or start-end. See docs/DISCOVERY.md.
    /// </summary>
    public List<string> ScanRanges { get; set; } = [];
}

public sealed class NodeConfig
{
    /// <summary>Stable lowercase id, e.g. <c>meshtastic-1</c>. Changing it orphans that node's conversations.</summary>
    public string Id { get; set; } = "";

    public MeshNetwork Network { get; set; }

    public string Name { get; set; } = "";

    /// <summary>LAN host name or IP address of the node: the last address it was found at.</summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// The node's own identity, recorded when it was paired: Meshtastic node
    /// number as <c>!xxxxxxxx</c>, MeshCore public key as hex. A node at
    /// <see cref="Host"/> with a different identity is refused.
    /// </summary>
    public string? Identity { get; set; }

    /// <summary>Look for the paired node again if it stops answering at <see cref="Host"/>.</summary>
    public bool FindIfAddressChanges { get; set; } = true;

    /// <summary>TCP port; 0 means the protocol default.</summary>
    public int Port { get; set; }

    public bool Enabled { get; set; } = true;
}

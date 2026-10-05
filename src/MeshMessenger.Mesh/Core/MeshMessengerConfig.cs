// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshMessenger.Core;

/// <summary>
/// Persisted through the plugin-scoped settings store. Plain classes with
/// setters so the host's document mapper can round-trip them.
/// </summary>
public sealed class MeshMessengerConfig
{
    public List<NodeConfig> Nodes { get; set; } = [];
}

public sealed class NodeConfig
{
    /// <summary>Stable lowercase id, e.g. <c>meshtastic-1</c>. Changing it orphans that node's conversations.</summary>
    public string Id { get; set; } = "";

    public MeshNetwork Network { get; set; }

    public string Name { get; set; } = "";

    /// <summary>LAN host name or IP address of the node.</summary>
    public string Host { get; set; } = "";

    /// <summary>TCP port; 0 means the protocol default.</summary>
    public int Port { get; set; }

    public bool Enabled { get; set; } = true;
}

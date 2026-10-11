// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshMessenger.Core;

/// <summary>
/// One configured LoRa node (Heltec V4 or similar) reached over the LAN.
/// A connector only ever sends on its own node. It never knows about other
/// connectors, so it cannot hand a message to a different network.
/// </summary>
public interface IMeshConnector : IAsyncDisposable
{
    /// <summary>Stable id from configuration; part of every conversation key.</summary>
    string Id { get; }

    MeshNetwork Network { get; }

    string DisplayName { get; }

    ConnectorState State { get; }

    /// <summary>Human-readable reason for the current state, e.g. the last error.</summary>
    string? StateDetail { get; }

    /// <summary>The node's own description once connected.</summary>
    SelfInfo? Self { get; }

    /// <summary>The address currently in use (may differ from configuration after a re-find).</summary>
    string Host { get; }

    IReadOnlyList<PeerInfo> Peers { get; }

    /// <summary>Recent protocol activity, newest last. For the Debug tab and bug reports.</summary>
    IReadOnlyList<LogEntry> Activity { get; }

    /// <summary>True when the node can be asked to announce itself on the air.</summary>
    bool CanAnnounce { get; }

    /// <summary>
    /// Asks the node to transmit an announcement of itself, so other stations
    /// learn its name and key. TRANSMITS; only ever called from an explicit action.
    /// </summary>
    Task<SendResult> AnnounceAsync(bool flood, CancellationToken ct);

    /// <summary>Re-reads the node's contact or peer list. Local to the node; transmits nothing.</summary>
    Task RefreshPeersAsync(CancellationToken ct);

    /// <summary>How many contacts the node can hold, when the firmware reports it. A full list can't learn new stations.</summary>
    int? PeerCapacity { get; }

    /// <summary>Whether the node recycles its oldest contact when the list is full; null when the node has no such setting.</summary>
    bool? RecycleOldestWhenFull { get; }

    /// <summary>Removes a contact from the node's list. Local to the node. Returns null on success, or the reason.</summary>
    Task<string?> RemovePeerAsync(string peerId, CancellationToken ct);

    /// <summary>Turns the node's "recycle the oldest contact when full" setting on or off.</summary>
    Task<string?> SetRecycleOldestWhenFullAsync(bool enabled, CancellationToken ct);

    IReadOnlyList<ChannelInfo> Channels { get; }

    /// <summary>
    /// Largest UTF-8 text the node will send intact for this kind of conversation.
    /// Can differ by kind (MeshCore prefixes channel text with our node name).
    /// </summary>
    int MaxTextBytes(ConversationKind kind);

    /// <summary>Raised for every text message the node delivers. Keys must carry this connector's <see cref="Id"/>.</summary>
    event Action<InboundMessage>? MessageReceived;

    /// <summary>Raised when an earlier send is acknowledged, fails or times out.</summary>
    event Action<DeliveryUpdate>? DeliveryChanged;

    /// <summary>Raised when state, self info, peers or channels change.</summary>
    event Action? Changed;

    /// <summary>Begin connecting in the background. Must return quickly (the host gives plugin init 10 seconds).</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Send on this node only. <paramref name="to"/> always belongs to this connector.</summary>
    Task<SendResult> SendAsync(ConversationKey to, string text, CancellationToken ct);

    /// <summary>A display name for a conversation's peer, if the node knows one.</summary>
    string? TitleFor(ConversationKey key);
}

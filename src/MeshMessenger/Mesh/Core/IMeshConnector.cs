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

    /// <summary>
    /// The peers a direct conversation could be opened with for whoever sent a
    /// channel or room message. Meshtastic carries the sender's node number in
    /// the packet, so the answer is exact. MeshCore channel frames carry no
    /// sender key at all — the only identity is a self-asserted name in the
    /// text — so there the answer may be none, one or several contacts.
    /// </summary>
    IReadOnlyList<string> ResolveSenderPeers(ConversationKind kind, string fromId, string? fromName);

    /// <summary>How many contacts the node can hold, when the firmware reports it. A full list can't learn new stations.</summary>
    int? PeerCapacity { get; }

    /// <summary>
    /// How many stations Zeus itself remembers for this node, beyond what the node holds.
    /// Shown in the Logs tab so a table at its limit is visible rather than silent.
    /// </summary>
    int RememberedPeerLimit { get; }

    /// <summary>
    /// How many stations the node has added to its own contact list, unprompted, since Zeus
    /// connected. Anything above zero means its list grows on its own, so contacts removed
    /// from it come back — which is otherwise a mystery to whoever removed them.
    /// </summary>
    int PeersSavedByTheNode { get; }

    /// <summary>Whether the node recycles its oldest contact when the list is full; null when the node has no such setting.</summary>
    bool? RecycleOldestWhenFull { get; }

    /// <summary>Removes a contact from the node's list. Local to the node. Returns null on success, or the reason.</summary>
    Task<string?> RemovePeerAsync(string peerId, CancellationToken ct);

    /// <summary>
    /// Removes many contacts in one operation. Separate from <see cref="RemovePeerAsync"/>
    /// because clearing hundreds of them one call at a time is not the same job: it has to
    /// pace itself so the node keeps up, read the list back once rather than once per
    /// contact, and stop cleanly without taking the connection down with it.
    /// </summary>
    Task<BulkRemoval> RemovePeersAsync(IReadOnlyList<string> peerIds, CancellationToken ct);

    /// <summary>Turns the node's "recycle the oldest contact when full" setting on or off.</summary>
    Task<string?> SetRecycleOldestWhenFullAsync(bool enabled, CancellationToken ct);

    /// <summary>Marks a contact as a favourite on the node so it is never recycled.</summary>
    Task<string?> SetPeerFavouriteAsync(string peerId, bool favourite, CancellationToken ct);

    /// <summary>True when channels can be joined and left on this node from Zeus.</summary>
    bool CanEditChannels { get; }

    /// <summary>
    /// Puts a channel in the node's first free slot. A name beginning with '#'
    /// derives its own key; anything else needs <paramref name="secretHex"/>,
    /// the key shared by whoever runs the channel. Local to the node; transmits
    /// nothing. Returns null on success, or the reason it couldn't.
    /// </summary>
    Task<string?> JoinChannelAsync(string name, string? secretHex, CancellationToken ct);

    /// <summary>Clears a channel slot on the node. Local; transmits nothing.</summary>
    Task<string?> LeaveChannelAsync(int index, CancellationToken ct);

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

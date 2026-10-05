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

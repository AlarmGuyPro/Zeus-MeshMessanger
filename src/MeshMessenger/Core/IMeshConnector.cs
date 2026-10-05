// SPDX-License-Identifier: GPL-2.0-or-later
namespace MeshMessenger.Core;

/// <summary>
/// One configured LoRa node (Heltec V4 or similar) reached over Wi-Fi.
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

    /// <summary>
    /// Largest UTF-8 text the node will send intact for this kind of conversation.
    /// Can differ by kind (MeshCore prefixes channel text with our node name).
    /// </summary>
    int MaxTextBytes(ConversationKind kind);

    /// <summary>Raised for every text message the node delivers. Keys must carry this connector's <see cref="Id"/>.</summary>
    event Action<InboundMessage>? MessageReceived;

    /// <summary>Begin connecting in the background. Must return quickly (the host gives plugin init 10 seconds).</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>Send on this node only. <paramref name="to"/> always belongs to this connector.</summary>
    Task<SendResult> SendAsync(ConversationKey to, string text, CancellationToken ct);
}

// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshMessenger.Core;

/// <summary>Which mesh protocol a connector speaks.</summary>
public enum MeshNetwork
{
    Meshtastic,
    MeshCore,
}

/// <summary>A channel (group) conversation or a direct conversation with one node.</summary>
public enum ConversationKind
{
    Channel,
    Direct,
}

public enum ConnectorState
{
    Disconnected,
    Connecting,
    Connected,
    Error,
}

public enum MessageDirection
{
    Inbound,
    Outbound,
}

public enum DeliveryStatus
{
    Received,
    Sent,
    Failed,
}

/// <summary>
/// The identity of a conversation. It always includes the connector it came in
/// on, so the same channel name or contact on two networks (or two nodes) is
/// two different conversations. Replies are routed by this key and nothing else.
/// </summary>
/// <param name="ConnectorId">Configured node id, e.g. <c>meshtastic-1</c>.</param>
/// <param name="Kind">Channel or direct.</param>
/// <param name="Peer">Channel index, or the remote node id / public-key prefix.</param>
public sealed record ConversationKey(string ConnectorId, ConversationKind Kind, string Peer)
{
    public string Id => $"{ConnectorId}:{(Kind == ConversationKind.Channel ? "ch" : "dm")}:{Peer}";
}

/// <summary>A message a connector received from its node.</summary>
public sealed record InboundMessage(
    ConversationKey Conversation,
    string FromId,
    string? FromName,
    string Text,
    DateTimeOffset ReceivedAt,
    string? NetworkMessageId);

public sealed record SendResult(bool Ok, string? Error, string? NetworkMessageId)
{
    public static SendResult Success(string? networkMessageId = null) => new(true, null, networkMessageId);
    public static SendResult Failure(string error) => new(false, error, null);
}

public sealed record StoredMessage(
    string Id,
    MessageDirection Direction,
    string FromId,
    string? FromName,
    string Text,
    DateTimeOffset Timestamp,
    DeliveryStatus Status,
    string? Error);

public sealed record Conversation(ConversationKey Key, MeshNetwork Network, string? Title);

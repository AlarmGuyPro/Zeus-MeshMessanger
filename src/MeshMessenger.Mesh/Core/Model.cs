// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshMessenger.Core;

/// <summary>Which mesh protocol a connector speaks.</summary>
public enum MeshNetwork
{
    Meshtastic,
    MeshCore,
}

/// <summary>A channel (group), a direct conversation with one node, or a MeshCore room server.</summary>
public enum ConversationKind
{
    Channel,
    Direct,
    Room,
}

public enum ConnectorState
{
    Disconnected,
    Connecting,
    Connected,
    /// <summary>Looking for the paired node at a new address.</summary>
    Searching,
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
    /// <summary>Handed to the node; waiting for the node to accept it.</summary>
    Sending,
    /// <summary>The node accepted (and, for MeshCore, transmitted) it.</summary>
    Sent,
    /// <summary>Acknowledged by the recipient (DM) or relayed by another node (Meshtastic channel).</summary>
    Delivered,
    /// <summary>The acknowledgement window passed without an ack.</summary>
    NotConfirmed,
    Failed,
}

/// <summary>
/// The identity of a conversation. It always includes the connector it came in
/// on, so the same channel name or contact on two networks (or two nodes) is
/// two different conversations. Replies are routed by this key and nothing else.
/// </summary>
/// <param name="ConnectorId">Configured node id, e.g. <c>mc-7f3a</c>.</param>
/// <param name="Kind">Channel, direct or room.</param>
/// <param name="Peer">Channel index, the remote node id / public key, or the room's public key.</param>
public sealed record ConversationKey(string ConnectorId, ConversationKind Kind, string Peer)
{
    public string Id => $"{ConnectorId}:{KindCode(Kind)}:{Peer}";

    public static string KindCode(ConversationKind kind) => kind switch
    {
        ConversationKind.Channel => "ch",
        ConversationKind.Direct => "dm",
        _ => "room",
    };
}

/// <summary>How a received packet arrived at our node: last hop only.</summary>
public sealed record Reception(double? Snr, int? Rssi, int? Hops, bool Direct, bool ViaMqtt);

/// <summary>A message a connector received from its node.</summary>
public sealed record InboundMessage(
    ConversationKey Conversation,
    string FromId,
    string? FromName,
    string Text,
    DateTimeOffset SentAt,
    string? NetworkMessageId,
    Reception? Reception,
    bool QueuedWhileAway);

/// <summary>Result of handing a message to the node.</summary>
/// <param name="AckTag">Token the connector will use in <see cref="DeliveryUpdate"/> for this message.</param>
/// <param name="Airtime">Estimated time on air, used to space transmissions across nodes.</param>
public sealed record SendResult(bool Ok, string? Error, string? AckTag, TimeSpan Airtime, bool Flood = false)
{
    public static SendResult Success(string? ackTag, TimeSpan airtime, bool flood = false) => new(true, null, ackTag, airtime, flood);
    public static SendResult Failure(string error) => new(false, error, null, TimeSpan.Zero);
}

/// <summary>Later news about a sent message, matched by <see cref="SendResult.AckTag"/>.</summary>
public sealed record DeliveryUpdate(string AckTag, DeliveryStatus Status, string? Error, int? RoundTripMs);

public sealed record StoredMessage(
    string Id,
    MessageDirection Direction,
    string FromId,
    string? FromName,
    string Text,
    DateTimeOffset Timestamp,
    DeliveryStatus Status,
    string? Error,
    Reception? Reception = null,
    bool QueuedWhileAway = false,
    string? AckTag = null,
    int? RoundTripMs = null,
    bool Flood = false,
    string? NetworkId = null);

public sealed record Conversation(ConversationKey Key, MeshNetwork Network, string? Title);

/// <summary>What our own node says about itself.</summary>
public sealed record SelfInfo(
    string Identity,
    string Name,
    string? ShortName,
    string? Firmware,
    string? Model,
    string? RadioSummary);

public enum PeerKind
{
    Chat,
    Repeater,
    Room,
    Sensor,
    Unknown,
}

/// <summary>A node our node knows about (Meshtastic node DB, MeshCore contacts).</summary>
public sealed record PeerInfo(
    string Id,
    string Name,
    string? ShortName,
    PeerKind Kind,
    DateTimeOffset? LastHeard,
    double? Snr,
    int? Hops,
    int? BatteryPercent,
    double? Latitude,
    double? Longitude,
    bool Favorite);

public enum ChannelKind
{
    Public,
    Hashtag,
    Private,
}

/// <summary>A channel slot configured on our node.</summary>
public sealed record ChannelInfo(int Index, string Name, ChannelKind Kind, bool Primary);

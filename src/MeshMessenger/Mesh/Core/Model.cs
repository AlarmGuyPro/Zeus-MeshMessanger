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

    /// <summary>Reads back an <see cref="Id"/>. The peer may itself contain ':' so only the first two parts are split.</summary>
    public static bool TryParse(string? id, out ConversationKey key)
    {
        key = new ConversationKey("", ConversationKind.Channel, "");
        if (string.IsNullOrWhiteSpace(id)) return false;
        var first = id.IndexOf(':');
        if (first <= 0) return false;
        var second = id.IndexOf(':', first + 1);
        if (second < 0) return false;
        var kind = id[(first + 1)..second] switch
        {
            "ch" => ConversationKind.Channel,
            "dm" => ConversationKind.Direct,
            "room" => ConversationKind.Room,
            _ => (ConversationKind?)null,
        };
        if (kind is null) return false;
        key = new ConversationKey(id[..first], kind.Value, id[(second + 1)..]);
        return key.Peer.Length > 0;
    }
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

/// <summary>How serious a logged event is; drives the colour in the Debug tab.</summary>
public enum LogLevel
{
    Info,
    Send,
    Receive,
    Warning,
    Error,
}

/// <summary>
/// One line in a connector's rolling activity log. Written for anything that
/// explains what the node and Zeus did: connects, commands, replies, errors.
/// Message text is never logged; only what the protocol did with it.
/// </summary>
public sealed record LogEntry(DateTimeOffset At, LogLevel Level, string Text);

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
    bool Favorite,
    bool SavedOnNode = true,
    /// <summary>When our own node last updated this record. More trustworthy than the advert's own timestamp.</summary>
    DateTimeOffset? LastUpdated = null);

public enum ChannelKind
{
    Public,
    Hashtag,
    Private,
}

/// <summary>A channel slot configured on our node.</summary>
public sealed record ChannelInfo(int Index, string Name, ChannelKind Kind, bool Primary);

/// <summary>What a bulk contact removal managed, and why it stopped if it did.</summary>
public sealed record BulkRemoval(int Removed, int Requested, string? Error)
{
    public bool Complete => Error is null && Removed == Requested;
}

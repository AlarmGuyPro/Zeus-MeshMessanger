// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace MeshMessenger.Core;

/// <summary>
/// The single place outbound messages are routed. Rules (see DESIGN.md):
/// <list type="number">
/// <item>A reply goes to the connector named in the conversation key. Callers
/// never pass a network or connector for a reply.</item>
/// <item>If that connector is missing or not connected, the send fails. There
/// is no fallback to another connector, ever.</item>
/// <item>Only a new conversation names its connector, explicitly.</item>
/// <item>Inbound messages whose key names a different connector are dropped.</item>
/// </list>
/// </summary>
public sealed class MessageRouter
{
    private readonly MessageStore _store;
    private readonly IReadOnlyDictionary<string, IMeshConnector> _connectors;

    public MessageRouter(MessageStore store, IEnumerable<IMeshConnector> connectors)
    {
        _store = store;
        _connectors = connectors.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var connector in _connectors.Values)
        {
            var source = connector;
            connector.MessageReceived += message => OnInbound(source, message);
        }
    }

    public IReadOnlyCollection<IMeshConnector> Connectors => _connectors.Values.ToArray();

    public sealed record RouteResult(bool Ok, string? Error, string? ConversationId, StoredMessage? Message)
    {
        public static RouteResult Fail(string error, string? conversationId = null) => new(false, error, conversationId, null);
    }

    /// <summary>Reply in an existing conversation, on the connector it belongs to.</summary>
    public async Task<RouteResult> ReplyAsync(string conversationId, string text, CancellationToken ct)
    {
        var conversation = _store.Find(conversationId);
        if (conversation is null)
        {
            return RouteResult.Fail("Conversation not found.");
        }
        return await SendOnOwnConnectorAsync(conversation.Key, text, ct);
    }

    /// <summary>Start a conversation. The caller must name the connector explicitly.</summary>
    public async Task<RouteResult> StartAsync(string connectorId, ConversationKind kind, string peer, string text, CancellationToken ct)
    {
        if (!_connectors.TryGetValue(connectorId, out var connector))
        {
            return RouteResult.Fail($"No node is configured with id '{connectorId}'.");
        }
        if (string.IsNullOrWhiteSpace(peer))
        {
            return RouteResult.Fail("A channel or destination is required.");
        }
        var key = new ConversationKey(connector.Id, kind, peer.Trim());
        _store.GetOrAdd(key, connector.Network, title: null);
        return await SendOnOwnConnectorAsync(key, text, ct);
    }

    private async Task<RouteResult> SendOnOwnConnectorAsync(ConversationKey key, string text, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return RouteResult.Fail("Message is empty.", key.Id);
        }

        // The connector is looked up from the key, never chosen by the caller.
        if (!_connectors.TryGetValue(key.ConnectorId, out var connector))
        {
            return RouteResult.Fail(
                $"The node this conversation belongs to ('{key.ConnectorId}') is no longer configured. Messages are never re-sent on another node.",
                key.Id);
        }
        if (connector.State != ConnectorState.Connected)
        {
            return RouteResult.Fail($"{connector.DisplayName} is not connected. The message was not sent.", key.Id);
        }
        var bytes = Encoding.UTF8.GetByteCount(text);
        var limit = connector.MaxTextBytes(key.Kind);
        if (bytes > limit)
        {
            return RouteResult.Fail($"Message is {bytes} bytes; {connector.DisplayName} allows {limit} here.", key.Id);
        }

        SendResult result;
        try
        {
            result = await connector.SendAsync(key, text, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = SendResult.Failure(ex.Message);
        }

        var stored = new StoredMessage(
            Id: Guid.NewGuid().ToString("N"),
            Direction: MessageDirection.Outbound,
            FromId: connector.Id,
            FromName: connector.DisplayName,
            Text: text,
            Timestamp: DateTimeOffset.UtcNow,
            Status: result.Ok ? DeliveryStatus.Sent : DeliveryStatus.Failed,
            Error: result.Error);
        _store.Append(key, stored);
        return new RouteResult(result.Ok, result.Error, key.Id, stored);
    }

    private void OnInbound(IMeshConnector source, InboundMessage message)
    {
        // Defence in depth: a connector may only file messages under its own id.
        if (!string.Equals(message.Conversation.ConnectorId, source.Id, StringComparison.Ordinal))
        {
            return;
        }
        _store.GetOrAdd(message.Conversation, source.Network, title: null);
        _store.Append(message.Conversation, new StoredMessage(
            Id: Guid.NewGuid().ToString("N"),
            Direction: MessageDirection.Inbound,
            FromId: message.FromId,
            FromName: message.FromName,
            Text: message.Text,
            Timestamp: message.ReceivedAt,
            Status: DeliveryStatus.Received,
            Error: null));
    }
}

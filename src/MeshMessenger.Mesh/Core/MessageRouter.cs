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
/// <item>Every transmission goes through the shared <see cref="TransmitCoordinator"/>.</item>
/// </list>
/// </summary>
public sealed class MessageRouter : IDisposable
{
    private readonly MessageStore _store;
    private readonly TransmitCoordinator _tx;
    private readonly IReadOnlyDictionary<string, IMeshConnector> _connectors;
    private readonly List<Action> _unsubscribe = [];

    public MessageRouter(MessageStore store, TransmitCoordinator tx, IEnumerable<IMeshConnector> connectors)
    {
        _store = store;
        _tx = tx;
        _connectors = connectors.ToDictionary(c => c.Id, StringComparer.Ordinal);
        foreach (var connector in _connectors.Values)
        {
            var source = connector;
            Action<InboundMessage> onMessage = message => OnInbound(source, message);
            Action<DeliveryUpdate> onDelivery = update => OnDelivery(source, update);
            connector.MessageReceived += onMessage;
            connector.DeliveryChanged += onDelivery;
            _unsubscribe.Add(() =>
            {
                connector.MessageReceived -= onMessage;
                connector.DeliveryChanged -= onDelivery;
            });
        }
    }

    public IReadOnlyCollection<IMeshConnector> Connectors => _connectors.Values.ToArray();

    public IMeshConnector? Connector(string id) => _connectors.TryGetValue(id, out var c) ? c : null;

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
        return await SendOnOwnConnectorAsync(conversation.Key, text, ct).ConfigureAwait(false);
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
        var check = Validate(connector, key, text.Trim());
        if (check is not null)
        {
            return RouteResult.Fail(check);
        }
        _store.GetOrAdd(key, connector.Network, connector.TitleFor(key));
        return await SendOnOwnConnectorAsync(key, text, ct).ConfigureAwait(false);
    }

    private static string? Validate(IMeshConnector connector, ConversationKey key, string text)
    {
        if (text.Length == 0)
        {
            return "Message is empty.";
        }
        if (connector.State != ConnectorState.Connected)
        {
            return $"{connector.DisplayName} is not connected. The message was not sent.";
        }
        var bytes = Encoding.UTF8.GetByteCount(text);
        var limit = connector.MaxTextBytes(key.Kind);
        if (bytes > limit)
        {
            return $"Message is {bytes} bytes; {connector.DisplayName} allows {limit} here.";
        }
        return null;
    }

    private async Task<RouteResult> SendOnOwnConnectorAsync(ConversationKey key, string text, CancellationToken ct)
    {
        text = text.Trim();

        // The connector is looked up from the key, never chosen by the caller.
        if (!_connectors.TryGetValue(key.ConnectorId, out var connector))
        {
            return RouteResult.Fail(
                $"The node this conversation belongs to ('{key.ConnectorId}') is no longer configured. Messages are never re-sent on another node.",
                key.Id);
        }
        var problem = Validate(connector, key, text);
        if (problem is not null)
        {
            return RouteResult.Fail(problem, key.Id);
        }

        var id = Guid.NewGuid().ToString("N");
        var pending = new StoredMessage(
            Id: id,
            Direction: MessageDirection.Outbound,
            FromId: connector.Self?.Identity ?? connector.Id,
            FromName: connector.Self?.Name ?? connector.DisplayName,
            Text: text,
            Timestamp: DateTimeOffset.UtcNow,
            Status: DeliveryStatus.Sending,
            Error: null);
        _store.Append(key, pending);

        SendResult result;
        try
        {
            result = await _tx.RunAsync(token => connector.SendAsync(key, text, token), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = SendResult.Failure("Cancelled before it was sent.");
        }
        catch (Exception ex)
        {
            result = SendResult.Failure(ex.Message);
        }

        var final = pending with
        {
            Status = result.Ok ? DeliveryStatus.Sent : DeliveryStatus.Failed,
            Error = result.Error,
            AckTag = result.AckTag,
            Flood = result.Flood,
        };
        _store.Replace(key, final);
        ApplyEarly(connector.Id, result.AckTag);
        return new RouteResult(result.Ok, result.Error, key.Id, final);
    }

    // An ack can, in principle, arrive before SendAsync has returned the tag
    // to us. Such early updates are parked briefly and applied once the
    // outbound message carries its tag.
    private readonly Dictionary<string, (DeliveryUpdate Update, DateTimeOffset At)> _early = new(StringComparer.Ordinal);

    private void OnDelivery(IMeshConnector source, DeliveryUpdate update)
    {
        if (_store.UpdateDelivery(source.Id, update.AckTag, update.Status, update.Error, update.RoundTripMs)) return;
        lock (_early)
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-2);
            foreach (var stale in _early.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList()) _early.Remove(stale);
            _early[$"{source.Id}|{update.AckTag}"] = (update, DateTimeOffset.UtcNow);
        }
    }

    private void ApplyEarly(string connectorId, string? ackTag)
    {
        if (ackTag is null) return;
        DeliveryUpdate? update = null;
        lock (_early)
        {
            if (_early.Remove($"{connectorId}|{ackTag}", out var parked)) update = parked.Update;
        }
        if (update is not null) _store.UpdateDelivery(connectorId, ackTag, update.Status, update.Error, update.RoundTripMs);
    }

    private void OnInbound(IMeshConnector source, InboundMessage message)
    {
        // Defence in depth: a connector may only file messages under its own id.
        if (!string.Equals(message.Conversation.ConnectorId, source.Id, StringComparison.Ordinal))
        {
            return;
        }
        if (message.NetworkMessageId is not null &&
            _store.HasNetworkMessage(message.Conversation, message.NetworkMessageId, message.FromId))
        {
            return; // the same packet heard twice
        }
        _store.GetOrAdd(message.Conversation, source.Network, source.TitleFor(message.Conversation));
        _store.Append(message.Conversation, new StoredMessage(
            Id: Guid.NewGuid().ToString("N"),
            Direction: MessageDirection.Inbound,
            FromId: message.FromId,
            FromName: message.FromName,
            Text: message.Text,
            Timestamp: message.SentAt,
            Status: DeliveryStatus.Received,
            Error: null,
            Reception: message.Reception,
            QueuedWhileAway: message.QueuedWhileAway,
            NetworkId: message.NetworkMessageId));
    }

    public void Dispose()
    {
        foreach (var unsubscribe in _unsubscribe) unsubscribe();
        _unsubscribe.Clear();
    }
}

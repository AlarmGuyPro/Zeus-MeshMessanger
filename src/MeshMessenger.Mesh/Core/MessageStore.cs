// SPDX-License-Identifier: GPL-3.0-or-later
namespace MeshMessenger.Core;

/// <summary>Everything the store holds, for saving and restoring across restarts.</summary>
public sealed record HistorySnapshot(int Version, IReadOnlyList<ConversationSnapshot> Conversations);

public sealed record ConversationSnapshot(
    ConversationKey Key,
    MeshNetwork Network,
    string? Title,
    int Unread,
    bool Muted,
    IReadOnlyList<StoredMessage> Messages);

/// <summary>
/// Conversations and their messages. Thread-safe. Bounded: at most
/// <see cref="MaxMessagesPerConversation"/> per conversation and
/// <see cref="MaxConversations"/> conversations (least recently active dropped).
/// </summary>
public sealed class MessageStore
{
    public const int MaxConversations = 200;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _conversations = new(StringComparer.Ordinal);
    private int _maxPerConversation = 500;
    private long _version;

    public int MaxMessagesPerConversation
    {
        get { lock (_gate) return _maxPerConversation; }
        set { lock (_gate) _maxPerConversation = Math.Clamp(value, 20, 5000); }
    }

    /// <summary>Raised (outside the lock) after any change, for persistence.</summary>
    public event Action? Changed;

    /// <summary>Increments on every change; lets callers detect "anything new".</summary>
    public long Version { get { lock (_gate) return _version; } }

    private sealed class Entry(Conversation conversation)
    {
        public Conversation Conversation { get; set; } = conversation;
        public List<StoredMessage> Messages { get; } = [];
        public int Unread { get; set; }
        public bool Muted { get; set; }
        public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.MinValue;
    }

    public sealed record Summary(Conversation Conversation, StoredMessage? Last, int Unread, bool Muted);

    public Conversation GetOrAdd(ConversationKey key, MeshNetwork network, string? title)
    {
        Conversation result;
        var changed = false;
        lock (_gate)
        {
            if (!_conversations.TryGetValue(key.Id, out var entry))
            {
                entry = new Entry(new Conversation(key, network, title)) { LastActivity = DateTimeOffset.UtcNow };
                _conversations.Add(key.Id, entry);
                TrimConversations();
                changed = Touch();
            }
            else if (title is not null && entry.Conversation.Title != title)
            {
                entry.Conversation = entry.Conversation with { Title = title };
                changed = Touch();
            }
            result = entry.Conversation;
        }
        if (changed) Changed?.Invoke();
        return result;
    }

    public Conversation? Find(string conversationId)
    {
        lock (_gate)
        {
            return _conversations.TryGetValue(conversationId, out var entry) ? entry.Conversation : null;
        }
    }

    public void Append(ConversationKey key, StoredMessage message)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(key.Id, out var entry))
            {
                throw new InvalidOperationException($"Unknown conversation {key.Id}");
            }
            entry.Messages.Add(message);
            while (entry.Messages.Count > _maxPerConversation)
            {
                entry.Messages.RemoveAt(0);
            }
            if (message.Direction == MessageDirection.Inbound && !entry.Muted)
            {
                entry.Unread++;
            }
            entry.LastActivity = DateTimeOffset.UtcNow;
            Touch();
        }
        Changed?.Invoke();
    }

    /// <summary>Replaces a message (matched by <see cref="StoredMessage.Id"/>) in its conversation.</summary>
    public bool Replace(ConversationKey key, StoredMessage message)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(key.Id, out var entry)) return false;
            var i = entry.Messages.FindLastIndex(m => m.Id == message.Id);
            if (i < 0) return false;
            entry.Messages[i] = message;
            Touch();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>True when the conversation already has an inbound message with this network id (duplicate delivery).</summary>
    public bool HasNetworkMessage(ConversationKey key, string networkMessageId, string fromId)
    {
        lock (_gate)
        {
            return _conversations.TryGetValue(key.Id, out var entry) &&
                   entry.Messages.Any(m => m.Direction == MessageDirection.Inbound && m.NetworkId == networkMessageId && m.FromId == fromId);
        }
    }

    /// <summary>Applies a delivery update to the outbound message carrying <paramref name="ackTag"/> on that connector.</summary>
    public bool UpdateDelivery(string connectorId, string ackTag, DeliveryStatus status, string? error, int? roundTripMs)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var entry in _conversations.Values)
            {
                if (entry.Conversation.Key.ConnectorId != connectorId) continue;
                for (var i = entry.Messages.Count - 1; i >= 0; i--)
                {
                    var m = entry.Messages[i];
                    if (m.Direction != MessageDirection.Outbound || m.AckTag != ackTag) continue;
                    // Never downgrade a delivered message (a late timeout after the ack).
                    if (m.Status == DeliveryStatus.Delivered && status != DeliveryStatus.Delivered) return false;
                    entry.Messages[i] = m with { Status = status, Error = error, RoundTripMs = roundTripMs ?? m.RoundTripMs };
                    changed = Touch();
                    break;
                }
                if (changed) break;
            }
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    public IReadOnlyList<StoredMessage>? Messages(string conversationId, bool markRead)
    {
        var changed = false;
        IReadOnlyList<StoredMessage>? result;
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out var entry))
            {
                return null;
            }
            if (markRead && entry.Unread != 0)
            {
                entry.Unread = 0;
                changed = Touch();
            }
            result = entry.Messages.ToArray();
        }
        if (changed) Changed?.Invoke();
        return result;
    }

    public bool SetMuted(string conversationId, bool muted)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out var entry)) return false;
            entry.Muted = muted;
            if (muted) entry.Unread = 0;
            Touch();
        }
        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<Summary> Summaries()
    {
        lock (_gate)
        {
            return _conversations.Values
                .OrderByDescending(e => e.Messages.Count > 0 ? e.Messages[^1].Timestamp : e.LastActivity)
                .Select(e => new Summary(e.Conversation, e.Messages.LastOrDefault(), e.Unread, e.Muted))
                .ToArray();
        }
    }

    public HistorySnapshot Snapshot()
    {
        lock (_gate)
        {
            return new HistorySnapshot(1, _conversations.Values
                .Select(e => new ConversationSnapshot(e.Conversation.Key, e.Conversation.Network, e.Conversation.Title,
                    e.Unread, e.Muted, e.Messages.ToArray()))
                .ToArray());
        }
    }

    public void Restore(HistorySnapshot snapshot)
    {
        lock (_gate)
        {
            _conversations.Clear();
            foreach (var c in snapshot.Conversations)
            {
                if (c.Key is null || string.IsNullOrEmpty(c.Key.ConnectorId)) continue;
                var entry = new Entry(new Conversation(c.Key, c.Network, c.Title))
                {
                    Unread = Math.Max(0, c.Unread),
                    Muted = c.Muted,
                };
                // A message still "sending" when Zeus closed never got an answer.
                entry.Messages.AddRange((c.Messages ?? []).TakeLast(_maxPerConversation).Select(m =>
                    m.Status == DeliveryStatus.Sending ? m with { Status = DeliveryStatus.Failed, Error = "Zeus closed before the node answered." } : m));
                entry.LastActivity = entry.Messages.Count > 0 ? entry.Messages[^1].Timestamp : DateTimeOffset.MinValue;
                _conversations[c.Key.Id] = entry;
            }
            TrimConversations();
            Touch();
        }
    }

    private void TrimConversations()
    {
        while (_conversations.Count > MaxConversations)
        {
            var oldest = _conversations.MinBy(kv => kv.Value.LastActivity).Key;
            _conversations.Remove(oldest);
        }
    }

    private bool Touch()
    {
        _version++;
        return true;
    }
}

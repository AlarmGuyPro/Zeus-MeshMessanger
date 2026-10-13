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
    IReadOnlyList<StoredMessage> Messages,
    DateTimeOffset LastActivity = default);

/// <summary>
/// Conversations and their messages. Thread-safe, and bounded three ways: at most
/// <see cref="MaxMessagesPerConversation"/> messages in a conversation,
/// <see cref="MaxConversations"/> conversations, and <see cref="MaxTotalBytes"/> of text
/// in all of them together.
/// </summary>
public sealed class MessageStore
{
    public const int MaxConversations = 200;

    /// <summary>
    /// How much history is kept, in bytes of stored text. History is persisted into the
    /// Zeus settings database, which is shared with the rest of Zeus and holds a document
    /// of about 16 MB; the host replaces a value by deleting the old one first, so a write
    /// that crosses the limit loses what was already saved rather than merely failing. The
    /// budget is therefore a small fraction of it, enforced here by dropping the oldest
    /// material on purpose so that nothing is ever lost by accident.
    /// </summary>
    public const int MaxTotalBytes = 4 * 1024 * 1024;

    /// <summary>No single conversation may fill the whole budget.</summary>
    public const int MaxBytesPerConversation = 192 * 1024;

    /// <summary>
    /// What one message costs once serialised, beyond its text: id, timestamps, sender,
    /// status and JSON punctuation. Deliberately generous — the budget is a ceiling, and
    /// counting precisely would mean serialising on every append.
    /// </summary>
    private const int MessageOverheadBytes = 220;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _conversations = new(StringComparer.Ordinal);
    private int _maxPerConversation = 500;
    private long _version;
    private long _bytes;

    /// <summary>Bytes of history currently held, against <see cref="MaxTotalBytes"/>.</summary>
    public long Bytes { get { lock (_gate) return _bytes; } }

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
        /// <summary>Running total of <see cref="Cost"/> for the messages held here.</summary>
        public long Bytes { get; set; }
    }

    private static int Cost(StoredMessage m) =>
        MessageOverheadBytes +
        System.Text.Encoding.UTF8.GetByteCount(m.Text ?? "") +
        System.Text.Encoding.UTF8.GetByteCount(m.Error ?? "") +
        System.Text.Encoding.UTF8.GetByteCount(m.FromName ?? "");

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
            entry.Bytes += Cost(message);
            _bytes += Cost(message);
            TrimEntry(entry);
            TrimBudget();
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
            Recost(entry, entry.Messages[i], message);
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
                    var updated = m with { Status = status, Error = error, RoundTripMs = roundTripMs ?? m.RoundTripMs };
                    Recost(entry, m, updated);
                    entry.Messages[i] = updated;
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

    /// <summary>Forgets a conversation and its messages. Zeus-side only; the node keeps nothing.</summary>
    public bool Remove(string conversationId)
    {
        lock (_gate)
        {
            if (!_conversations.Remove(conversationId, out var gone)) return false;
            _bytes -= gone.Bytes;
            Touch();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes messages that never went out, so a thread can be tidied after a bad patch.</summary>
    /// <returns>How many were removed, or -1 when the conversation is unknown.</returns>
    public int ClearFailed(string conversationId)
    {
        int removed;
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out var entry)) return -1;
            var freed = entry.Messages
                .Where(m => m.Direction == MessageDirection.Outbound && m.Status is DeliveryStatus.Failed or DeliveryStatus.NotConfirmed)
                .Sum(m => (long)Cost(m));
            removed = entry.Messages.RemoveAll(m => m.Direction == MessageDirection.Outbound && m.Status is DeliveryStatus.Failed or DeliveryStatus.NotConfirmed);
            if (removed == 0) return 0;
            entry.Bytes -= freed;
            _bytes -= freed;
            Touch();
        }
        Changed?.Invoke();
        return removed;
    }

    /// <summary>Finds one message in a conversation, for resending it.</summary>
    public StoredMessage? Message(string conversationId, string messageId)
    {
        lock (_gate)
        {
            return _conversations.TryGetValue(conversationId, out var entry)
                ? entry.Messages.FirstOrDefault(m => m.Id == messageId)
                : null;
        }
    }

    /// <summary>Removes one message from a conversation.</summary>
    public bool RemoveMessage(string conversationId, string messageId)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out var entry)) return false;
            var freed = entry.Messages.Where(m => m.Id == messageId).Sum(m => (long)Cost(m));
            if (entry.Messages.RemoveAll(m => m.Id == messageId) == 0) return false;
            entry.Bytes -= freed;
            _bytes -= freed;
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
                    e.Unread, e.Muted, e.Messages.ToArray(), e.LastActivity))
                .ToArray());
        }
    }

    public void Restore(HistorySnapshot snapshot)
    {
        lock (_gate)
        {
            _conversations.Clear();
            _bytes = 0;
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
                entry.Bytes = entry.Messages.Sum(m => (long)Cost(m));
                entry.LastActivity = c.LastActivity != default ? c.LastActivity
                    : entry.Messages.Count > 0 ? entry.Messages[^1].Timestamp
                    : DateTimeOffset.MinValue;
                _bytes += entry.Bytes;
                TrimEntry(entry);
                _conversations[c.Key.Id] = entry;
            }
            TrimConversations();
            TrimBudget();
            Touch();
        }
    }

    /// <summary>Keeps one conversation inside its message and byte caps, oldest first.</summary>
    private void TrimEntry(Entry entry)
    {
        while (entry.Messages.Count > _maxPerConversation ||
               (entry.Bytes > MaxBytesPerConversation && entry.Messages.Count > 1))
        {
            var cost = Cost(entry.Messages[0]);
            entry.Messages.RemoveAt(0);
            entry.Bytes -= cost;
            _bytes -= cost;
        }
    }

    /// <summary>
    /// Keeps the whole store inside <see cref="MaxTotalBytes"/> by forgetting the
    /// least recently active conversations. Dropping history deliberately is the point:
    /// the alternative is a write that exceeds the host's document limit and loses the lot.
    /// </summary>
    private void TrimBudget()
    {
        while (_bytes > MaxTotalBytes && _conversations.Count > 1)
        {
            var oldest = _conversations.MinBy(kv => kv.Value.LastActivity);
            _conversations.Remove(oldest.Key);
            _bytes -= oldest.Value.Bytes;
        }
    }

    private void TrimConversations()
    {
        while (_conversations.Count > MaxConversations)
        {
            var oldest = _conversations.MinBy(kv => kv.Value.LastActivity);
            _conversations.Remove(oldest.Key);
            _bytes -= oldest.Value.Bytes;
        }
    }

    private void Recost(Entry entry, StoredMessage before, StoredMessage after)
    {
        var delta = Cost(after) - Cost(before);
        entry.Bytes += delta;
        _bytes += delta;
    }

    private bool Touch()
    {
        _version++;
        return true;
    }
}

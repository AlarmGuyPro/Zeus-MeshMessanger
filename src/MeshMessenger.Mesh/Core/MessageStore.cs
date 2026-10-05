// SPDX-License-Identifier: GPL-2.0-or-later
namespace MeshMessenger.Core;

/// <summary>
/// In-memory store of conversations and their messages. Thread-safe.
/// Persistence is a later milestone; see DESIGN.md.
/// </summary>
public sealed class MessageStore
{
    private const int MaxMessagesPerConversation = 500;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _conversations = new(StringComparer.Ordinal);

    private sealed class Entry(Conversation conversation)
    {
        public Conversation Conversation { get; } = conversation;
        public List<StoredMessage> Messages { get; } = [];
        public int Unread { get; set; }
    }

    public sealed record Summary(Conversation Conversation, StoredMessage? Last, int Unread);

    public Conversation GetOrAdd(ConversationKey key, MeshNetwork network, string? title)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(key.Id, out var entry))
            {
                entry = new Entry(new Conversation(key, network, title));
                _conversations.Add(key.Id, entry);
            }
            return entry.Conversation;
        }
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
            if (entry.Messages.Count > MaxMessagesPerConversation)
            {
                entry.Messages.RemoveAt(0);
            }
            if (message.Direction == MessageDirection.Inbound)
            {
                entry.Unread++;
            }
        }
    }

    public IReadOnlyList<StoredMessage>? Messages(string conversationId, bool markRead)
    {
        lock (_gate)
        {
            if (!_conversations.TryGetValue(conversationId, out var entry))
            {
                return null;
            }
            if (markRead)
            {
                entry.Unread = 0;
            }
            return entry.Messages.ToArray();
        }
    }

    public IReadOnlyList<Summary> Summaries()
    {
        lock (_gate)
        {
            return _conversations.Values
                .Select(e => new Summary(e.Conversation, e.Messages.LastOrDefault(), e.Unread))
                .OrderByDescending(s => s.Last?.Timestamp ?? DateTimeOffset.MinValue)
                .ToArray();
        }
    }
}

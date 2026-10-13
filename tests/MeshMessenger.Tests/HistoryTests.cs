// SPDX-License-Identifier: GPL-3.0-or-later
using MeshMessenger.Core;
using MeshMessenger.Services;

namespace MeshMessenger.Tests;

/// <summary>
/// How history is bounded and how it is written. Review finding #2: up to 0.4.1 the whole
/// history was one settings value rewritten every two seconds, so one new message rewrote
/// every message in every conversation, and crossing the host's document limit lost the lot
/// because the host deletes the old value before inserting the new one.
/// </summary>
public static class HistoryTests
{
    private static StoredMessage Message(string id, string text, DateTimeOffset at) =>
        new(id, MessageDirection.Inbound, "someone", "Someone", text, at, DeliveryStatus.Delivered, null);

    private static ConversationKey Add(MessageStore store, string peer)
    {
        var key = new ConversationKey("mc", ConversationKind.Direct, peer);
        store.GetOrAdd(key, MeshNetwork.MeshCore, peer);
        return key;
    }

    [Test]
    public static void AVeryTalkativeConversationIsCappedAndKeepsTheRecentEnd()
    {
        var store = new MessageStore();
        var key = Add(store, "chatty");
        var text = new string('x', 2000);
        var at = DateTimeOffset.UtcNow;
        for (var i = 0; i < 400; i++)
        {
            store.Append(key, Message($"m{i}", $"{i}:{text}", at.AddSeconds(i)));
        }
        var kept = store.Messages(key.Id, false)!;
        Assert.True(store.Bytes <= MessageStore.MaxBytesPerConversation,
            $"conversation held {store.Bytes} bytes, cap is {MessageStore.MaxBytesPerConversation}");
        Assert.True(kept.Count < 400, "older messages were dropped");
        Assert.Equal("m399", kept[^1].Id, "the newest message is kept");
        Assert.True(kept.All(m => m.Id != "m0"), "the oldest message is gone");
    }

    [Test]
    public static void ManyConversationsStayInsideTheTotalBudgetAndTheQuietestGoFirst()
    {
        var store = new MessageStore();
        var text = new string('y', 4000);
        var at = DateTimeOffset.UtcNow.AddDays(-1);
        var keys = new List<ConversationKey>();
        for (var c = 0; c < 60; c++)
        {
            var key = Add(store, $"peer{c:000}");
            keys.Add(key);
            for (var i = 0; i < 40; i++)
            {
                store.Append(key, Message($"c{c}m{i}", text, at.AddMinutes(c * 60 + i)));
            }
        }
        Assert.True(store.Bytes <= MessageStore.MaxTotalBytes,
            $"store held {store.Bytes} bytes, budget is {MessageStore.MaxTotalBytes}");
        // The last conversation to see traffic must still be there, whole.
        Assert.NotNull(store.Messages(keys[^1].Id, false), "the most recent conversation survived");
        Assert.Equal(40, store.Messages(keys[^1].Id, false)!.Count, "and kept its messages");
        // Something had to go, and it was the oldest.
        Assert.Equal(null, store.Messages(keys[0].Id, false), "the least recently active was evicted");
    }

    [Test]
    public static async Task ANewMessageRewritesOneConversationAndNotTheWholeHistory()
    {
        var settings = new MemorySettings();
        var store = new SettingsStore(settings);
        var messages = new MessageStore();
        var a = Add(messages, "aaa");
        var b = Add(messages, "bbb");
        var at = DateTimeOffset.UtcNow;
        messages.Append(a, Message("a1", "first", at));
        messages.Append(b, Message("b1", "first", at));

        await store.SaveHistoryAsync(messages.Snapshot(), default);
        var keyA = SettingsStore.KeyFor(a.Id);
        var keyB = SettingsStore.KeyFor(b.Id);
        Assert.Equal(1, settings.Writes[keyA], "A written once");
        Assert.Equal(1, settings.Writes[keyB], "B written once");

        // Nothing changed: nothing is written, not even the index.
        var report = await store.SaveHistoryAsync(messages.Snapshot(), default);
        Assert.Equal(0, report.Written, "an unchanged history writes nothing");
        Assert.True(report.Quiet, "and reports itself quiet");
        Assert.Equal(1, settings.Writes[SettingsStore.HistoryIndexKey], "the index was not rewritten");

        // One new message in B rewrites B alone.
        messages.Append(b, Message("b2", "second", at.AddSeconds(1)));
        var second = await store.SaveHistoryAsync(messages.Snapshot(), default);
        Assert.Equal(1, second.Written, "one conversation written");
        Assert.Equal(1, settings.Writes[keyA], "A was not touched");
        Assert.Equal(2, settings.Writes[keyB], "B was rewritten");
    }

    [Test]
    public static async Task ADeletedConversationLosesItsStoredValue()
    {
        var settings = new MemorySettings();
        var store = new SettingsStore(settings);
        var messages = new MessageStore();
        var a = Add(messages, "aaa");
        var b = Add(messages, "bbb");
        messages.Append(a, Message("a1", "first", DateTimeOffset.UtcNow));
        messages.Append(b, Message("b1", "first", DateTimeOffset.UtcNow));
        await store.SaveHistoryAsync(messages.Snapshot(), default);
        Assert.True(settings.Values.ContainsKey(SettingsStore.KeyFor(b.Id)), "B stored");

        Assert.True(messages.Remove(b.Id), "conversation forgotten");
        var report = await store.SaveHistoryAsync(messages.Snapshot(), default);
        Assert.Equal(1, report.Deleted, "one value deleted");
        Assert.False(settings.Values.ContainsKey(SettingsStore.KeyFor(b.Id)), "B's value is gone");
        Assert.True(settings.Values.ContainsKey(SettingsStore.KeyFor(a.Id)), "A's value is not");
    }

    [Test]
    public static async Task HistorySavedByTheOldSingleValueLayoutIsStillThereAfterUpgrading()
    {
        // What an operator upgrading from 0.4.1 has in their settings store.
        var settings = new MemorySettings();
        var before = new MessageStore();
        var key = Add(before, "upgrader");
        before.Append(key, Message("old1", "said before the upgrade", DateTimeOffset.UtcNow.AddHours(-2)));
        settings.Values[SettingsStore.LegacyHistoryKey] =
            System.Text.Json.JsonSerializer.Serialize(before.Snapshot(), Json.Options);

        var store = new SettingsStore(settings);
        var loaded = await store.LoadHistoryAsync(default);
        Assert.NotNull(loaded, "the old value was read");
        Assert.Equal(1, loaded!.Version, "and recognised as the old layout");

        var after = new MessageStore();
        after.Restore(loaded);
        Assert.Equal("said before the upgrade", after.Messages(key.Id, false)!.Single().Text, "message survived");

        // What the plugin then does: write the new layout, drop the old value.
        await store.SaveHistoryAsync(after.Snapshot(), default);
        await store.DiscardLegacyHistoryAsync(default);
        Assert.False(settings.Values.ContainsKey(SettingsStore.LegacyHistoryKey), "old value removed");

        // And a restart now reads the new layout.
        var reloaded = await new SettingsStore(settings).LoadHistoryAsync(default);
        Assert.Equal(2, reloaded!.Version, "new layout");
        Assert.Equal("said before the upgrade", reloaded.Conversations.Single().Messages.Single().Text, "still there");
    }

    [Test]
    public static async Task AFreshStartRewritesNothingItAlreadyHas()
    {
        var settings = new MemorySettings();
        var messages = new MessageStore();
        var key = Add(messages, "peer");
        messages.Append(key, Message("m1", "hello", DateTimeOffset.UtcNow));
        await new SettingsStore(settings).SaveHistoryAsync(messages.Snapshot(), default);
        var writesBefore = settings.Writes.Values.Sum();

        // Zeus restarts: load, then the save loop ticks with nothing new.
        var store = new SettingsStore(settings);
        var restored = new MessageStore();
        restored.Restore((await store.LoadHistoryAsync(default))!);
        var report = await store.SaveHistoryAsync(restored.Snapshot(), default);
        Assert.Equal(0, report.Written, "a restart with no new traffic writes nothing");
        Assert.Equal(writesBefore, settings.Writes.Values.Sum(), "no writes at all");
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeshMessenger.Core;
using Zeus.Plugins.Contracts;

namespace MeshMessenger.Services;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>What a history save actually did, for the log.</summary>
internal sealed record SaveReport(int Written, int Deleted, long Bytes)
{
    public bool Quiet => Written == 0 && Deleted == 0;
}

/// <summary>
/// Configuration and history in the plugin-scoped Zeus settings store. Values are JSON
/// strings we serialise ourselves (as PowerStation does), so the stored shape is under our
/// control and versioned by key.
///
/// History is stored one value per conversation, with a small index listing them. Until
/// 1.0 the whole history was a single value rewritten every two seconds, which meant one
/// new message rewrote every message in every conversation into a database shared with the
/// rest of Zeus, and crossing the host's document limit — the host deletes the old value
/// before inserting the new one — lost everything that had already been saved. A value per
/// conversation makes a write proportional to what changed, and the index makes eviction
/// explicit rather than a failed write.
/// </summary>
internal sealed class SettingsStore(IPluginSettings settings)
{
    internal const string ConfigKey = "config.v1";

    /// <summary>The single-value layout used up to 0.4.1. Read once, then deleted.</summary>
    internal const string LegacyHistoryKey = "history.v1";

    internal const string HistoryIndexKey = "history.index.v2";
    internal const string HistoryPrefix = "history.conv.v2.";

    /// <summary>Written on each save so the ids in memory and the values on disk agree.</summary>
    private sealed record HistoryIndex(int Version, IReadOnlyList<string> Conversations);

    // id -> what was last written for it, so an unchanged conversation is not rewritten.
    private readonly Dictionary<string, string> _lastWritten = new(StringComparer.Ordinal);

    public async Task<MeshMessengerConfig> LoadConfigAsync(CancellationToken ct)
    {
        var json = await settings.GetAsync<string>(ConfigKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return new MeshMessengerConfig();
        try
        {
            return JsonSerializer.Deserialize<MeshMessengerConfig>(json, Json.Options) ?? new MeshMessengerConfig();
        }
        catch (JsonException)
        {
            return new MeshMessengerConfig();
        }
    }

    public Task SaveConfigAsync(MeshMessengerConfig config, CancellationToken ct) =>
        settings.SetAsync(ConfigKey, JsonSerializer.Serialize(config, Json.Options), ct);

    /// <summary>
    /// The settings key for a conversation. Conversation ids carry a peer, which is a
    /// public key or a channel index, so they are hashed rather than used directly: the
    /// key stays a fixed, safe length whatever a station calls itself.
    /// </summary>
    internal static string KeyFor(string conversationId) =>
        HistoryPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conversationId)), 0, 10).ToLowerInvariant();

    public async Task<HistorySnapshot?> LoadHistoryAsync(CancellationToken ct)
    {
        var index = await ReadAsync<HistoryIndex>(HistoryIndexKey, ct).ConfigureAwait(false);
        if (index?.Conversations is null) return await MigrateLegacyAsync(ct).ConfigureAwait(false);

        var conversations = new List<ConversationSnapshot>(index.Conversations.Count);
        foreach (var id in index.Conversations)
        {
            var key = KeyFor(id);
            var conversation = await ReadAsync<ConversationSnapshot>(key, ct).ConfigureAwait(false);
            if (conversation?.Key is null) continue;
            conversations.Add(conversation);
            // Remember what is on disk so the first save after start rewrites nothing.
            var json = await settings.GetAsync<string>(key, ct).ConfigureAwait(false);
            if (json is not null) _lastWritten[conversation.Key.Id] = json;
        }
        return new HistorySnapshot(2, conversations);
    }

    /// <summary>Reads the 0.4.1 single-value history, if it is still there, and removes it.</summary>
    private async Task<HistorySnapshot?> MigrateLegacyAsync(CancellationToken ct)
    {
        var legacy = await ReadAsync<HistorySnapshot>(LegacyHistoryKey, ct).ConfigureAwait(false);
        if (legacy?.Conversations is null) return null;
        // The caller restores this into the store, which trims it to the current budget and
        // then saves it in the new layout; the old value goes once that has happened.
        return legacy;
    }

    public async Task DiscardLegacyHistoryAsync(CancellationToken ct)
    {
        try { await settings.DeleteAsync(LegacyHistoryKey, ct).ConfigureAwait(false); }
        catch (Exception) { /* it will be overwritten by the next migration anyway */ }
    }

    /// <summary>
    /// Writes only the conversations whose content changed, deletes the values of
    /// conversations that have gone, and keeps the index in step.
    /// </summary>
    public async Task<SaveReport> SaveHistoryAsync(HistorySnapshot snapshot, CancellationToken ct)
    {
        var written = 0;
        long bytes = 0;
        var live = new List<string>(snapshot.Conversations.Count);

        foreach (var conversation in snapshot.Conversations)
        {
            if (conversation.Key is null) continue;
            var id = conversation.Key.Id;
            live.Add(id);
            var json = JsonSerializer.Serialize(conversation, Json.Options);
            bytes += json.Length;
            if (_lastWritten.TryGetValue(id, out var before) && string.Equals(before, json, StringComparison.Ordinal))
            {
                continue;
            }
            await settings.SetAsync(KeyFor(id), json, ct).ConfigureAwait(false);
            _lastWritten[id] = json;
            written++;
        }

        var deleted = 0;
        foreach (var gone in _lastWritten.Keys.Except(live, StringComparer.Ordinal).ToList())
        {
            await settings.DeleteAsync(KeyFor(gone), ct).ConfigureAwait(false);
            _lastWritten.Remove(gone);
            deleted++;
        }

        // The index is what a later start reads, so it is written last: a crash between the
        // two leaves a stale index entry, which loads as a missing conversation rather than
        // as a conversation whose value was never written.
        if (written > 0 || deleted > 0)
        {
            await settings.SetAsync(HistoryIndexKey,
                JsonSerializer.Serialize(new HistoryIndex(2, live), Json.Options), ct).ConfigureAwait(false);
        }
        return new SaveReport(written, deleted, bytes);
    }

    private async Task<T?> ReadAsync<T>(string key, CancellationToken ct)
    {
        var json = await settings.GetAsync<string>(key, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
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

/// <summary>
/// Configuration and history in the plugin-scoped Zeus settings store. Values
/// are JSON strings we serialise ourselves (as PowerStation does), so the
/// stored shape is under our control and versioned by key.
/// </summary>
internal sealed class SettingsStore(IPluginSettings settings)
{
    internal const string ConfigKey = "config.v1";
    internal const string HistoryKey = "history.v1";

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

    public async Task<HistorySnapshot?> LoadHistoryAsync(CancellationToken ct)
    {
        var json = await settings.GetAsync<string>(HistoryKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<HistorySnapshot>(json, Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task SaveHistoryAsync(HistorySnapshot snapshot, CancellationToken ct) =>
        settings.SetAsync(HistoryKey, JsonSerializer.Serialize(snapshot, Json.Options), ct);
}

// SPDX-License-Identifier: GPL-3.0-or-later
using MeshMessenger.Connectors;
using MeshMessenger.Core;
using MeshMessenger.Discovery;
using MeshMessenger.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MeshMessenger.Api;

// Wire shapes. Enums go out as lowercase strings so the panel never depends on enum ordering.
public sealed record SelfDto(string Identity, string Name, string? ShortName, string? Firmware, string? Model, string? Radio);

public sealed record ChannelDto(int Index, string Name, string Kind, bool Primary);

public sealed record NodeDto(
    string Id, string Network, string Name, string Host, int Port, bool Enabled, string State, string? Detail,
    string? Identity, bool FindIfAddressChanges, string? PendingMove, SelfDto? Self,
    int MaxDirectTextBytes, int MaxChannelTextBytes, IReadOnlyList<ChannelDto> Channels, int PeerCount,
    int? PeerCapacity, bool? RecycleOldestWhenFull, bool CanAnnounce, bool CanEditChannels,
    int PeersSavedByTheNode = 0, AutoAddDto? AutoAdd = null);

/// <summary>
/// What the node saves on its own. <c>selective</c> false means it saves everything it hears
/// and <c>kinds</c> is ignored by the firmware — both have to change together to stop a kind
/// being saved. <c>maxHops</c> 0 means no distance limit.
/// </summary>
public sealed record AutoAddDto(bool Selective, bool Companions, bool Repeaters, bool RoomServers, bool Sensors, int MaxHops);

public sealed record AutoAddRequest(bool? Selective, bool? Companions, bool? Repeaters, bool? RoomServers, bool? Sensors, int? MaxHops);

public sealed record StatusDto(IReadOnlyList<NodeDto> Nodes, IReadOnlyList<string> ScanRanges, int HistoryPerConversation,
    long Version, string PluginVersion = "");

public sealed record PeerDto(
    string Id, string Name, string? ShortName, string Kind, DateTimeOffset? LastHeard, double? Snr, int? Hops,
    int? BatteryPercent, double? Latitude, double? Longitude, bool Favorite, bool SavedOnNode,
    DateTimeOffset? LastUpdated);

public sealed record ReceptionDto(double? Snr, int? Rssi, int? Hops, bool Direct, bool ViaMqtt);

public sealed record MessageDto(
    string Id, string Direction, string FromId, string? FromName, string Text, DateTimeOffset Timestamp,
    string Status, string? Error, ReceptionDto? Reception, bool QueuedWhileAway, int? RoundTripMs, bool Flood,
    /// <summary>Peers a direct conversation could be opened with for this message's sender. Only set in channel and room threads.</summary>
    string[]? SenderPeers = null);

public sealed record ConversationDto(
    string Id, string ConnectorId, string Network, string Kind, string Peer, string? Title,
    MessageDto? Last, int Unread, bool Muted);

public sealed record ReplyRequest(string? Text);

public sealed record MuteRequest(bool Muted);

public sealed record StartConversationRequest(string? ConnectorId, string? Kind, string? Peer, string? Text);

public sealed record SendResponse(bool Ok, string? Error, string? ConversationId, MessageDto? Message);

public sealed record ScanRequest(List<string>? Ranges, bool? Mdns);

public sealed record FoundDto(string Network, string Host, int Port, string Identity, string Name, string? ShortName,
    string? Firmware, string? Model, IReadOnlyList<string> FoundBy, string? AddedAs);

public sealed record ScanDto(bool Running, string Phase, int Probed, int Total, IReadOnlyList<string> Networks,
    IReadOnlyList<string> QuietNetworks, bool UsedMdns, IReadOnlyList<FoundDto> Found, int OtherAnswers,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, bool Cancelled, string? Error);

public sealed record DiscoveryDto(ScanDto Scan, IReadOnlyList<string> DefaultNetworks);

public sealed record SettingsRequest(List<string>? ScanRanges, int? HistoryPerConversation);

public sealed record ErrorDto(string Error);

public sealed record LogEntryDto(DateTimeOffset At, string Level, string Text);

public sealed record NodeDebugDto(
    string Id, string Name, string Network, string State, string? Detail, string Host, int Port,
    string? Identity, SelfDto? Self, int Contacts, int ContactsHeardOnly, int? ContactCapacity, int Channels,
    bool CanAnnounce, bool? RecycleOldestWhenFull, IReadOnlyList<LogEntryDto> Activity,
    int? RememberedPeerLimit = null);

public sealed record DebugDto(DateTimeOffset At, string PluginVersion, IReadOnlyList<NodeDebugDto> Nodes,
    HistoryDto? History = null);

/// <summary>How much history Zeus is holding, against the budget it keeps itself inside.</summary>
public sealed record HistoryDto(int Conversations, long Bytes, long Budget);

public sealed record AnnounceRequest(bool? Flood);

public sealed record RecycleRequest(bool Enabled);

/// <summary>Join a channel. A "#name" derives its own key; anything else needs <c>Secret</c>, 32 hex characters.</summary>
public sealed record JoinChannelRequest(string? Name, string? Secret);

public sealed record FavouriteRequest(bool Favourite);

public sealed record ActionResultDto(bool Ok, string? Error, int Count);

/// <summary>Which contacts to clear off a node. <c>Preview</c> counts them without removing anything.</summary>
public sealed record CleanupRequest(bool? Repeaters, int? NotHeardDays, bool? Preview);

/// <summary>
/// HTTP surface under <c>/api/plugins/io.github.alarmguypro.meshmessenger/</c>.
/// Note there is no endpoint that sends "on network X": replies are addressed
/// by conversation id only.
/// </summary>
internal static class Endpoints
{
    /// <summary>
    /// The version, taken from the manifest Zeus loaded. <c>plugin.json</c> is the one place
    /// it is written down; nothing here keeps a copy of it to fall out of step.
    /// </summary>
    private static string _pluginVersion = "";

    public static void Map(IEndpointRouteBuilder e, Func<NodeManager?> nodes, string pluginVersion = "")
    {
        _pluginVersion = pluginVersion;
        // ---------------------------------------------------------------- status & nodes
        e.MapGet("status", () => nodes() is { } m ? Results.Ok(Status(m)) : Unavailable());

        e.MapGet("nodes/{id}/peers", (string id) =>
        {
            if (nodes() is not { } m) return Unavailable();
            var c = m.Connector(id);
            return c is null ? Results.NotFound() : Results.Ok(c.Peers.OrderByDescending(p => p.LastHeard).Select(PeerToDto).ToArray());
        });

        e.MapPost("nodes", async (AddNodeRequest body, CancellationToken ct) =>
        {
            if (nodes() is not { } m) return Unavailable();
            var (node, error) = await m.AddAsync(body, ct);
            return node is null ? Results.BadRequest(new ErrorDto(error ?? "Couldn't add the node.")) : Results.Ok(new { id = node.Id });
        });

        e.MapMethods("nodes/{id}", ["PATCH"], async (string id, UpdateNodeRequest body, CancellationToken ct) =>
        {
            if (nodes() is not { } m) return Unavailable();
            var error = await m.UpdateAsync(id, body, ct);
            return error is null ? Results.Ok(Status(m)) : Results.BadRequest(new ErrorDto(error));
        });

        e.MapDelete("nodes/{id}", async (string id, CancellationToken ct) =>
        {
            if (nodes() is not { } m) return Unavailable();
            return await m.RemoveAsync(id, ct) ? Results.Ok(Status(m)) : Results.NotFound();
        });

        e.MapPost("nodes/{id}/repair", async (string id, CancellationToken ct) =>
        {
            if (nodes() is not { } m) return Unavailable();
            return await m.RepairAsync(id, ct) ? Results.Ok(Status(m)) : Results.NotFound();
        });

        e.MapPost("nodes/{id}/move", (string id) =>
        {
            if (nodes() is not { } m) return Unavailable();
            return m.Connector(id) is { } c && c.AcceptMove() ? Results.Ok(Status(m)) : Results.NotFound();
        });

        e.MapDelete("nodes/{id}/move", (string id) =>
        {
            if (nodes() is not { } m) return Unavailable();
            return m.Connector(id) is { } c && c.IgnoreMove() ? Results.Ok(Status(m)) : Results.NotFound();
        });

        e.MapPut("settings", async (SettingsRequest body, CancellationToken ct) =>
        {
            if (nodes() is not { } m) return Unavailable();
            if (body.ScanRanges is not null)
            {
                var error = await m.SetScanRangesAsync(body.ScanRanges, ct);
                if (error is not null) return Results.BadRequest(new ErrorDto(error));
            }
            if (body.HistoryPerConversation is { } h) await m.SetHistoryLimitAsync(h, ct);
            return Results.Ok(Status(m));
        });

        // ---------------------------------------------------------------- discovery
        e.MapGet("discovery", () => nodes() is { } m ? Results.Ok(Discovery(m)) : Unavailable());

        e.MapPost("discovery/scan", (ScanRequest? body) =>
        {
            if (nodes() is not { } m) return Unavailable();
            return m.Discovery.StartScan(body?.Ranges, body?.Mdns, out var error)
                ? Results.Ok(Discovery(m))
                : Results.BadRequest(new ErrorDto(error ?? "Couldn't start the scan."));
        });

        e.MapDelete("discovery/scan", () =>
        {
            if (nodes() is not { } m) return Unavailable();
            m.Discovery.CancelScan();
            return Results.Ok(Discovery(m));
        });

        // ---------------------------------------------------------------- conversations
        // Channels the node has joined are listed even before anyone talks on them,
        // so Public and your hashtag channels are always there to post to.
        e.MapGet("conversations", () => nodes() is { } m ? Results.Ok(Conversations(m)) : Unavailable());

        e.MapGet("conversations/{id}/messages", (string id, bool? markRead) =>
        {
            if (nodes() is not { } m) return Unavailable();
            var messages = m.Store.Messages(id, markRead ?? true);
            // A joined channel nobody has used yet is listed but has no history: that's empty, not missing.
            if (messages is null) return Listed(m, id) ? Results.Ok(Array.Empty<MessageDto>()) : Results.NotFound();
            // In a channel or room thread, work out who each sender is so the
            // operator can message them directly. Pointless in a direct thread,
            // where there is only ever one other station.
            IMeshConnector? connector = null;
            if (ConversationKey.TryParse(id, out var ck) && ck.Kind != ConversationKind.Direct)
            {
                connector = m.Router?.Connector(ck.ConnectorId);
            }
            return Results.Ok(messages.Select(msg => MessageToDto(msg, SenderPeers(connector, ck.Kind, msg))).ToArray());
        });

        e.MapPost("conversations/{id}/mute", (string id, MuteRequest body) =>
        {
            if (nodes() is not { } m) return Unavailable();
            if (m.Store.SetMuted(id, body.Muted)) return Results.Ok();
            // Muting a channel with no history yet: remember it by creating the conversation.
            if (!Listed(m, id) || !ConversationKey.TryParse(id, out var key)) return Results.NotFound();
            var connector = m.Router?.Connector(key.ConnectorId);
            if (connector is null) return Results.NotFound();
            m.Store.GetOrAdd(key, connector.Network, connector.TitleFor(key));
            return m.Store.SetMuted(id, body.Muted) ? Results.Ok() : Results.NotFound();
        });

        e.MapPost("conversations/{id}/reply", async (string id, ReplyRequest body, CancellationToken ct) =>
        {
            if (nodes() is not { Router: { } r }) return Unavailable();
            return ToResult(await r.ReplyAsync(id, body.Text ?? "", ct));
        });

        // ---------------------------------------------------------------- housekeeping

        e.MapDelete("conversations/{id}", (string id) =>
        {
            if (nodes() is not { } m) return Unavailable();
            if (m.Store.Remove(id)) return Results.Ok();
            // Nothing stored for a joined channel with no history; it stays listed because you are still joined.
            return Listed(m, id) ? Results.Ok() : Results.NotFound();
        });

        e.MapPost("conversations/{id}/clear-failed", (string id) =>
        {
            if (nodes() is not { } m) return Unavailable();
            var removed = m.Store.ClearFailed(id);
            if (removed < 0) return Listed(m, id) ? Results.Ok(new ActionResultDto(true, null, 0)) : Results.NotFound();
            return Results.Ok(new ActionResultDto(true, null, removed));
        });

        e.MapPost("conversations/{id}/messages/{messageId}/retry", async (string id, string messageId, CancellationToken ct) =>
        {
            if (nodes() is not { Router: { } r } m) return Unavailable();
            var message = m.Store.Message(id, messageId);
            if (message is null) return Results.NotFound();
            if (message.Direction != MessageDirection.Outbound)
            {
                return Results.BadRequest(new SendResponse(false, "Only messages you sent can be retried.", id, null));
            }
            var result = await r.ReplyAsync(id, message.Text, ct);
            if (result.Ok) m.Store.RemoveMessage(id, messageId); // the retry replaces the failed entry
            return ToResult(result);
        });

        // ---------------------------------------------------------------- contacts & debug

        e.MapPost("nodes/{id}/refresh-peers", async (string id, CancellationToken ct) =>
        {
            if (nodes() is not { Router: { } r }) return Unavailable();
            if (r.Connector(id) is not { } c) return Results.NotFound();
            await c.RefreshPeersAsync(ct);
            return Results.Ok(new ActionResultDto(true, null, c.Peers.Count));
        });

        // Transmits: only ever from an explicit button press.
        e.MapPost("nodes/{id}/announce", async (string id, AnnounceRequest? body, CancellationToken ct) =>
        {
            if (nodes() is not { Router: { } r }) return Unavailable();
            if (r.Connector(id) is null) return Results.NotFound();
            var error = await r.AnnounceAsync(id, body?.Flood ?? true, ct);
            return Results.Ok(new ActionResultDto(error is null, error, 0));
        });

        e.MapDelete("nodes/{id}/peers/{peerId}", async (string id, string peerId, CancellationToken ct) =>
        {
            if (nodes() is not { Router: { } r }) return Unavailable();
            if (r.Connector(id) is not { } c) return Results.NotFound();
            var error = await c.RemovePeerAsync(peerId, ct);
            return Results.Ok(new ActionResultDto(error is null, error, c.Peers.Count));
        });

        e.MapPost("nodes/{id}/peers/cleanup", async (string id, CleanupRequest body, CancellationToken ct) =>
        {
            if (nodes() is not { Router: { } r }) return Unavailable();
            if (r.Connector(id) is not { } c) return Results.NotFound();
            var doomed = Doomed(c, body).ToList();
            // Preview first: the panel shows the count and asks before anything is removed.
            if (body.Preview == true) return Results.Ok(new ActionResultDto(true, null, doomed.Count));
            // One bulk call, not a loop of single removals: the connector paces itself and
            // reads the contact list back once at the end instead of once per contact.
            var result = await c.RemovePeersAsync(doomed.Select(p => p.Id).ToList(), ct).ConfigureAwait(false);
            return Results.Ok(new ActionResultDto(result.Error is null, result.Error, result.Removed));
        });

        e.MapPost("nodes/{id}/autoadd", async (string id, AutoAddRequest body, CancellationToken ct) =>
        {
            if (nodes() is not { Router: { } r }) return Unavailable();
            if (r.Connector(id) is not { } c) return Results.NotFound();
            if (c.AutoAdd is not { } now) return Results.BadRequest(new { error = "This node can't report that setting." });
            // Anything the request leaves out keeps its current value, so the panel can send
            // one changed field without having to restate the rest.
            var kinds = AutoAddKinds.None;
            if (body.Companions ?? now.Kinds.HasFlag(AutoAddKinds.Companions)) kinds |= AutoAddKinds.Companions;
            if (body.Repeaters ?? now.Kinds.HasFlag(AutoAddKinds.Repeaters)) kinds |= AutoAddKinds.Repeaters;
            if (body.RoomServers ?? now.Kinds.HasFlag(AutoAddKinds.RoomServers)) kinds |= AutoAddKinds.RoomServers;
            if (body.Sensors ?? now.Kinds.HasFlag(AutoAddKinds.Sensors)) kinds |= AutoAddKinds.Sensors;
            var wanted = new AutoAddPolicy(body.Selective ?? now.Selective, kinds, body.MaxHops ?? now.MaxHops);
            var error = await c.SetAutoAddAsync(wanted, ct).ConfigureAwait(false);
            return Results.Ok(new ActionResultDto(error is null, error, 0));
        });

        e.MapPost("nodes/{id}/channels", async (string id, JoinChannelRequest body, CancellationToken ct) =>
        {
            if (nodes() is not { Router: { } r }) return Unavailable();
            if (r.Connector(id) is not { } c) return Results.NotFound();
            var error = await c.JoinChannelAsync(body.Name ?? "", body.Secret, ct);
            return Results.Ok(new ActionResultDto(error is null, error, c.Channels.Count));
        });

        e.MapDelete("nodes/{id}/channels/{index:int}", async (string id, int index, CancellationToken ct) =>
        {
            if (nodes() is not { Router: { } r }) return Unavailable();
            if (r.Connector(id) is not { } c) return Results.NotFound();
            var error = await c.LeaveChannelAsync(index, ct);
            return Results.Ok(new ActionResultDto(error is null, error, c.Channels.Count));
        });

        e.MapPost("nodes/{id}/recycle-oldest", async (string id, RecycleRequest body, CancellationToken ct) =>
        {
            if (nodes() is not { Router: { } r }) return Unavailable();
            if (r.Connector(id) is not { } c) return Results.NotFound();
            var error = await c.SetRecycleOldestWhenFullAsync(body.Enabled, ct);
            return Results.Ok(new ActionResultDto(error is null, error, 0));
        });

        e.MapPost("nodes/{id}/peers/{peerId}/favourite", async (string id, string peerId, FavouriteRequest body, CancellationToken ct) =>
        {
            if (nodes() is not { Router: { } r }) return Unavailable();
            if (r.Connector(id) is not { } c) return Results.NotFound();
            var error = await c.SetPeerFavouriteAsync(peerId, body.Favourite, ct);
            return Results.Ok(new ActionResultDto(error is null, error, 0));
        });

        e.MapGet("debug", () => nodes() is { } m ? Results.Ok(Debug(m)) : Unavailable());

        e.MapPost("conversations", async (StartConversationRequest body, CancellationToken ct) =>
        {
            if (nodes() is not { Router: { } r }) return Unavailable();
            if (string.IsNullOrWhiteSpace(body.ConnectorId))
            {
                return Results.BadRequest(new SendResponse(false, "Choose which node to send from.", null, null));
            }
            if (!TryParseKind(body.Kind, out var kind))
            {
                return Results.BadRequest(new SendResponse(false, "Kind must be 'channel' or 'direct'.", null, null));
            }
            return ToResult(await r.StartAsync(body.ConnectorId, kind, body.Peer ?? "", body.Text ?? "", ct));
        });
    }

    /// <summary>
    /// The contacts a cleanup would remove. Favourites are never touched —
    /// that is the whole point of starring someone — and neither are rooms,
    /// since losing a room server costs you the room. A station whose age we
    /// don't know is left alone rather than guessed at.
    /// </summary>
    private static IEnumerable<PeerInfo> Doomed(IMeshConnector c, CleanupRequest body)
    {
        var cutoff = body.NotHeardDays is > 0 ? DateTimeOffset.UtcNow.AddDays(-body.NotHeardDays.Value) : (DateTimeOffset?)null;
        foreach (var p in c.Peers)
        {
            if (p.Favorite || p.Kind == PeerKind.Room || !p.SavedOnNode) continue;
            if (body.Repeaters == true && p.Kind == PeerKind.Repeater) { yield return p; continue; }
            if (cutoff is null) continue;
            var seen = p.LastHeard ?? p.LastUpdated;
            if (seen is not null && seen < cutoff) yield return p;
        }
    }

    /// <summary>True when the id names a conversation Zeus shows, even if nothing is stored for it yet.</summary>
    private static bool Listed(NodeManager m, string id) =>
        ConversationKey.TryParse(id, out var key) && m.Router?.Connector(key.ConnectorId) is { } c &&
        (key.Kind != ConversationKind.Channel || c.Channels.Any(ch => ch.Index.ToString() == key.Peer));

    private static DebugDto Debug(NodeManager m)
    {
        var connectors = (m.Router?.Connectors ?? []).ToDictionary(c => c.Id, StringComparer.Ordinal);
        var list = new List<NodeDebugDto>();
        foreach (var n in m.Config.Nodes)
        {
            connectors.TryGetValue(n.Id, out var c);
            var peers = c?.Peers ?? [];
            list.Add(new NodeDebugDto(
                n.Id, c?.DisplayName ?? DisplayName(n), Lower(n.Network),
                c is null ? (n.Enabled ? "disconnected" : "disabled") : Lower(c.State),
                c?.StateDetail, c?.Host ?? n.Host, PortOf(n), n.Identity,
                c?.Self is { } s ? new SelfDto(s.Identity, s.Name, s.ShortName, s.Firmware, s.Model, s.RadioSummary) : null,
                peers.Count(p => p.SavedOnNode), peers.Count(p => !p.SavedOnNode), c?.PeerCapacity,
                c?.Channels.Count ?? 0, c?.CanAnnounce ?? false, c?.RecycleOldestWhenFull,
                (c?.Activity ?? []).Select(a => new LogEntryDto(a.At, Lower(a.Level), a.Text)).ToArray(),
                c is null || c.RememberedPeerLimit == int.MaxValue ? null : c.RememberedPeerLimit));
        }
        return new DebugDto(DateTimeOffset.UtcNow, _pluginVersion, list,
            new HistoryDto(m.Store.Summaries().Count, m.Store.Bytes, MessageStore.MaxTotalBytes));
    }

    private static IResult Unavailable() =>
        Results.Problem("Mesh Messenger is not initialized.", statusCode: StatusCodes.Status503ServiceUnavailable);

    // A failed send is a normal outcome (node offline, too long), not an HTTP error:
    // 200 with ok=false so the panel can show the reason next to the message.
    private static IResult ToResult(MessageRouter.RouteResult result) =>
        Results.Ok(new SendResponse(result.Ok, result.Error, result.ConversationId,
            result.Message is null ? null : MessageToDto(result.Message)));

    private static bool TryParseKind(string? value, out ConversationKind kind)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "channel":
                kind = ConversationKind.Channel;
                return true;
            case "direct":
                kind = ConversationKind.Direct;
                return true;
            case "room":
                kind = ConversationKind.Room;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static StatusDto Status(NodeManager m)
    {
        var configs = m.Config.Nodes.ToArray();
        var list = configs.Select(n =>
        {
            var c = m.Connector(n.Id);
            return c is null
                ? new NodeDto(n.Id, Lower(n.Network), DisplayName(n), n.Host, PortOf(n), n.Enabled, n.Enabled ? "disconnected" : "disabled",
                    n.Enabled ? null : "Turned off in settings.", n.Identity, n.FindIfAddressChanges, null, null, 0, 0, [], 0, null, null, false, false)
                : NodeToDto(n, c);
        }).ToArray();
        return new StatusDto(list, m.Config.ScanRanges ?? [], m.Config.HistoryPerConversation, m.Store.Version, _pluginVersion);
    }

    private static string DisplayName(NodeConfig n) => string.IsNullOrWhiteSpace(n.Name) ? n.Id : n.Name;

    private static int PortOf(NodeConfig n) => n.Port > 0 ? n.Port : n.Network == MeshNetwork.MeshCore ? 5000 : 4403;

    private static NodeDto NodeToDto(NodeConfig n, MeshConnectorBase c) => new(
        c.Id, Lower(c.Network), c.DisplayName, c.Host, c.Port, n.Enabled, Lower(c.State), c.StateDetail,
        c.Identity, n.FindIfAddressChanges, c.PendingMove,
        c.Self is null ? null : new SelfDto(c.Self.Identity, c.Self.Name, c.Self.ShortName, c.Self.Firmware, c.Self.Model, c.Self.RadioSummary),
        c.MaxTextBytes(ConversationKind.Direct), c.MaxTextBytes(ConversationKind.Channel),
        c.Channels.Select(ch => new ChannelDto(ch.Index, ch.Name, Lower(ch.Kind), ch.Primary)).ToArray(),
        c.Peers.Count, c.PeerCapacity, c.RecycleOldestWhenFull, c.CanAnnounce, c.CanEditChannels,
        c.PeersSavedByTheNode,
        c.AutoAdd is { } a
            ? new AutoAddDto(a.Selective, a.Kinds.HasFlag(AutoAddKinds.Companions), a.Kinds.HasFlag(AutoAddKinds.Repeaters),
                a.Kinds.HasFlag(AutoAddKinds.RoomServers), a.Kinds.HasFlag(AutoAddKinds.Sensors), a.MaxHops)
            : null);

    private static PeerDto PeerToDto(PeerInfo p) => new(
        p.Id, p.Name, p.ShortName, Lower(p.Kind), p.LastHeard, p.Snr, p.Hops, p.BatteryPercent, p.Latitude, p.Longitude, p.Favorite, p.SavedOnNode, p.LastUpdated);

    private static DiscoveryDto Discovery(NodeManager m)
    {
        var s = m.Discovery.State;
        var known = m.Config.Nodes.Where(n => n.Identity is not null)
            .ToDictionary(n => n.Identity!, n => n.Id, StringComparer.OrdinalIgnoreCase);
        var found = s.Found.Select(f => new FoundDto(
            Lower(f.Node.Network), f.Node.Host, f.Node.Port, f.Node.Identity, f.Node.Name, f.Node.ShortName,
            f.Node.Firmware, f.Node.Model, f.FoundBy, known.TryGetValue(f.Node.Identity, out var id) ? id : null)).ToArray();
        return new DiscoveryDto(
            new ScanDto(s.Running, s.Phase, s.Probed, s.Total, s.Networks, s.QuietNetworks, s.UsedMdns, found, s.OtherAnswers,
                s.StartedAt, s.FinishedAt, s.Cancelled, s.Error),
            m.Discovery.DefaultNetworks());
    }

    private static ConversationDto[] Conversations(NodeManager m)
    {
        var list = m.Store.Summaries().Select(ConversationToDto).ToList();
        var seen = list.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var c in m.Router?.Connectors ?? [])
        {
            foreach (var channel in c.Channels)
            {
                var key = new ConversationKey(c.Id, ConversationKind.Channel, channel.Index.ToString());
                if (seen.Add(key.Id))
                {
                    list.Add(new ConversationDto(key.Id, c.Id, Lower(c.Network), "channel", key.Peer,
                        channel.Name, null, 0, false));
                }
            }
        }
        return list.ToArray();
    }

    private static ConversationDto ConversationToDto(MessageStore.Summary s) => new(
        s.Conversation.Key.Id, s.Conversation.Key.ConnectorId, Lower(s.Conversation.Network),
        Lower(s.Conversation.Key.Kind), s.Conversation.Key.Peer, s.Conversation.Title,
        s.Last is null ? null : MessageToDto(s.Last), s.Unread, s.Muted);

    /// <summary>Who an inbound channel or room message could be answered privately. Null elsewhere, so the UI shows nothing.</summary>
    private static string[]? SenderPeers(IMeshConnector? c, ConversationKind kind, StoredMessage m)
    {
        if (c is null || m.Direction != MessageDirection.Inbound) return null;
        var peers = c.ResolveSenderPeers(kind, m.FromId, m.FromName);
        return peers.Count == 0 ? [] : peers.ToArray();
    }

    private static MessageDto MessageToDto(StoredMessage m, string[]? senderPeers = null) => new(
        m.Id, Lower(m.Direction), m.FromId, m.FromName, m.Text, m.Timestamp, Lower(m.Status), m.Error,
        m.Reception is null ? null : new ReceptionDto(m.Reception.Snr, m.Reception.Rssi, m.Reception.Hops, m.Reception.Direct, m.Reception.ViaMqtt),
        m.QueuedWhileAway, m.RoundTripMs, m.Flood, senderPeers);
}

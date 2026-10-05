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
    int MaxDirectTextBytes, int MaxChannelTextBytes, IReadOnlyList<ChannelDto> Channels, int PeerCount);

public sealed record StatusDto(IReadOnlyList<NodeDto> Nodes, IReadOnlyList<string> ScanRanges, int HistoryPerConversation, long Version);

public sealed record PeerDto(
    string Id, string Name, string? ShortName, string Kind, DateTimeOffset? LastHeard, double? Snr, int? Hops,
    int? BatteryPercent, double? Latitude, double? Longitude, bool Favorite);

public sealed record ReceptionDto(double? Snr, int? Rssi, int? Hops, bool Direct, bool ViaMqtt);

public sealed record MessageDto(
    string Id, string Direction, string FromId, string? FromName, string Text, DateTimeOffset Timestamp,
    string Status, string? Error, ReceptionDto? Reception, bool QueuedWhileAway, int? RoundTripMs, bool Flood);

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

/// <summary>
/// HTTP surface under <c>/api/plugins/io.github.alarmguypro.meshmessenger/</c>.
/// Note there is no endpoint that sends "on network X": replies are addressed
/// by conversation id only.
/// </summary>
internal static class Endpoints
{
    public static void Map(IEndpointRouteBuilder e, Func<NodeManager?> nodes)
    {
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
        e.MapGet("conversations", () => nodes() is { } m
            ? Results.Ok(m.Store.Summaries().Select(ConversationToDto).ToArray())
            : Unavailable());

        e.MapGet("conversations/{id}/messages", (string id, bool? markRead) =>
        {
            if (nodes() is not { } m) return Unavailable();
            var messages = m.Store.Messages(id, markRead ?? true);
            return messages is null ? Results.NotFound() : Results.Ok(messages.Select(MessageToDto).ToArray());
        });

        e.MapPost("conversations/{id}/mute", (string id, MuteRequest body) =>
        {
            if (nodes() is not { } m) return Unavailable();
            return m.Store.SetMuted(id, body.Muted) ? Results.Ok() : Results.NotFound();
        });

        e.MapPost("conversations/{id}/reply", async (string id, ReplyRequest body, CancellationToken ct) =>
        {
            if (nodes() is not { Router: { } r }) return Unavailable();
            return ToResult(await r.ReplyAsync(id, body.Text ?? "", ct));
        });

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
                    n.Enabled ? null : "Turned off in settings.", n.Identity, n.FindIfAddressChanges, null, null, 0, 0, [], 0)
                : NodeToDto(n, c);
        }).ToArray();
        return new StatusDto(list, m.Config.ScanRanges ?? [], m.Config.HistoryPerConversation, m.Store.Version);
    }

    private static string DisplayName(NodeConfig n) => string.IsNullOrWhiteSpace(n.Name) ? n.Id : n.Name;

    private static int PortOf(NodeConfig n) => n.Port > 0 ? n.Port : n.Network == MeshNetwork.MeshCore ? 5000 : 4403;

    private static NodeDto NodeToDto(NodeConfig n, MeshConnectorBase c) => new(
        c.Id, Lower(c.Network), c.DisplayName, c.Host, c.Port, n.Enabled, Lower(c.State), c.StateDetail,
        c.Identity, n.FindIfAddressChanges, c.PendingMove,
        c.Self is null ? null : new SelfDto(c.Self.Identity, c.Self.Name, c.Self.ShortName, c.Self.Firmware, c.Self.Model, c.Self.RadioSummary),
        c.MaxTextBytes(ConversationKind.Direct), c.MaxTextBytes(ConversationKind.Channel),
        c.Channels.Select(ch => new ChannelDto(ch.Index, ch.Name, Lower(ch.Kind), ch.Primary)).ToArray(),
        c.Peers.Count);

    private static PeerDto PeerToDto(PeerInfo p) => new(
        p.Id, p.Name, p.ShortName, Lower(p.Kind), p.LastHeard, p.Snr, p.Hops, p.BatteryPercent, p.Latitude, p.Longitude, p.Favorite);

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

    private static ConversationDto ConversationToDto(MessageStore.Summary s) => new(
        s.Conversation.Key.Id, s.Conversation.Key.ConnectorId, Lower(s.Conversation.Network),
        Lower(s.Conversation.Key.Kind), s.Conversation.Key.Peer, s.Conversation.Title,
        s.Last is null ? null : MessageToDto(s.Last), s.Unread, s.Muted);

    private static MessageDto MessageToDto(StoredMessage m) => new(
        m.Id, Lower(m.Direction), m.FromId, m.FromName, m.Text, m.Timestamp, Lower(m.Status), m.Error,
        m.Reception is null ? null : new ReceptionDto(m.Reception.Snr, m.Reception.Rssi, m.Reception.Hops, m.Reception.Direct, m.Reception.ViaMqtt),
        m.QueuedWhileAway, m.RoundTripMs, m.Flood);
}

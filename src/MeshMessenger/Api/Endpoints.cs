// SPDX-License-Identifier: GPL-3.0-or-later
using MeshMessenger.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MeshMessenger.Api;

// Wire shapes. Enums are sent as lowercase strings so the panel never depends on enum ordering.
public sealed record NodeDto(
    string Id, string Network, string Name, string State, string? Detail,
    int MaxDirectTextBytes, int MaxChannelTextBytes);

public sealed record ConversationDto(
    string Id, string ConnectorId, string Network, string Kind, string Peer, string? Title,
    MessageDto? Last, int Unread);

public sealed record MessageDto(
    string Id, string Direction, string FromId, string? FromName, string Text,
    DateTimeOffset Timestamp, string Status, string? Error);

public sealed record StatusDto(IReadOnlyList<NodeDto> Nodes);

public sealed record ReplyRequest(string? Text);

public sealed record StartConversationRequest(string? ConnectorId, string? Kind, string? Peer, string? Text);

public sealed record SendResponse(bool Ok, string? Error, string? ConversationId, MessageDto? Message);

/// <summary>
/// HTTP surface under <c>/api/plugins/io.github.alarmguypro.meshmessenger/</c>.
/// Note there is no endpoint that sends "on network X": replies are addressed
/// by conversation id only.
/// </summary>
internal static class Endpoints
{
    public static void Map(IEndpointRouteBuilder endpoints, Func<MessageRouter?> router, Func<MessageStore?> store)
    {
        endpoints.MapGet("status", () => router() is { } r
            ? Results.Ok(new StatusDto(r.Connectors.Select(NodeToDto).ToArray()))
            : Unavailable());

        endpoints.MapGet("conversations", () => store() is { } s
            ? Results.Ok(s.Summaries().Select(ConversationToDto).ToArray())
            : Unavailable());

        endpoints.MapGet("conversations/{id}/messages", (string id, bool? markRead) =>
        {
            if (store() is not { } s)
            {
                return Unavailable();
            }
            var messages = s.Messages(id, markRead ?? true);
            return messages is null ? Results.NotFound() : Results.Ok(messages.Select(MessageToDto).ToArray());
        });

        endpoints.MapPost("conversations/{id}/reply", async (string id, ReplyRequest body, CancellationToken ct) =>
        {
            if (router() is not { } r)
            {
                return Unavailable();
            }
            var result = await r.ReplyAsync(id, body.Text ?? "", ct);
            return ToResult(result);
        });

        endpoints.MapPost("conversations", async (StartConversationRequest body, CancellationToken ct) =>
        {
            if (router() is not { } r)
            {
                return Unavailable();
            }
            if (string.IsNullOrWhiteSpace(body.ConnectorId))
            {
                return Results.BadRequest(new SendResponse(false, "Choose which node to send from.", null, null));
            }
            if (!TryParseKind(body.Kind, out var kind))
            {
                return Results.BadRequest(new SendResponse(false, "Kind must be 'channel' or 'direct'.", null, null));
            }
            var result = await r.StartAsync(body.ConnectorId, kind, body.Peer ?? "", body.Text ?? "", ct);
            return ToResult(result);
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
            default:
                kind = default;
                return false;
        }
    }

    private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static NodeDto NodeToDto(IMeshConnector c) =>
        new(c.Id, Lower(c.Network), c.DisplayName, Lower(c.State), c.StateDetail,
            c.MaxTextBytes(ConversationKind.Direct), c.MaxTextBytes(ConversationKind.Channel));

    private static ConversationDto ConversationToDto(MessageStore.Summary s) =>
        new(s.Conversation.Key.Id, s.Conversation.Key.ConnectorId, Lower(s.Conversation.Network),
            Lower(s.Conversation.Key.Kind), s.Conversation.Key.Peer, s.Conversation.Title,
            s.Last is null ? null : MessageToDto(s.Last), s.Unread);

    private static MessageDto MessageToDto(StoredMessage m) =>
        new(m.Id, Lower(m.Direction), m.FromId, m.FromName, m.Text, m.Timestamp, Lower(m.Status), m.Error);
}

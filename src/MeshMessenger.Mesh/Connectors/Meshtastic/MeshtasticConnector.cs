// SPDX-License-Identifier: GPL-2.0-or-later
using MeshMessenger.Core;

namespace MeshMessenger.Connectors.Meshtastic;

/// <summary>
/// Meshtastic node on the LAN. Planned transport: the firmware's TCP API
/// (port 4403), framed protobuf ToRadio / FromRadio packets, with a
/// hand-written codec for the few messages we need (no third-party NuGet).
/// Not implemented yet: the connector reports itself disconnected and refuses
/// to send, which exercises the router's "never fall back" rule.
/// </summary>
public sealed class MeshtasticConnector(NodeConfig config) : MeshConnectorBase(config)
{
    public const int DefaultPort = 4403;

    public override MeshNetwork Network => MeshNetwork.Meshtastic;

    // Protocol hard limit is DATA_PAYLOAD_LEN = 233 bytes; the official apps cap
    // text at 200. See docs/protocols/meshtastic.md.
    public const int TextLimitBytes = 200;

    public override int MaxTextBytes(ConversationKind kind) => TextLimitBytes;

    public override Task StartAsync(CancellationToken ct)
    {
        SetState(ConnectorState.Disconnected, "Meshtastic connector is not implemented yet.");
        return Task.CompletedTask;
    }

    public override Task<SendResult> SendAsync(ConversationKey to, string text, CancellationToken ct) =>
        Task.FromResult(SendResult.Failure("Meshtastic connector is not implemented yet."));
}

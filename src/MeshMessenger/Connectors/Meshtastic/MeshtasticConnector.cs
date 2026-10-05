// SPDX-License-Identifier: GPL-2.0-or-later
using MeshMessenger.Core;

namespace MeshMessenger.Connectors.Meshtastic;

/// <summary>
/// Meshtastic node on the LAN. Planned transport: the firmware's TCP API
/// (default port 4403), framed protobuf ToRadio / FromRadio packets, with a
/// hand-written codec for the few messages we need (no third-party NuGet).
/// Not implemented yet: the connector reports itself disconnected and refuses
/// to send, which exercises the router's "never fall back" rule.
/// </summary>
public sealed class MeshtasticConnector(NodeConfig config) : MeshConnectorBase(config)
{
    public const int DefaultPort = 4403;

    public override MeshNetwork Network => MeshNetwork.Meshtastic;

    // Meshtastic clients cap a text message at 200 bytes. Confirm against firmware during adapter work.
    public override int MaxTextBytes => 200;

    public override Task StartAsync(CancellationToken ct)
    {
        SetState(ConnectorState.Disconnected, "Meshtastic connector is not implemented yet.");
        return Task.CompletedTask;
    }

    public override Task<SendResult> SendAsync(ConversationKey to, string text, CancellationToken ct) =>
        Task.FromResult(SendResult.Failure("Meshtastic connector is not implemented yet."));
}

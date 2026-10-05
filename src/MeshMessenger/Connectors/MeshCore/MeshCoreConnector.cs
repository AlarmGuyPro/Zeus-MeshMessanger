// SPDX-License-Identifier: GPL-2.0-or-later
using MeshMessenger.Core;

namespace MeshMessenger.Connectors.MeshCore;

/// <summary>
/// MeshCore companion-radio node on the LAN. Planned transport: the companion
/// Wi-Fi firmware's TCP interface (port 5000 by default; confirm against the
/// firmware build), speaking the companion binary frame protocol.
/// Not implemented yet: the connector reports itself disconnected and refuses
/// to send, which exercises the router's "never fall back" rule.
/// </summary>
public sealed class MeshCoreConnector(NodeConfig config) : MeshConnectorBase(config)
{
    public const int DefaultPort = 5000;

    public override MeshNetwork Network => MeshNetwork.MeshCore;

    // MeshCore's MAX_TEXT_LEN is 160 bytes. Confirm against firmware during adapter work.
    public override int MaxTextBytes => 160;

    public override Task StartAsync(CancellationToken ct)
    {
        SetState(ConnectorState.Disconnected, "MeshCore connector is not implemented yet.");
        return Task.CompletedTask;
    }

    public override Task<SendResult> SendAsync(ConversationKey to, string text, CancellationToken ct) =>
        Task.FromResult(SendResult.Failure("MeshCore connector is not implemented yet."));
}

// SPDX-License-Identifier: GPL-3.0-or-later
using MeshMessenger.Core;

namespace MeshMessenger.Connectors.MeshCore;

/// <summary>
/// MeshCore companion-radio node on the LAN. Planned transport: the companion
/// Wi-Fi firmware's TCP interface (port 5000), speaking the companion binary
/// frame protocol. See docs/protocols/meshcore.md.
/// Not implemented yet: the connector reports itself disconnected and refuses
/// to send, which exercises the router's "never fall back" rule.
/// </summary>
public sealed class MeshCoreConnector(NodeConfig config) : MeshConnectorBase(config)
{
    public const int DefaultPort = 5000;

    public override MeshNetwork Network => MeshNetwork.MeshCore;

    /// <summary>Firmware MAX_TEXT_LEN.</summary>
    public const int TextLimitBytes = 160;

    /// <summary>Longest advert name the firmware stores.</summary>
    private const int MaxNodeNameBytes = 32;

    /// <summary>Our node's advert name, from RESP_CODE_SELF_INFO once connected.</summary>
    private string? _selfName = null;

    // Channel text goes out as "<our name>: <text>" and the firmware silently
    // truncates the whole thing to 160 bytes, so the usable limit depends on
    // our name. Until we know it, assume the longest possible name.
    public override int MaxTextBytes(ConversationKind kind)
    {
        if (kind == ConversationKind.Direct)
        {
            return TextLimitBytes;
        }
        var nameBytes = _selfName is null ? MaxNodeNameBytes : System.Text.Encoding.UTF8.GetByteCount(_selfName);
        return TextLimitBytes - nameBytes - 2;
    }

    public override Task StartAsync(CancellationToken ct)
    {
        SetState(ConnectorState.Disconnected, "MeshCore connector is not implemented yet.");
        return Task.CompletedTask;
    }

    public override Task<SendResult> SendAsync(ConversationKey to, string text, CancellationToken ct) =>
        Task.FromResult(SendResult.Failure("MeshCore connector is not implemented yet."));
}

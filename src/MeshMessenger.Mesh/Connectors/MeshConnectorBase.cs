// SPDX-License-Identifier: GPL-3.0-or-later
using MeshMessenger.Core;

namespace MeshMessenger.Connectors;

/// <summary>Shared state handling for connectors. Protocol code lives in the subclasses.</summary>
public abstract class MeshConnectorBase(NodeConfig config) : IMeshConnector
{
    protected NodeConfig Config { get; } = config;

    public string Id => Config.Id;

    public abstract MeshNetwork Network { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(Config.Name) ? Config.Id : Config.Name;

    public ConnectorState State { get; private set; } = ConnectorState.Disconnected;

    public string? StateDetail { get; private set; }

    public abstract int MaxTextBytes(ConversationKind kind);

    public event Action<InboundMessage>? MessageReceived;

    public abstract Task StartAsync(CancellationToken ct);

    public abstract Task<SendResult> SendAsync(ConversationKey to, string text, CancellationToken ct);

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    protected void SetState(ConnectorState state, string? detail = null)
    {
        State = state;
        StateDetail = detail;
    }

    /// <summary>Publish a received message, always filed under this connector's id.</summary>
    protected void PublishInbound(ConversationKind kind, string peer, string fromId, string? fromName, string text, string? networkMessageId)
    {
        var key = new ConversationKey(Id, kind, peer);
        MessageReceived?.Invoke(new InboundMessage(key, fromId, fromName, text, DateTimeOffset.UtcNow, networkMessageId));
    }
}

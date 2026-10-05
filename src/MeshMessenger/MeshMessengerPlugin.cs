// SPDX-License-Identifier: GPL-3.0-or-later
using MeshMessenger.Api;
using MeshMessenger.Connectors.MeshCore;
using MeshMessenger.Connectors.Meshtastic;
using MeshMessenger.Core;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace MeshMessenger;

public sealed class MeshMessengerPlugin : IZeusPlugin, IBackendPlugin
{
    internal const string ConfigKey = "config";

    private IPluginContext? _context;
    private MessageStore? _store;
    private MessageRouter? _router;
    private readonly List<IMeshConnector> _connectors = [];

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        var config = await context.Settings.GetAsync<MeshMessengerConfig>(ConfigKey, ct) ?? new MeshMessengerConfig();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in (config.Nodes ?? []).Where(n => n.Enabled))
        {
            if (string.IsNullOrWhiteSpace(node.Id) || !seen.Add(node.Id))
            {
                context.Logger.LogWarning("Skipping node with missing or duplicate id '{Id}'", node.Id);
                continue;
            }
            _connectors.Add(node.Network switch
            {
                MeshNetwork.Meshtastic => new MeshtasticConnector(node),
                MeshNetwork.MeshCore => new MeshCoreConnector(node),
                _ => throw new InvalidOperationException($"Unknown network {node.Network}"),
            });
        }

        _store = new MessageStore();
        _router = new MessageRouter(_store, _connectors);

        // Connectors connect in the background; StartAsync must not block plugin init.
        foreach (var connector in _connectors)
        {
            await connector.StartAsync(ct);
        }

        context.Logger.LogInformation("Mesh Messenger initialized with {Count} node(s)", _connectors.Count);
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        foreach (var connector in _connectors)
        {
            try
            {
                await connector.DisposeAsync();
            }
            catch (Exception ex)
            {
                _context?.Logger.LogWarning(ex, "Error stopping node {Id}", connector.Id);
            }
        }
        _connectors.Clear();
        _router = null;
        _store = null;
        _context?.Logger.LogInformation("Mesh Messenger stopped");
        _context = null;
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        Endpoints.Map(endpoints, () => _router, () => _store);
}

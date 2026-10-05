// SPDX-License-Identifier: GPL-3.0-or-later
using MeshMessenger.Api;
using MeshMessenger.Core;
using MeshMessenger.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Zeus.Plugins.Contracts;
using Zeus.Plugins.Contracts.Extensions;

namespace MeshMessenger;

public sealed class MeshMessengerPlugin : IZeusPlugin, IBackendPlugin
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    private IPluginContext? _context;
    private NodeManager? _nodes;
    private SettingsStore? _settings;
    private CancellationTokenSource? _life;
    private Task? _saver;
    private int _dirty;

    public async Task InitializeAsync(IPluginContext context, CancellationToken ct)
    {
        _context = context;
        _settings = new SettingsStore(context.Settings);
        var store = new MessageStore();
        var history = await _settings.LoadHistoryAsync(ct).ConfigureAwait(false);
        if (history is not null)
        {
            store.Restore(history);
        }
        store.Changed += () => Interlocked.Exchange(ref _dirty, 1);

        _nodes = new NodeManager(_settings, store, context.Logger);
        // Connectors start in the background; loading must not block plugin init (10 s budget).
        await _nodes.LoadAsync(ct).ConfigureAwait(false);

        _life = new CancellationTokenSource();
        _saver = Task.Run(() => SaveLoopAsync(_life.Token), CancellationToken.None);
        context.Logger.LogInformation("Mesh Messenger initialized with {Count} node(s)", _nodes.Connectors.Count);
    }

    private async Task SaveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SaveDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            await SaveHistoryIfDirtyAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task SaveHistoryIfDirtyAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0 || _nodes is null || _settings is null) return;
        try
        {
            await _settings.SaveHistoryAsync(_nodes.Store.Snapshot(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Interlocked.Exchange(ref _dirty, 1);
            _context?.Logger.LogWarning(ex, "Couldn't save message history");
        }
    }

    public async Task ShutdownAsync(CancellationToken ct)
    {
        _life?.Cancel();
        if (_saver is not null)
        {
            await Task.WhenAny(_saver, Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
        }
        await SaveHistoryIfDirtyAsync(ct).ConfigureAwait(false);
        if (_nodes is not null)
        {
            try
            {
                await _nodes.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _context?.Logger.LogWarning(ex, "Error stopping nodes");
            }
        }
        _nodes = null;
        _life?.Dispose();
        _life = null;
        _context?.Logger.LogInformation("Mesh Messenger stopped");
        _context = null;
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => Endpoints.Map(endpoints, () => _nodes);
}

// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using MeshMessenger.Connectors;
using MeshMessenger.Connectors.MeshCore;
using MeshMessenger.Connectors.Meshtastic;
using MeshMessenger.Core;
using MeshMessenger.Discovery;
using Microsoft.Extensions.Logging;

namespace MeshMessenger.Services;

public sealed record AddNodeRequest(string? Network, string? Host, int? Port, string? Name, string? Identity);

public sealed record UpdateNodeRequest(string? Name, string? Host, int? Port, bool? Enabled, bool? FindIfAddressChanges);

/// <summary>
/// Owns the node configuration and the live connectors built from it.
/// Configuration changes rebuild the connectors; the message store and the
/// transmit coordinator survive rebuilds.
/// </summary>
internal sealed class NodeManager : IAsyncDisposable
{
    private readonly SettingsStore _settings;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private MeshMessengerConfig _config = new();
    private List<MeshConnectorBase> _connectors = [];
    private MessageRouter? _router;

    public NodeManager(SettingsStore settings, MessageStore store, ILogger logger, DiscoveryOptions? discoveryOptions = null)
    {
        _settings = settings;
        _logger = logger;
        Store = store;
        Discovery = new DiscoveryService(() => _config.ScanRanges ?? [], BusyHosts, discoveryOptions);
    }

    public MessageStore Store { get; }

    public TransmitCoordinator Transmit { get; } = new();

    public DiscoveryService Discovery { get; }

    public MessageRouter? Router => _router;

    public IReadOnlyList<MeshConnectorBase> Connectors => _connectors;

    public MeshMessengerConfig Config => _config;

    public MeshConnectorBase? Connector(string id) => _connectors.FirstOrDefault(c => c.Id == id);

    public async Task LoadAsync(CancellationToken ct)
    {
        _config = await _settings.LoadConfigAsync(ct).ConfigureAwait(false);
        Store.MaxMessagesPerConversation = _config.HistoryPerConversation;
        await RebuildAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------ changes

    public async Task<(NodeConfig? Node, string? Error)> AddAsync(AddNodeRequest r, CancellationToken ct)
    {
        if (!TryNetwork(r.Network, out var network)) return (null, "Choose Meshtastic or MeshCore.");
        if (!HostValidator.TryNormalize(r.Host, out var host, out var error)) return (null, error);
        error = await HostValidator.CheckLocalAsync(host, ct).ConfigureAwait(false);
        if (error is not null) return (null, error);
        if (r.Port is < 0 or > 65535) return (null, "Port must be between 1 and 65535.");
        var (hostOnly, typedPort) = SplitPort(host);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(r.Identity) &&
                _config.Nodes.Any(n => string.Equals(n.Identity, r.Identity, StringComparison.OrdinalIgnoreCase)))
            {
                return (null, "That node is already added.");
            }
            var node = new NodeConfig
            {
                Id = NewId(network, r.Identity),
                Network = network,
                Name = (r.Name ?? "").Trim(),
                Host = hostOnly,
                Port = r.Port ?? typedPort ?? 0,
                Identity = string.IsNullOrWhiteSpace(r.Identity) ? null : r.Identity.Trim().ToLowerInvariant(),
                Enabled = true,
                FindIfAddressChanges = true,
            };
            _config.Nodes.Add(node);
            await SaveConfigAsync(ct).ConfigureAwait(false);
            await RebuildLockedAsync().ConfigureAwait(false);
            return (node, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> UpdateAsync(string id, UpdateNodeRequest r, CancellationToken ct)
    {
        string? host = null;
        if (r.Host is not null)
        {
            if (!HostValidator.TryNormalize(r.Host, out var h, out var error)) return error;
            error = await HostValidator.CheckLocalAsync(h, ct).ConfigureAwait(false);
            if (error is not null) return error;
            host = h;
        }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var node = _config.Nodes.FirstOrDefault(n => n.Id == id);
            if (node is null) return "Node not found.";
            if (r.Name is not null) node.Name = r.Name.Trim();
            if (host is not null)
            {
                var (hostOnly, typedPort) = SplitPort(host);
                node.Host = hostOnly;
                if (typedPort is not null && r.Port is null) node.Port = typedPort.Value;
            }
            if (r.Port is not null) node.Port = r.Port.Value is >= 0 and <= 65535 ? r.Port.Value : node.Port;
            if (r.Enabled is not null) node.Enabled = r.Enabled.Value;
            if (r.FindIfAddressChanges is not null) node.FindIfAddressChanges = r.FindIfAddressChanges.Value;
            await SaveConfigAsync(ct).ConfigureAwait(false);
            await RebuildLockedAsync().ConfigureAwait(false);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_config.Nodes.RemoveAll(n => n.Id == id) == 0) return false;
            await SaveConfigAsync(ct).ConfigureAwait(false);
            await RebuildLockedAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forget the node's recorded identity: the next node found at its address becomes the paired one.</summary>
    public async Task<bool> RepairAsync(string id, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var node = _config.Nodes.FirstOrDefault(n => n.Id == id);
            if (node is null) return false;
            node.Identity = null;
            await SaveConfigAsync(ct).ConfigureAwait(false);
            await RebuildLockedAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> SetScanRangesAsync(IReadOnlyList<string> ranges, CancellationToken ct)
    {
        var clean = new List<string>();
        foreach (var r in ranges.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            if (!Ipv4Network.TryParse(r, out var n, out var error)) return error;
            if (!clean.Contains(n.ToString())) clean.Add(n.ToString());
        }
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _config.ScanRanges = clean;
            await SaveConfigAsync(ct).ConfigureAwait(false);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SetHistoryLimitAsync(int perConversation, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _config.HistoryPerConversation = Math.Clamp(perConversation, 20, 5000);
            Store.MaxMessagesPerConversation = _config.HistoryPerConversation;
            await SaveConfigAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ------------------------------------------------------------ connectors

    private async Task RebuildAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await RebuildLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RebuildLockedAsync()
    {
        var old = _connectors;
        _router?.Dispose();
        _router = null;
        _connectors = [];
        await Task.WhenAll(old.Select(async c =>
        {
            try { await c.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping node {Id}", c.Id); }
        })).ConfigureAwait(false);

        var built = new List<MeshConnectorBase>();
        foreach (var node in _config.Nodes.Where(n => n.Enabled))
        {
            var options = new ConnectorOptions
            {
                Refind = Discovery.RefindAsync,
                HasStoredSecrets = () => false, // rooms / repeater passwords: none stored yet
            };
            MeshConnectorBase connector = node.Network == MeshNetwork.MeshCore
                ? new MeshCoreConnector(node, options)
                : new MeshtasticConnector(node, options);
            var id = node.Id;
            connector.IdentityLearned += identity => _ = PersistNodeAsync(id, n => n.Identity = identity);
            connector.HostChanged += host => _ = PersistNodeAsync(id, n => n.Host = host);
            built.Add(connector);
        }
        _connectors = built;
        _router = new MessageRouter(Store, Transmit, built);
        foreach (var c in built)
        {
            await c.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task PersistNodeAsync(string id, Action<NodeConfig> change)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var node = _config.Nodes.FirstOrDefault(n => n.Id == id);
            if (node is null) return;
            change(node);
            await SaveConfigAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't save node {Id}", id);
        }
        finally
        {
            _lock.Release();
        }
    }

    private Task SaveConfigAsync(CancellationToken ct) => _settings.SaveConfigAsync(_config, ct);

    private IReadOnlyCollection<string> BusyHosts() =>
        _connectors.Where(c => c.Network == MeshNetwork.MeshCore && c.State is ConnectorState.Connected or ConnectorState.Connecting)
            .Select(c => $"{HostOnly(c.Host)}:{c.Port}").ToArray();

    /// <summary>"192.168.1.5:5001" → ("192.168.1.5", 5001); a bare host keeps its configured port.</summary>
    internal static (string Host, int? Port) SplitPort(string host)
    {
        if (!Uri.TryCreate("http://" + host, UriKind.Absolute, out var uri)) return (host, null);
        return (uri.Host, uri.IsDefaultPort ? null : uri.Port);
    }

    private static string HostOnly(string host) =>
        Uri.TryCreate("http://" + host, UriKind.Absolute, out var uri) ? uri.Host.Trim('[', ']') : host;

    private string NewId(MeshNetwork network, string? identity)
    {
        var prefix = network == MeshNetwork.MeshCore ? "mc" : "mt";
        var tail = !string.IsNullOrWhiteSpace(identity)
            ? new string(identity.Where(char.IsAsciiHexDigit).Take(6).ToArray()).ToLowerInvariant()
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        var id = $"{prefix}-{tail}";
        var n = 2;
        while (_config.Nodes.Any(x => x.Id == id)) id = $"{prefix}-{tail}-{n++}";
        return id;
    }

    internal static bool TryNetwork(string? text, out MeshNetwork network)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "meshtastic":
                network = MeshNetwork.Meshtastic;
                return true;
            case "meshcore":
                network = MeshNetwork.MeshCore;
                return true;
            default:
                network = default;
                return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _router?.Dispose();
        await Task.WhenAll(_connectors.Select(async c =>
        {
            try { await c.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { }
        }).Append(Discovery.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}

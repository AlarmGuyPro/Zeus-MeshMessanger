// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using MeshMessenger.Core;

namespace MeshMessenger.Discovery;

public sealed record FoundNode(ProbedNode Node, IReadOnlyList<string> FoundBy);

public sealed record ScanState
{
    public bool Running { get; init; }
    public string Phase { get; init; } = "idle";
    public int Probed { get; init; }
    public int Total { get; init; }
    public IReadOnlyList<string> Networks { get; init; } = [];
    /// <summary>Networks where nothing at all answered on the mesh ports: usually a firewall between VLANs.</summary>
    public IReadOnlyList<string> QuietNetworks { get; init; } = [];
    public bool UsedMdns { get; init; }
    public IReadOnlyList<FoundNode> Found { get; init; } = [];
    public int OtherAnswers { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public bool Cancelled { get; init; }
    public string? Error { get; init; }
}

public sealed class DiscoveryOptions
{
    public int MeshtasticPort { get; init; } = 4403;
    public int MeshCorePort { get; init; } = 5000;
    public int Concurrency { get; init; } = 48;
    public TimeSpan ConnectTimeout { get; init; } = NodeProbe.DefaultConnectTimeout;
    public TimeSpan IdentifyTimeout { get; init; } = NodeProbe.DefaultIdentifyTimeout;
    public TimeSpan MdnsListen { get; init; } = TimeSpan.FromSeconds(2);
    public bool UseMdns { get; init; } = true;
    /// <summary>Include this computer's own networks in scans (tests turn this off).</summary>
    public bool IncludeLocalNetworks { get; init; } = true;
}

/// <summary>
/// Finds Meshtastic and MeshCore nodes: mDNS (Meshtastic, same VLAN) and an
/// identity-confirmed probe of TCP 4403 / 5000 across this computer's
/// networks and operator-added ranges. Also re-finds a paired node by
/// identity when it moves. See docs/DISCOVERY.md.
/// </summary>
public sealed class DiscoveryService : IAsyncDisposable
{
    private readonly Func<IReadOnlyList<string>> _savedRanges;
    private readonly Func<IReadOnlyCollection<string>> _busyHosts;
    private readonly DiscoveryOptions _options;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _life = new();
    private ScanState _state = new();
    private CancellationTokenSource? _scanCts;
    private Task? _scanTask;

    /// <param name="savedRanges">Operator-added networks (CIDR).</param>
    /// <param name="busyHosts">"host:port" of nodes Zeus is connected to: never probed (on MeshCore a probe would drop our own connection).</param>
    public DiscoveryService(Func<IReadOnlyList<string>> savedRanges, Func<IReadOnlyCollection<string>> busyHosts,
        DiscoveryOptions? options = null, TimeProvider? time = null)
    {
        _savedRanges = savedRanges;
        _busyHosts = busyHosts;
        _options = options ?? new DiscoveryOptions();
        _time = time ?? TimeProvider.System;
    }

    public ScanState State { get { lock (_gate) return _state; } }

    /// <summary>Networks a scan would cover by default (shown to the operator before scanning).</summary>
    public IReadOnlyList<string> DefaultNetworks()
    {
        var nets = new List<Ipv4Network>();
        if (_options.IncludeLocalNetworks) nets.AddRange(Ipv4Network.LocalNetworks());
        foreach (var r in _savedRanges())
        {
            if (Ipv4Network.TryParse(r, out var n, out _) && !nets.Contains(n)) nets.Add(n);
        }
        return nets.Select(n => n.ToString()).ToArray();
    }

    public bool StartScan(IReadOnlyList<string>? extraRanges, bool? useMdns, out string? error)
    {
        error = null;
        var networks = new List<Ipv4Network>();
        if (_options.IncludeLocalNetworks) networks.AddRange(Ipv4Network.LocalNetworks());
        foreach (var r in _savedRanges().Concat(extraRanges ?? []))
        {
            if (!Ipv4Network.TryParse(r, out var n, out var e))
            {
                error = e;
                return false;
            }
            if (!networks.Contains(n)) networks.Add(n);
        }
        if (networks.Count == 0 && useMdns == false)
        {
            error = "No networks to scan. Add one, such as 192.168.1.0/24.";
            return false;
        }
        lock (_gate)
        {
            if (_state.Running)
            {
                error = "A scan is already running.";
                return false;
            }
            _scanCts?.Dispose();
            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
            _state = new ScanState
            {
                Running = true,
                Phase = "starting",
                Networks = networks.Select(n => n.ToString()).ToArray(),
                StartedAt = _time.GetUtcNow(),
            };
            var token = _scanCts.Token;
            var mdns = useMdns ?? _options.UseMdns;
            _scanTask = Task.Run(() => RunScanAsync(networks, mdns, token), CancellationToken.None);
        }
        return true;
    }

    public void CancelScan()
    {
        lock (_gate) _scanCts?.Cancel();
    }

    private async Task RunScanAsync(IReadOnlyList<Ipv4Network> networks, bool useMdns, CancellationToken ct)
    {
        var found = new Dictionary<string, FoundNode>(StringComparer.OrdinalIgnoreCase);
        var answered = new HashSet<Ipv4Network>();
        var otherAnswers = 0;
        void Add(ProbedNode node, string how)
        {
            lock (found)
            {
                var key = $"{node.Network}|{node.Identity}";
                found[key] = found.TryGetValue(key, out var existing)
                    ? existing with { FoundBy = existing.FoundBy.Contains(how) ? existing.FoundBy : [.. existing.FoundBy, how] }
                    : new FoundNode(node, [how]);
                Update(s => s with { Found = found.Values.ToArray() });
            }
        }

        try
        {
            var busy = new HashSet<string>(_busyHosts(), StringComparer.OrdinalIgnoreCase);
            if (useMdns)
            {
                Update(s => s with { Phase = "mdns", UsedMdns = true });
                var hits = await Mdns.DiscoverAsync([Mdns.MeshtasticService], _options.MdnsListen, ct).ConfigureAwait(false);
                foreach (var hit in hits)
                {
                    var node = await NodeProbe.ProbeAsync(MeshNetwork.Meshtastic, hit.Address.ToString(), hit.Port ?? _options.MeshtasticPort, ct,
                        _options.ConnectTimeout, _options.IdentifyTimeout).ConfigureAwait(false);
                    if (node is not null) Add(node, "mdns");
                }
            }

            var targets = new List<(IPAddress Address, MeshNetwork Network, int Port, Ipv4Network Net)>();
            foreach (var net in networks)
            {
                foreach (var address in net.Hosts())
                {
                    if (!busy.Contains($"{address}:{_options.MeshtasticPort}"))
                        targets.Add((address, MeshNetwork.Meshtastic, _options.MeshtasticPort, net));
                    if (!busy.Contains($"{address}:{_options.MeshCorePort}"))
                        targets.Add((address, MeshNetwork.MeshCore, _options.MeshCorePort, net));
                }
            }
            Update(s => s with { Phase = "scan", Total = targets.Count, Probed = 0 });
            var probed = 0;
            await Parallel.ForEachAsync(targets,
                new ParallelOptions { MaxDegreeOfParallelism = _options.Concurrency, CancellationToken = ct },
                async (t, token) =>
                {
                    var connected = false;
                    var node = await NodeProbe.ProbeAsync(t.Network, t.Address.ToString(), t.Port, token,
                        _options.ConnectTimeout, _options.IdentifyTimeout, () => connected = true).ConfigureAwait(false);
                    if (connected)
                    {
                        lock (answered) answered.Add(t.Net);
                    }
                    if (node is not null)
                    {
                        Add(node, "scan");
                    }
                    else if (connected)
                    {
                        Interlocked.Increment(ref otherAnswers);
                    }
                    var done = Interlocked.Increment(ref probed);
                    if (done % 16 == 0 || done == targets.Count) Update(s => s with { Probed = done });
                }).ConfigureAwait(false);

            Update(s => s with
            {
                Running = false,
                Phase = "done",
                Probed = targets.Count,
                OtherAnswers = otherAnswers,
                QuietNetworks = networks.Where(n => !answered.Contains(n)).Select(n => n.ToString()).ToArray(),
                FinishedAt = _time.GetUtcNow(),
            });
        }
        catch (OperationCanceledException)
        {
            Update(s => s with { Running = false, Phase = "cancelled", Cancelled = true, FinishedAt = _time.GetUtcNow() });
        }
        catch (Exception ex)
        {
            Update(s => s with { Running = false, Phase = "error", Error = ex.Message, FinishedAt = _time.GetUtcNow() });
        }
    }

    private void Update(Func<ScanState, ScanState> change)
    {
        lock (_gate) _state = change(_state);
    }

    /// <summary>
    /// Looks for the node with <paramref name="identity"/>: its host name, mDNS
    /// (Meshtastic), the /24 it was last seen in, then the saved ranges.
    /// Returns the address it was found at, or null.
    /// </summary>
    public async Task<string?> RefindAsync(MeshNetwork network, string identity, string lastHost, int port, CancellationToken ct)
    {
        var hostOnly = Uri.TryCreate("http://" + lastHost, UriKind.Absolute, out var uri) ? uri.Host.Trim('[', ']') : lastHost;
        var candidates = new List<IPAddress>();

        if (!IPAddress.TryParse(hostOnly, out var lastIp))
        {
            // A host name: maybe the router's DNS already knows the new address.
            if (await HostValidator.CheckLocalAsync(hostOnly, ct).ConfigureAwait(false) is null)
            {
                try { candidates.AddRange(await Dns.GetHostAddressesAsync(hostOnly, ct).ConfigureAwait(false)); }
                catch (System.Net.Sockets.SocketException) { }
            }
        }

        if (network == MeshNetwork.Meshtastic && _options.UseMdns)
        {
            var hits = await Mdns.DiscoverAsync([Mdns.MeshtasticService], _options.MdnsListen, ct).ConfigureAwait(false);
            candidates.AddRange(hits
                .OrderByDescending(h => h.Txt.TryGetValue("id", out var id) && string.Equals(id, identity, StringComparison.OrdinalIgnoreCase))
                .Select(h => h.Address));
        }

        foreach (var address in candidates.Distinct())
        {
            if (!HostValidator.IsLocal(address)) continue;
            var node = await NodeProbe.ProbeAsync(network, address.ToString(), port, ct, _options.ConnectTimeout, _options.IdentifyTimeout).ConfigureAwait(false);
            if (node is not null && string.Equals(node.Identity, identity, StringComparison.OrdinalIgnoreCase)) return address.ToString();
        }

        var nets = new List<Ipv4Network>();
        if (lastIp is not null && lastIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && HostValidator.IsLocal(lastIp))
        {
            nets.Add(Ipv4Network.Around(lastIp));
        }
        foreach (var r in _savedRanges())
        {
            if (Ipv4Network.TryParse(r, out var n, out _) && !nets.Contains(n)) nets.Add(n);
        }
        string? result = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Parallel.ForEachAsync(nets.SelectMany(n => n.Hosts()).Distinct(),
                new ParallelOptions { MaxDegreeOfParallelism = _options.Concurrency, CancellationToken = stop.Token },
                async (address, token) =>
                {
                    var node = await NodeProbe.ProbeAsync(network, address.ToString(), port, token, _options.ConnectTimeout, _options.IdentifyTimeout).ConfigureAwait(false);
                    if (node is not null && string.Equals(node.Identity, identity, StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.CompareExchange(ref result, address.ToString(), null);
                        await stop.CancelAsync().ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Found it; the rest of the sweep was stopped.
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        Task? scan;
        lock (_gate) scan = _scanTask;
        if (scan is not null) await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        _life.Dispose();
    }
}

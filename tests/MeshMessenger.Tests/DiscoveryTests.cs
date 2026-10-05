// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using MeshMessenger.Connectors;
using MeshMessenger.Connectors.MeshCore;
using MeshMessenger.Core;
using MeshMessenger.Discovery;

namespace MeshMessenger.Tests;

public static class DiscoveryTests
{
    [Test]
    public static async Task ProbeIdentifiesBothFirmwaresAndIgnoresOtherServices()
    {
        await using var mc = new FakeMeshCoreNode();
        await using var mt = new FakeMeshtasticNode();
        var a = await NodeProbe.ProbeAsync(MeshNetwork.MeshCore, "127.0.0.1", mc.Port, default);
        var b = await NodeProbe.ProbeAsync(MeshNetwork.Meshtastic, "127.0.0.1", mt.Port, default);
        Assert.Equal(mc.Identity, a?.Identity, "meshcore identity");
        Assert.Equal("KD0ABC Shack", a?.Name, "meshcore name");
        Assert.Equal("v1.12.0", a?.Firmware, "meshcore firmware");
        Assert.Equal(mt.Identity, b?.Identity, "meshtastic identity");
        Assert.Equal("KD0ABC Shack MT", b?.Name, "meshtastic name");

        // Something else answering on the port isn't a node.
        var other = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        other.Start();
        _ = Task.Run(async () =>
        {
            using var c = await other.AcceptTcpClientAsync();
            await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\n\r\n"));
            await Task.Delay(4000);
        });
        var port = ((IPEndPoint)other.LocalEndpoint).Port;
        var none = await NodeProbe.ProbeAsync(MeshNetwork.MeshCore, "127.0.0.1", port, default, identifyTimeout: TimeSpan.FromMilliseconds(800));
        Assert.Equal(null, none, "web server is not a MeshCore node");
        other.Stop();
    }

    [Test]
    public static async Task ScanFindsNodesOnAnAddedRange()
    {
        await using var mc = new FakeMeshCoreNode("127.0.0.2", 0);
        await using var mt = new FakeMeshtasticNode("127.0.0.3");
        // Each fake listens on its own port; the scanner is pointed at those ports.
        await using var discovery = new DiscoveryService(() => ["127.0.0.0/29"], () => [],
            new DiscoveryOptions { MeshCorePort = mc.Port, MeshtasticPort = mt.Port, UseMdns = false, IncludeLocalNetworks = false });
        Assert.True(discovery.StartScan(null, false, out var error), error ?? "start");
        await Assert.Eventually(() => !discovery.State.Running, "scan finished", 20_000);
        var found = discovery.State.Found;
        Assert.True(found.Any(f => f.Node.Identity == mc.Identity && f.Node.Host == "127.0.0.2"), "meshcore found at its address");
        Assert.True(found.Any(f => f.Node.Identity == mt.Identity && f.Node.Host == "127.0.0.3"), "meshtastic found at its address");
        Assert.Equal(0, discovery.State.QuietNetworks.Count, "range answered");
    }

    [Test]
    public static void RangesAreLimitedToLocalNetworksOfReasonableSize()
    {
        Assert.False(Ipv4Network.TryParse("8.8.8.0/24", out _, out var e1), "public refused");
        Assert.Contains("local network", e1, "explains");
        Assert.False(Ipv4Network.TryParse("10.0.0.0/8", out _, out var e2), "too large refused");
        Assert.Contains("too large", e2, "explains");
        Assert.True(Ipv4Network.TryParse("192.168.30.0/24", out var n, out _), "vlan accepted");
        Assert.Equal(254, n.HostCount, "hosts");
        Assert.False(HostValidator.IsLocal(IPAddress.Parse("1.1.1.1")), "public address not local");
    }

    [Test]
    public static async Task RefindFindsTheSameNodeAtANewAddressAndTheConnectorFollowsIt()
    {
        await using var moved = new FakeMeshCoreNode("127.0.0.5", 0);
        await using var discovery = new DiscoveryService(() => [], () => [],
            new DiscoveryOptions { MeshCorePort = moved.Port, UseMdns = false, IncludeLocalNetworks = false });
        var host = await discovery.RefindAsync(MeshNetwork.MeshCore, moved.Identity, "127.0.0.9", moved.Port, default);
        Assert.Equal("127.0.0.5", host, "found by identity in the old /24");

        var config = new NodeConfig { Id = "mc", Network = MeshNetwork.MeshCore, Host = "127.0.0.9", Port = moved.Port, Identity = moved.Identity };
        string? newHost = null;
        await using var c = new MeshCoreConnector(config, new ConnectorOptions
        {
            MinBackoff = TimeSpan.FromMilliseconds(50),
            ConnectTimeout = TimeSpan.FromMilliseconds(300),
            RefindAfterFailures = 1,
            Refind = discovery.RefindAsync,
        });
        c.HostChanged += h => newHost = h;
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "reconnected at the new address", 20_000);
        Assert.Equal("127.0.0.5", newHost, "address updated");
    }

    [Test]
    public static async Task NodeWithSavedPasswordsWaitsForConfirmationBeforeMoving()
    {
        await using var moved = new FakeMeshCoreNode("127.0.0.6", 0);
        await using var discovery = new DiscoveryService(() => [], () => [],
            new DiscoveryOptions { MeshCorePort = moved.Port, UseMdns = false, IncludeLocalNetworks = false });
        var config = new NodeConfig { Id = "mc", Network = MeshNetwork.MeshCore, Host = "127.0.0.10", Port = moved.Port, Identity = moved.Identity };
        await using var c = new MeshCoreConnector(config, new ConnectorOptions
        {
            MinBackoff = TimeSpan.FromMilliseconds(50),
            ConnectTimeout = TimeSpan.FromMilliseconds(300),
            RefindAfterFailures = 1,
            Refind = discovery.RefindAsync,
            HasStoredSecrets = () => true,
        });
        await c.StartAsync(default);
        await Assert.Eventually(() => c.PendingMove == "127.0.0.6", "new address offered", 20_000);
        var before = moved.Connections; // the identity probe only
        await Task.Delay(500);
        Assert.Equal(before, moved.Connections, "nothing more sent until confirmed");
        Assert.True(c.AcceptMove(), "operator accepts");
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected after Use", 20_000);
    }

    [Test]
    public static void MdnsAnswerForMeshtasticIsParsedAndForeignAddressesDropped()
    {
        var packet = BuildMdnsAnswer("Meshtastic._meshtastic._tcp.local", "Meshtastic.local", 4403, [192, 168, 1, 83], "id=!9e41c07b");
        var hits = Mdns.ParseResponse(packet, IPAddress.Parse("192.168.1.83"), [Mdns.MeshtasticService]);
        Assert.Equal(1, hits.Count, "one hit");
        Assert.Equal(4403, hits[0].Port, "srv port");
        Assert.Equal("!9e41c07b", hits[0].Txt["id"], "node id from TXT");

        var foreign = BuildMdnsAnswer("Meshtastic._meshtastic._tcp.local", "Meshtastic.local", 4403, [8, 8, 8, 8], "id=!1");
        Assert.Equal(0, Mdns.ParseResponse(foreign, IPAddress.Parse("192.168.1.83"), [Mdns.MeshtasticService]).Count, "public A record dropped");
        Assert.Equal(0, Mdns.ParseResponse(new byte[] { 1, 2, 3 }, IPAddress.Loopback, [Mdns.MeshtasticService]).Count, "garbage ignored");
    }

    private static byte[] BuildMdnsAnswer(string instance, string target, int port, byte[] a, string txt)
    {
        var b = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 4, 0, 0, 0, 0 };
        void Name(string n)
        {
            foreach (var label in n.Split('.'))
            {
                b.Add((byte)label.Length);
                b.AddRange(Encoding.ASCII.GetBytes(label));
            }
            b.Add(0);
        }
        void Rr(string name, ushort type, byte[] data)
        {
            Name(name);
            b.AddRange([(byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0x11, 0x94, (byte)(data.Length >> 8), (byte)data.Length]);
            b.AddRange(data);
        }
        var ptr = new List<byte>();
        foreach (var label in instance.Split('.')) { ptr.Add((byte)label.Length); ptr.AddRange(Encoding.ASCII.GetBytes(label)); }
        ptr.Add(0);
        Rr("_meshtastic._tcp.local", 12, ptr.ToArray());
        var srv = new List<byte> { 0, 0, 0, 0, (byte)(port >> 8), (byte)port };
        foreach (var label in target.Split('.')) { srv.Add((byte)label.Length); srv.AddRange(Encoding.ASCII.GetBytes(label)); }
        srv.Add(0);
        Rr(instance, 33, srv.ToArray());
        var t = Encoding.ASCII.GetBytes(txt);
        Rr(instance, 16, [(byte)t.Length, .. t]);
        Rr(target, 1, a);
        return b.ToArray();
    }
}

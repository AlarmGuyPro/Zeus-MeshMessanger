// SPDX-License-Identifier: GPL-3.0-or-later
using MeshMessenger.Connectors;
using MeshMessenger.Connectors.MeshCore;
using MeshMessenger.Connectors.Meshtastic;
using MeshMessenger.Core;

namespace MeshMessenger.Tests;

/// <summary>
/// Review finding #3: both connectors remembered every station they ever heard of, with no
/// cap, and the Meshtastic table was copied whole on every packet. On a mesh bridged to MQTT
/// that is traffic from far outside radio range, arriving all day.
/// </summary>
public static class PeerTableTests
{
    private static ConnectorOptions Fast => new()
    {
        MinBackoff = TimeSpan.FromMilliseconds(100),
        ConnectTimeout = TimeSpan.FromSeconds(2),
    };

    private static NodeConfig Config(string id, MeshNetwork network, int port) =>
        new() { Id = id, Network = network, Host = "127.0.0.1", Port = port };

    [Test(Loopback = true)]
    public static async Task MeshCoreForgetsTheStationsItHeardLongestAgo()
    {
        await using var node = new FakeMeshCoreNode();
        await using var c = new MeshCoreConnector(Config("mc", MeshNetwork.MeshCore, node.Port), Fast);
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");
        var saved = c.Peers.Count;

        var overflow = MeshCoreConnector.MaxHeardStations + 40;
        for (var i = 0; i < overflow; i++)
        {
            await node.HearUnsavedAdvertAsync($"Station {i:0000}");
        }
        // Wait for the last advert rather than for a count: the cap means the count stops
        // rising before the queue is drained.
        await Assert.Eventually(() => c.Peers.Any(p => p.Name == $"Station {overflow - 1:0000}"),
            "the last advert landed", 20_000);

        var heard = c.Peers.Where(p => !p.SavedOnNode).ToArray();
        Assert.True(heard.Length <= MeshCoreConnector.MaxHeardStations,
            $"{heard.Length} heard stations kept, cap is {MeshCoreConnector.MaxHeardStations}");
        Assert.Equal(saved, c.Peers.Count(p => p.SavedOnNode), "the node's own contacts are untouched");
        Assert.True(heard.Any(p => p.Name == $"Station {overflow - 1:0000}"), "the most recent advert is kept");
        Assert.True(heard.All(p => p.Name != "Station 0000"), "the first advert was forgotten");
        Assert.Equal(MeshCoreConnector.MaxHeardStations, c.RememberedPeerLimit, "the limit is reported");
    }

    [Test(Loopback = true)]
    public static async Task MeshtasticForgetsTheStationsItHeardLongestAgo()
    {
        await using var node = new FakeMeshtasticNode();
        await using var c = new MeshtasticConnector(Config("mt", MeshNetwork.Meshtastic, node.Port), Fast);
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");

        // Each packet comes from a different station, as an MQTT-bridged mesh delivers.
        var overflow = MeshtasticConnector.MaxKnownNodes + 50;
        for (var i = 0; i < overflow; i++)
        {
            await node.HearTextAsync((uint)(0x01000000 + i), 0xFFFFFFFF, 0, $"packet {i}");
        }
        var newest = $"!{0x01000000 + overflow - 1:x8}";
        await Assert.Eventually(() => c.Peers.Any(p => p.Id == newest), "the last packet landed", 20_000);

        Assert.True(c.Peers.Count <= MeshtasticConnector.MaxKnownNodes,
            $"{c.Peers.Count} stations kept, cap is {MeshtasticConnector.MaxKnownNodes}");
        Assert.True(c.Peers.Any(p => p.Id == newest), "the most recently heard station is kept");
        Assert.True(c.Peers.All(p => p.Id != "!01000000"), "the first station heard was forgotten");
        Assert.Equal(MeshtasticConnector.MaxKnownNodes, c.RememberedPeerLimit, "the limit is reported");
    }
}

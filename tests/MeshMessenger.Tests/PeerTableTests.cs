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

/// <summary>
/// Clearing a long contact list. Found on real hardware: removing 323 repeaters one call at
/// a time re-read the whole contact list after every single removal, which buried the node
/// in frames until it stopped answering, and the first unanswered keep-alive check tore the
/// session down — so the node appeared to vanish part-way through.
/// </summary>
public static class BulkRemovalTests
{
    private static ConnectorOptions Fast => new()
    {
        MinBackoff = TimeSpan.FromMilliseconds(100),
        ConnectTimeout = TimeSpan.FromSeconds(2),
    };

    private static NodeConfig Config(int port) =>
        new() { Id = "mc", Network = MeshNetwork.MeshCore, Host = "127.0.0.1", Port = port };

    [Test(Loopback = true)]
    public static async Task ClearingALongContactListReadsItBackOnceAndKeepsTheNode()
    {
        await using var node = new FakeMeshCoreNode();
        for (var i = 0; i < 120; i++) node.AddContact($"Repeater {i:000}", type: 2);
        node.AddContact("KE0XYZ Mobile");

        await using var c = new MeshCoreConnector(Config(node.Port), Fast);
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");
        await Assert.Eventually(() => c.Peers.Count(p => p.SavedOnNode) == 121, "contacts read");

        var readsBefore = node.ContactListReads;
        var repeaters = c.Peers.Where(p => p.Kind == PeerKind.Repeater).Select(p => p.Id).ToList();
        Assert.Equal(120, repeaters.Count, "repeaters to remove");

        var result = await c.RemovePeersAsync(repeaters, default);

        Assert.Equal(120, result.Removed, "all removed");
        Assert.True(result.Complete, result.Error ?? "complete");
        Assert.Equal(1, node.ContactListReads - readsBefore,
            "the contact list is read back once, not once per contact");
        Assert.Equal(ConnectorState.Connected, c.State, "the node did not go missing");
        Assert.Equal(1, c.Peers.Count(p => p.SavedOnNode), "only the station that was kept");
        Assert.True(c.Peers.Any(p => p.Name == "KE0XYZ Mobile"), "and it is the right one");
    }

    [Test(Loopback = true)]
    public static async Task ANodeThatStopsAnsweringMidClearSaysHowFarItGotAndStaysConnected()
    {
        await using var node = new FakeMeshCoreNode();
        for (var i = 0; i < 40; i++) node.AddContact($"Repeater {i:000}", type: 2);
        node.StallAfterRemovals = 12;

        await using var c = new MeshCoreConnector(Config(node.Port), Fast);
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");
        await Assert.Eventually(() => c.Peers.Count(p => p.SavedOnNode) == 40, "contacts read");

        var result = await c.RemovePeersAsync(c.Peers.Select(p => p.Id).ToList(), default);

        Assert.Equal(12, result.Removed, "stopped where the node did");
        Assert.False(result.Complete, "and says it did not finish");
        Assert.Contains("12 of 40", result.Error ?? "", "says how far it got");
        // The important part: one unanswered command is not a lost node.
        Assert.Equal(ConnectorState.Connected, c.State, "still connected");
    }
}

/// <summary>
/// Telling the operator what the node is doing on its own. A node that auto-adds every
/// station it hears refills its contact list continuously, so repeaters removed from it come
/// straight back — which looks exactly like Zeus failing to remove them.
/// </summary>
public static class SelfAddingNodeTests
{
    [Test(Loopback = true)]
    public static async Task ZeusCountsTheStationsTheNodeSavesWithoutBeingAsked()
    {
        await using var node = new FakeMeshCoreNode();
        await using var c = new MeshCoreConnector(
            new NodeConfig { Id = "mc", Network = MeshNetwork.MeshCore, Host = "127.0.0.1", Port = node.Port },
            new ConnectorOptions { MinBackoff = TimeSpan.FromMilliseconds(100), ConnectTimeout = TimeSpan.FromSeconds(2) });
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");
        Assert.Equal(0, c.PeersSavedByTheNode, "nothing saved yet");

        // A station the node merely heard and did not save is not the node adding anything.
        await node.HearUnsavedAdvertAsync("KQ4WLR Wio L1");
        await Assert.Eventually(() => c.Peers.Any(p => !p.SavedOnNode), "heard station listed");
        Assert.Equal(0, c.PeersSavedByTheNode, "hearing is not saving");

        // A station the node saved by itself is.
        await node.AdvertiseSavedAsync("Tower Repeater");
        await node.AdvertiseSavedAsync("Hilltop Repeater");
        await Assert.Eventually(() => c.PeersSavedByTheNode == 2, "both counted");
    }
}

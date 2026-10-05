// SPDX-License-Identifier: GPL-3.0-or-later
using MeshMessenger.Connectors;
using MeshMessenger.Connectors.MeshCore;
using MeshMessenger.Connectors.Meshtastic;
using MeshMessenger.Core;

namespace MeshMessenger.Tests;

public static class ConnectorTests
{
    private static ConnectorOptions Fast => new() { MinBackoff = TimeSpan.FromMilliseconds(100), ConnectTimeout = TimeSpan.FromSeconds(2) };

    private static NodeConfig Config(string id, MeshNetwork network, int port, string? identity = null) =>
        new() { Id = id, Network = network, Host = "127.0.0.1", Port = port, Identity = identity };

    [Test]
    public static async Task MeshCoreConnectsLearnsIdentityChannelsAndContacts()
    {
        await using var node = new FakeMeshCoreNode();
        node.AddContact("KE0XYZ Mobile");
        node.AddContact("Shack Board", type: 3);
        await using var c = new MeshCoreConnector(Config("mc", MeshNetwork.MeshCore, node.Port), Fast);
        string? learned = null;
        c.IdentityLearned += id => learned = id;
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");

        Assert.Equal(node.Identity, learned, "identity learned");
        Assert.Equal("KD0ABC Shack", c.Self!.Name, "self name");
        Assert.Contains("SF7", c.Self.RadioSummary, "radio summary");
        var channels = c.Channels;
        Assert.Equal(3, channels.Count, "channels");
        Assert.Equal(ChannelKind.Public, channels[0].Kind, "public channel");
        Assert.Equal(ChannelKind.Hashtag, channels[1].Kind, "hashtag channel");
        Assert.Equal(ChannelKind.Private, channels[2].Kind, "private channel");
        Assert.Equal(2, c.Peers.Count, "contacts");
        Assert.True(c.Peers.Any(p => p.Kind == PeerKind.Room), "room contact");
        // 160 minus "KD0ABC Shack: " (14 bytes)
        Assert.Equal(146, c.MaxTextBytes(ConversationKind.Channel), "channel limit");
        Assert.Equal(160, c.MaxTextBytes(ConversationKind.Direct), "direct limit");
    }

    [Test]
    public static async Task MeshCoreDrainsMessagesHeldWhileAwayAndSplitsChannelSender()
    {
        await using var node = new FakeMeshCoreNode();
        await node.HearChannelAsync(0, "W0QQ Base", "morning all");
        await using var c = new MeshCoreConnector(Config("mc", MeshNetwork.MeshCore, node.Port), Fast);
        var got = new List<InboundMessage>();
        c.MessageReceived += m => { lock (got) got.Add(m); };
        await c.StartAsync(default);
        await Assert.Eventually(() => got.Count == 1, "queued message delivered");
        var m = got[0];
        Assert.Equal(ConversationKind.Channel, m.Conversation.Kind, "kind");
        Assert.Equal("0", m.Conversation.Peer, "channel index");
        Assert.Equal("W0QQ Base", m.FromName, "sender");
        Assert.Equal("morning all", m.Text, "text");
        Assert.True(m.QueuedWhileAway, "marked as held while away");
        Assert.Equal(6.5, m.Reception!.Snr, "snr");
        Assert.Equal(2, m.Reception.Hops, "hops");

        // Live message after connect: tickle + sync.
        var key = node.AddContact("KE0XYZ Mobile");
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");
        await node.HearDirectAsync(key, "copy that"); // contact unknown to the connector yet (prefix only)
        await Assert.Eventually(() => got.Count == 2, "live DM delivered", 10_000);
        Assert.Equal(ConversationKind.Direct, got[1].Conversation.Kind, "dm kind");
        Assert.False(got[1].QueuedWhileAway, "live message not marked");
        Assert.True(got[1].Reception!.Direct, "direct route");
    }

    [Test]
    public static async Task MeshCoreDirectMessageIsSentThenDelivered()
    {
        await using var node = new FakeMeshCoreNode();
        var key = node.AddContact("KE0XYZ Mobile");
        await using var c = new MeshCoreConnector(Config("mc", MeshNetwork.MeshCore, node.Port), Fast);
        var updates = new List<DeliveryUpdate>();
        c.DeliveryChanged += u => { lock (updates) updates.Add(u); };
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");

        var to = new ConversationKey("mc", ConversationKind.Direct, Convert.ToHexString(key).ToLowerInvariant());
        var result = await c.SendAsync(to, "battery swap Saturday", default);
        Assert.True(result.Ok, result.Error ?? "send");
        Assert.NotNull(result.AckTag, "ack tag");
        await Assert.Eventually(() => updates.Any(u => u.AckTag == result.AckTag && u.Status == DeliveryStatus.Delivered), "delivered");
        Assert.Equal(1234, updates.Single().RoundTripMs, "round trip");
        Assert.True(node.Received.Any(r => r[0] == MeshCoreProtocol.CmdSendTxtMsg), "node got the DM");

        var channel = await c.SendAsync(new ConversationKey("mc", ConversationKind.Channel, "1"), "hello denver", default);
        Assert.True(channel.Ok, channel.Error ?? "channel send");
        Assert.Equal(null, channel.AckTag, "channels have no ack");
    }

    [Test]
    public static async Task MeshCoreRoomPostIsFiledUnderTheRoomWithItsAuthor()
    {
        await using var node = new FakeMeshCoreNode();
        var room = node.AddContact("Shack Board", type: 3);
        var sarah = node.AddContact("Sarah");
        await using var c = new MeshCoreConnector(Config("mc", MeshNetwork.MeshCore, node.Port), Fast);
        var got = new List<InboundMessage>();
        c.MessageReceived += m => { lock (got) got.Add(m); };
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");
        await node.HearRoomPostAsync(room, sarah, "dinner at 6");
        await Assert.Eventually(() => got.Count == 1, "post delivered");
        Assert.Equal(ConversationKind.Room, got[0].Conversation.Kind, "room conversation");
        Assert.Equal(Convert.ToHexString(room).ToLowerInvariant(), got[0].Conversation.Peer, "keyed by the room");
        Assert.Equal("Sarah", got[0].FromName, "author resolved from contacts");
        Assert.Equal("dinner at 6", got[0].Text, "text without the signature bytes");
    }

    [Test]
    public static async Task MeshCoreRefusesADifferentNodeAtTheAddress()
    {
        await using var node = new FakeMeshCoreNode();
        await using var c = new MeshCoreConnector(Config("mc", MeshNetwork.MeshCore, node.Port, identity: new string('a', 64)), Fast);
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Error, "refused");
        Assert.Contains("different node", c.StateDetail, "explains why");
        var send = await c.SendAsync(new ConversationKey("mc", ConversationKind.Channel, "0"), "x", default);
        Assert.False(send.Ok, "nothing sent to the wrong node");
    }

    [Test]
    public static async Task MeshtasticConnectsAndNamesChannels()
    {
        await using var node = new FakeMeshtasticNode();
        await using var c = new MeshtasticConnector(Config("mt", MeshNetwork.Meshtastic, node.Port), Fast);
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");
        Assert.Equal(node.Identity, c.Self!.Identity, "identity");
        Assert.Equal("KD0ABC Shack MT", c.Self.Name, "name");
        Assert.Equal("2.7.15.abcdef", c.Self.Firmware, "firmware");
        var channels = c.Channels;
        Assert.Equal(2, channels.Count, "disabled slot hidden");
        Assert.Equal("LongFast", channels[0].Name, "primary named after preset");
        Assert.Equal(ChannelKind.Public, channels[0].Kind, "default key is public");
        Assert.Equal("TrailCrew", channels[1].Name, "secondary");
        Assert.Equal(ChannelKind.Private, channels[1].Kind, "private");
        Assert.Equal("Trail Base", c.Peers.Single().Name, "peer");
    }

    [Test]
    public static async Task MeshtasticTextInAndAcksOut()
    {
        await using var node = new FakeMeshtasticNode();
        await using var c = new MeshtasticConnector(Config("mt", MeshNetwork.Meshtastic, node.Port), Fast);
        var got = new List<InboundMessage>();
        var updates = new List<DeliveryUpdate>();
        c.MessageReceived += m => { lock (got) got.Add(m); };
        c.DeliveryChanged += u => { lock (updates) updates.Add(u); };
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");

        await node.HearTextAsync(0x77e0a913, MeshtasticProtocol.Broadcast, 0, "net at 19:00");
        await node.HearTextAsync(0x77e0a913, node.MyNum, 0, "you there?");
        await Assert.Eventually(() => got.Count == 2, "two messages");
        Assert.Equal(ConversationKind.Channel, got[0].Conversation.Kind, "broadcast is channel");
        Assert.Equal("Trail Base", got[0].FromName, "name from node DB");
        Assert.Equal(1, got[0].Reception!.Hops, "hops = start - limit");
        Assert.Equal(-97, got[0].Reception!.Rssi, "rssi (negative varint)");
        Assert.Equal(6.25, got[0].Reception!.Snr, "snr");
        Assert.Equal(ConversationKind.Direct, got[1].Conversation.Kind, "addressed to us is direct");
        Assert.Equal("!77e0a913", got[1].Conversation.Peer, "peer id");

        var dm = await c.SendAsync(got[1].Conversation, "yes", default);
        Assert.True(dm.Ok, dm.Error ?? "dm");
        await Assert.Eventually(() => updates.Any(u => u.AckTag == dm.AckTag && u.Status == DeliveryStatus.Delivered), "dm delivered");
        var ch = await c.SendAsync(got[0].Conversation, "copy", default);
        await Assert.Eventually(() => updates.Any(u => u.AckTag == ch.AckTag && u.Status == DeliveryStatus.Delivered), "channel relayed");
        Assert.True(node.Sent.Any(s => s.To == 0x77e0a913 && s.Text == "yes"), "dm addressed to the node");
        Assert.True(node.Sent.Any(s => s.To == MeshtasticProtocol.Broadcast && s.Channel == 0 && s.Text == "copy"), "broadcast on channel 0");
    }
}

// SPDX-License-Identifier: GPL-3.0-or-later
using MeshMessenger.Connectors;
using MeshMessenger.Connectors.MeshCore;
using MeshMessenger.Connectors.Meshtastic;
using MeshMessenger.Core;
using System.Security.Cryptography;
using System.Text;

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

    /// <summary>
    /// A MeshCore channel frame carries no sender key, so the only way to answer
    /// a channel sender privately is to match the self-asserted name against the
    /// node's contacts. That must find the contact when it exists, find nothing
    /// when it doesn't, and offer both when a name is ambiguous — never guess.
    /// </summary>
    [Test]
    public static async Task MeshCoreResolvesAChannelSenderOnlyByNameAndSaysWhenItCannot()
    {
        await using var node = new FakeMeshCoreNode();
        var mobile = node.AddContact("KE0XYZ Mobile");
        node.AddContact("Tower Repeater", type: 2);
        await using var c = new MeshCoreConnector(Config("mc", MeshNetwork.MeshCore, node.Port), Fast);
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");

        var hex = Convert.ToHexString(mobile).ToLowerInvariant();
        var found = c.ResolveSenderPeers(ConversationKind.Channel, "KE0XYZ Mobile", "KE0XYZ Mobile");
        Assert.Equal(1, found.Count, "known name resolves to one contact");
        Assert.Equal(hex, found[0], "resolves to that contact's key");

        // Case and surrounding whitespace are not identity; neither is a stranger.
        Assert.Equal(1, c.ResolveSenderPeers(ConversationKind.Channel, "x", " ke0xyz mobile ").Count, "match ignores case and padding");
        Assert.Equal(0, c.ResolveSenderPeers(ConversationKind.Channel, "ZERO", "ZERO").Count, "unknown name resolves to nobody");

        // Repeaters are contacts but not people to message.
        Assert.Equal(0, c.ResolveSenderPeers(ConversationKind.Channel, "Tower Repeater", "Tower Repeater").Count, "repeaters are not offered");

        // A room post is signed, so the id is real — but only if we hold it.
        Assert.Equal(1, c.ResolveSenderPeers(ConversationKind.Room, hex, "KE0XYZ Mobile").Count, "known room author resolves");
        Assert.Equal(0, c.ResolveSenderPeers(ConversationKind.Room, "deadbeef", null).Count, "unheld room author does not");
    }

    /// <summary>
    /// Joining a channel writes a name and a 128-bit key into a free slot. A
    /// "#name" channel must derive the same key everyone else derives, or two
    /// people typing the same name end up on different channels and never find
    /// out. Anything else needs its key given, and the primary slot is not
    /// something Zeus will clear.
    /// </summary>
    [Test]
    public static async Task MeshCoreJoinsAndLeavesChannelsAndDerivesTheHashtagKey()
    {
        await using var node = new FakeMeshCoreNode();
        await using var c = new MeshCoreConnector(Config("mc", MeshNetwork.MeshCore, node.Port), Fast);
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");
        Assert.Equal(3, c.Channels.Count, "three channels to start");

        // A name with no '#' and no key is not a channel anyone could share.
        Assert.Contains("needs its key", await c.JoinChannelAsync("Neighbours", null, default) ?? "", "plain name refused");
        Assert.Contains("32 hex", await c.JoinChannelAsync("Neighbours", "nonsense", default) ?? "", "bad key refused");

        Assert.Equal(null, await c.JoinChannelAsync("#testing", null, default), "hashtag channel joined");
        await Assert.Eventually(() => c.Channels.Count == 4, "channel appears");
        var joined = node.ChannelSlots[3];
        Assert.Equal("#testing", joined.Name, "name written to the first free slot");
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("#testing")).AsSpan(0, 16));
        Assert.Equal(expected, Convert.ToHexString(joined.Secret), "key derived from the name, like every other client");

        // Joining the same channel twice would waste a slot and confuse the inbox.
        Assert.Contains("Already joined", await c.JoinChannelAsync("#testing", null, default) ?? "", "duplicate refused");

        // The primary channel is where ordinary traffic goes; Zeus won't strand the node.
        Assert.Contains("primary", await c.LeaveChannelAsync(0, default) ?? "", "slot 0 protected");

        Assert.Equal(null, await c.LeaveChannelAsync(3, default), "left the channel");
        await Assert.Eventually(() => c.Channels.Count == 3, "channel gone");
        Assert.Equal("", node.ChannelSlots[3].Name, "slot cleared on the node");
        Assert.True(node.ChannelSlots[3].Secret.All(b => b == 0), "key cleared too");
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
    public static async Task MeshCoreNeverTransmitsOnAChannelSlotTheNodeDoesNotHave()
    {
        // The firmware takes the low byte of the slot index it is handed, so an index with
        // no channel behind it used to go out on the public channel for everyone to read.
        await using var node = new FakeMeshCoreNode();
        await using var c = new MeshCoreConnector(Config("mc", MeshNetwork.MeshCore, node.Port), Fast);
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");
        var before = node.Received.Count(r => r[0] == MeshCoreProtocol.CmdSendChannelTxtMsg);

        foreach (var slot in new[] { "7", "256", "-1" })
        {
            var result = await c.SendAsync(new ConversationKey("mc", ConversationKind.Channel, slot), "hello", default);
            Assert.False(result.Ok, $"slot {slot} refused");
            Assert.Contains("no channel", result.Error ?? "", $"slot {slot} says why");
        }
        Assert.Equal(before, node.Received.Count(r => r[0] == MeshCoreProtocol.CmdSendChannelTxtMsg),
            "nothing was transmitted");
    }

    [Test]
    public static async Task MeshCoreSavesAHeardStationBeforeTheFirstDirectMessage()
    {
        // Real-hardware finding: with manual-add on, the node pushes NEW_ADVERT but doesn't
        // save the contact, and SEND_TXT_MSG then fails with "not found".
        await using var node = new FakeMeshCoreNode();
        await using var c = new MeshCoreConnector(Config("mc", MeshNetwork.MeshCore, node.Port), Fast);
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");
        var wio = await node.HearUnsavedAdvertAsync("KQ4WLR Wio L1");
        var id = Convert.ToHexString(wio).ToLowerInvariant();
        await Assert.Eventually(() => c.Peers.Any(p => p.Id == id), "heard station listed");
        Assert.False(c.Peers.Single(p => p.Id == id).SavedOnNode, "marked not saved");
        Assert.Equal(0, node.Contacts.Count, "node hasn't saved it");

        var sent = await c.SendAsync(new ConversationKey("mc", ConversationKind.Direct, id), "testing", default);
        Assert.True(sent.Ok, sent.Error ?? "dm after save");
        Assert.Equal(1, node.Contacts.Count, "saved on the node first");
        Assert.True(c.Peers.Single(p => p.Id == id).SavedOnNode, "now saved");
        Assert.True(node.Received.Any(r => r[0] == MeshCoreProtocol.CmdSendTxtMsg), "then sent");
    }

    [Test]
    public static async Task MeshCoreReportsAFullContactListAndCanMakeRoom()
    {
        // Real-hardware finding: on a busy mesh the node auto-adds every station it
        // hears until its contact table is full, and then it can't save a new one,
        // so direct messages to that station fail with TABLE_FULL.
        // DEVICE_INFO carries MAX_CONTACTS/2 in one byte, so capacities are even.
        await using var node = new FakeMeshCoreNode { MaxContacts = 4 };
        var old = node.AddContact("W0QQ Base");
        node.AddContact("Tower Repeater", type: 2);
        node.AddContact("Sarah");
        node.AddContact("KE0XYZ Mobile");
        await using var c = new MeshCoreConnector(Config("mc", MeshNetwork.MeshCore, node.Port), Fast);
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");

        Assert.Equal(4, c.PeerCapacity, "capacity read from the node");
        Assert.Equal(4, c.Peers.Count(p => p.SavedOnNode), "reserved slots are not counted as contacts");
        Assert.Equal(false, c.RecycleOldestWhenFull, "recycling off to begin with");

        var wio = await node.HearUnsavedAdvertAsync("KQ4WLR Wio L1");
        var id = Convert.ToHexString(wio).ToLowerInvariant();
        await Assert.Eventually(() => c.Peers.Any(p => p.Id == id), "heard station listed");
        var blocked = await c.SendAsync(new ConversationKey("mc", ConversationKind.Direct, id), "hello", default);
        Assert.False(blocked.Ok, "refused while the list is full");
        Assert.Contains("contact list is full", blocked.Error!, "says why");

        // Making room by hand lets it through.
        Assert.Equal(null, await c.RemovePeerAsync(Convert.ToHexString(old).ToLowerInvariant(), default), "contact removed");
        Assert.Equal(3, c.Peers.Count(p => p.SavedOnNode), "one slot free");
        var sent = await c.SendAsync(new ConversationKey("mc", ConversationKind.Direct, id), "hello", default);
        Assert.True(sent.Ok, sent.Error ?? "sent once there was room");
    }

    [Test]
    public static async Task MeshCoreCanTurnOnContactRecycling()
    {
        await using var node = new FakeMeshCoreNode { MaxContacts = 2 };
        node.AddContact("W0QQ Base");
        node.AddContact("Sarah");
        await using var c = new MeshCoreConnector(Config("mc", MeshNetwork.MeshCore, node.Port), Fast);
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");

        Assert.Equal(null, await c.SetRecycleOldestWhenFullAsync(true, default), "setting saved");
        Assert.Equal(true, c.RecycleOldestWhenFull, "reported back");

        var wio = await node.HearUnsavedAdvertAsync("KQ4WLR Wio L1");
        var id = Convert.ToHexString(wio).ToLowerInvariant();
        await Assert.Eventually(() => c.Peers.Any(p => p.Id == id), "heard station listed");
        var sent = await c.SendAsync(new ConversationKey("mc", ConversationKind.Direct, id), "hello", default);
        Assert.True(sent.Ok, sent.Error ?? "the node recycled a slot and sent");
    }

    [Test]
    public static async Task MeshCoreFavouriteIsSetOnTheNodeAndLastUpdatedComesFromOurOwnClock()
    {
        await using var node = new FakeMeshCoreNode();
        var key = node.AddContact("KQ4WLR Wio L1");
        var id = Convert.ToHexString(key).ToLowerInvariant();
        await using var c = new MeshCoreConnector(Config("mc", MeshNetwork.MeshCore, node.Port), Fast);
        await c.StartAsync(default);
        await Assert.Eventually(() => c.State == ConnectorState.Connected, "connected");

        var before = c.Peers.Single(p => p.Id == id);
        Assert.False(before.Favorite, "not a favourite to begin with");
        // The advert's own timestamp is the sender's clock and here it is two hours ahead;
        // LastUpdated is our node's clock, which is what the panel shows.
        Assert.True(before.LastHeard > DateTimeOffset.UtcNow.AddHours(1), "advert time is ahead, as the field showed");
        Assert.True(before.LastUpdated < DateTimeOffset.UtcNow, "our node's update time is in the past");

        Assert.Equal(null, await c.SetPeerFavouriteAsync(id, true, default), "favourite set");
        Assert.True(c.Peers.Single(p => p.Id == id).Favorite, "reported back as a favourite");
        Assert.Equal(MeshCoreProtocol.ContactFlagFavourite, node.ContactFlags[Convert.ToHexString(key)], "flag written to the node");

        Assert.Equal(null, await c.SetPeerFavouriteAsync(id, false, default), "favourite cleared");
        Assert.False(c.Peers.Single(p => p.Id == id).Favorite, "no longer a favourite");
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

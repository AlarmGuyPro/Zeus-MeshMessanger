// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using MeshMessenger.Core;

namespace MeshMessenger.Tests;

/// <summary>A connector double for routing rules: records sends, never touches a network.</summary>
public sealed class StubConnector(string id, MeshNetwork network) : IMeshConnector
{
    public string Id => id;
    public MeshNetwork Network => network;
    public string DisplayName => id;
    public ConnectorState State { get; set; } = ConnectorState.Connected;
    public string? StateDetail => null;
    public SelfInfo? Self => new(id, id, null, null, null, null);
    public string Host => "127.0.0.1";
    public IReadOnlyList<PeerInfo> Peers => [];
    public IReadOnlyList<ChannelInfo> Channels => [];
    public int Limit { get; set; } = 200;
    public TimeSpan Airtime { get; set; } = TimeSpan.Zero;
    public List<(ConversationKey To, string Text, long At)> Sends { get; } = [];
    public static readonly Stopwatch Clock = Stopwatch.StartNew();
    public int MaxTextBytes(ConversationKind kind) => Limit;
    public event Action<InboundMessage>? MessageReceived;
    public event Action<DeliveryUpdate>? DeliveryChanged;
    public event Action? Changed;
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public string? TitleFor(ConversationKey key) => null;

    public Task<SendResult> SendAsync(ConversationKey to, string text, CancellationToken ct)
    {
        lock (Sends) Sends.Add((to, text, Clock.ElapsedMilliseconds));
        return Task.FromResult(SendResult.Success($"t{Sends.Count}", Airtime));
    }

    public void Hear(InboundMessage m) => MessageReceived?.Invoke(m);
    public void Ack(string tag) => DeliveryChanged?.Invoke(new DeliveryUpdate(tag, DeliveryStatus.Delivered, null, 900));
    public void Touch() => Changed?.Invoke();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public static class RouterTests
{
    private static InboundMessage Msg(string connector, ConversationKind kind, string peer, string text) =>
        new(new ConversationKey(connector, kind, peer), "from", "From", text, DateTimeOffset.UtcNow, Guid.NewGuid().ToString(), null, false);

    [Test]
    public static async Task ReplyGoesOnlyToTheConversationsNodeAndNeverFallsBack()
    {
        var mt = new StubConnector("mt", MeshNetwork.Meshtastic);
        var mc = new StubConnector("mc", MeshNetwork.MeshCore);
        var store = new MessageStore();
        using var router = new MessageRouter(store, new TransmitCoordinator(), [mt, mc]);
        mc.Hear(Msg("mc", ConversationKind.Channel, "0", "hi"));
        var conv = store.Summaries().Single().Conversation.Key.Id;

        var ok = await router.ReplyAsync(conv, "reply", default);
        Assert.True(ok.Ok, "reply sent");
        Assert.Equal(1, mc.Sends.Count, "on MeshCore");
        Assert.Equal(0, mt.Sends.Count, "never on Meshtastic");

        mc.State = ConnectorState.Disconnected;
        var fail = await router.ReplyAsync(conv, "while offline", default);
        Assert.False(fail.Ok, "fails when its node is offline");
        Assert.Equal(1, mc.Sends.Count, "nothing more on MeshCore");
        Assert.Equal(0, mt.Sends.Count, "and still nothing on Meshtastic");
    }

    [Test]
    public static void SameChannelOnTwoNetworksIsTwoConversations()
    {
        var mt = new StubConnector("mt", MeshNetwork.Meshtastic);
        var mc = new StubConnector("mc", MeshNetwork.MeshCore);
        var store = new MessageStore();
        using var router = new MessageRouter(store, new TransmitCoordinator(), [mt, mc]);
        mt.Hear(Msg("mt", ConversationKind.Channel, "0", "a"));
        mc.Hear(Msg("mc", ConversationKind.Channel, "0", "b"));
        Assert.Equal(2, store.Summaries().Count, "kept separate");
    }

    [Test]
    public static void ConnectorCannotFileMessagesUnderAnotherNode()
    {
        var mt = new StubConnector("mt", MeshNetwork.Meshtastic);
        var mc = new StubConnector("mc", MeshNetwork.MeshCore);
        var store = new MessageStore();
        using var router = new MessageRouter(store, new TransmitCoordinator(), [mt, mc]);
        mt.Hear(Msg("mc", ConversationKind.Channel, "0", "spoofed"));
        Assert.Equal(0, store.Summaries().Count, "dropped");
    }

    [Test]
    public static async Task TooLongIsRefusedBeforeSending()
    {
        var mc = new StubConnector("mc", MeshNetwork.MeshCore) { Limit = 10 };
        var store = new MessageStore();
        using var router = new MessageRouter(store, new TransmitCoordinator(), [mc]);
        var r = await router.StartAsync("mc", ConversationKind.Channel, "0", "this is far too long", default);
        Assert.False(r.Ok, "refused");
        Assert.Contains("allows 10", r.Error, "says the limit");
        Assert.Equal(0, mc.Sends.Count, "nothing sent");
    }

    [Test]
    public static async Task DeliveryUpdatesTheMessage()
    {
        var mc = new StubConnector("mc", MeshNetwork.MeshCore);
        var store = new MessageStore();
        using var router = new MessageRouter(store, new TransmitCoordinator(), [mc]);
        var r = await router.StartAsync("mc", ConversationKind.Direct, "abc", "hello", default);
        Assert.Equal(DeliveryStatus.Sent, r.Message!.Status, "sent");
        mc.Ack(r.Message.AckTag!);
        var m = store.Messages(r.ConversationId!, false)!.Single();
        Assert.Equal(DeliveryStatus.Delivered, m.Status, "delivered");
        Assert.Equal(900, m.RoundTripMs, "rtt");
    }

    [Test]
    public static async Task TransmissionsAcrossNodesAreSerialisedAndSpaced()
    {
        var mt = new StubConnector("mt", MeshNetwork.Meshtastic) { Airtime = TimeSpan.FromMilliseconds(300) };
        var mc = new StubConnector("mc", MeshNetwork.MeshCore) { Airtime = TimeSpan.FromMilliseconds(300) };
        var store = new MessageStore();
        using var router = new MessageRouter(store, new TransmitCoordinator(), [mt, mc]);
        await Task.WhenAll(
            router.StartAsync("mt", ConversationKind.Channel, "0", "one", default),
            router.StartAsync("mc", ConversationKind.Channel, "0", "two", default));
        var times = mt.Sends.Concat(mc.Sends).Select(s => s.At).Order().ToArray();
        Assert.True(times[1] - times[0] >= 500, $"second transmission waited for the first's airtime (gap {times[1] - times[0]} ms)");
    }

    [Test]
    public static void HistorySurvivesARestartAndUnansweredSendsBecomeFailed()
    {
        var store = new MessageStore();
        var key = new ConversationKey("mc", ConversationKind.Channel, "0");
        store.GetOrAdd(key, MeshNetwork.MeshCore, "Public");
        store.Append(key, new StoredMessage("1", MessageDirection.Outbound, "me", "Me", "pending", DateTimeOffset.UtcNow, DeliveryStatus.Sending, null));
        var json = System.Text.Json.JsonSerializer.Serialize(store.Snapshot());
        var restored = new MessageStore();
        restored.Restore(System.Text.Json.JsonSerializer.Deserialize<HistorySnapshot>(json)!);
        var m = restored.Messages(key.Id, false)!.Single();
        Assert.Equal(DeliveryStatus.Failed, m.Status, "sending → failed");
        Assert.Equal("Public", restored.Find(key.Id)!.Title, "title kept");
    }
}

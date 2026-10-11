// SPDX-License-Identifier: GPL-3.0-or-later
using MeshMessenger.Connectors.MeshCore;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshMessenger.Tests;

/// <summary>Runs the plugin's real endpoints on a loopback Kestrel, mounted the way Zeus mounts them.</summary>
public sealed class PluginHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    public MeshMessengerPlugin Plugin { get; }
    public HttpClient Http { get; }

    private PluginHost(WebApplication app, MeshMessengerPlugin plugin, HttpClient http)
    {
        _app = app;
        Plugin = plugin;
        Http = http;
    }

    public static async Task<PluginHost> StartAsync(FakePluginContext context)
    {
        var plugin = new MeshMessengerPlugin();
        await plugin.InitializeAsync(context, default);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        plugin.MapEndpoints(app.MapGroup("/api/plugins/io.github.alarmguypro.meshmessenger"));
        await app.StartAsync();
        var address = app.Urls.First();
        var http = new HttpClient { BaseAddress = new Uri(address + "/api/plugins/io.github.alarmguypro.meshmessenger/") };
        return new PluginHost(app, plugin, http);
    }

    public async Task<JsonNode> GetAsync(string path) =>
        JsonNode.Parse(await Http.GetStringAsync(path))!;

    public async Task<(HttpStatusCode Status, JsonNode? Body)> PostAsync(string path, object body)
    {
        var r = await Http.PostAsJsonAsync(path, body);
        var text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
    }

    /// <summary>Only the conversations with messages; joined channels are listed too, even when silent.</summary>
    public async Task<List<JsonNode>> DirectConversationsAsync() =>
        (await GetAsync("conversations")).AsArray()
            .Where(c => c!["kind"]!.GetValue<string>() == "direct").Select(c => c!).ToList();

    public async Task<HttpStatusCode> DeleteAsync(string path) => (await Http.DeleteAsync(path)).StatusCode;

    public async ValueTask DisposeAsync()
    {
        await Plugin.ShutdownAsync(default);
        await _app.StopAsync();
        await _app.DisposeAsync();
        Http.Dispose();
    }
}

public static class ApiTests
{
    [Test]
    public static async Task AddNodeReceiveReplyAndSurviveARestart()
    {
        await using var node = new FakeMeshCoreNode();
        var friend = node.AddContact("KE0XYZ Mobile");
        var settings = new MemorySettings();
        var context = new FakePluginContext { Settings = settings };

        string conversationId;
        await using (var host = await PluginHost.StartAsync(context))
        {
            var (status, body) = await host.PostAsync("nodes", new { network = "meshcore", host = $"127.0.0.1:{node.Port}", name = "" });
            Assert.Equal(HttpStatusCode.OK, status, $"add node: {body}");
            await Assert.Eventually(() => host.GetAsync("status").Result["nodes"]![0]!["state"]!.GetValue<string>() == "connected", "connected over the API");
            var nodeJson = (await host.GetAsync("status"))["nodes"]![0]!;
            Assert.Equal(node.Identity, nodeJson["identity"]!.GetValue<string>(), "identity recorded");
            Assert.Equal(146, nodeJson["maxChannelTextBytes"]!.GetValue<int>(), "channel limit reported");

            await node.HearDirectAsync(friend, "are you on?");
            await Assert.Eventually(() => host.DirectConversationsAsync().Result.Count == 1, "conversation listed");
            var conv = (await host.DirectConversationsAsync())[0];
            // The node's three channel slots are listed as well, ready to post to.
            Assert.Equal(4, (await host.GetAsync("conversations")).AsArray().Count, "joined channels listed too");
            conversationId = conv["id"]!.GetValue<string>();
            Assert.Equal("KE0XYZ Mobile", conv["title"]!.GetValue<string>(), "titled from contacts");
            Assert.Equal(1, conv["unread"]!.GetValue<int>(), "unread");

            var (_, reply) = await host.PostAsync($"conversations/{Uri.EscapeDataString(conversationId)}/reply", new { text = "yes, on the air" });
            Assert.True(reply!["ok"]!.GetValue<bool>(), $"reply: {reply}");
            await Assert.Eventually(() => host.GetAsync($"conversations/{Uri.EscapeDataString(conversationId)}/messages").Result
                .AsArray().Any(m => m!["status"]!.GetValue<string>() == "delivered"), "delivered");

            // Wait for the history to be saved.
            await Assert.Eventually(() => settings.Values.ContainsKey("history.v1"), "history saved", 8000);
        }

        await using (var again = await PluginHost.StartAsync(new FakePluginContext { Settings = settings }))
        {
            Assert.Equal(1, (await again.DirectConversationsAsync()).Count, "conversation restored after restart");
            var messages = await again.GetAsync($"conversations/{Uri.EscapeDataString(conversationId)}/messages");
            Assert.Equal(2, messages.AsArray().Count, "both messages restored");
            await Assert.Eventually(() => again.GetAsync("status").Result["nodes"]![0]!["state"]!.GetValue<string>() == "connected", "node config restored");
        }
    }

    [Test]
    public static async Task DebugLogAdvertAndHousekeepingOverTheApi()
    {
        await using var node = new FakeMeshCoreNode();
        var friend = node.AddContact("KE0XYZ Mobile");
        await using var host = await PluginHost.StartAsync(new FakePluginContext());
        var (added, _) = await host.PostAsync("nodes", new { network = "meshcore", host = $"127.0.0.1:{node.Port}", name = "" });
        Assert.Equal(HttpStatusCode.OK, added, "node added");
        await Assert.Eventually(() => host.GetAsync("status").Result["nodes"]![0]!["state"]!.GetValue<string>() == "connected", "connected");
        var nodeId = (await host.GetAsync("status"))["nodes"]![0]!["id"]!.GetValue<string>();

        // Debug: the node's details and a log of what happened.
        var debug = await host.GetAsync("debug");
        var first = debug["nodes"]![0]!;
        Assert.Equal("v1.12.0", first["self"]!["firmware"]!.GetValue<string>(), "firmware in debug");
        Assert.Equal(1, first["contacts"]!.GetValue<int>(), "contact counted");
        Assert.True(first["activity"]!.AsArray().Count > 0, "activity logged");
        Assert.True(first["activity"]!.AsArray().Any(a => a!["text"]!.GetValue<string>().Contains("Connected to")), "connect logged");

        // Advert: transmits, so only from an explicit call.
        var (advertStatus, advert) = await host.PostAsync($"nodes/{nodeId}/announce", new { flood = true });
        Assert.Equal(HttpStatusCode.OK, advertStatus, "advert accepted");
        Assert.True(advert!["ok"]!.GetValue<bool>(), $"advert sent: {advert}");
        Assert.True(node.Received.Any(r => r[0] == MeshCoreProtocol.CmdSendSelfAdvert), "node asked to advertise");

        // Re-read contacts without transmitting.
        node.AddContact("W0QQ Base");
        var (refreshStatus, refresh) = await host.PostAsync($"nodes/{nodeId}/refresh-peers", new { });
        Assert.Equal(HttpStatusCode.OK, refreshStatus, "refresh accepted");
        Assert.Equal(2, refresh!["count"]!.GetValue<int>(), "second contact picked up");

        // A failed send can be cleared, and the conversation deleted.
        await node.HearDirectAsync(friend, "are you on?");
        await Assert.Eventually(() => host.DirectConversationsAsync().Result.Count == 1, "conversation listed");
        var id = (await host.DirectConversationsAsync())[0]["id"]!.GetValue<string>();
        var escaped = Uri.EscapeDataString(id);
        node.FailSends = true;
        var (_, failed) = await host.PostAsync($"conversations/{escaped}/reply", new { text = "this one fails" });
        Assert.False(failed!["ok"]!.GetValue<bool>(), "send refused by the node");
        Assert.Contains("packet buffer", failed["error"]!.GetValue<string>(), "explains the node's reason");

        var (_, cleared) = await host.PostAsync($"conversations/{escaped}/clear-failed", new { });
        Assert.Equal(1, cleared!["count"]!.GetValue<int>(), "failed message cleared");
        Assert.Equal(1, (await host.GetAsync($"conversations/{escaped}/messages")).AsArray().Count, "only the inbound one left");

        Assert.Equal(HttpStatusCode.OK, await host.DeleteAsync($"conversations/{escaped}"), "conversation deleted");
        Assert.Equal(0, (await host.DirectConversationsAsync()).Count, "gone from the list");
    }

    [Test]
    public static async Task JoinedChannelsAreListedAndCanBePostedToBeforeAnyTraffic()
    {
        await using var node = new FakeMeshCoreNode();
        await using var host = await PluginHost.StartAsync(new FakePluginContext());
        await host.PostAsync("nodes", new { network = "meshcore", host = $"127.0.0.1:{node.Port}", name = "" });
        await Assert.Eventually(() => host.GetAsync("status").Result["nodes"]![0]!["state"]!.GetValue<string>() == "connected", "connected");

        var channels = (await host.GetAsync("conversations")).AsArray()
            .Where(c => c!["kind"]!.GetValue<string>() == "channel").ToList();
        Assert.Equal(3, channels.Count, "every joined channel listed, with no traffic yet");
        Assert.Equal("Public", channels[0]!["title"]!.GetValue<string>(), "named from the node");

        // Posting to a channel nobody has spoken on yet must work.
        var id = Uri.EscapeDataString(channels[0]!["id"]!.GetValue<string>());
        var (_, sent) = await host.PostAsync($"conversations/{id}/reply", new { text = "net at 19:00" });
        Assert.True(sent!["ok"]!.GetValue<bool>(), $"posted: {sent}");
        Assert.True(node.Received.Any(r => r[0] == MeshCoreProtocol.CmdSendChannelTxtMsg), "the node was asked to send it");
        Assert.Equal(1, (await host.GetAsync($"conversations/{id}/messages")).AsArray().Count, "kept in the thread");
    }

    [Test]
    public static async Task PublicAddressesAndBadInputAreRefused()
    {
        await using var host = await PluginHost.StartAsync(new FakePluginContext());
        var (s1, b1) = await host.PostAsync("nodes", new { network = "meshcore", host = "8.8.8.8" });
        Assert.Equal(HttpStatusCode.BadRequest, s1, "public address refused");
        Assert.Contains("local-network", b1!["error"]!.GetValue<string>(), "explains");
        var (s2, _) = await host.PostAsync("nodes", new { network = "lora", host = "192.168.1.5" });
        Assert.Equal(HttpStatusCode.BadRequest, s2, "unknown network refused");
        var (s3, b3) = await host.PostAsync("discovery/scan", new { ranges = new[] { "10.0.0.0/8" } });
        Assert.Equal(HttpStatusCode.BadRequest, s3, "huge range refused");
        Assert.Contains("too large", b3!["error"]!.GetValue<string>(), "explains");
        var (s4, b4) = await host.PostAsync("conversations", new { connectorId = "nope", kind = "channel", peer = "0", text = "hi" });
        Assert.Equal(HttpStatusCode.OK, s4, "send result");
        Assert.False(b4!["ok"]!.GetValue<bool>(), "no such node");
    }

    [Test]
    public static async Task ScanOverTheApiFindsANodeAndMarksItAddedOnceAdded()
    {
        // The plugin scans the real MeshCore port, so the fake must listen on 5000.
        FakeMeshCoreNode mc;
        try { mc = new FakeMeshCoreNode("127.0.0.7", 5000); }
        catch (System.Net.Sockets.SocketException)
        {
            Console.WriteLine("        (skipped: port 5000 is in use on this machine)");
            return;
        }
        await using var _ = mc;
        await using var host = await PluginHost.StartAsync(new FakePluginContext());
        var (status, body) = await host.PostAsync("discovery/scan", new { ranges = new[] { "127.0.0.7/32" }, mdns = false });
        Assert.Equal(HttpStatusCode.OK, status, $"scan started: {body}");
        await Assert.Eventually(() => !host.GetAsync("discovery").Result["scan"]!["running"]!.GetValue<bool>(), "scan finished", 20_000);
        var found = (await host.GetAsync("discovery"))["scan"]!["found"]!.AsArray();
        var hit = found.Single(f => f!["identity"]!.GetValue<string>() == mc.Identity)!;
        Assert.Equal("127.0.0.7", hit["host"]!.GetValue<string>(), "address");
        Assert.Equal("meshcore", hit["network"]!.GetValue<string>(), "network");
        Assert.True(hit["addedAs"] is null, "not added yet");

        var (added, _) = await host.PostAsync("nodes", new { network = "meshcore", host = "127.0.0.7", identity = mc.Identity });
        Assert.Equal(HttpStatusCode.OK, added, "added from the scan result");
        var again = (await host.GetAsync("discovery"))["scan"]!["found"]!.AsArray().Single()!;
        Assert.True(again["addedAs"] is not null, "now marked as added");
        var (dup, dupBody) = await host.PostAsync("nodes", new { network = "meshcore", host = "127.0.0.7", identity = mc.Identity });
        Assert.Equal(HttpStatusCode.BadRequest, dup, "same node can't be added twice");
        Assert.Contains("already added", dupBody!["error"]!.GetValue<string>(), "explains");
    }
}

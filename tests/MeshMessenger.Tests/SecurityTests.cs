// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using MeshMessenger.Connectors;
using MeshMessenger.Connectors.MeshCore;
using MeshMessenger.Core;
using MeshMessenger.Discovery;

namespace MeshMessenger.Tests;

/// <summary>
/// The guards the submission claims in writing, each with a test that fails when the guard
/// is removed. Written after a maintainer's mutation run: four changes to security-relevant
/// code — allowing loopback, dropping the address check when a node is followed to a new
/// address, dropping it again on re-find candidates, and removing the frame-size cap — left
/// the whole suite passing. A guard with no failing test is a comment.
///
/// None of these carry <c>Loopback = true</c>: they run with the shipped default, which is
/// the whole point.
/// </summary>
public static class SecurityTests
{
    [Test]
    public static void TheShippedPackageRefusesLoopback()
    {
        // The switch exists only so the suite can reach a simulated node on 127.0.0.x. If it
        // ever ships on, a node address could name a service on the Zeus computer itself.
        Assert.False(HostValidator.ShippedAllowsLoopback, "the shipped default allows loopback");
        Assert.False(HostValidator.AllowLoopback, "this test was granted loopback and shouldn't have been");
    }

    [Test]
    public static async Task AnAddressOnTheZeusComputerIsRefusedWhereverAnOperatorCanTypeOne()
    {
        foreach (var host in new[] { "127.0.0.1", "127.0.0.1:5000", "localhost", "[::1]", "0.0.0.0" })
        {
            Assert.False(HostValidator.IsLocalHost(host), $"{host} treated as a LAN address");
        }
        Assert.NotNull(await HostValidator.CheckLocalAsync("127.0.0.1", default), "loopback literal refused");
        Assert.NotNull(await HostValidator.CheckLocalAsync("localhost", default), "loopback by name refused");

        // And through the real endpoint the panel posts to, not just the validator.
        await using var host2 = await PluginHost.StartAsync(new FakePluginContext());
        var (status, body) = await host2.PostAsync("nodes",
            new { network = "meshcore", host = "127.0.0.1", port = 5000, name = "Not on the LAN" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, status, "add node refused");
        Assert.Contains("local", body?["error"]?.GetValue<string>(), "says why");
        Assert.Equal(0, (await host2.GetAsync("status"))["nodes"]!.AsArray().Count, "nothing was added");
    }

    [Test]
    public static async Task AnInternetAddressIsRefusedTheSameWay()
    {
        foreach (var host in new[] { "8.8.8.8", "1.1.1.1:4403", "203.0.113.7" })
        {
            Assert.False(HostValidator.IsLocalHost(host), $"{host} treated as a LAN address");
            Assert.NotNull(await HostValidator.CheckLocalAsync(host, default), $"{host} refused");
        }
        foreach (var junk in new[] { "http://192.168.1.5", "192.168.1.5/admin", "user@192.168.1.5", "192.168.1.5?x=1" })
        {
            Assert.False(HostValidator.TryNormalize(junk, out _, out var error), $"{junk} refused");
            Assert.NotNull(error, "says why");
        }
    }

    [Test]
    public static async Task ANodeIsNeverFollowedToANonLocalAddress()
    {
        // Covers the check in the connector's own re-find: discovery hands back an address
        // and the connector must refuse to move there if it isn't on the LAN.
        var offered = "203.0.113.7";
        var config = new NodeConfig
        {
            Id = "mc",
            Network = MeshNetwork.MeshCore,
            Host = "10.255.255.1", // private, and nothing answers there
            Port = 5000,
            Identity = new string('a', 64),
        };
        var asked = 0;
        string? moved = null;
        await using var c = new MeshCoreConnector(config, new ConnectorOptions
        {
            MinBackoff = TimeSpan.FromMilliseconds(50),
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            RefindAfterFailures = 1,
            RefindSchedule = [TimeSpan.Zero],
            Refind = (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref asked);
                return Task.FromResult<string?>(offered);
            },
        });
        c.HostChanged += h => moved = h;
        await c.StartAsync(default);
        await Assert.Eventually(() => Volatile.Read(ref asked) >= 2, "re-find was tried", 20_000);
        Assert.Equal(null, moved, "the connector moved to a non-local address");
        Assert.Equal("10.255.255.1", c.Host, "host changed");
        Assert.Equal(null, c.PendingMove, "a non-local address was offered to the operator");
    }

    [Test]
    public static void OnlyLocalAddressesAreEverProbedWhenANodeHasMoved()
    {
        // Covers the filter on re-find candidates. DNS and mDNS answers come from the
        // network, so an answer naming an Internet address must be dropped, not probed.
        var candidates = new[]
        {
            IPAddress.Parse("203.0.113.9"),
            IPAddress.Parse("192.168.50.100"),
            IPAddress.Parse("8.8.8.8"),
            IPAddress.Parse("192.168.50.100"),
            IPAddress.Parse("127.0.0.1"),
            IPAddress.Parse("169.254.4.4"),
        };
        var kept = DiscoveryService.LocalCandidates(candidates).Select(a => a.ToString()).ToArray();
        Assert.Equal(2, kept.Length, "candidates kept");
        Assert.Equal("192.168.50.100", kept[0], "the LAN address");
        Assert.Equal("169.254.4.4", kept[1], "link-local kept");
    }

    [Test]
    public static async Task AFrameClaimingAnAbsurdLengthIsSkippedRatherThanAllocated()
    {
        // The length is two bytes the node controls, so it can claim 65535. The reader caps
        // it and resynchronises on the next header instead of allocating or stalling.
        var stream = new MemoryStream();
        foreach (var length in new[] { 60000, (int)ushort.MaxValue, MeshCoreProtocol.MaxFrame + 1, 0 })
        {
            stream.WriteByte(MeshCoreProtocol.RadioToApp);
            stream.WriteByte((byte)(length & 0xFF));
            stream.WriteByte((byte)(length >> 8));
        }
        stream.WriteByte(MeshCoreProtocol.RadioToApp);
        stream.WriteByte(3);
        stream.WriteByte(0);
        stream.Write([0x0A, 0x01, 0x02]); // a real, small frame
        stream.Position = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frame = await MeshCoreProtocol.ReadFrameAsync(stream, cts.Token);
        Assert.Equal(3, frame.Length, "the oversized headers were skipped and the real frame read");
        Assert.Equal(0x0A, frame[0], "frame code");
    }
}

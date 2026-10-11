// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Sockets;
using System.Security.Cryptography;
using MeshMessenger.Connectors.MeshCore;
using MeshMessenger.Connectors.Meshtastic;
using MeshMessenger.Core;

namespace MeshMessenger.Discovery;

/// <summary>A node that answered and proved, by speaking its protocol, that it is a mesh node.</summary>
public sealed record ProbedNode(
    MeshNetwork Network,
    string Host,
    int Port,
    string Identity,
    string Name,
    string? ShortName,
    string? Firmware,
    string? Model);

/// <summary>
/// Confirms what is listening at an address. A port that answers isn't proof
/// (plenty of things listen on 5000); only a correct protocol reply is.
/// </summary>
public static class NodeProbe
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMilliseconds(900);
    public static readonly TimeSpan DefaultIdentifyTimeout = TimeSpan.FromSeconds(3);

    /// <param name="onConnected">Called when the TCP connection succeeds, before identification (used to tell "nothing answers" from "something else answers").</param>
    public static async Task<ProbedNode?> ProbeAsync(MeshNetwork network, string host, int port, CancellationToken ct,
        TimeSpan? connectTimeout = null, TimeSpan? identifyTimeout = null, Action? onConnected = null)
    {
        using var client = new TcpClient { NoDelay = true };
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(connectTimeout ?? DefaultConnectTimeout);
                await client.ConnectAsync(HostOnly(host), port, connect.Token).ConfigureAwait(false);
            }
            onConnected?.Invoke();
            using var identify = CancellationTokenSource.CreateLinkedTokenSource(ct);
            identify.CancelAfter(identifyTimeout ?? DefaultIdentifyTimeout);
            await using var stream = client.GetStream();
            return network == MeshNetwork.MeshCore
                ? await IdentifyMeshCoreAsync(stream, host, port, identify.Token).ConfigureAwait(false)
                : await IdentifyMeshtasticAsync(stream, host, port, identify.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is SocketException or IOException or FormatException or EndOfStreamException)
        {
            return null;
        }
    }

    private static async Task<ProbedNode?> IdentifyMeshCoreAsync(Stream stream, string host, int port, CancellationToken ct)
    {
        await stream.WriteAsync(MeshCoreProtocol.Frame(MeshCoreProtocol.DeviceQuery()), ct).ConfigureAwait(false);
        var device = await ReadUntilAsync(stream, MeshCoreProtocol.RespDeviceInfo, ct).ConfigureAwait(false);
        await stream.WriteAsync(MeshCoreProtocol.Frame(MeshCoreProtocol.AppStart(MeshCoreConnector.AppName)), ct).ConfigureAwait(false);
        var self = await ReadUntilAsync(stream, MeshCoreProtocol.RespSelfInfo, ct).ConfigureAwait(false);
        var info = MeshCoreProtocol.ParseDeviceInfo(device);
        var s = MeshCoreProtocol.ParseSelfInfo(self);
        return new ProbedNode(MeshNetwork.MeshCore, host, port, Convert.ToHexString(s.PublicKey).ToLowerInvariant(),
            s.Name.Length > 0 ? s.Name : "MeshCore node", null, info.Version, info.Model);

        static async Task<byte[]> ReadUntilAsync(Stream stream, byte code, CancellationToken ct)
        {
            for (var i = 0; i < 16; i++)
            {
                var f = await MeshCoreProtocol.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                if (f[0] == code) return f;
                if (f[0] == MeshCoreProtocol.RespErr) throw new FormatException("not a companion node");
            }
            throw new FormatException("no reply");
        }
    }

    private static async Task<ProbedNode?> IdentifyMeshtasticAsync(Stream stream, string host, int port, CancellationToken ct)
    {
        var configId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        await stream.WriteAsync(MeshtasticProtocol.Wake(), ct).ConfigureAwait(false);
        await stream.WriteAsync(MeshtasticProtocol.Frame(MeshtasticProtocol.WantConfig(configId)), ct).ConfigureAwait(false);
        uint me = 0;
        string? longName = null, shortName = null, firmware = null;
        try
        {
            for (var i = 0; i < 400; i++)
            {
                var frame = await MeshtasticProtocol.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                switch (MeshtasticProtocol.ParseFromRadio(frame))
                {
                    case MeshtasticProtocol.MyInfo my:
                        me = my.NodeNum;
                        break;
                    case MeshtasticProtocol.NodeInfoMsg ni when me != 0 && ni.Node.Num == me:
                        longName = ni.Node.User?.LongName;
                        shortName = ni.Node.User?.ShortName;
                        break;
                    case MeshtasticProtocol.MetadataMsg md:
                        firmware = md.Firmware;
                        break;
                    case MeshtasticProtocol.ConfigComplete:
                        i = int.MaxValue - 1;
                        break;
                }
                if (me != 0 && longName is not null && firmware is not null) break;
            }
        }
        catch (OperationCanceledException) when (me != 0)
        {
            // Identified; the rest of the config doesn't matter for a probe.
        }
        if (me == 0) return null;
        try
        {
            await stream.WriteAsync(MeshtasticProtocol.Frame(MeshtasticProtocol.Disconnect()), CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        return new ProbedNode(MeshNetwork.Meshtastic, host, port, MeshtasticProtocol.NodeId(me),
            longName ?? MeshtasticProtocol.NodeId(me), shortName, firmware, null);
    }

    private static string HostOnly(string host) =>
        Uri.TryCreate("http://" + host, UriKind.Absolute, out var uri) ? uri.Host.Trim('[', ']') : host;
}

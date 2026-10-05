// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MeshMessenger.Connectors.MeshCore;
using static MeshMessenger.Connectors.MeshCore.MeshCoreProtocol;

namespace MeshMessenger.Tests;

/// <summary>
/// A simulated MeshCore companion node on 127.0.0.x speaking the companion
/// frame protocol, with the behaviour the connector depends on (one client at
/// a time, offline queue, acks).
/// </summary>
public sealed class FakeMeshCoreNode : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private NetworkStream? _client;
    private readonly SemaphoreSlim _write = new(1, 1);

    public byte[] PublicKey { get; }
    public string Name { get; set; } = "KD0ABC Shack";
    public List<(byte[] Key, int Type, string Name)> Contacts { get; } = [];
    public List<(string Name, byte[] Secret)> ChannelSlots { get; } = [];
    public ConcurrentQueue<byte[]> Received { get; } = new();
    public bool AckDirectMessages { get; set; } = true;
    public int Connections;

    public FakeMeshCoreNode(string address = "127.0.0.1", int port = 0, byte[]? publicKey = null)
    {
        PublicKey = publicKey ?? RandomNumberGenerator.GetBytes(32);
        ChannelSlots.Add(("Public", PublicChannelKey));
        ChannelSlots.Add(("#denver", SHA256.HashData(Encoding.UTF8.GetBytes("#denver"))[..16]));
        ChannelSlots.Add(("Family", RandomNumberGenerator.GetBytes(16)));
        _listener = new TcpListener(IPAddress.Parse(address), port);
        _listener.Start();
        _accept = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Identity => Convert.ToHexString(PublicKey).ToLowerInvariant();

    public byte[] AddContact(string name, int type = 1)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Contacts.Add((key, type, name));
        return key;
    }

    /// <summary>Queue a channel message as if heard on air; tickles the client like the firmware.</summary>
    public Task HearChannelAsync(int index, string sender, string text, sbyte snrX4 = 26, byte pathLen = 2)
    {
        var t = Encoding.UTF8.GetBytes($"{sender}: {text}");
        var f = new byte[11 + t.Length];
        f[0] = RespChannelMsgRecvV3;
        f[1] = (byte)snrX4;
        f[4] = (byte)index;
        f[5] = pathLen;
        f[6] = TxtPlain;
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(7), (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        t.CopyTo(f, 11);
        _queue.Enqueue(f);
        return TickleAsync();
    }

    public Task HearDirectAsync(byte[] fromKey, string text, sbyte snrX4 = -18, byte pathLen = 0xFF)
    {
        var t = Encoding.UTF8.GetBytes(text);
        var f = new byte[16 + t.Length];
        f[0] = RespContactMsgRecvV3;
        f[1] = (byte)snrX4;
        fromKey.AsSpan(0, 6).CopyTo(f.AsSpan(4));
        f[10] = pathLen;
        f[11] = TxtPlain;
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(12), (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        t.CopyTo(f, 16);
        _queue.Enqueue(f);
        return TickleAsync();
    }

    private async Task TickleAsync()
    {
        if (_client is not null) await WriteAsync(_client, [PushMsgWaiting]);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            Interlocked.Increment(ref Connections);
            // Like the firmware: a new client replaces the old one.
            _client?.Dispose();
            var stream = client.GetStream();
            _client = stream;
            _ = Task.Run(() => ServeAsync(client, stream));
        }
    }

    private async Task ServeAsync(TcpClient client, NetworkStream stream)
    {
        using var _ = client;
        var header = new byte[3];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header.AsMemory(0, 1), _cts.Token);
                if (header[0] != AppToRadio) continue;
                await stream.ReadExactlyAsync(header.AsMemory(1, 2), _cts.Token);
                var len = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
                var cmd = new byte[len];
                await stream.ReadExactlyAsync(cmd, _cts.Token);
                await HandleAsync(stream, cmd);
            }
        }
        catch
        {
            // client gone
        }
    }

    private async Task HandleAsync(NetworkStream s, byte[] cmd)
    {
        switch (cmd[0])
        {
            case CmdDeviceQuery:
            {
                var f = new byte[82];
                f[0] = RespDeviceInfo;
                f[1] = 13;
                f[2] = 175;
                f[3] = (byte)Math.Max(8, ChannelSlots.Count);
                Encoding.ASCII.GetBytes("1 Oct 2026").CopyTo(f, 8);
                Encoding.ASCII.GetBytes("Heltec V4").CopyTo(f, 20);
                Encoding.ASCII.GetBytes("v1.12.0").CopyTo(f, 60);
                await WriteAsync(s, f);
                break;
            }
            case CmdAppStart:
            {
                var name = Encoding.UTF8.GetBytes(Name);
                var f = new byte[58 + name.Length];
                f[0] = RespSelfInfo;
                f[1] = 1;
                f[2] = 22;
                f[3] = 22;
                PublicKey.CopyTo(f, 4);
                BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(48), 910525);
                BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(52), 62500);
                f[56] = 7;
                f[57] = 5;
                name.CopyTo(f, 58);
                await WriteAsync(s, f);
                break;
            }
            case CmdSetDeviceTime:
                await WriteAsync(s, [RespOk]);
                break;
            case CmdGetContacts:
            {
                var start = new byte[5];
                start[0] = RespContactsStart;
                BinaryPrimitives.WriteUInt32LittleEndian(start.AsSpan(1), (uint)Contacts.Count);
                await WriteAsync(s, start);
                foreach (var (key, type, name) in Contacts)
                {
                    var f = new byte[ContactFrameLength];
                    f[0] = RespContact;
                    key.CopyTo(f, 1);
                    f[33] = (byte)type;
                    f[35] = 1;
                    Encoding.UTF8.GetBytes(name).CopyTo(f, 100);
                    BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(132), (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    await WriteAsync(s, f);
                }
                await WriteAsync(s, [RespEndOfContacts]);
                break;
            }
            case CmdGetChannel:
            {
                var i = cmd[1];
                if (i >= Math.Max(8, ChannelSlots.Count))
                {
                    await WriteAsync(s, [RespErr, 2]);
                    break;
                }
                var f = new byte[50];
                f[0] = RespChannelInfo;
                f[1] = i;
                if (i < ChannelSlots.Count)
                {
                    Encoding.UTF8.GetBytes(ChannelSlots[i].Name).CopyTo(f, 2);
                    ChannelSlots[i].Secret.CopyTo(f, 34);
                }
                await WriteAsync(s, f);
                break;
            }
            case CmdSyncNextMessage:
                await WriteAsync(s, _queue.TryDequeue(out var m) ? m : [RespNoMoreMessages]);
                break;
            case CmdSendChannelTxtMsg:
                Received.Enqueue(cmd);
                await WriteAsync(s, cmd[2] < ChannelSlots.Count ? [RespOk] : [RespErr, 2]);
                break;
            case CmdSendTxtMsg:
            {
                Received.Enqueue(cmd);
                var prefix = cmd.AsSpan(7, 6).ToArray();
                if (!Contacts.Any(c => c.Key.AsSpan().StartsWith(prefix)))
                {
                    await WriteAsync(s, [RespErr, 2]);
                    break;
                }
                var ack = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                var f = new byte[10];
                f[0] = RespSent;
                f[1] = 1;
                BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(2), ack);
                BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(6), 3000);
                await WriteAsync(s, f);
                if (AckDirectMessages)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(80);
                        var p = new byte[9];
                        p[0] = PushSendConfirmed;
                        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(1), ack);
                        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(5), 1234);
                        await WriteAsync(s, p);
                    });
                }
                break;
            }
            case CmdGetBattAndStorage:
                await WriteAsync(s, [RespBattAndStorage, 0x10, 0x10, 0, 0, 0, 0, 0, 0, 0, 0]);
                break;
            default:
                await WriteAsync(s, [RespErr, 1]);
                break;
        }
    }

    private async Task WriteAsync(Stream s, byte[] payload)
    {
        var f = new byte[3 + payload.Length];
        f[0] = RadioToApp;
        BinaryPrimitives.WriteUInt16LittleEndian(f.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(f, 3);
        await _write.WaitAsync();
        try { await s.WriteAsync(f); }
        catch { }
        finally { _write.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        _client?.Dispose();
        try { await _accept; } catch { }
    }
}

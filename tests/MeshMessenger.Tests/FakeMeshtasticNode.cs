// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MeshMessenger.Connectors.Meshtastic;
using static MeshMessenger.Connectors.Meshtastic.MeshtasticProtocol;

namespace MeshMessenger.Tests;

/// <summary>A simulated Meshtastic node on 127.0.0.x speaking the TCP stream API.</summary>
public sealed class FakeMeshtasticNode : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;
    private readonly SemaphoreSlim _write = new(1, 1);
    private NetworkStream? _client;

    public uint MyNum { get; }
    public string LongName { get; set; } = "KD0ABC Shack MT";
    public List<(uint Num, string Long, string Short)> Others { get; } = [(0x77e0a913, "Trail Base", "TRB")];
    public ConcurrentQueue<(uint To, int Channel, uint Id, string Text)> Sent { get; } = new();
    public bool AckFromRecipient { get; set; } = true;

    public FakeMeshtasticNode(string address = "127.0.0.1", int port = 0, uint myNum = 0x9e41c07b)
    {
        MyNum = myNum;
        _listener = new TcpListener(IPAddress.Parse(address), port);
        _listener.Start();
        _accept = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string Identity => NodeId(MyNum);

    public Task HearTextAsync(uint from, uint to, int channel, string text, float snr = 6.25f, int rssi = -97, int hopStart = 3, int hopLimit = 2)
    {
        var data = new ProtoWriter().Varint(1, PortText).String(2, text);
        var packet = new ProtoWriter()
            .Fixed32(1, from).Fixed32(2, to).Varint(3, (ulong)channel).Message(4, data)
            .Fixed32(6, 0x1234u + (uint)text.Length).Fixed32(7, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .Fixed32(8, (uint)BitConverter.SingleToInt32Bits(snr)).Varint(9, (ulong)hopLimit)
            .Varint(12, unchecked((ulong)(long)rssi)).Varint(15, (ulong)hopStart);
        return SendFromRadioAsync(new ProtoWriter().Message(2, packet));
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            var stream = client.GetStream();
            _client = stream;
            _ = Task.Run(() => ServeAsync(client, stream));
        }
    }

    private async Task ServeAsync(TcpClient client, NetworkStream stream)
    {
        using var _ = client;
        var b = new byte[1];
        var len = new byte[2];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(b, _cts.Token);
                if (b[0] != Start1) continue;
                await stream.ReadExactlyAsync(b, _cts.Token);
                if (b[0] != Start2) continue;
                await stream.ReadExactlyAsync(len, _cts.Token);
                var payload = new byte[BinaryPrimitives.ReadUInt16BigEndian(len)];
                await stream.ReadExactlyAsync(payload, _cts.Token);
                await HandleAsync(payload);
            }
        }
        catch
        {
        }
    }

    private async Task HandleAsync(byte[] toRadio)
    {
        foreach (var f in ProtoReader.Fields(toRadio))
        {
            if (f.Number == 3 && f.WireType == 0)
            {
                await SendConfigAsync(f.UInt32);
            }
            else if (f.Number == 1 && f.WireType == 2)
            {
                var p = ParsePacket(f.Data);
                var text = Encoding.UTF8.GetString(p.Payload.Span);
                Sent.Enqueue((p.To, p.Channel, p.Id, text));
                if (AckFromRecipient)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(60);
                        var from = p.To == Broadcast ? MyNum : p.To; // channel: implicit ack from a relay
                        var routing = new ProtoWriter().Varint(3, 0);
                        var data = new ProtoWriter().Varint(1, PortRouting).Message(2, routing).Fixed32(6, p.Id);
                        var ack = new ProtoWriter().Fixed32(1, from).Fixed32(2, MyNum).Message(4, data).Fixed32(6, 0xabcd);
                        await SendFromRadioAsync(new ProtoWriter().Message(2, ack));
                    });
                }
            }
        }
    }

    private async Task SendConfigAsync(uint configId)
    {
        await SendFromRadioAsync(new ProtoWriter().Message(3, new ProtoWriter().Varint(1, MyNum)));
        await SendFromRadioAsync(new ProtoWriter().Message(13, new ProtoWriter().String(1, "2.7.15.abcdef").Varint(9, 50)));
        await SendFromRadioAsync(new ProtoWriter().Message(4, Node(MyNum, LongName, "KSHK")));
        foreach (var (num, l, s) in Others)
        {
            await SendFromRadioAsync(new ProtoWriter().Message(4, Node(num, l, s)));
        }
        await SendFromRadioAsync(new ProtoWriter().Message(10, new ProtoWriter()
            .Varint(1, 0).Message(2, new ProtoWriter().Bytes(2, [1]).String(3, "")).Varint(3, 1)));
        await SendFromRadioAsync(new ProtoWriter().Message(10, new ProtoWriter()
            .Varint(1, 1).Message(2, new ProtoWriter().Bytes(2, new byte[32]).String(3, "TrailCrew")).Varint(3, 2)));
        await SendFromRadioAsync(new ProtoWriter().Message(10, new ProtoWriter().Varint(1, 2).Varint(3, 0)));
        await SendFromRadioAsync(new ProtoWriter().Message(5, new ProtoWriter().Message(6, new ProtoWriter()
            .Bool(1, true).Varint(2, 0).Varint(7, 1))));
        await SendFromRadioAsync(new ProtoWriter().Varint(7, configId));
    }

    private static ProtoWriter Node(uint num, string longName, string shortName) => new ProtoWriter()
        .Varint(1, num)
        .Message(2, new ProtoWriter().String(1, NodeId(num)).String(2, longName).String(3, shortName))
        .Fixed32(4, (uint)BitConverter.SingleToInt32Bits(4.5f))
        .Fixed32(5, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        .Varint(9, 1);

    private async Task SendFromRadioAsync(ProtoWriter fromRadio)
    {
        var s = _client;
        if (s is null) return;
        var bytes = Frame(fromRadio.ToArray());
        await _write.WaitAsync();
        try { await s.WriteAsync(bytes); }
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

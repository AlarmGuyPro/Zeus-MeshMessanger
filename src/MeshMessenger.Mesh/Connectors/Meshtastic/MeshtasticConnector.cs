// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MeshMessenger.Core;
using static MeshMessenger.Connectors.Meshtastic.MeshtasticProtocol;

namespace MeshMessenger.Connectors.Meshtastic;

/// <summary>
/// Meshtastic node on the LAN: the firmware's TCP API on port 4403, framed
/// protobuf ToRadio / FromRadio packets with a small hand-written codec. See
/// docs/protocols/meshtastic.md.
/// </summary>
public sealed class MeshtasticConnector : MeshConnectorBase
{
    public const int Port4403 = 4403;

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(90);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, PendingAck> _awaitingAck = new();
    private Stream? _stream;
    private uint _myNum;
    private Dictionary<uint, NodeRecord> _nodes = [];
    private SortedDictionary<int, ChannelRecord> _channels = [];
    private LoraConfig? _lora;
    private string? _firmware;
    private DateTimeOffset _sessionStart;

    private sealed record PendingAck(ConversationKey Key, uint To, DateTimeOffset Due);

    public MeshtasticConnector(NodeConfig config, ConnectorOptions? options = null) : base(config, options)
    {
    }

    public override MeshNetwork Network => MeshNetwork.Meshtastic;

    public override int DefaultPort => Port4403;

    public override int MaxTextBytes(ConversationKind kind) => TextLimitBytes;

    public override IReadOnlyList<PeerInfo> Peers
    {
        get
        {
            lock (_gate)
            {
                return _nodes.Values.Where(n => n.Num != _myNum).Select(n => new PeerInfo(
                    NodeId(n.Num),
                    n.User?.LongName ?? NodeId(n.Num),
                    n.User?.ShortName,
                    KindOfRole(n.User?.Role ?? 0),
                    n.LastHeard,
                    n.Snr,
                    n.HopsAway,
                    n.BatteryLevel is > 0 and <= 100 ? n.BatteryLevel : null,
                    n.Latitude,
                    n.Longitude,
                    n.Favorite)).ToArray();
            }
        }
    }

    public override IReadOnlyList<ChannelInfo> Channels
    {
        get
        {
            lock (_gate)
            {
                return _channels.Values.Where(c => c.Role != 0)
                    .Select(c => new ChannelInfo(c.Index, ChannelName(c), KindOfPsk(c.Psk), c.Role == 1))
                    .ToArray();
            }
        }
    }

    public override string? TitleFor(ConversationKey key)
    {
        lock (_gate)
        {
            if (key.Kind == ConversationKind.Channel && int.TryParse(key.Peer, out var index))
            {
                return _channels.TryGetValue(index, out var c) && c.Role != 0 ? ChannelName(c) : null;
            }
            if (TryParseNodeId(key.Peer, out var num) && _nodes.TryGetValue(num, out var n))
            {
                return n.User?.LongName ?? n.User?.ShortName;
            }
            return null;
        }
    }

    private string ChannelName(ChannelRecord c) =>
        c.Name.Length > 0 ? c.Name : (c.Role == 1 ? PresetName(_lora?.Preset ?? 0) : $"Channel {c.Index}");

    // ------------------------------------------------------------ session

    protected override async Task RunSessionAsync(Stream stream, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var configId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var configDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _stream = stream;
            _myNum = 0;
            _sessionStart = Options.Time.GetUtcNow();
        }

        await stream.WriteAsync(Wake(), session.Token).ConfigureAwait(false);
        await Task.Delay(100, session.Token).ConfigureAwait(false);
        await WriteAsync(WantConfig(configId), session.Token).ConfigureAwait(false);

        var reader = Task.Run(() => ReadLoopAsync(stream, configId, configDone, session.Token), CancellationToken.None);
        try
        {
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
            {
                handshake.CancelAfter(HandshakeTimeout);
                var done = await Task.WhenAny(configDone.Task, reader, Task.Delay(Timeout.Infinite, handshake.Token)).ConfigureAwait(false);
                if (done == reader) await reader.ConfigureAwait(false); // surfaces identity mismatch / IO errors
                if (!configDone.Task.IsCompleted) throw new TimeoutException("The node didn't finish sending its configuration.");
            }
            SetState(ConnectorState.Connected, null);
            var heartbeat = HeartbeatLoopAsync(session.Token);
            var finished = await Task.WhenAny(reader, heartbeat).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
        }
        finally
        {
            session.Cancel();
            try { await reader.ConfigureAwait(false); } catch { /* session over */ }
        }
    }

    protected override void OnSessionEnded()
    {
        lock (_gate) _stream = null;
        foreach (var (id, _) in _awaitingAck)
        {
            if (_awaitingAck.TryRemove(id, out _))
            {
                PublishDelivery(new DeliveryUpdate(Tag(id), DeliveryStatus.NotConfirmed, "Connection to the node was lost before an acknowledgement arrived.", null));
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var lastBeat = Options.Time.GetUtcNow();
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), Options.Time, ct).ConfigureAwait(false);
            var now = Options.Time.GetUtcNow();
            foreach (var (id, pending) in _awaitingAck)
            {
                if (pending.Due <= now && _awaitingAck.TryRemove(id, out _))
                {
                    PublishDelivery(new DeliveryUpdate(Tag(id), DeliveryStatus.NotConfirmed, "No acknowledgement arrived.", null));
                }
            }
            if (now - lastBeat >= HeartbeatInterval)
            {
                await WriteAsync(Heartbeat(), ct).ConfigureAwait(false); // also notices a dead connection
                lastBeat = now;
            }
        }
    }

    private async Task ReadLoopAsync(Stream stream, uint configId, TaskCompletionSource configDone, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
            FromRadio message;
            try
            {
                message = ParseFromRadio(frame);
            }
            catch (FormatException)
            {
                continue; // a malformed packet must never end the session or reach Zeus
            }
            switch (message)
            {
                case MyInfo my:
                    CheckIdentity(NodeId(my.NodeNum)); // throws on a different node: ends the session
                    lock (_gate) _myNum = my.NodeNum;
                    break;
                case NodeInfoMsg ni:
                    lock (_gate) _nodes = new Dictionary<uint, NodeRecord>(_nodes) { [ni.Node.Num] = ni.Node };
                    if (ni.Node.Num == _myNum) UpdateSelf();
                    RaiseChanged();
                    break;
                case ChannelMsg ch:
                    lock (_gate) _channels = new SortedDictionary<int, ChannelRecord>(_channels) { [ch.Channel.Index] = ch.Channel };
                    RaiseChanged();
                    break;
                case MetadataMsg md:
                    lock (_gate) _firmware = md.Firmware;
                    UpdateSelf();
                    break;
                case LoraMsg lora:
                    lock (_gate) _lora = lora.Lora;
                    UpdateSelf();
                    break;
                case ConfigComplete cc when cc.Id == configId:
                    UpdateSelf();
                    configDone.TrySetResult();
                    break;
                case PacketMsg pm:
                    OnPacket(pm.Packet);
                    break;
                case Rebooted:
                    throw new IOException("The node rebooted.");
            }
        }
    }

    private void UpdateSelf()
    {
        lock (_gate)
        {
            if (_myNum == 0) return;
            _nodes.TryGetValue(_myNum, out var me);
            var radio = _lora is null ? null :
                (_lora.UsePreset ? PresetName(_lora.Preset) : $"SF{_lora.SpreadFactor} BW{_lora.Bandwidth}") + (_lora.Region > 0 ? $" · region {_lora.Region}" : "");
            Self = new SelfInfo(NodeId(_myNum), me?.User?.LongName ?? NodeId(_myNum), me?.User?.ShortName, _firmware, null, radio);
        }
        RaiseChanged();
    }

    private void OnPacket(Packet p)
    {
        uint me;
        lock (_gate) me = _myNum;
        var now = Options.Time.GetUtcNow();
        var hops = p.HopStart > 0 && p.HopStart >= p.HopLimit ? p.HopStart - p.HopLimit : (int?)null;

        // Keep the node list fresh from any packet heard.
        if (p.From != 0 && p.From != me)
        {
            lock (_gate)
            {
                _nodes.TryGetValue(p.From, out var known);
                var updated = (known ?? new NodeRecord(p.From, null, null, null, null, null, null, null, false)) with
                {
                    LastHeard = now,
                    Snr = p.RxSnr ?? known?.Snr,
                    HopsAway = hops ?? known?.HopsAway,
                };
                if (p.PortNum == PortNodeInfo && !p.Payload.IsEmpty)
                {
                    try { updated = updated with { User = ParseUser(p.Payload) }; } catch (FormatException) { }
                }
                if (p.PortNum == PortPosition && !p.Payload.IsEmpty)
                {
                    try
                    {
                        var (lat, lon) = ParsePosition(p.Payload);
                        if (lat is not null) updated = updated with { Latitude = lat, Longitude = lon };
                    }
                    catch (FormatException) { }
                }
                _nodes = new Dictionary<uint, NodeRecord>(_nodes) { [p.From] = updated };
            }
        }

        switch (p.PortNum)
        {
            case PortText:
                OnText(p, me, hops, now);
                break;
            case PortRouting when p.RequestId != 0:
                OnRouting(p);
                break;
        }
    }

    private void OnText(Packet p, uint me, int? hops, DateTimeOffset now)
    {
        if (p.From == me) return; // our own message echoed back
        var text = Encoding.UTF8.GetString(p.Payload.Span);
        ConversationKey key;
        if (p.To == Broadcast)
        {
            key = new ConversationKey(Id, ConversationKind.Channel, p.Channel.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else if (p.To == me)
        {
            key = new ConversationKey(Id, ConversationKind.Direct, NodeId(p.From));
        }
        else
        {
            return; // addressed to someone else
        }
        string? name;
        lock (_gate) name = _nodes.TryGetValue(p.From, out var n) ? n.User?.LongName ?? n.User?.ShortName : null;
        var sent = p.RxTime is { } rx && rx <= now.AddMinutes(5) && rx >= now.AddDays(-7) ? rx : now;
        var queued = sent < _sessionStart.AddSeconds(-10);
        var reception = new Reception(p.RxSnr, p.RxRssi, hops, hops == 0, p.ViaMqtt);
        PublishInbound(new InboundMessage(key, NodeId(p.From), name, text, sent, $"{p.From:x8}:{p.Id:x8}", reception, queued));
    }

    private void OnRouting(Packet p)
    {
        if (!_awaitingAck.TryGetValue(p.RequestId, out var pending)) return;
        int error;
        try { error = RoutingError(p.Payload); } catch (FormatException) { return; }
        if (error != 0)
        {
            _awaitingAck.TryRemove(p.RequestId, out _);
            PublishDelivery(new DeliveryUpdate(Tag(p.RequestId), DeliveryStatus.Failed, $"Not delivered: {RoutingErrorText(error)}.", null));
            return;
        }
        if (pending.To == Broadcast)
        {
            // Another node rebroadcast our channel message: the best a channel can tell us.
            _awaitingAck.TryRemove(p.RequestId, out _);
            PublishDelivery(new DeliveryUpdate(Tag(p.RequestId), DeliveryStatus.Delivered, null, null));
        }
        else if (p.From == pending.To)
        {
            _awaitingAck.TryRemove(p.RequestId, out _);
            PublishDelivery(new DeliveryUpdate(Tag(p.RequestId), DeliveryStatus.Delivered, null, null));
        }
        // An implicit ack from our own node on a DM only means a neighbour relayed it: keep waiting.
    }

    // ------------------------------------------------------------ sending

    public override async Task<SendResult> SendAsync(ConversationKey to, string text, CancellationToken ct)
    {
        if (!string.Equals(to.ConnectorId, Id, StringComparison.Ordinal))
        {
            return SendResult.Failure("That conversation belongs to a different node.");
        }
        if (State != ConnectorState.Connected)
        {
            return SendResult.Failure($"{DisplayName} is not connected.");
        }
        uint dest;
        int channel;
        switch (to.Kind)
        {
            case ConversationKind.Channel when int.TryParse(to.Peer, out var index) && index is >= 0 and < 8:
                dest = Broadcast;
                channel = index;
                break;
            case ConversationKind.Direct when TryParseNodeId(to.Peer, out var num):
                dest = num;
                channel = 0; // firmware ≥ 2.5 encrypts DMs with the recipient's key when it has it
                break;
            default:
                return SendResult.Failure("Unknown destination.");
        }
        var id = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        try
        {
            await WriteAsync(TextPacket(id, dest, channel, text), ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return SendResult.Failure("Not sent: the connection to the node dropped.");
        }
        _awaitingAck[id] = new PendingAck(to, dest, Options.Time.GetUtcNow() + AckTimeout);
        return SendResult.Success(Tag(id), Airtime(text), flood: dest == Broadcast);
    }

    private TimeSpan Airtime(string text)
    {
        LoraConfig? lora;
        lock (_gate) lora = _lora;
        var bytes = Encoding.UTF8.GetByteCount(text) + 16 + 16; // header + protobuf/MAC overhead
        (int Sf, int Bw, int Cr)? radio = lora is null ? null :
            lora.UsePreset ? PresetRadio(lora.Preset) :
            lora.SpreadFactor > 0 && lora.Bandwidth > 0 ? (lora.SpreadFactor, lora.Bandwidth * 1000, Math.Clamp(lora.CodingRate, 5, 8)) : null;
        return radio is { } r ? LoRaAirtime.Estimate(bytes, r.Sf, r.Bw, r.Cr) : TimeSpan.FromSeconds(1.5);
    }

    private async Task WriteAsync(byte[] toRadio, CancellationToken ct)
    {
        Stream stream;
        lock (_gate) stream = _stream ?? throw new IOException("Not connected.");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(Frame(toRadio), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string Tag(uint id) => id.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
}

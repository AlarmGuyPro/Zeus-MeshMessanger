// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Text;
using MeshMessenger.Core;
using static MeshMessenger.Connectors.MeshCore.MeshCoreProtocol;

namespace MeshMessenger.Connectors.MeshCore;

/// <summary>
/// MeshCore companion-radio node on the LAN (Wi-Fi companion firmware, TCP
/// port 5000). Speaks the companion frame protocol: see
/// docs/protocols/meshcore.md.
///
/// The node serves one client at a time and drops the previous one when a
/// new client connects; the reconnect backoff in the base class keeps us
/// from fighting another app for it.
/// </summary>
public sealed class MeshCoreConnector : MeshConnectorBase
{
    public const int Port5000 = 5000;
    public const string AppName = "Zeus Mesh Messenger";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(45);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private readonly ConcurrentDictionary<uint, (ConversationKey Key, DateTimeOffset Due)> _awaitingAck = new();
    private Stream? _stream;
    private Pending? _pending;
    private Dictionary<string, Contact> _contacts = new(StringComparer.OrdinalIgnoreCase); // full key hex -> contact
    private List<ChannelSlot> _slots = [];
    private SelfInfoFrame? _self;
    private int _syncRequested;
    private readonly SemaphoreSlim _wake = new(0, 1);

    public MeshCoreConnector(NodeConfig config, ConnectorOptions? options = null) : base(config, options)
    {
    }

    public override MeshNetwork Network => MeshNetwork.MeshCore;

    public override int DefaultPort => Port5000;

    public override IReadOnlyList<PeerInfo> Peers
    {
        get
        {
            lock (_gate)
            {
                return _contacts.Values.Select(c => new PeerInfo(
                    Convert.ToHexString(c.PublicKey).ToLowerInvariant(), c.Name, null, c.Kind, c.LastAdvert,
                    null, c.OutPathLength < 0 ? null : c.OutPathLength, null, c.Latitude, c.Longitude,
                    (c.Flags & 1) != 0)).ToArray();
            }
        }
    }

    public override IReadOnlyList<ChannelInfo> Channels
    {
        get
        {
            lock (_gate)
            {
                return _slots.Where(s => !s.IsEmpty)
                    .Select(s => new ChannelInfo(s.Index, s.Name.Length > 0 ? s.Name : $"Channel {s.Index}", KindOf(s), s.Index == 0))
                    .ToArray();
            }
        }
    }

    public override int MaxTextBytes(ConversationKind kind)
    {
        if (kind != ConversationKind.Channel) return TextLimitBytes;
        string? name;
        lock (_gate) name = _self?.Name;
        // Channel text goes out as "<our name>: <text>" and the firmware
        // silently truncates the whole thing to 160 bytes.
        var nameBytes = name is null ? MaxNodeNameBytes : Encoding.UTF8.GetByteCount(name);
        return TextLimitBytes - nameBytes - 2;
    }

    public override string? TitleFor(ConversationKey key)
    {
        lock (_gate)
        {
            switch (key.Kind)
            {
                case ConversationKind.Channel when int.TryParse(key.Peer, out var index):
                    var slot = _slots.FirstOrDefault(s => s.Index == index);
                    return slot is null || slot.IsEmpty ? null : (slot.Name.Length > 0 ? slot.Name : $"Channel {index}");
                default:
                    return FindContact(key.Peer)?.Name;
            }
        }
    }

    // ------------------------------------------------------------ session

    protected override async Task RunSessionAsync(Stream stream, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) _stream = stream;
        var reader = Task.Run(() => ReadLoopAsync(stream, session.Token), CancellationToken.None);
        try
        {
            var deviceFrame = await CommandAsync(DeviceQuery(), c => c == RespDeviceInfo, session.Token).ConfigureAwait(false);
            var device = ParseDeviceInfo(deviceFrame);
            var selfFrame = await CommandAsync(AppStart(AppName), c => c == RespSelfInfo, session.Token).ConfigureAwait(false);
            var self = ParseSelfInfo(selfFrame);
            CheckIdentity(Convert.ToHexString(self.PublicKey).ToLowerInvariant());
            lock (_gate) _self = self;
            Self = new SelfInfo(
                Convert.ToHexString(self.PublicKey).ToLowerInvariant(),
                self.Name.Length > 0 ? self.Name : "MeshCore node",
                null,
                device.Version,
                device.Model,
                self.SpreadingFactor > 0
                    ? $"{self.FrequencyMHz:0.###} MHz · BW {self.BandwidthKHz:0.#} kHz · SF{self.SpreadingFactor} CR{self.CodingRate} · {self.TxPowerDbm} dBm"
                    : null);

            // MeshCore needs a correct clock for message timestamps; it only accepts time moving forward.
            try
            {
                await CommandAsync(SetDeviceTime((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()), c => c is RespOk or RespErr, session.Token).ConfigureAwait(false);
            }
            catch (MeshCoreException)
            {
                // Node clock already ahead of ours: fine.
            }

            await LoadContactsAsync(session.Token).ConfigureAwait(false);
            await LoadChannelsAsync(Math.Clamp(device.MaxChannels, 1, 64), session.Token).ConfigureAwait(false);
            SetState(ConnectorState.Connected, null);

            // Messages the node held while Zeus was away.
            await DrainMessagesAsync(queuedWhileAway: true, session.Token).ConfigureAwait(false);

            var keepAlive = KeepAliveAsync(session.Token);
            var finished = await Task.WhenAny(reader, keepAlive).ConfigureAwait(false);
            await finished.ConfigureAwait(false); // surface the reason
        }
        finally
        {
            session.Cancel();
            try { await reader.ConfigureAwait(false); } catch { /* session over */ }
        }
    }

    protected override void OnSessionEnded()
    {
        lock (_gate)
        {
            _stream = null;
            _pending?.Completion.TrySetException(new IOException("Connection closed."));
            _pending = null;
        }
        foreach (var (tag, _) in _awaitingAck)
        {
            if (_awaitingAck.TryRemove(tag, out _))
            {
                PublishDelivery(new DeliveryUpdate(Tag(tag), DeliveryStatus.NotConfirmed, "Connection to the node was lost before an acknowledgement arrived.", null));
            }
        }
    }

    private async Task KeepAliveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Wake at once when the node says a message is waiting; otherwise every 5 s.
            await _wake.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            ExpireAcks();
            if (Interlocked.Exchange(ref _syncRequested, 0) == 1)
            {
                await DrainMessagesAsync(queuedWhileAway: false, ct).ConfigureAwait(false);
            }
            if (Options.Time.GetUtcNow() - _lastTraffic > KeepAliveInterval)
            {
                // Also notices a dead TCP connection.
                await CommandAsync(GetBattery(), c => c == RespBattAndStorage, ct).ConfigureAwait(false);
            }
        }
    }

    private DateTimeOffset _lastTraffic = DateTimeOffset.MinValue;

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
            _lastTraffic = Options.Time.GetUtcNow();
            try
            {
                Dispatch(frame);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // A malformed frame must never end the session or reach Zeus.
            }
        }
    }

    private void Dispatch(byte[] frame)
    {
        var code = frame[0];
        if (code >= 0x80)
        {
            OnPush(frame);
            return;
        }
        Pending? pending;
        lock (_gate) pending = _pending;
        if (pending is not null && pending.Accept(frame))
        {
            return;
        }
        // Unsolicited replies: nothing to do.
    }

    private void OnPush(byte[] f)
    {
        switch (f[0])
        {
            case PushMsgWaiting:
                Interlocked.Exchange(ref _syncRequested, 1);
                try { _wake.Release(); } catch (SemaphoreFullException) { }
                break;
            case PushSendConfirmed when f.Length >= 9:
            {
                var tag = BitConverter.ToUInt32(f, 1);
                var rtt = BitConverter.ToInt32(f, 5);
                if (_awaitingAck.TryRemove(tag, out _))
                {
                    PublishDelivery(new DeliveryUpdate(Tag(tag), DeliveryStatus.Delivered, null, rtt));
                }
                break;
            }
            case PushNewAdvert when f.Length >= ContactFrameLength - 4:
            {
                var contact = ParseContact(f);
                lock (_gate)
                {
                    _contacts = new Dictionary<string, Contact>(_contacts, StringComparer.OrdinalIgnoreCase)
                    {
                        [Convert.ToHexString(contact.PublicKey)] = contact,
                    };
                }
                RaiseChanged();
                break;
            }
            case PushAdvert or PushPathUpdated or PushContactDeleted:
                // Contact details changed; refresh on the next quiet moment.
                _ = RefreshContactsSoonAsync();
                break;
        }
    }

    private int _refreshQueued;

    private async Task RefreshContactsSoonAsync()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            if (State == ConnectorState.Connected)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await LoadContactsAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Best effort.
        }
        finally
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
        }
    }

    private async Task LoadContactsAsync(CancellationToken ct)
    {
        var list = new List<Contact>();
        await CommandAsync(GetContacts(), f =>
        {
            switch (f[0])
            {
                case RespContactsStart:
                    return false;
                case RespContact:
                    list.Add(ParseContact(f));
                    return false;
                case RespEndOfContacts:
                    return true;
                default:
                    return null;
            }
        }, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        lock (_gate)
        {
            _contacts = list.GroupBy(c => Convert.ToHexString(c.PublicKey), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        }
        RaiseChanged();
    }

    private async Task LoadChannelsAsync(int maxChannels, CancellationToken ct)
    {
        var slots = new List<ChannelSlot>();
        for (var i = 0; i < maxChannels; i++)
        {
            try
            {
                var f = await CommandAsync(GetChannel(i), c => c == RespChannelInfo, ct).ConfigureAwait(false);
                slots.Add(ParseChannelInfo(f));
            }
            catch (MeshCoreException)
            {
                break; // past the last slot
            }
        }
        lock (_gate) _slots = slots;
        RaiseChanged();
    }

    private async Task DrainMessagesAsync(bool queuedWhileAway, CancellationToken ct)
    {
        for (var n = 0; n < 1000; n++) // the node's queue holds at most a few hundred
        {
            var f = await CommandAsync(SyncNextMessage(),
                c => c is RespNoMoreMessages or RespContactMsgRecv or RespContactMsgRecvV3 or RespChannelMsgRecv or RespChannelMsgRecvV3 or 27,
                ct).ConfigureAwait(false);
            if (f[0] == RespNoMoreMessages) return;
            var received = ParseReceived(f);
            if (received is not null) Deliver(received, queuedWhileAway);
        }
    }

    private void Deliver(ReceivedText r, bool queuedWhileAway)
    {
        var sent = PlausibleTime(r.Timestamp);
        var hops = HopsFromPathLength(r.PathLength);
        var reception = new Reception(r.Snr, null, hops, hops is null, false);
        // Packet identity for de-duplication: sender + their timestamp + text.
        var networkId = $"{Convert.ToHexString(r.SenderPrefix)}:{r.ChannelIndex}:{r.Timestamp}:{r.Text.GetHashCode(StringComparison.Ordinal):x8}";
        if (r.IsChannel)
        {
            var (sender, text) = SplitChannelText(r.Text);
            PublishInbound(new InboundMessage(
                new ConversationKey(Id, ConversationKind.Channel, r.ChannelIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                sender ?? "?", sender, text, sent, networkId, reception, queuedWhileAway));
            return;
        }

        Contact? contact;
        Contact? author = null;
        lock (_gate)
        {
            contact = FindContactByPrefix(r.SenderPrefix);
            if (r.AuthorPrefix is not null) author = FindContactByPrefix(r.AuthorPrefix);
        }
        var peer = contact is not null ? Convert.ToHexString(contact.PublicKey).ToLowerInvariant() : Convert.ToHexString(r.SenderPrefix).ToLowerInvariant();
        if (contact?.Kind == PeerKind.Room || r.TextType == TxtSignedPlain)
        {
            // A room server pushing a post: the author is the signer, the conversation is the room.
            var authorId = r.AuthorPrefix is null ? peer : (author is not null ? Convert.ToHexString(author.PublicKey).ToLowerInvariant() : Convert.ToHexString(r.AuthorPrefix).ToLowerInvariant());
            PublishInbound(new InboundMessage(
                new ConversationKey(Id, ConversationKind.Room, peer),
                authorId, author?.Name, r.Text, sent, networkId, reception, queuedWhileAway));
            return;
        }
        PublishInbound(new InboundMessage(
            new ConversationKey(Id, ConversationKind.Direct, peer),
            peer, contact?.Name, r.Text, sent, networkId, reception, queuedWhileAway));
    }

    private DateTimeOffset PlausibleTime(uint senderTimestamp)
    {
        var now = Options.Time.GetUtcNow();
        if (senderTimestamp == 0) return now;
        var t = DateTimeOffset.FromUnixTimeSeconds(senderTimestamp);
        // Sender clocks can be wrong; trust them only within a sane window.
        return t > now.AddMinutes(5) || t < now.AddDays(-7) ? now : t;
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
        var ts = (uint)Options.Time.GetUtcNow().ToUnixTimeSeconds();
        try
        {
            switch (to.Kind)
            {
                case ConversationKind.Channel:
                {
                    if (!int.TryParse(to.Peer, out var index)) return SendResult.Failure("Unknown channel.");
                    await CommandAsync(SendChannel(index, ts, text), c => c is RespOk, ct).ConfigureAwait(false);
                    // Channels have no acknowledgement in MeshCore; "Sent" is final.
                    return SendResult.Success(null, Airtime(text, channel: true), flood: true);
                }
                case ConversationKind.Direct or ConversationKind.Room:
                {
                    byte[] prefix;
                    lock (_gate)
                    {
                        var contact = FindContact(to.Peer);
                        if (contact is null) return SendResult.Failure("This contact isn't in your node's contact list any more.");
                        prefix = contact.PublicKey[..6];
                    }
                    var f = await CommandAsync(SendDirect(prefix, ts, text), c => c is RespSent, ct).ConfigureAwait(false);
                    var sent = ParseSent(f);
                    var due = Options.Time.GetUtcNow() + TimeSpan.FromMilliseconds(Math.Clamp(sent.SuggestedTimeoutMs, 2000u, 120_000u) * 2 + 5000);
                    _awaitingAck[sent.AckTag] = (to, due);
                    return SendResult.Success(Tag(sent.AckTag), Airtime(text, channel: false), sent.Flood);
                }
                default:
                    return SendResult.Failure("Unsupported conversation type.");
            }
        }
        catch (MeshCoreException ex)
        {
            return SendResult.Failure($"Not sent: {ex.Message}.");
        }
        catch (IOException)
        {
            return SendResult.Failure("Not sent: the connection to the node dropped.");
        }
        catch (TimeoutException)
        {
            return SendResult.Failure("Not sent: the node didn't answer.");
        }
    }

    private void ExpireAcks()
    {
        var now = Options.Time.GetUtcNow();
        foreach (var (tag, entry) in _awaitingAck)
        {
            if (entry.Due <= now && _awaitingAck.TryRemove(tag, out _))
            {
                PublishDelivery(new DeliveryUpdate(Tag(tag), DeliveryStatus.NotConfirmed, "No acknowledgement from the recipient.", null));
            }
        }
    }

    private TimeSpan Airtime(string text, bool channel)
    {
        SelfInfoFrame? self;
        lock (_gate) self = _self;
        var bytes = Encoding.UTF8.GetByteCount(text) + (channel ? MaxNodeNameBytes + 2 : 0) + 40; // header, MAC, path
        if (self is null || self.SpreadingFactor == 0) return TimeSpan.FromSeconds(1);
        return LoRaAirtime.Estimate(bytes, self.SpreadingFactor, (int)(self.BandwidthKHz * 1000), Math.Clamp(self.CodingRate, 5, 8), preambleSymbols: 8);
    }

    private static string Tag(uint tag) => tag.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);

    // ------------------------------------------------------------ helpers

    private Contact? FindContact(string peer)
    {
        if (_contacts.TryGetValue(peer, out var c)) return c;
        // A peer stored by prefix only (unknown when the message arrived).
        return _contacts.Values.FirstOrDefault(x => Convert.ToHexString(x.PublicKey).StartsWith(peer, StringComparison.OrdinalIgnoreCase));
    }

    private Contact? FindContactByPrefix(byte[] prefix) =>
        _contacts.Values.FirstOrDefault(c => c.PublicKey.AsSpan().StartsWith(prefix));

    /// <summary>Send one command and wait for its reply. One command at a time, as the protocol requires.</summary>
    private Task<byte[]> CommandAsync(byte[] command, Func<byte, bool> isReply, CancellationToken ct, TimeSpan? timeout = null) =>
        CommandAsync(command, f =>
        {
            if (isReply(f[0])) return true;
            if (f[0] == RespErr) throw new MeshCoreException(ErrorText(f));
            return (bool?)null;
        }, ct, timeout);

    private async Task<byte[]> CommandAsync(byte[] command, Func<byte[], bool?> handle, CancellationToken ct, TimeSpan? timeout = null)
    {
        await _commandLock.WaitAsync(ct).ConfigureAwait(false);
        var pending = new Pending(handle);
        try
        {
            Stream stream;
            lock (_gate)
            {
                stream = _stream ?? throw new IOException("Not connected.");
                _pending = pending;
            }
            await stream.WriteAsync(Frame(command), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(timeout ?? CommandTimeout);
            try
            {
                return await pending.Completion.Task.WaitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"No reply to command {command[0]}.");
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, pending)) _pending = null;
            }
            _commandLock.Release();
        }
    }

    private sealed class Pending(Func<byte[], bool?> handle)
    {
        public TaskCompletionSource<byte[]> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>True when the frame belonged to this command.</summary>
        public bool Accept(byte[] frame)
        {
            try
            {
                var done = handle(frame);
                if (done is null) return false;
                if (done.Value) Completion.TrySetResult(frame);
                return true;
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
                return true;
            }
        }
    }
}

/// <summary>The node answered a command with an error frame.</summary>
public sealed class MeshCoreException(string message) : Exception(message);

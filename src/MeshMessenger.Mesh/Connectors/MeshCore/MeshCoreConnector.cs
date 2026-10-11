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
    // Heard (PUSH_NEW_ADVERT) but not saved on the node, e.g. when the node is set to add
    // contacts manually. The node can't exchange direct messages with these until saved.
    private Dictionary<string, (Contact Contact, byte[] Frame)> _heard = new(StringComparer.OrdinalIgnoreCase);
    private int _maxContacts;
    private byte? _autoAddConfig;
    private byte _autoAddMaxHops;
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
                var saved = _contacts.Values.Select(c => ToPeer(c, true));
                var heard = _heard.Where(h => !_contacts.ContainsKey(h.Key)).Select(h => ToPeer(h.Value.Contact, false));
                return saved.Concat(heard).ToArray();
            }
        }
    }

    private static PeerInfo ToPeer(Contact c, bool saved) => new(
        Convert.ToHexString(c.PublicKey).ToLowerInvariant(), c.Name, null, c.Kind, c.LastAdvert,
        null, c.OutPathLength < 0 ? null : c.OutPathLength, null, c.Latitude, c.Longitude,
        (c.Flags & 1) != 0, saved);

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
            Log(LogLevel.Info, $"Connected to {device.Model ?? "a MeshCore node"} running {device.Version ?? "unknown firmware"}.");
            lock (_gate) _maxContacts = device.MaxContacts;
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

            await LoadAutoAddConfigAsync(session.Token).ConfigureAwait(false);
            await LoadContactsAsync(session.Token).ConfigureAwait(false);
            await LoadChannelsAsync(Math.Clamp(device.MaxChannels, 1, 64), session.Token).ConfigureAwait(false);
            Log(LogLevel.Info, $"{_contacts.Count} contact(s) and {_slots.Count(x => !x.IsEmpty)} channel(s) on the node.");
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
                    Log(LogLevel.Receive, $"Delivered: ack {tag:x8} after {rtt} ms.");
                    PublishDelivery(new DeliveryUpdate(Tag(tag), DeliveryStatus.Delivered, null, rtt));
                }
                else
                {
                    Log(LogLevel.Receive, $"Acknowledgement {tag:x8} arrived for a message Zeus wasn't tracking.");
                }
                break;
            }
            case PushNewAdvert when f.Length >= ContactFrameLength - 4:
            {
                // The firmware sends NEW_ADVERT only for a node it did NOT save
                // (manual-add mode, hop limit, or a full table); saved ones come as PUSH_ADVERT.
                var contact = ParseContact(f);
                Log(LogLevel.Receive, $"Heard an advert from \"{contact.Name}\" ({Short(Convert.ToHexString(contact.PublicKey))}). The node has not saved it as a contact.");
                lock (_gate)
                {
                    _heard = new Dictionary<string, (Contact, byte[])>(_heard, StringComparer.OrdinalIgnoreCase)
                    {
                        [Convert.ToHexString(contact.PublicKey)] = (contact, f.ToArray()),
                    };
                }
                RaiseChanged();
                break;
            }
            case PushContactsFull:
                Log(LogLevel.Warning, "The node reports its contact list is full.");
                break;
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

    /// <summary>Reads the node's auto-add settings. Older firmware doesn't have the command; that's fine.</summary>
    private async Task LoadAutoAddConfigAsync(CancellationToken ct)
    {
        try
        {
            var f = await CommandAsync(GetAutoAddConfig(), c => c is RespAutoAddConfig, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _autoAddConfig = f.Length > 1 ? f[1] : (byte)0;
                _autoAddMaxHops = f.Length > 2 ? f[2] : (byte)0;
            }
        }
        catch (Exception ex) when (ex is MeshCoreException or TimeoutException)
        {
            lock (_gate) _autoAddConfig = null;
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
            // The firmware's iterator also hands back its reserved scratch slots
            // (all-zero key, type NONE). They aren't contacts, so drop them.
            _contacts = list.Where(c => c.Kind != PeerKind.Unknown && c.PublicKey.Any(b => b != 0))
                .GroupBy(c => Convert.ToHexString(c.PublicKey), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
            if (_maxContacts > 0 && _contacts.Count >= _maxContacts)
            {
                Log(LogLevel.Warning,
                    $"The node's contact list is full ({_contacts.Count} of {_maxContacts}). It can't learn new stations, " +
                    "so direct messages to a station it hasn't already saved will fail until contacts are removed.");
            }
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
                    Log(LogLevel.Send, $"Channel {index}: sending {Encoding.UTF8.GetByteCount(text)} bytes.");
                    await CommandAsync(SendChannel(index, ts, text), c => c is RespOk, ct).ConfigureAwait(false);
                    Log(LogLevel.Send, $"Channel {index}: the node accepted it (channels are not acknowledged).");
                    // Channels have no acknowledgement in MeshCore; "Sent" is final.
                    return SendResult.Success(null, Airtime(text, channel: true), flood: true);
                }
                case ConversationKind.Direct or ConversationKind.Room:
                {
                    byte[] prefix;
                    byte[]? saveFirst = null;
                    lock (_gate)
                    {
                        var contact = FindContact(to.Peer);
                        if (contact is null)
                        {
                            var heard = FindHeard(to.Peer);
                            if (heard is null)
                            {
                                return SendResult.Failure("This station isn't in your node's contacts. Ask them to send an advert, then try again.");
                            }
                            contact = heard.Value.Contact;
                            saveFirst = heard.Value.Frame;
                        }
                        prefix = contact.PublicKey[..6];
                    }
                    if (saveFirst is not null)
                    {
                        // Heard but not saved: save it on the node first (local, no transmission),
                        // as the MeshCore app does when you tap "Add contact".
                        Log(LogLevel.Info, $"Saving heard station {Short(to.Peer)} as a contact on the node.");
                        try
                        {
                            await CommandAsync(AddContactFromAdvert(saveFirst), c => c is RespOk, ct).ConfigureAwait(false);
                        }
                        catch (MeshCoreException ex)
                        {
                            Log(LogLevel.Error, $"The node refused to save the contact: {ex.Message} (code {ex.Code}).");
                            return SendResult.Failure(ex.Code == ErrTableFull
                                ? "Not sent: the node's contact list is full, so it can't save this station. Remove some contacts on the Nodes tab, or turn on \"Recycle the oldest contact when the list is full\"."
                                : $"Not sent: the node wouldn't save this contact ({ex.Message}).");
                        }
                        await LoadContactsAsync(ct).ConfigureAwait(false);
                    }
                    Log(LogLevel.Send, $"Direct to {Short(to.Peer)}: sending {Encoding.UTF8.GetByteCount(text)} bytes.");
                    var f = await CommandAsync(SendDirect(prefix, ts, text), c => c is RespSent, ct).ConfigureAwait(false);
                    var sent = ParseSent(f);
                    Log(LogLevel.Send, $"Direct to {Short(to.Peer)}: {(sent.Flood ? "flooded" : "sent on a known path")}, ack tag {sent.AckTag:x8}, node suggests waiting {sent.SuggestedTimeoutMs} ms.");
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
            Log(LogLevel.Error, $"Send failed: {ex.Message} (code {ex.Code}).");
            return SendResult.Failure(ex.Code switch
            {
                // MSG_SEND_FAILED: the node had no free packet buffer. It clears on its own.
                ErrTableFull => "Not sent: the node had no free packet buffer. It is busy on the air — try again in a few seconds.",
                ErrNotFound => "Not sent: the node no longer has this contact or channel.",
                _ => $"Not sent: {ex.Message}.",
            });
        }
        catch (IOException)
        {
            Log(LogLevel.Error, "Send failed: the connection to the node dropped.");
            return SendResult.Failure("Not sent: the connection to the node dropped.");
        }
        catch (TimeoutException)
        {
            Log(LogLevel.Error, "Send failed: the node didn't answer in time.");
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

    public override int? PeerCapacity { get { lock (_gate) return _maxContacts > 0 ? _maxContacts : null; } }

    public override bool? RecycleOldestWhenFull
    {
        get { lock (_gate) return _autoAddConfig is { } c ? (c & AutoAddOverwriteOldest) != 0 : null; }
    }

    /// <summary>
    /// Deletes a contact from the node. The node only exchanges direct messages
    /// with stations in this list, and the list is capped, so on a busy mesh it
    /// can fill up and block new stations until something is removed.
    /// </summary>
    public override async Task<string?> RemovePeerAsync(string peerId, CancellationToken ct)
    {
        if (State != ConnectorState.Connected) return $"{DisplayName} is not connected.";
        Contact? contact;
        lock (_gate) contact = FindContact(peerId);
        if (contact is null) return "That contact is no longer on the node.";
        try
        {
            await CommandAsync(RemoveContact(contact.PublicKey), c => c is RespOk, ct).ConfigureAwait(false);
            Log(LogLevel.Info, $"Removed contact \"{contact.Name}\" from the node.");
            await LoadContactsAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (MeshCoreException ex)
        {
            Log(LogLevel.Error, $"Removing \"{contact.Name}\" failed: {ex.Message} (code {ex.Code}).");
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return "The node didn't answer.";
        }
    }

    public override async Task<string?> SetRecycleOldestWhenFullAsync(bool enabled, CancellationToken ct)
    {
        if (State != ConnectorState.Connected) return $"{DisplayName} is not connected.";
        byte current;
        byte hops;
        lock (_gate)
        {
            if (_autoAddConfig is null) return "This node's firmware doesn't have that setting.";
            current = _autoAddConfig.Value;
            hops = _autoAddMaxHops;
        }
        var next = (byte)(enabled ? current | AutoAddOverwriteOldest : current & ~AutoAddOverwriteOldest);
        try
        {
            await CommandAsync(SetAutoAddConfig(next, hops), c => c is RespOk, ct).ConfigureAwait(false);
            lock (_gate) _autoAddConfig = next;
            Log(LogLevel.Info, enabled
                ? "The node will now recycle its oldest contact when the list is full."
                : "The node will no longer recycle contacts; a full list stays full.");
            RaiseChanged();
            return null;
        }
        catch (MeshCoreException ex)
        {
            Log(LogLevel.Error, $"Changing the contact-recycling setting failed: {ex.Message} (code {ex.Code}).");
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return "The node didn't answer.";
        }
    }

    public override bool CanAnnounce => true;

    /// <summary>
    /// CMD_SEND_SELF_ADVERT. Flood reaches the whole mesh through repeaters;
    /// zero-hop only stations that hear this node directly.
    /// </summary>
    public override async Task<SendResult> AnnounceAsync(bool flood, CancellationToken ct)
    {
        if (State != ConnectorState.Connected) return SendResult.Failure($"{DisplayName} is not connected.");
        try
        {
            Log(LogLevel.Send, flood ? "Sending an advert to the whole mesh." : "Sending an advert to stations in direct range.");
            await CommandAsync(SelfAdvert(flood), c => c is RespOk, ct).ConfigureAwait(false);
            return SendResult.Success(null, Airtime(new string('x', 96), channel: false), flood);
        }
        catch (MeshCoreException ex)
        {
            Log(LogLevel.Error, $"Advert failed: {ex.Message} (code {ex.Code}).");
            return SendResult.Failure(ex.Code == ErrTableFull
                ? "The node had no free packet buffer. It is busy on the air — try again in a few seconds."
                : $"The node refused: {ex.Message}.");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return SendResult.Failure("The node didn't answer.");
        }
    }

    public override async Task RefreshPeersAsync(CancellationToken ct)
    {
        if (State != ConnectorState.Connected) return;
        await LoadContactsAsync(ct).ConfigureAwait(false);
        Log(LogLevel.Info, $"Re-read the node's contacts: {_contacts.Count} saved.");
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

    private (Contact Contact, byte[] Frame)? FindHeard(string peer)
    {
        if (_heard.TryGetValue(peer, out var h)) return h;
        foreach (var (key, value) in _heard)
        {
            if (key.StartsWith(peer, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    private Contact? FindContactByPrefix(byte[] prefix) =>
        _contacts.Values.FirstOrDefault(c => c.PublicKey.AsSpan().StartsWith(prefix));

    /// <summary>Send one command and wait for its reply. One command at a time, as the protocol requires.</summary>
    private Task<byte[]> CommandAsync(byte[] command, Func<byte, bool> isReply, CancellationToken ct, TimeSpan? timeout = null) =>
        CommandAsync(command, f =>
        {
            if (isReply(f[0])) return true;
            if (f[0] == RespErr) throw new MeshCoreException(ErrorText(f), ErrorCode(f));
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
public sealed class MeshCoreException(string message, byte code = 0) : Exception(message)
{
    /// <summary>The firmware's error code, or 0 when the failure wasn't an ERR frame.</summary>
    public byte Code { get; } = code;
}

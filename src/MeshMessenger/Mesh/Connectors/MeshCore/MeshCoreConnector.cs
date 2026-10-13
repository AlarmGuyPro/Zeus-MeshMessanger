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
    /// <summary>
    /// Stations heard advertising that the node did not save as contacts, kept so the
    /// operator can message one (saving it on the node first). Mutated in place under
    /// <see cref="_gate"/>.
    /// </summary>
    private readonly Dictionary<string, HeardStation> _heard = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A station heard advertising, with the advert frame kept so it can be saved on the
    /// node later. <see cref="Seq"/> is when Zeus heard it, counted locally: the advert
    /// carries the sender's own clock, which is theirs to set, so it must not decide which
    /// station is forgotten first.
    /// </summary>
    private readonly record struct HeardStation(Contact Contact, byte[] Frame, long Seq);

    private long _heardSeq;

    /// <summary>
    /// How many heard-but-unsaved stations are remembered. Every advert on the air adds one,
    /// and a busy mesh adverts constantly, so without a cap this grows for as long as Zeus
    /// runs. The oldest advert is forgotten first; the node's own contact list is untouched,
    /// and a station that adverts again comes straight back.
    /// </summary>
    public const int MaxHeardStations = 300;
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
        c.Favourite, saved, c.LastModified);

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

    /// <summary>
    /// How many checks in a row may go unanswered before the session is treated as dead.
    /// One is not enough: a node busy writing flash — clearing hundreds of contacts, say —
    /// can miss a check and still be perfectly alive, and tearing the session down there
    /// makes the node look like it has vanished in the middle of the operation.
    /// </summary>
    private const int KeepAliveStrikes = 3;

    private async Task KeepAliveAsync(CancellationToken ct)
    {
        var missed = 0;
        while (!ct.IsCancellationRequested)
        {
            // Wake at once when the node says a message is waiting; otherwise every 5 s.
            await _wake.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            ExpireAcks();
            try
            {
                if (Interlocked.Exchange(ref _syncRequested, 0) == 1)
                {
                    await DrainMessagesAsync(queuedWhileAway: false, ct).ConfigureAwait(false);
                }
                // A bulk operation is talking to the node constantly; it needs no help
                // proving the connection is alive, and the extra command only adds load.
                if (!BulkRunning && Options.Time.GetUtcNow() - _lastTraffic > KeepAliveInterval)
                {
                    // Also notices a dead TCP connection.
                    await CommandAsync(GetBattery(), c => c == RespBattAndStorage, ct).ConfigureAwait(false);
                }
                missed = 0;
            }
            catch (TimeoutException)
            {
                missed++;
                Log(LogLevel.Warning, missed >= KeepAliveStrikes
                    ? $"The node has not answered {missed} checks in a row; reconnecting."
                    : $"The node didn't answer a check ({missed} of {KeepAliveStrikes}). It may be busy; waiting.");
                if (missed >= KeepAliveStrikes) throw;
            }
            catch (MeshCoreException ex)
            {
                // The node answered, with a complaint. That is not a dead connection.
                Log(LogLevel.Warning, $"The node refused a routine check: {ex.Message} (code {ex.Code}).");
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
                // A malformed frame must never end the session or reach Zeus, but it
                // should not vanish without trace either.
                Log(LogLevel.Warning,
                    $"A frame from the node couldn't be read (code {(frame.Length > 0 ? frame[0] : 0)}, {frame.Length} bytes): {ex.GetType().Name}.");
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
                Log(LogLevel.Info, "The node says a message is waiting; fetching it.");
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
                    _heard[Convert.ToHexString(contact.PublicKey)] = new HeardStation(contact, f.ToArray(), ++_heardSeq);
                    TrimHeard();
                }
                RaiseChanged();
                break;
            }
            case PushContactsFull:
                Log(LogLevel.Warning, "The node reports its contact list is full.");
                break;
            case PushAdvert when f.Length >= 33:
            {
                // The node heard an advert from a station it already has (or just saved).
                var key = Convert.ToHexString(f.AsSpan(1, 32));
                lock (_gate)
                {
                    var known = _contacts.TryGetValue(key, out var c);
                    Log(LogLevel.Receive, known
                        ? $"Heard an advert from \"{c!.Name}\" ({Short(key)}); its contact was updated."
                        : $"Heard an advert from {Short(key)}; the node saved it as a new contact.");
                }
                _ = RefreshContactsSoonAsync();
                break;
            }
            case PushPathUpdated or PushContactDeleted:
                // Contact details changed; refresh on the next quiet moment.
                _ = RefreshContactsSoonAsync();
                break;
            default:
                Log(LogLevel.Info, $"The node sent push code 0x{f[0]:x2} ({f.Length} bytes), which Zeus doesn't use.");
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
            Log(LogLevel.Info, $"Read {_contacts.Count} contact{(_contacts.Count == 1 ? "" : "s")} from the node.");
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
        string summary;
        lock (_gate)
        {
            _slots = slots;
            summary = slots.Count(s => !s.IsEmpty) == 0
                ? "no channels"
                : string.Join(", ", slots.Where(s => !s.IsEmpty)
                    .Select(s => $"{s.Index}=\"{(s.Name.Length > 0 ? s.Name : $"Channel {s.Index}")}\" ({KindOf(s).ToString().ToLowerInvariant()})"));
        }
        Log(LogLevel.Info, $"Channels on the node: {summary}.");
        RaiseChanged();
    }

    private async Task DrainMessagesAsync(bool queuedWhileAway, CancellationToken ct)
    {
        var drained = 0;
        for (var n = 0; n < 1000; n++) // the node's queue holds at most a few hundred
        {
            var f = await CommandAsync(SyncNextMessage(),
                c => c is RespNoMoreMessages or RespContactMsgRecv or RespContactMsgRecvV3 or RespChannelMsgRecv or RespChannelMsgRecvV3 or 27,
                ct).ConfigureAwait(false);
            if (f[0] == RespNoMoreMessages)
            {
                if (drained > 1)
                {
                    Log(LogLevel.Receive, $"Fetched {drained} messages the node was holding.");
                }
                return;
            }
            var received = ParseReceived(f);
            if (received is null)
            {
                // Something the node called a message and Zeus could not read. Silence here
                // used to look exactly like no message ever arriving.
                Log(LogLevel.Warning,
                    $"The node handed Zeus a message frame it couldn't read (code {f[0]}, {f.Length} bytes), so it was dropped.");
                continue;
            }
            drained++;
            Deliver(received, queuedWhileAway);
        }
    }

    private void Deliver(ReceivedText r, bool queuedWhileAway)
    {
        var sent = PlausibleTime(r.Timestamp);
        var hops = HopsFromPathLength(r.PathLength);
        var reception = new Reception(r.Snr, null, hops, hops is null, false);
        LogArrival(r, queuedWhileAway);
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

    /// <summary>
    /// Says that a message arrived, and on which channel or from which station, without
    /// recording what it said. Written because the only way to tell "the node never
    /// decrypted it" from "Zeus received it and filed it somewhere unexpected" is to see
    /// the arrival in the log.
    /// </summary>
    private void LogArrival(ReceivedText r, bool queuedWhileAway)
    {
        var bytes = Encoding.UTF8.GetByteCount(r.Text);
        var how = queuedWhileAway ? ", queued while Zeus was away" : "";
        var signal = r.Snr is null ? "" : $", SNR {r.Snr:0.#} dB";
        var hops = HopsFromPathLength(r.PathLength);
        var route = hops is null ? ", direct route" : hops == 0 ? ", no hops" : $", {hops} hop{(hops == 1 ? "" : "s")}";
        if (!r.IsChannel)
        {
            var kind = r.TextType == TxtSignedPlain ? "A room post" : "A direct message";
            Log(LogLevel.Receive, $"{kind} from {Short(Convert.ToHexString(r.SenderPrefix))}: {bytes} bytes{signal}{route}{how}.");
            return;
        }
        string? name;
        lock (_gate)
        {
            var slot = _slots.FirstOrDefault(s => s.Index == r.ChannelIndex);
            name = slot is null || slot.IsEmpty ? null : slot.Name;
        }
        if (name is null)
        {
            Log(LogLevel.Warning,
                $"A channel message arrived on slot {r.ChannelIndex}, which has no channel configured on the node: " +
                $"{bytes} bytes{signal}{route}{how}. Zeus can't name this conversation.");
            return;
        }
        Log(LogLevel.Receive,
            $"Channel {r.ChannelIndex} (\"{name}\"): received {bytes} bytes{signal}{route}{how}.");
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
                    // An index with no channel behind it must never transmit: the firmware
                    // takes the low byte of whatever it is handed, so an out-of-range slot
                    // would go out on the public channel for everyone to read.
                    string? channelName;
                    lock (_gate)
                    {
                        var slot = _slots.FirstOrDefault(s => s.Index == index);
                        channelName = slot is null || slot.IsEmpty ? null : (slot.Name.Length > 0 ? slot.Name : $"Channel {index}");
                    }
                    if (channelName is null)
                    {
                        Log(LogLevel.Warning, $"Refused to send on channel slot {index}: the node has no channel there.");
                        return SendResult.Failure(
                            $"{DisplayName} has no channel in slot {index}, so nothing was transmitted. " +
                            "Check the channel list on the Nodes tab.");
                    }
                    Log(LogLevel.Send, $"Channel {index} (\"{channelName}\"): sending {Encoding.UTF8.GetByteCount(text)} bytes.");
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

    public override int RememberedPeerLimit => MaxHeardStations;

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

    /// <summary>
    /// How long to wait between removals. A node writes its contact list to flash, and a
    /// tight loop of deletes starves everything else it has to do — on a real node with 323
    /// repeaters to clear, it stopped answering altogether and Zeus dropped the session.
    /// </summary>
    private static readonly TimeSpan BulkPace = TimeSpan.FromMilliseconds(25);

    private int _bulkDepth;

    /// <summary>True while a bulk operation holds the node's attention.</summary>
    private bool BulkRunning => Volatile.Read(ref _bulkDepth) > 0;

    public override async Task<BulkRemoval> RemovePeersAsync(IReadOnlyList<string> peerIds, CancellationToken ct)
    {
        if (State != ConnectorState.Connected) return new BulkRemoval(0, peerIds.Count, $"{DisplayName} is not connected.");
        if (peerIds.Count == 0) return new BulkRemoval(0, 0, null);

        Interlocked.Increment(ref _bulkDepth);
        var removed = 0;
        string? error = null;
        try
        {
            Log(LogLevel.Info, $"Removing {peerIds.Count} contact{(peerIds.Count == 1 ? "" : "s")} from the node.");
            foreach (var id in peerIds)
            {
                if (ct.IsCancellationRequested) { error = "Stopped before it finished."; break; }
                Contact? contact;
                lock (_gate) contact = FindContact(id);
                if (contact is null) continue; // already gone
                try
                {
                    await CommandAsync(RemoveContact(contact.PublicKey), c => c is RespOk, ct).ConfigureAwait(false);
                }
                catch (MeshCoreException ex)
                {
                    // One contact the node won't part with is not a reason to stop.
                    error ??= $"{ex.Message} (code {ex.Code})";
                    continue;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException)
                {
                    error = $"The node stopped answering after {removed} of {peerIds.Count}. Nothing else was removed; run it again when it has caught up.";
                    Log(LogLevel.Warning, error);
                    break;
                }
                // Keep Zeus's own copy in step rather than re-reading the whole list each
                // time: that re-read is what turned 323 removals into tens of thousands of
                // frames and buried the node.
                lock (_gate) _contacts.Remove(Convert.ToHexString(contact.PublicKey));
                removed++;
                if (removed % 25 == 0)
                {
                    Log(LogLevel.Info, $"Removed {removed} of {peerIds.Count}...");
                    RaiseChanged();
                }
                await Task.Delay(BulkPace, Options.Time, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            error ??= "Stopped before it finished.";
        }
        finally
        {
            Interlocked.Decrement(ref _bulkDepth);
        }

        // One read at the end, so Zeus and the node agree however it went.
        try
        {
            await LoadContactsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MeshCoreException or IOException or TimeoutException or OperationCanceledException)
        {
            RaiseChanged();
        }
        Log(LogLevel.Info, $"Removed {removed} of {peerIds.Count} contact{(peerIds.Count == 1 ? "" : "s")}.");
        return new BulkRemoval(removed, peerIds.Count, error);
    }

    public override bool CanEditChannels => true;

    public override async Task<string?> JoinChannelAsync(string name, string? secretHex, CancellationToken ct)
    {
        if (State != ConnectorState.Connected) return $"{DisplayName} is not connected.";
        name = (name ?? "").Trim();
        if (name.Length == 0) return "Give the channel a name.";
        if (Encoding.UTF8.GetByteCount(name) > 31) return "That name is too long for the node (31 bytes).";

        byte[] secret;
        var hex = (secretHex ?? "").Replace(" ", "").Replace("-", "");
        if (hex.Length > 0)
        {
            // 128-bit keys only: that is all the companion protocol carries.
            if (hex.Length != 32 || !hex.All(Uri.IsHexDigit)) return "A channel key is 32 hex characters.";
            secret = Convert.FromHexString(hex);
        }
        else if (name.StartsWith('#'))
        {
            secret = DerivedChannelKey(name);
        }
        else
        {
            return "A channel name that doesn't start with # needs its key as well.";
        }

        List<ChannelSlot> slots;
        lock (_gate) slots = [.. _slots];
        if (slots.FirstOrDefault(s => s.Secret.AsSpan().SequenceEqual(secret)) is { } already)
        {
            return $"Already joined, as \"{already.Name}\" in slot {already.Index}.";
        }
        if (slots.FirstOrDefault(s => s.IsEmpty) is not { } free)
        {
            return "The node has no free channel slot. Leave one first.";
        }

        try
        {
            await CommandAsync(SetChannel(free.Index, name, secret), c => c is RespOk, ct).ConfigureAwait(false);
            Log(LogLevel.Info, $"Joined channel \"{name}\" in slot {free.Index}.");
            await LoadChannelsAsync(slots.Count, ct).ConfigureAwait(false);
            return null;
        }
        catch (MeshCoreException ex)
        {
            Log(LogLevel.Error, $"Joining \"{name}\" failed: {ex.Message} (code {ex.Code}).");
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return "The node didn't answer.";
        }
    }

    public override async Task<string?> LeaveChannelAsync(int index, CancellationToken ct)
    {
        if (State != ConnectorState.Connected) return $"{DisplayName} is not connected.";
        // Slot 0 is the node's primary channel. Clearing it from here would
        // leave the node with nowhere to put ordinary traffic, and the operator
        // would have no obvious way back; the MeshCore app can still do it.
        if (index == 0) return "Slot 0 is the node's primary channel and can't be cleared from Zeus.";
        List<ChannelSlot> slots;
        lock (_gate) slots = [.. _slots];
        var slot = slots.FirstOrDefault(s => s.Index == index);
        if (slot is null || slot.IsEmpty) return "That slot is already empty.";

        try
        {
            await CommandAsync(SetChannel(index, "", new byte[16]), c => c is RespOk, ct).ConfigureAwait(false);
            Log(LogLevel.Info, $"Left channel \"{slot.Name}\" (slot {index}).");
            await LoadChannelsAsync(slots.Count, ct).ConfigureAwait(false);
            return null;
        }
        catch (MeshCoreException ex)
        {
            Log(LogLevel.Error, $"Leaving \"{slot.Name}\" failed: {ex.Message} (code {ex.Code}).");
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

    /// <summary>
    /// Marks a contact as a favourite on the node, or clears it. The firmware never
    /// recycles a favourite, so this is what keeps a station you message from being
    /// dropped when the contact list fills. Local to the node; transmits nothing.
    /// </summary>
    public override async Task<string?> SetPeerFavouriteAsync(string peerId, bool favourite, CancellationToken ct)
    {
        if (State != ConnectorState.Connected) return $"{DisplayName} is not connected.";
        Contact? contact;
        lock (_gate) contact = FindContact(peerId);
        if (contact is null) return "That contact is no longer on the node.";
        if (contact.Frame is null) return "This node's firmware didn't send enough detail to change the contact.";
        try
        {
            await CommandAsync(SetContactFavourite(contact, favourite), c => c is RespOk, ct).ConfigureAwait(false);
            Log(LogLevel.Info, favourite
                ? $"\"{contact.Name}\" is now a favourite and will never be recycled."
                : $"\"{contact.Name}\" is no longer a favourite.");
            await LoadContactsAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (MeshCoreException ex)
        {
            Log(LogLevel.Error, $"Changing the favourite flag failed: {ex.Message} (code {ex.Code}).");
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

    /// <summary>
    /// A MeshCore channel frame carries no sender key: the payload is encrypted
    /// with the shared channel key and the sender is only the "Name: " prefix in
    /// the text, which anyone on the channel can type. So the best we can do is
    /// match that name against the node's contacts, and say honestly when that
    /// matches nothing or more than one station. Room posts are signed, so there
    /// the id is real — but only if we actually hold that contact.
    /// </summary>
    public override IReadOnlyList<string> ResolveSenderPeers(ConversationKind kind, string fromId, string? fromName)
    {
        if (kind != ConversationKind.Channel)
        {
            return Peers.Any(p => string.Equals(p.Id, fromId, StringComparison.OrdinalIgnoreCase)) ? [fromId] : [];
        }
        var name = (fromName ?? fromId).Trim();
        if (name.Length == 0) return [];
        return Peers
            .Where(p => p.Kind is PeerKind.Chat or PeerKind.Room)
            .Where(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .ToArray();
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

    /// <summary>
    /// Forgets the stations heard longest ago once the table is full. Called with
    /// <see cref="_gate"/> held. One the node has since saved as a contact goes first,
    /// because the contact itself is the record that matters.
    /// </summary>
    private void TrimHeard()
    {
        if (_heard.Count <= MaxHeardStations) return;
        var expendable = _heard
            .OrderBy(h => _contacts.ContainsKey(h.Key) ? 0 : 1)
            .ThenBy(h => h.Value.Seq)
            .Select(h => h.Key)
            .ToList();
        var forgotten = 0;
        foreach (var key in expendable)
        {
            if (_heard.Count <= MaxHeardStations) break;
            _heard.Remove(key);
            forgotten++;
        }
        if (forgotten > 0)
        {
            Log(LogLevel.Info,
                $"Forgot {forgotten} station{(forgotten == 1 ? "" : "s")} heard but not saved; Zeus remembers " +
                $"{MaxHeardStations}. They come back when they advert again, and the node's contacts are unchanged.");
        }
    }

    private HeardStation? FindHeard(string peer)
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

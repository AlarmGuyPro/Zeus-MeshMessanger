// SPDX-License-Identifier: GPL-3.0-or-later
// Companion-radio protocol, written from meshcore-dev/meshcore @ a366955
// (examples/companion_radio/MyMesh.cpp, docs/companion_protocol.md). See
// docs/protocols/meshcore.md. No code copied.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MeshMessenger.Core;

namespace MeshMessenger.Connectors.MeshCore;

public static class MeshCoreProtocol
{
    public const byte AppToRadio = (byte)'<';
    public const byte RadioToApp = (byte)'>';
    public const int MaxFrame = 1024; // firmware MAX_FRAME_SIZE is 176; leave room for future versions
    public const int TextLimitBytes = 160;  // MAX_TEXT_LEN
    public const int MaxNodeNameBytes = 31;
    public const int AppProtocolVersion = 3; // asks for V3 message frames (with SNR)

    // Commands
    public const byte CmdAppStart = 1, CmdSendTxtMsg = 2, CmdSendChannelTxtMsg = 3, CmdGetContacts = 4,
        CmdSetDeviceTime = 6, CmdSendSelfAdvert = 7, CmdAddUpdateContact = 9, CmdSyncNextMessage = 10, CmdResetPath = 13,
        CmdGetBattAndStorage = 20, CmdDeviceQuery = 22, CmdSendLogin = 26, CmdSendStatusReq = 27,
        CmdRemoveContact = 15, CmdGetChannel = 31, CmdSetChannel = 32, CmdSendTracePath = 36, CmdSendPathDiscovery = 52, CmdGetStats = 56,
        CmdSetAutoAddConfig = 58, CmdGetAutoAddConfig = 59, CmdSetOtherParams = 38;

    // Replies
    public const byte RespOk = 0, RespErr = 1, RespContactsStart = 2, RespContact = 3, RespEndOfContacts = 4,
        RespSelfInfo = 5, RespSent = 6, RespContactMsgRecv = 7, RespChannelMsgRecv = 8, RespNoMoreMessages = 10,
        RespBattAndStorage = 12, RespDeviceInfo = 13, RespContactMsgRecvV3 = 16, RespChannelMsgRecvV3 = 17,
        RespChannelInfo = 18, RespStats = 24, RespAutoAddConfig = 25;

    // Pushes (asynchronous)
    public const byte PushAdvert = 0x80, PushPathUpdated = 0x81, PushSendConfirmed = 0x82, PushMsgWaiting = 0x83,
        PushLoginSuccess = 0x85, PushLoginFail = 0x86, PushStatusResponse = 0x87, PushLogRxData = 0x88,
        PushTraceData = 0x89, PushNewAdvert = 0x8A, PushPathDiscoveryResponse = 0x8D, PushContactDeleted = 0x8F,
        PushContactsFull = 0x90;

    public const byte TxtPlain = 0, TxtSignedPlain = 2;

    public static readonly byte[] PublicChannelKey = Convert.FromHexString("8b3387e9c5cdea6ac9e5edbaa115cd72");

    // ------------------------------------------------------------ framing

    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[3 + payload.Length];
        frame[0] = AppToRadio;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(3));
        return frame;
    }

    /// <summary>Reads one node-to-app frame, skipping stray bytes until a '>' header.</summary>
    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var one = new byte[1];
        var len = new byte[2];
        while (true)
        {
            await stream.ReadExactlyAsync(one, ct).ConfigureAwait(false);
            if (one[0] != RadioToApp) continue;
            await stream.ReadExactlyAsync(len, ct).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(len);
            if (length == 0 || length > MaxFrame) continue;
            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
            return payload;
        }
    }

    // ------------------------------------------------------------ commands

    public static byte[] DeviceQuery() => [CmdDeviceQuery, AppProtocolVersion];

    public static byte[] AppStart(string appName)
    {
        var name = Encoding.UTF8.GetBytes(appName);
        var b = new byte[8 + name.Length];
        b[0] = CmdAppStart;
        name.CopyTo(b, 8);
        return b;
    }

    public static byte[] SetDeviceTime(uint epochSeconds)
    {
        var b = new byte[5];
        b[0] = CmdSetDeviceTime;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), epochSeconds);
        return b;
    }

    public static byte[] GetContacts() => [CmdGetContacts];

    public static byte[] GetChannel(int index) => [CmdGetChannel, (byte)index];

    /// <summary>
    /// Writes a channel into a slot: 32 bytes of name then the 128-bit key.
    /// An empty name with a zero key clears the slot, which is how you leave.
    /// Local to the node; nothing goes out on the air.
    /// </summary>
    public static byte[] SetChannel(int index, string name, ReadOnlySpan<byte> secret16)
    {
        var b = new byte[2 + 32 + 16];
        b[0] = CmdSetChannel;
        b[1] = (byte)index;
        var bytes = Encoding.UTF8.GetBytes(name);
        // The firmware strncpy's 32 bytes and needs room for the terminator.
        bytes.AsSpan(0, Math.Min(bytes.Length, 31)).CopyTo(b.AsSpan(2));
        secret16[..Math.Min(secret16.Length, 16)].CopyTo(b.AsSpan(34));
        return b;
    }

    /// <summary>
    /// A "#name" channel's key is derived from the name itself, so two people
    /// who type the same name are on the same channel without exchanging
    /// anything. Any other channel needs its key shared out of band.
    /// </summary>
    public static byte[] DerivedChannelKey(string hashtagName) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(hashtagName)).AsSpan(0, 16).ToArray();

    public static byte[] SyncNextMessage() => [CmdSyncNextMessage];

    public static byte[] GetBattery() => [CmdGetBattAndStorage];

    // The node's auto-add config byte, from the firmware (examples/companion_radio/MyMesh.cpp,
    // AUTO_ADD_* defines). The type bits decide which kinds of station the node saves on its
    // own — but only when it is in selective mode. See AutoAddIsSelective below, which is the
    // part that catches people out.
    /// <summary>Bit 0: recycle the oldest non-favourite contact when the list is full.</summary>
    public const byte AutoAddOverwriteOldest = 1 << 0;
    /// <summary>Bit 1: auto-add other companion ("chat") stations.</summary>
    public const byte AutoAddChat = 1 << 1;
    /// <summary>Bit 2: auto-add repeaters. On a busy mesh this is the one that fills the list.</summary>
    public const byte AutoAddRepeater = 1 << 2;
    /// <summary>Bit 3: auto-add room servers.</summary>
    public const byte AutoAddRoomServer = 1 << 3;
    /// <summary>Bit 4: auto-add sensor nodes.</summary>
    public const byte AutoAddSensor = 1 << 4;

    /// <summary>Every station kind the type bits cover, so unknown bits are never disturbed.</summary>
    public const byte AutoAddAllKinds = AutoAddChat | AutoAddRepeater | AutoAddRoomServer | AutoAddSensor;

    /// <summary>
    /// Whether the node consults the type bits at all. The firmware reads
    /// <c>manual_add_contacts</c> bit 0: when it is clear the node saves <em>everything</em> it
    /// hears and the type bits are ignored entirely, so clearing the repeater bit on its own
    /// changes nothing. Both have to be set together to stop repeaters being saved.
    /// </summary>
    public static bool AutoAddIsSelective(byte manualAddContacts) => (manualAddContacts & 1) != 0;

    /// <summary>
    /// Sets <c>manual_add_contacts</c>. The firmware (CMD_SET_OTHER_PARAMS) only touches the
    /// telemetry, advert-location and multi-ack preferences when the frame is longer than two
    /// bytes, so a two-byte frame changes this and nothing else.
    /// </summary>
    public static byte[] SetOtherParams(byte manualAddContacts) => [CmdSetOtherParams, manualAddContacts];

    /// <summary>Delete a contact from the node's list. Local to the node; transmits nothing.</summary>
    public static byte[] RemoveContact(ReadOnlySpan<byte> publicKey32)
    {
        var b = new byte[33];
        b[0] = CmdRemoveContact;
        publicKey32[..32].CopyTo(b.AsSpan(1));
        return b;
    }

    public static byte[] GetAutoAddConfig() => [CmdGetAutoAddConfig];

    public static byte[] SetAutoAddConfig(byte config, byte maxHops) => [CmdSetAutoAddConfig, config, maxHops];

    /// <summary>Ask the node to announce itself. 1 = flood the mesh, 0 = direct range only.</summary>
    public static byte[] SelfAdvert(bool flood) => [CmdSendSelfAdvert, (byte)(flood ? 1 : 0)];

    public static byte[] SendDirect(ReadOnlySpan<byte> recipientKeyPrefix6, uint timestamp, string text, byte attempt = 0)
    {
        var t = Encoding.UTF8.GetBytes(text);
        var b = new byte[13 + t.Length];
        b[0] = CmdSendTxtMsg;
        b[1] = TxtPlain;
        b[2] = attempt;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(3), timestamp);
        recipientKeyPrefix6[..6].CopyTo(b.AsSpan(7));
        t.CopyTo(b, 13);
        return b;
    }

    /// <summary>
    /// ADD_UPDATE_CONTACT built from a PUSH_NEW_ADVERT frame: the push carries
    /// exactly the contact layout the command takes, after its own code byte.
    /// Saving a contact is local to the node; nothing is transmitted.
    /// </summary>
    public static byte[] AddContactFromAdvert(ReadOnlySpan<byte> newAdvertPush)
    {
        var b = newAdvertPush.ToArray();
        b[0] = CmdAddUpdateContact;
        return b;
    }

    public static byte[] SendChannel(int channelIndex, uint timestamp, string text)
    {
        var t = Encoding.UTF8.GetBytes(text);
        var b = new byte[7 + t.Length];
        b[0] = CmdSendChannelTxtMsg;
        b[1] = TxtPlain;
        b[2] = (byte)channelIndex;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(3), timestamp);
        t.CopyTo(b, 7);
        return b;
    }

    // ------------------------------------------------------------ replies

    public sealed record DeviceInfo(int FirmwareCode, int MaxContacts, int MaxChannels, string? Build, string? Model, string? Version);

    public static DeviceInfo ParseDeviceInfo(ReadOnlySpan<byte> f)
    {
        var code = f.Length > 1 ? f[1] : 0;
        if (code >= 3 && f.Length >= 80)
        {
            return new DeviceInfo(code, f[2] * 2, f[3], ZString(f.Slice(8, 12)), ZString(f.Slice(20, 40)), ZString(f.Slice(60, 20)));
        }
        return new DeviceInfo(code, 0, 8, null, null, null);
    }

    public sealed record SelfInfoFrame(byte[] PublicKey, string Name, double FrequencyMHz, double BandwidthKHz,
        int SpreadingFactor, int CodingRate, int TxPowerDbm, byte ManualAddContacts = 0);

    public static SelfInfoFrame ParseSelfInfo(ReadOnlySpan<byte> f)
    {
        if (f.Length < 36) throw new FormatException("SELF_INFO too short");
        var key = f.Slice(4, 32).ToArray();
        double freq = 0, bw = 0;
        int sf = 0, cr = 0;
        var name = "";
        byte manualAdd = 0;
        if (f.Length >= 58)
        {
            // The firmware writes manual_add_contacts immediately before the frequency.
            manualAdd = f[47];
            freq = BinaryPrimitives.ReadUInt32LittleEndian(f[48..]) / 1000.0;
            bw = BinaryPrimitives.ReadUInt32LittleEndian(f[52..]) / 1000.0;
            sf = f[56];
            cr = f[57];
            name = Encoding.UTF8.GetString(f[58..]).TrimEnd('\0').Trim();
        }
        return new SelfInfoFrame(key, name, freq, bw, sf, cr, (sbyte)f[2], manualAdd);
    }

    /// <summary>
    /// A station the node knows about. <paramref name="LastAdvert"/> is the timestamp the
    /// SENDER put in its advert, by its own clock, so it can be wrong or in the future;
    /// <paramref name="LastModified"/> is our node's clock when it last updated the record,
    /// which is what "heard" really means to us. The raw frame is kept so the contact can be
    /// written back (favourite, re-save) without rebuilding it.
    /// </summary>
    public sealed record Contact(byte[] PublicKey, PeerKind Kind, byte Flags, int OutPathLength, string Name,
        DateTimeOffset? LastAdvert, double? Latitude, double? Longitude, DateTimeOffset? LastModified = null,
        byte[]? Frame = null)
    {
        public bool Favourite => (Flags & ContactFlagFavourite) != 0;
    }

    /// <summary>Bit 0 of a contact's flags. The firmware never recycles a favourite.</summary>
    public const byte ContactFlagFavourite = 1 << 0;

    /// <summary>Rewrites a contact with its favourite flag set or cleared. Local to the node.</summary>
    public static byte[] SetContactFavourite(Contact contact, bool favourite)
    {
        var frame = contact.Frame is { Length: >= ContactFrameLength }
            ? contact.Frame.ToArray()
            : throw new FormatException("the contact's original frame is needed to change it");
        frame[0] = CmdAddUpdateContact;
        frame[34] = (byte)(favourite ? frame[34] | ContactFlagFavourite : frame[34] & ~ContactFlagFavourite);
        return frame;
    }

    public const int ContactFrameLength = 1 + 32 + 1 + 1 + 1 + 64 + 32 + 4 + 4 + 4 + 4;

    public static Contact ParseContact(ReadOnlySpan<byte> f)
    {
        if (f.Length < 1 + 32 + 3 + 64 + 32 + 4) throw new FormatException("contact frame too short");
        var key = f.Slice(1, 32).ToArray();
        var kind = f[33] switch { 1 => PeerKind.Chat, 2 => PeerKind.Repeater, 3 => PeerKind.Room, 4 => PeerKind.Sensor, _ => PeerKind.Unknown };
        var flags = f[34];
        var outPath = f[35];
        var name = ZString(f.Slice(100, 32)) ?? "";
        var advert = BinaryPrimitives.ReadUInt32LittleEndian(f[132..]);
        double? lat = null, lon = null;
        if (f.Length >= 144)
        {
            var la = BinaryPrimitives.ReadInt32LittleEndian(f[136..]);
            var lo = BinaryPrimitives.ReadInt32LittleEndian(f[140..]);
            if (la != 0 || lo != 0) { lat = la / 1e6; lon = lo / 1e6; }
        }
        uint lastmod = 0;
        if (f.Length >= 148) lastmod = BinaryPrimitives.ReadUInt32LittleEndian(f[144..]);
        return new Contact(key, kind, flags, outPath == 0xFF ? -1 : outPath & 0x3F, name,
            advert == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(advert), lat, lon,
            lastmod == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(lastmod),
            f.Length >= ContactFrameLength ? f[..ContactFrameLength].ToArray() : null);
    }

    public sealed record ChannelSlot(int Index, string Name, byte[] Secret)
    {
        public bool IsEmpty => Name.Length == 0 && Secret.All(b => b == 0);
    }

    public static ChannelSlot ParseChannelInfo(ReadOnlySpan<byte> f)
    {
        if (f.Length < 50) throw new FormatException("channel frame too short");
        return new ChannelSlot(f[1], ZString(f.Slice(2, 32)) ?? "", f.Slice(34, 16).ToArray());
    }

    public static ChannelKind KindOf(ChannelSlot slot)
    {
        if (slot.Secret.AsSpan().SequenceEqual(PublicChannelKey)) return ChannelKind.Public;
        if (slot.Name.StartsWith('#'))
        {
            var derived = SHA256.HashData(Encoding.UTF8.GetBytes(slot.Name)).AsSpan(0, 16);
            if (derived.SequenceEqual(slot.Secret)) return ChannelKind.Hashtag;
        }
        return ChannelKind.Private;
    }

    public sealed record Sent(bool Flood, uint AckTag, uint SuggestedTimeoutMs);

    public static Sent ParseSent(ReadOnlySpan<byte> f)
    {
        if (f.Length < 10) throw new FormatException("SENT too short");
        return new Sent(f[1] == 1, BinaryPrimitives.ReadUInt32LittleEndian(f[2..]), BinaryPrimitives.ReadUInt32LittleEndian(f[6..]));
    }

    public sealed record ReceivedText(
        bool IsChannel, int ChannelIndex, byte[] SenderPrefix, double? Snr, int PathLength, byte TextType,
        uint Timestamp, byte[]? AuthorPrefix, string Text);

    /// <summary>Parses contact/channel message frames (V1 and V3). Null for other codes.</summary>
    public static ReceivedText? ParseReceived(ReadOnlySpan<byte> f)
    {
        var code = f[0];
        var i = 1;
        double? snr = null;
        if (code is RespContactMsgRecvV3 or RespChannelMsgRecvV3)
        {
            if (f.Length < 4) return null;
            snr = (sbyte)f[1] / 4.0;
            i = 4;
        }
        switch (code)
        {
            case RespContactMsgRecv or RespContactMsgRecvV3:
            {
                if (f.Length < i + 12) return null;
                var prefix = f.Slice(i, 6).ToArray();
                var pathLen = f[i + 6];
                var txtType = f[i + 7];
                var ts = BinaryPrimitives.ReadUInt32LittleEndian(f[(i + 8)..]);
                i += 12;
                byte[]? author = null;
                if (txtType == TxtSignedPlain)
                {
                    if (f.Length < i + 4) return null;
                    author = f.Slice(i, 4).ToArray();
                    i += 4;
                }
                return new ReceivedText(false, -1, prefix, snr, pathLen, txtType, ts, author, Utf8(f[i..]));
            }
            case RespChannelMsgRecv or RespChannelMsgRecvV3:
            {
                if (f.Length < i + 7) return null;
                var index = f[i];
                var pathLen = f[i + 1];
                var txtType = f[i + 2];
                var ts = BinaryPrimitives.ReadUInt32LittleEndian(f[(i + 3)..]);
                return new ReceivedText(true, index, [], snr, pathLen, txtType, ts, null, Utf8(f[(i + 7)..]));
            }
            default:
                return null;
        }
    }

    /// <summary>Hops for a received frame: null when it came by direct route (0xFF).</summary>
    public static int? HopsFromPathLength(int pathLength) => pathLength == 0xFF ? null : pathLength & 0x3F;

    /// <summary>Splits MeshCore channel text "Sender: message".</summary>
    public static (string? Sender, string Text) SplitChannelText(string text)
    {
        var at = text.IndexOf(": ", StringComparison.Ordinal);
        if (at <= 0 || at > MaxNodeNameBytes + 1) return (null, text);
        return (text[..at], text[(at + 2)..]);
    }

    // Firmware error codes (examples/companion_radio/MyMesh.cpp):
    // 1 UNSUPPORTED_CMD, 2 NOT_FOUND, 3 TABLE_FULL, 4 BAD_STATE, 5 FILE_IO_ERROR, 6 ILLEGAL_ARG.
    public const byte ErrUnsupported = 1, ErrNotFound = 2, ErrTableFull = 3, ErrBadState = 4, ErrFileIo = 5, ErrIllegalArg = 6;

    public static byte ErrorCode(ReadOnlySpan<byte> f) => f.Length > 1 ? f[1] : (byte)0;

    public static string ErrorText(ReadOnlySpan<byte> f) => ErrorCode(f) switch
    {
        ErrUnsupported => "the node doesn't support that command",
        ErrNotFound => "the node couldn't find that contact or channel",
        // TABLE_FULL from a send means the node had no free packet buffer, not that a list is full.
        ErrTableFull => "the node had no free packet buffer (it is busy on the air)",
        ErrBadState => "the node is busy with another request",
        ErrFileIo => "storage error on the node",
        ErrIllegalArg => "the node rejected a value in the request",
        _ => "the node reported an error",
    };

    public static string? ZString(ReadOnlySpan<byte> s)
    {
        var end = s.IndexOf((byte)0);
        if (end >= 0) s = s[..end];
        var text = Encoding.UTF8.GetString(s).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string Utf8(ReadOnlySpan<byte> s)
    {
        var end = s.IndexOf((byte)0);
        if (end >= 0) s = s[..end];
        return Encoding.UTF8.GetString(s);
    }
}

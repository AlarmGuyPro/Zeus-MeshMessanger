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
        CmdSetDeviceTime = 6, CmdSendSelfAdvert = 7, CmdSyncNextMessage = 10, CmdResetPath = 13,
        CmdGetBattAndStorage = 20, CmdDeviceQuery = 22, CmdSendLogin = 26, CmdSendStatusReq = 27,
        CmdGetChannel = 31, CmdSetChannel = 32, CmdSendTracePath = 36, CmdSendPathDiscovery = 52, CmdGetStats = 56;

    // Replies
    public const byte RespOk = 0, RespErr = 1, RespContactsStart = 2, RespContact = 3, RespEndOfContacts = 4,
        RespSelfInfo = 5, RespSent = 6, RespContactMsgRecv = 7, RespChannelMsgRecv = 8, RespNoMoreMessages = 10,
        RespBattAndStorage = 12, RespDeviceInfo = 13, RespContactMsgRecvV3 = 16, RespChannelMsgRecvV3 = 17,
        RespChannelInfo = 18, RespStats = 24;

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

    public static byte[] SyncNextMessage() => [CmdSyncNextMessage];

    public static byte[] GetBattery() => [CmdGetBattAndStorage];

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

    public sealed record SelfInfoFrame(byte[] PublicKey, string Name, double FrequencyMHz, double BandwidthKHz, int SpreadingFactor, int CodingRate, int TxPowerDbm);

    public static SelfInfoFrame ParseSelfInfo(ReadOnlySpan<byte> f)
    {
        if (f.Length < 36) throw new FormatException("SELF_INFO too short");
        var key = f.Slice(4, 32).ToArray();
        double freq = 0, bw = 0;
        int sf = 0, cr = 0;
        var name = "";
        if (f.Length >= 58)
        {
            freq = BinaryPrimitives.ReadUInt32LittleEndian(f[48..]) / 1000.0;
            bw = BinaryPrimitives.ReadUInt32LittleEndian(f[52..]) / 1000.0;
            sf = f[56];
            cr = f[57];
            name = Encoding.UTF8.GetString(f[58..]).TrimEnd('\0').Trim();
        }
        return new SelfInfoFrame(key, name, freq, bw, sf, cr, (sbyte)f[2]);
    }

    public sealed record Contact(byte[] PublicKey, PeerKind Kind, byte Flags, int OutPathLength, string Name, DateTimeOffset? LastAdvert, double? Latitude, double? Longitude);

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
        return new Contact(key, kind, flags, outPath == 0xFF ? -1 : outPath & 0x3F, name,
            advert == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(advert), lat, lon);
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

    public static string ErrorText(ReadOnlySpan<byte> f) => (f.Length > 1 ? f[1] : 0) switch
    {
        1 => "the node doesn't support that command",
        2 => "not found on the node",
        3 => "the node's send queue is full; try again shortly",
        4 => "the node is busy",
        5 => "storage error on the node",
        6 => "the node rejected the request",
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

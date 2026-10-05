// SPDX-License-Identifier: GPL-3.0-or-later
// Field numbers transcribed from meshtastic/protobufs @ 95c5f8c (GPL-3.0);
// stream framing from meshtastic/python @ 0a18357. See
// docs/protocols/meshtastic.md. No generated or copied code.
using System.Buffers.Binary;
using MeshMessenger.Core;

namespace MeshMessenger.Connectors.Meshtastic;

public static class MeshtasticProtocol
{
    public const byte Start1 = 0x94, Start2 = 0xC3;
    public const int MaxPacket = 512;
    public const uint Broadcast = 0xFFFFFFFF;
    public const int TextLimitBytes = 200;     // official apps; protocol max DATA_PAYLOAD_LEN is 233

    public const int PortText = 1, PortPosition = 3, PortNodeInfo = 4, PortRouting = 5, PortAdmin = 6,
        PortTelemetry = 67, PortTraceroute = 70, PortNeighborInfo = 71;

    public static readonly byte[] DefaultKey = Convert.FromHexString("d4f1bb3a20290759f0bcffabcf4e6901");

    // ------------------------------------------------------------ framing

    public static byte[] Frame(byte[] toRadio)
    {
        var frame = new byte[4 + toRadio.Length];
        frame[0] = Start1;
        frame[1] = Start2;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)toRadio.Length);
        toRadio.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Bytes that wake a sleeping device and reset its frame parser.</summary>
    public static byte[] Wake() => Enumerable.Repeat(Start2, 32).ToArray();

    /// <summary>Reads one FromRadio payload, skipping debug text between frames.</summary>
    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var one = new byte[1];
        var len = new byte[2];
        while (true)
        {
            await stream.ReadExactlyAsync(one, ct).ConfigureAwait(false);
            if (one[0] != Start1) continue;
            await stream.ReadExactlyAsync(one, ct).ConfigureAwait(false);
            if (one[0] != Start2) continue;
            await stream.ReadExactlyAsync(len, ct).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt16BigEndian(len);
            if (length > MaxPacket) continue; // corrupt header: resync
            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
            return payload;
        }
    }

    // ------------------------------------------------------------ ToRadio

    public static byte[] WantConfig(uint configId) => new ProtoWriter().Varint(3, configId).ToArray();

    public static byte[] Heartbeat() => new ProtoWriter().Message(7, new ProtoWriter()).ToArray();

    public static byte[] Disconnect() => new ProtoWriter().Bool(4, true).ToArray();

    public static byte[] TextPacket(uint packetId, uint to, int channel, string text, bool wantAck = true)
    {
        var data = new ProtoWriter()
            .Varint(1, PortText)
            .String(2, text);
        var packet = new ProtoWriter()
            .Fixed32(2, to)
            .Varint(3, (ulong)channel)
            .Message(4, data)
            .Fixed32(6, packetId)
            .Bool(10, wantAck);
        return new ProtoWriter().Message(1, packet).ToArray();
    }

    // ------------------------------------------------------------ FromRadio

    public sealed record User(string? Id, string? LongName, string? ShortName, int Role);

    public sealed record NodeRecord(uint Num, User? User, double? Snr, DateTimeOffset? LastHeard, int? HopsAway,
        int? BatteryLevel, double? Latitude, double? Longitude, bool Favorite);

    public sealed record ChannelRecord(int Index, string Name, byte[] Psk, int Role);

    public sealed record LoraConfig(bool UsePreset, int Preset, int Bandwidth, int SpreadFactor, int CodingRate, int Region);

    public sealed record Packet(uint From, uint To, int Channel, uint Id, DateTimeOffset? RxTime, double? RxSnr, int? RxRssi,
        int HopLimit, int HopStart, bool ViaMqtt, bool PkiEncrypted, int? PortNum, ReadOnlyMemory<byte> Payload, uint RequestId);

    public abstract record FromRadio;
    public sealed record MyInfo(uint NodeNum) : FromRadio;
    public sealed record NodeInfoMsg(NodeRecord Node) : FromRadio;
    public sealed record ChannelMsg(ChannelRecord Channel) : FromRadio;
    public sealed record MetadataMsg(string? Firmware, int HwModel) : FromRadio;
    public sealed record LoraMsg(LoraConfig Lora) : FromRadio;
    public sealed record ConfigComplete(uint Id) : FromRadio;
    public sealed record PacketMsg(Packet Packet) : FromRadio;
    public sealed record Rebooted : FromRadio;
    public sealed record Other : FromRadio;

    public static FromRadio ParseFromRadio(ReadOnlyMemory<byte> data)
    {
        foreach (var f in ProtoReader.Fields(data))
        {
            switch (f.Number)
            {
                case 2 when f.WireType == 2: return new PacketMsg(ParsePacket(f.Data));
                case 3 when f.WireType == 2: return new MyInfo(ReadMyNodeNum(f.Data));
                case 4 when f.WireType == 2: return new NodeInfoMsg(ParseNodeInfo(f.Data));
                case 5 when f.WireType == 2:
                {
                    var lora = ParseConfigLora(f.Data);
                    return lora is null ? new Other() : new LoraMsg(lora);
                }
                case 7 when f.WireType == 0: return new ConfigComplete(f.UInt32);
                case 8 when f.WireType == 0 && f.Bool: return new Rebooted();
                case 10 when f.WireType == 2: return new ChannelMsg(ParseChannel(f.Data));
                case 13 when f.WireType == 2: return ParseMetadata(f.Data);
            }
        }
        return new Other();
    }

    private static uint ReadMyNodeNum(ReadOnlyMemory<byte> data)
    {
        foreach (var f in ProtoReader.Fields(data))
            if (f.Number == 1 && f.WireType == 0) return f.UInt32;
        throw new FormatException("my_info without my_node_num");
    }

    public static Packet ParsePacket(ReadOnlyMemory<byte> data)
    {
        uint from = 0, to = 0, id = 0, requestId = 0;
        int channel = 0, hopLimit = 0, hopStart = 0;
        DateTimeOffset? rxTime = null;
        double? snr = null;
        int? rssi = null, port = null;
        bool mqtt = false, pki = false;
        ReadOnlyMemory<byte> payload = default;
        foreach (var f in ProtoReader.Fields(data))
        {
            switch (f.Number)
            {
                case 1: from = f.Fixed32; break;
                case 2: to = f.Fixed32; break;
                case 3: channel = (int)f.UInt32; break;
                case 4 when f.WireType == 2:
                    foreach (var d in ProtoReader.Fields(f.Data))
                    {
                        switch (d.Number)
                        {
                            case 1: port = (int)d.UInt32; break;
                            case 2 when d.WireType == 2: payload = d.Data; break;
                            case 6: requestId = d.Fixed32; break;
                        }
                    }
                    break;
                case 6: id = f.Fixed32; break;
                case 7: rxTime = f.Fixed32 == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(f.Fixed32); break;
                case 8 when f.WireType == 5: snr = Math.Round(f.Float, 2); break;
                case 9: hopLimit = (int)f.UInt32; break;
                case 12: rssi = f.Int32 == 0 ? null : f.Int32; break;
                case 14: mqtt = f.Bool; break;
                case 15: hopStart = (int)f.UInt32; break;
                case 17: pki = f.Bool; break;
            }
        }
        return new Packet(from, to, channel, id, rxTime, snr, rssi, hopLimit, hopStart, mqtt, pki, port, payload, requestId);
    }

    public static NodeRecord ParseNodeInfo(ReadOnlyMemory<byte> data)
    {
        uint num = 0;
        User? user = null;
        double? snr = null, lat = null, lon = null;
        DateTimeOffset? heard = null;
        int? hops = null, battery = null;
        var fav = false;
        foreach (var f in ProtoReader.Fields(data))
        {
            switch (f.Number)
            {
                case 1: num = f.UInt32; break;
                case 2 when f.WireType == 2: user = ParseUser(f.Data); break;
                case 3 when f.WireType == 2: (lat, lon) = ParsePosition(f.Data); break;
                case 4 when f.WireType == 5: snr = Math.Round(f.Float, 2); break;
                case 5: heard = f.Fixed32 == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(f.Fixed32); break;
                case 6 when f.WireType == 2:
                    foreach (var m in ProtoReader.Fields(f.Data))
                        if (m.Number == 1 && m.WireType == 0) battery = (int)m.UInt32;
                    break;
                case 9: hops = (int)f.UInt32; break;
                case 10: fav = f.Bool; break;
            }
        }
        return new NodeRecord(num, user, snr, heard, hops, battery, lat, lon, fav);
    }

    public static User ParseUser(ReadOnlyMemory<byte> data)
    {
        string? id = null, longName = null, shortName = null;
        var role = 0;
        foreach (var f in ProtoReader.Fields(data))
        {
            switch (f.Number)
            {
                case 1 when f.WireType == 2: id = f.String; break;
                case 2 when f.WireType == 2: longName = f.String; break;
                case 3 when f.WireType == 2: shortName = f.String; break;
                case 7 when f.WireType == 0: role = (int)f.UInt32; break;
            }
        }
        return new User(id, longName, shortName, role);
    }

    public static (double? Lat, double? Lon) ParsePosition(ReadOnlyMemory<byte> data)
    {
        int? lat = null, lon = null;
        foreach (var f in ProtoReader.Fields(data))
        {
            if (f.Number == 1 && f.WireType == 5) lat = (int)f.Fixed32;
            if (f.Number == 2 && f.WireType == 5) lon = (int)f.Fixed32;
        }
        return lat is null || lon is null || (lat == 0 && lon == 0) ? (null, null) : (lat.Value / 1e7, lon.Value / 1e7);
    }

    public static ChannelRecord ParseChannel(ReadOnlyMemory<byte> data)
    {
        int index = 0, role = 0;
        var name = "";
        var psk = Array.Empty<byte>();
        foreach (var f in ProtoReader.Fields(data))
        {
            switch (f.Number)
            {
                case 1: index = f.Int32; break;
                case 2 when f.WireType == 2:
                    foreach (var s in ProtoReader.Fields(f.Data))
                    {
                        if (s.Number == 2 && s.WireType == 2) psk = s.Data.ToArray();
                        if (s.Number == 3 && s.WireType == 2) name = s.String;
                    }
                    break;
                case 3: role = (int)f.UInt32; break;
            }
        }
        return new ChannelRecord(index, name, psk, role);
    }

    private static LoraConfig? ParseConfigLora(ReadOnlyMemory<byte> config)
    {
        foreach (var f in ProtoReader.Fields(config))
        {
            if (f.Number != 6 || f.WireType != 2) continue;
            bool usePreset = false;
            int preset = 0, bw = 0, sf = 0, cr = 0, region = 0;
            foreach (var l in ProtoReader.Fields(f.Data))
            {
                switch (l.Number)
                {
                    case 1: usePreset = l.Bool; break;
                    case 2: preset = (int)l.UInt32; break;
                    case 3: bw = (int)l.UInt32; break;
                    case 4: sf = (int)l.UInt32; break;
                    case 5: cr = (int)l.UInt32; break;
                    case 7: region = (int)l.UInt32; break;
                }
            }
            return new LoraConfig(usePreset, preset, bw, sf, cr, region);
        }
        return null;
    }

    private static MetadataMsg ParseMetadata(ReadOnlyMemory<byte> data)
    {
        string? fw = null;
        var hw = 0;
        foreach (var f in ProtoReader.Fields(data))
        {
            if (f.Number == 1 && f.WireType == 2) fw = f.String;
            if (f.Number == 9 && f.WireType == 0) hw = (int)f.UInt32;
        }
        return new MetadataMsg(fw, hw);
    }

    /// <summary>Routing.error_reason from a ROUTING_APP payload (0 = delivered / relayed).</summary>
    public static int RoutingError(ReadOnlyMemory<byte> payload)
    {
        foreach (var f in ProtoReader.Fields(payload))
            if (f.Number == 3 && f.WireType == 0) return (int)f.UInt32;
        return 0;
    }

    public static string RoutingErrorText(int code) => code switch
    {
        1 => "no route to that node",
        2 => "the recipient refused it (NAK)",
        3 => "timed out",
        4 => "no interface to send on",
        5 => "gave up after retries",
        6 => "no such channel on the node",
        7 => "too large",
        8 => "no response",
        9 => "duty-cycle limit reached; try later",
        32 => "bad request",
        33 => "not authorised",
        34 => "encryption with the recipient's key failed",
        35 => "the recipient's public key is unknown",
        38 => "rate limited by the node; try later",
        39 => "couldn't send with the recipient's public key",
        _ => $"routing error {code}",
    };

    // ------------------------------------------------------------ naming

    public static string NodeId(uint num) => $"!{num:x8}";

    public static bool TryParseNodeId(string id, out uint num)
    {
        num = 0;
        var s = id.Trim();
        if (s.StartsWith('!')) s = s[1..];
        return s.Length is > 0 and <= 8 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out num);
    }

    public static string PresetName(int preset) => preset switch
    {
        0 => "LongFast", 1 => "LongSlow", 2 => "VLongSlow", 3 => "MediumSlow", 4 => "MediumFast",
        5 => "ShortSlow", 6 => "ShortFast", 7 => "LongMod", 8 => "ShortTurbo", 9 => "LongTurbo",
        10 => "LiteFast", 11 => "LiteSlow", 12 => "NarrowFast", 13 => "NarrowSlow", 14 => "TinyFast",
        15 => "TinySlow", 16 => "MediumTurbo", _ => "Primary",
    };

    /// <summary>(SF, bandwidth Hz, CR denominator) for the common presets; null when unknown.</summary>
    public static (int Sf, int Bw, int Cr)? PresetRadio(int preset) => preset switch
    {
        0 => (11, 250_000, 5), 1 => (12, 125_000, 8), 3 => (10, 250_000, 5), 4 => (9, 250_000, 5),
        5 => (8, 250_000, 5), 6 => (7, 250_000, 5), 7 => (11, 125_000, 8), 8 => (7, 500_000, 5),
        9 => (11, 500_000, 8), 16 => (9, 500_000, 5),
        _ => null,
    };

    public static PeerKind KindOfRole(int role) => role switch
    {
        2 or 3 or 4 or 11 => PeerKind.Repeater,
        6 => PeerKind.Sensor,
        _ => PeerKind.Chat,
    };

    public static ChannelKind KindOfPsk(byte[] psk)
    {
        if (psk.Length <= 1) return ChannelKind.Public; // none, or the well-known default/simple keys
        return psk.AsSpan().SequenceEqual(DefaultKey) ? ChannelKind.Public : ChannelKind.Private;
    }
}

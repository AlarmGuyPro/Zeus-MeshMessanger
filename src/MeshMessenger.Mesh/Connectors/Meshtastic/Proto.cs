// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace MeshMessenger.Connectors.Meshtastic;

/// <summary>Minimal protobuf writer: only the wire types Meshtastic's messages need.</summary>
public sealed class ProtoWriter
{
    private readonly List<byte> _bytes = new(64);

    public byte[] ToArray() => _bytes.ToArray();

    public ProtoWriter Varint(int field, ulong value)
    {
        Tag(field, 0);
        WriteVarint(value);
        return this;
    }

    public ProtoWriter Bool(int field, bool value) => Varint(field, value ? 1UL : 0UL);

    public ProtoWriter Fixed32(int field, uint value)
    {
        Tag(field, 5);
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        _bytes.AddRange(b.ToArray());
        return this;
    }

    public ProtoWriter Bytes(int field, ReadOnlySpan<byte> value)
    {
        Tag(field, 2);
        WriteVarint((ulong)value.Length);
        _bytes.AddRange(value.ToArray());
        return this;
    }

    public ProtoWriter String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    public ProtoWriter Message(int field, ProtoWriter nested) => Bytes(field, nested.ToArray());

    private void Tag(int field, int wireType) => WriteVarint((ulong)((field << 3) | wireType));

    private void WriteVarint(ulong v)
    {
        while (v >= 0x80)
        {
            _bytes.Add((byte)(v | 0x80));
            v >>= 7;
        }
        _bytes.Add((byte)v);
    }
}

/// <summary>One decoded protobuf field.</summary>
public readonly record struct ProtoField(int Number, int WireType, ulong Varint, ReadOnlyMemory<byte> Data)
{
    public uint UInt32 => (uint)Varint;
    public int Int32 => (int)(long)Varint; // int32 negatives are sign-extended 10-byte varints
    public bool Bool => Varint != 0;
    public uint Fixed32 => (uint)Varint;
    public float Float => BitConverter.Int32BitsToSingle((int)(uint)Varint);
    public string String => Encoding.UTF8.GetString(Data.Span);
}

/// <summary>Minimal protobuf reader. Unknown fields are skipped; malformed input throws <see cref="FormatException"/>.</summary>
public static class ProtoReader
{
    public static IEnumerable<ProtoField> Fields(ReadOnlyMemory<byte> data)
    {
        var pos = 0;
        while (pos < data.Length)
        {
            var key = ReadVarint(data.Span, ref pos);
            var number = (int)(key >> 3);
            var wire = (int)(key & 7);
            if (number <= 0) throw new FormatException("bad field number");
            switch (wire)
            {
                case 0:
                    yield return new ProtoField(number, wire, ReadVarint(data.Span, ref pos), default);
                    break;
                case 1:
                    if (pos + 8 > data.Length) throw new FormatException("truncated fixed64");
                    yield return new ProtoField(number, wire, BinaryPrimitives.ReadUInt64LittleEndian(data.Span[pos..]), default);
                    pos += 8;
                    break;
                case 2:
                {
                    var len = ReadVarint(data.Span, ref pos);
                    if (len > (ulong)(data.Length - pos)) throw new FormatException("truncated length-delimited field");
                    yield return new ProtoField(number, wire, 0, data.Slice(pos, (int)len));
                    pos += (int)len;
                    break;
                }
                case 5:
                    if (pos + 4 > data.Length) throw new FormatException("truncated fixed32");
                    yield return new ProtoField(number, wire, BinaryPrimitives.ReadUInt32LittleEndian(data.Span[pos..]), default);
                    pos += 4;
                    break;
                default:
                    throw new FormatException($"unsupported wire type {wire}");
            }
        }
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> s, ref int pos)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (pos >= s.Length) throw new FormatException("truncated varint");
            var b = s[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new FormatException("varint too long");
    }
}

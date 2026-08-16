using System.Buffers.Binary;

namespace MockRdp.Util;

/// <summary>
/// Forward-only reader over a byte span. RDP mixes big-endian (TPKT/X.224) and
/// little-endian (RDP structures) fields, so both are exposed explicitly.
/// </summary>
public ref struct ByteReader(ReadOnlySpan<byte> buffer)
{
    private readonly ReadOnlySpan<byte> _buf = buffer;

    public int Position { get; private set; } = 0;
    public readonly int Remaining => _buf.Length - Position;

    public byte ReadUInt8()
    {
        Require(1);
        return _buf[Position++];
    }

    public ushort ReadUInt16BE()
    {
        Require(2);
        var v = BinaryPrimitives.ReadUInt16BigEndian(_buf.Slice(Position, 2));
        Position += 2;
        return v;
    }

    public ushort ReadUInt16LE()
    {
        Require(2);
        var v = BinaryPrimitives.ReadUInt16LittleEndian(_buf.Slice(Position, 2));
        Position += 2;
        return v;
    }

    public uint ReadUInt32LE()
    {
        Require(4);
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_buf.Slice(Position, 4));
        Position += 4;
        return v;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        Require(count);
        var slice = _buf.Slice(Position, count);
        Position += count;
        return slice;
    }

    public void Skip(int count)
    {
        Require(count);
        Position += count;
    }

    public readonly ReadOnlySpan<byte> PeekRemaining() => _buf[Position..];

    private readonly void Require(int count)
    {
        if (count < 0 || Remaining < count)
            throw new FormatException($"ByteReader underrun: need {count}, have {Remaining} at offset {Position}.");
    }
}

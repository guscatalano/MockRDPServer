using System.Buffers.Binary;

namespace MockRdp.Util;

/// <summary>
/// Growable byte buffer for assembling PDUs. Big- and little-endian writes are
/// explicit to match the mixed endianness of the RDP wire format.
/// </summary>
public sealed class ByteWriter(int capacity = 64)
{
    private byte[] _buf = new byte[Math.Max(capacity, 16)];
    private int _len;

    public int Length => _len;

    public void WriteUInt8(byte v)
    {
        Ensure(1);
        _buf[_len++] = v;
    }

    public void WriteUInt16BE(ushort v)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buf.AsSpan(_len, 2), v);
        _len += 2;
    }

    public void WriteUInt16LE(ushort v)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_len, 2), v);
        _len += 2;
    }

    public void WriteUInt32LE(uint v)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_len, 4), v);
        _len += 4;
    }

    public void WriteBytes(ReadOnlySpan<byte> v)
    {
        Ensure(v.Length);
        v.CopyTo(_buf.AsSpan(_len));
        _len += v.Length;
    }

    public ReadOnlySpan<byte> AsSpan() => _buf.AsSpan(0, _len);

    public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();

    private void Ensure(int extra)
    {
        if (_len + extra <= _buf.Length) return;
        int newCap = _buf.Length * 2;
        while (newCap < _len + extra) newCap *= 2;
        Array.Resize(ref _buf, newCap);
    }
}

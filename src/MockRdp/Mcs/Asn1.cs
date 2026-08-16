using MockRdp.Util;

namespace MockRdp.Mcs;

/// <summary>
/// Minimal BER and PER length helpers — just enough for the MCS (T.125, BER) and
/// GCC (T.124, ALIGNED PER) encodings RDP uses. Not a general ASN.1 codec.
/// </summary>
public static class Asn1
{
    /// <summary>Writes a BER definite-length field (short form, or long form for &gt;= 128).</summary>
    public static void WriteBerLength(ByteWriter w, int length)
    {
        if (length < 0x80)
        {
            w.WriteUInt8((byte)length);
        }
        else if (length < 0x100)
        {
            w.WriteUInt8(0x81);
            w.WriteUInt8((byte)length);
        }
        else
        {
            w.WriteUInt8(0x82);
            w.WriteUInt8((byte)(length >> 8));
            w.WriteUInt8((byte)length);
        }
    }

    /// <summary>Reads a BER definite length from <paramref name="data"/> starting at <paramref name="pos"/>.</summary>
    public static int ReadBerLength(ReadOnlySpan<byte> data, ref int pos)
    {
        byte first = data[pos++];
        if ((first & 0x80) == 0) return first;
        int n = first & 0x7F;
        int len = 0;
        for (int i = 0; i < n; i++) len = (len << 8) | data[pos++];
        return len;
    }

    /// <summary>Writes an ALIGNED PER length determinant (single byte, or two bytes for 128..16383).</summary>
    public static void WritePerLength(ByteWriter w, int length)
    {
        if (length < 0x80)
        {
            w.WriteUInt8((byte)length);
        }
        else
        {
            w.WriteUInt8((byte)(0x80 | (length >> 8)));
            w.WriteUInt8((byte)length);
        }
    }

    /// <summary>Reads an ALIGNED PER length determinant.</summary>
    public static int ReadPerLength(ReadOnlySpan<byte> data, ref int pos)
    {
        byte first = data[pos++];
        if ((first & 0x80) == 0) return first;
        return ((first & 0x7F) << 8) | data[pos++];
    }
}

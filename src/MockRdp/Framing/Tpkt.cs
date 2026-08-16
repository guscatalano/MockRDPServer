namespace MockRdp.Framing;

/// <summary>
/// TPKT framing (RFC 1006 / MS-RDPBCGR 2.2.1). A TPKT packet is a 4-byte header
/// { version=3, reserved=0, length[2] big-endian } followed by the X.224 TPDU.
/// The length field counts the whole packet including the 4-byte header.
/// </summary>
public static class Tpkt
{
    public const byte Version = 3;
    public const int HeaderLength = 4;
    public const int MaxLength = 0xFFFF;

    /// <summary>Reads the total packet length from a 4-byte TPKT header.</summary>
    public static int ReadLength(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderLength)
            throw new FormatException("TPKT header too short.");
        if (header[0] != Version)
            throw new FormatException($"Not a TPKT packet (first byte 0x{header[0]:X2}).");
        return (header[2] << 8) | header[3];
    }

    /// <summary>Wraps an X.224 payload in a TPKT header, returning the full packet.</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> payload)
    {
        int total = HeaderLength + payload.Length;
        if (total > MaxLength)
            throw new ArgumentException($"TPKT payload too large ({total} bytes).");
        var packet = new byte[total];
        packet[0] = Version;
        packet[1] = 0;
        packet[2] = (byte)(total >> 8);
        packet[3] = (byte)(total & 0xFF);
        payload.CopyTo(packet.AsSpan(HeaderLength));
        return packet;
    }
}

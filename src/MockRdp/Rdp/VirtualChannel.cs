using System.Buffers.Binary;
using MockRdp.Util;

namespace MockRdp.Rdp;

/// <summary>
/// Static virtual channel data framing (MS-RDPBCGR 2.2.6.1): a Channel PDU Header
/// (length + flags) prefixes the channel payload inside an MCS Send Data PDU.
/// </summary>
public static class VirtualChannel
{
    private const uint ChannelFlagFirst = 0x00000001;
    private const uint ChannelFlagLast = 0x00000002;

    /// <summary>Wraps channel payload in a single-chunk Channel PDU Header (FIRST | LAST).</summary>
    public static byte[] Wrap(ReadOnlySpan<byte> data)
    {
        var w = new ByteWriter();
        w.WriteUInt32LE((uint)data.Length);
        w.WriteUInt32LE(ChannelFlagFirst | ChannelFlagLast);
        w.WriteBytes(data);
        return w.ToArray();
    }

    /// <summary>Splits a payload larger than the negotiated VC chunk size into multiple Channel PDUs
    /// (FIRST … LAST), each carrying the total length — required for large clipboard file transfers.
    /// Small payloads yield a single FIRST|LAST chunk.</summary>
    public static IEnumerable<byte[]> WrapChunked(byte[] data, int chunkSize = 1590)
    {
        if (data.Length <= chunkSize) { yield return Wrap(data); yield break; }
        for (int off = 0; off < data.Length; off += chunkSize)
        {
            int len = Math.Min(chunkSize, data.Length - off);
            uint flags = 0;
            if (off == 0) flags |= ChannelFlagFirst;
            if (off + len >= data.Length) flags |= ChannelFlagLast;
            var w = new ByteWriter();
            w.WriteUInt32LE((uint)data.Length);   // total (uncompressed) length, in every chunk header
            w.WriteUInt32LE(flags);
            w.WriteBytes(data.AsSpan(off, len));
            yield return w.ToArray();
        }
    }

    /// <summary>Strips the 8-byte Channel PDU Header, returning the channel payload.</summary>
    public static ReadOnlySpan<byte> Unwrap(ReadOnlySpan<byte> channelPdu) =>
        channelPdu.Length >= 8 ? channelPdu[8..] : default;

    /// <summary>Reads the declared uncompressed length from a Channel PDU Header.</summary>
    public static uint DataLength(ReadOnlySpan<byte> channelPdu) =>
        channelPdu.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(channelPdu[..4]) : 0;
}

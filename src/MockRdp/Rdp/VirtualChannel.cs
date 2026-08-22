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

    /// <summary>Strips the 8-byte Channel PDU Header, returning the channel payload.</summary>
    public static ReadOnlySpan<byte> Unwrap(ReadOnlySpan<byte> channelPdu) =>
        channelPdu.Length >= 8 ? channelPdu[8..] : default;

    /// <summary>Reads the declared uncompressed length from a Channel PDU Header.</summary>
    public static uint DataLength(ReadOnlySpan<byte> channelPdu) =>
        channelPdu.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(channelPdu[..4]) : 0;
}

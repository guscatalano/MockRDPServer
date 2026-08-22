using System.Buffers.Binary;
using MockRdp.Util;

namespace MockRdp.Rdp;

/// <summary>
/// The Share Control Header (MS-RDPBCGR 2.2.8.1.1.1.1) that prefixes the "slow-path"
/// activation and data PDUs, plus its PDU-type constants.
/// </summary>
public static class ShareControl
{
    // pduType field = PDUTYPE | (TS_PROTOCOL_VERSION(1) << 4).
    public const ushort DemandActive = 0x11;
    public const ushort ConfirmActive = 0x13;
    public const ushort Deactivate = 0x16;
    public const ushort Data = 0x17;

    public static byte[] Wrap(ushort pduType, ushort pduSource, ReadOnlySpan<byte> body)
    {
        var w = new ByteWriter();
        w.WriteUInt16LE((ushort)(6 + body.Length)); // totalLength
        w.WriteUInt16LE(pduType);
        w.WriteUInt16LE(pduSource);
        w.WriteBytes(body);
        return w.ToArray();
    }

    /// <summary>Returns the low 4 bits (PDUTYPE) of a share control header's pduType field.</summary>
    public static int PduType(ReadOnlySpan<byte> shareControlPdu) =>
        BinaryPrimitives.ReadUInt16LittleEndian(shareControlPdu.Slice(2, 2)) & 0x0F;
}

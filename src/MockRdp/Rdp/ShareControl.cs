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

    /// <summary>Wraps type-specific data in a Share Data Header + Share Control Header (a Data PDU).</summary>
    public static byte[] BuildDataPdu(uint shareId, ushort pduSource, byte pduType2, ReadOnlySpan<byte> data)
    {
        var body = new ByteWriter();
        int total = 6 + 12 + data.Length; // share control (6) + share data header (12) + data
        body.WriteUInt32LE(shareId);
        body.WriteUInt8(0);                  // pad1
        body.WriteUInt8(1);                  // streamId = STREAM_LOW
        body.WriteUInt16LE((ushort)total);   // uncompressedLength
        body.WriteUInt8(pduType2);
        body.WriteUInt8(0);                  // compressedType
        body.WriteUInt16LE(0);               // compressedLength
        body.WriteBytes(data);
        return Wrap(Data, pduSource, body.AsSpan());
    }
}

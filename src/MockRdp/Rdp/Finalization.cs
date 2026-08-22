using MockRdp.Mcs;
using MockRdp.Util;

namespace MockRdp.Rdp;

/// <summary>
/// Connection finalization (MS-RDPBCGR 1.3.1.1 / 2.2.1.15–2.2.1.19): the Synchronize,
/// Control, and Font Map PDUs the server sends to complete activation, plus the Share Data
/// Header used to carry them and a reader for the client's data-PDU sub-type.
/// </summary>
public static class Finalization
{
    // PDUTYPE2 values carried in the Share Data Header.
    public const byte Pdu2Update = 2;
    public const byte Pdu2Control = 20;
    public const byte Pdu2Input = 28;
    public const byte Pdu2Synchronize = 31;
    public const byte Pdu2FontList = 39;
    public const byte Pdu2FontMap = 40;

    private const ushort CtrlActionCooperate = 0x0004;
    private const ushort CtrlActionGrantedControl = 0x0002;

    /// <summary>Wraps type-specific data in a server Data PDU (Share Data Header + Share Control Header).</summary>
    public static byte[] BuildDataPdu(byte pduType2, ReadOnlySpan<byte> data) =>
        ShareControl.BuildDataPdu(Capabilities.ShareId, Gcc.ServerChannelId, pduType2, data);

    public static byte[] BuildSynchronize(ushort targetUser)
    {
        var d = new ByteWriter();
        d.WriteUInt16LE(1);           // messageType = SYNCMSGTYPE_SYNC
        d.WriteUInt16LE(targetUser);
        return BuildDataPdu(Pdu2Synchronize, d.AsSpan());
    }

    public static byte[] BuildControlCooperate()
    {
        var d = new ByteWriter();
        d.WriteUInt16LE(CtrlActionCooperate);
        d.WriteUInt16LE(0);   // grantId
        d.WriteUInt32LE(0);   // controlId
        return BuildDataPdu(Pdu2Control, d.AsSpan());
    }

    public static byte[] BuildControlGranted(ushort clientUser)
    {
        var d = new ByteWriter();
        d.WriteUInt16LE(CtrlActionGrantedControl);
        d.WriteUInt16LE(clientUser);            // grantId
        d.WriteUInt32LE(Gcc.ServerChannelId);   // controlId
        return BuildDataPdu(Pdu2Control, d.AsSpan());
    }

    public static byte[] BuildFontMap()
    {
        var d = new ByteWriter();
        d.WriteUInt16LE(0);        // numberEntries
        d.WriteUInt16LE(0);        // totalNumEntries
        d.WriteUInt16LE(0x0003);   // mapFlags = FONTLIST_FIRST | FONTLIST_LAST
        d.WriteUInt16LE(4);        // entrySize
        return BuildDataPdu(Pdu2FontMap, d.AsSpan());
    }

    /// <summary>Reads the PDUTYPE2 sub-type from a client Data PDU (share control payload), or -1.</summary>
    public static int DataPduType2(ReadOnlySpan<byte> shareControlPdu) =>
        shareControlPdu.Length > 14 ? shareControlPdu[14] : -1;
}

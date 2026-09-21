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
    public const byte Pdu2SaveSessionInfo = 38;
    public const byte Pdu2ShutdownRequest = 36;   // PDUTYPE2_SHUTDOWN_REQUEST (client asks to log off)
    public const byte Pdu2ShutdownDenied = 37;    // PDUTYPE2_SHUTDOWN_DENIED (server refuses → client confirms)

    private const uint InfoTypeLogon = 0x00000000;   // INFOTYPE_LOGON -> TS_LOGON_INFO

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

    /// <summary>
    /// Server Save Session Info PDU (MS-RDPBCGR 2.2.10.1): the "logon PDU" the server sends
    /// once the user has logged on, notifying the client of the domain/user/session. We emit the
    /// simplest fixed-size variant — INFOTYPE_LOGON + TS_LOGON_INFO (2.2.10.1.1.1, 576 bytes).
    /// </summary>
    public static byte[] BuildSaveSessionInfo(string domain, string userName, uint sessionId)
    {
        // Fixed-length, NUL-terminated UTF-16LE, zero-padded field. Returns cb (bytes, excluding NUL).
        static byte[] FixedUnicode(string s, int fixedBytes, out uint cb)
        {
            var buf = new byte[fixedBytes];
            int max = (fixedBytes / 2) - 1;                     // leave room for the NUL terminator
            var bytes = System.Text.Encoding.Unicode.GetBytes(s.Length > max ? s[..max] : s);
            Array.Copy(bytes, buf, bytes.Length);
            cb = (uint)bytes.Length;
            return buf;
        }

        var domainBuf = FixedUnicode(domain, 52, out uint cbDomain);
        var userBuf = FixedUnicode(userName, 512, out uint cbUser);

        var d = new ByteWriter();
        d.WriteUInt32LE(InfoTypeLogon);   // infoType
        // TS_LOGON_INFO (2.2.10.1.1.1)
        d.WriteUInt32LE(cbDomain);
        d.WriteBytes(domainBuf);          // Domain[52]
        d.WriteUInt32LE(cbUser);
        d.WriteBytes(userBuf);            // UserName[512]
        d.WriteUInt32LE(sessionId);
        return BuildDataPdu(Pdu2SaveSessionInfo, d.AsSpan());
    }

    /// <summary>Reads the PDUTYPE2 sub-type from a client Data PDU (share control payload), or -1.</summary>
    public static int DataPduType2(ReadOnlySpan<byte> shareControlPdu) =>
        shareControlPdu.Length > 14 ? shareControlPdu[14] : -1;
}

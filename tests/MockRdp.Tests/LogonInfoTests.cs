using System.Buffers.Binary;
using System.Text;
using MockRdp.Rdp;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// Wire-format conformance for the Server Save Session Info ("logon") PDU
/// (MS-RDPBCGR 2.2.10.1 / TS_LOGON_INFO 2.2.10.1.1.1) the mock emits after sign-in.
/// </summary>
public class LogonInfoTests
{
    // Data body starts after the Share Control header (6) + Share Data header (12).
    private const int Body = 18;

    [Fact]
    public void SaveSessionInfo_HasLogonPduTypeAndTsLogonInfoLayout()
    {
        var pdu = Finalization.BuildSaveSessionInfo("MOCK", "alice", sessionId: 7);

        // PDUTYPE2_SAVE_SESSION_INFO (38) in the Share Data header.
        Assert.Equal(Finalization.Pdu2SaveSessionInfo, (byte)Finalization.DataPduType2(pdu));
        Assert.Equal(38, Finalization.DataPduType2(pdu));

        // infoType == INFOTYPE_LOGON (0) → TS_LOGON_INFO follows.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(pdu.AsSpan(Body, 4)));

        // TS_LOGON_INFO: cbDomain, Domain[52], cbUserName, UserName[512], SessionId.
        int p = Body + 4;
        uint cbDomain = BinaryPrimitives.ReadUInt32LittleEndian(pdu.AsSpan(p, 4)); p += 4;
        Assert.Equal((uint)Encoding.Unicode.GetByteCount("MOCK"), cbDomain);
        Assert.Equal("MOCK", Encoding.Unicode.GetString(pdu, p, (int)cbDomain));
        p += 52;

        uint cbUser = BinaryPrimitives.ReadUInt32LittleEndian(pdu.AsSpan(p, 4)); p += 4;
        Assert.Equal((uint)Encoding.Unicode.GetByteCount("alice"), cbUser);
        Assert.Equal("alice", Encoding.Unicode.GetString(pdu, p, (int)cbUser));
        p += 512;

        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(pdu.AsSpan(p, 4)));
    }

    [Fact]
    public void SaveSessionInfo_TruncatesOverlongUserNameAndStaysNulTerminated()
    {
        var longUser = new string('x', 400);   // 800 bytes UTF-16 > 512-byte fixed field
        var pdu = Finalization.BuildSaveSessionInfo("", longUser, sessionId: 1);

        int userCountOff = Body + 4 + 4 + 52;
        uint cbUser = BinaryPrimitives.ReadUInt32LittleEndian(pdu.AsSpan(userCountOff, 4));
        Assert.True(cbUser <= 510, "cbUserName must leave room for the NUL terminator in the 512-byte field.");

        // Final two bytes of the fixed UserName field are the NUL terminator.
        int userField = userCountOff + 4;
        Assert.Equal(0, pdu[userField + 510]);
        Assert.Equal(0, pdu[userField + 511]);
    }
}

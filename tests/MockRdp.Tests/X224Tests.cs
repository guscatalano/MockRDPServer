using MockRdp.X224;
using Xunit;

namespace MockRdp.Tests;

public class X224Tests
{
    // MS-RDPBCGR 4.1.1 Client X.224 Connection Request PDU (the TPDU after the 4-byte TPKT header).
    private static readonly byte[] SampleCrTpdu = Hex(
        "27 e0 00 00 00 00 00 " +
        "43 6f 6f 6b 69 65 3a 20 6d 73 74 73 68 61 73 68 3d 65 6c 74 6f 6e 73 0d 0a " + // "Cookie: mstshash=eltons\r\n"
        "01 00 08 00 03 00 00 00"); // rdpNegReq: SSL | HYBRID

    [Fact]
    public void ParseConnectionRequest_ReadsCookieAndProtocols()
    {
        var cr = Cotp.ParseConnectionRequest(SampleCrTpdu);

        Assert.Equal("Cookie: mstshash=eltons", cr.Cookie);
        Assert.True(cr.HasNegReq);
        Assert.Equal(RdpNegProtocol.Ssl | RdpNegProtocol.Hybrid, cr.RequestedProtocols);
    }

    [Fact]
    public void ParseConnectionRequest_WithoutCookie_StillReadsNegReq()
    {
        var tpdu = Hex("0e e0 00 00 00 00 00 01 00 08 00 01 00 00 00"); // LI=14 (6 fixed + 8 negReq), no cookie, SSL only
        var cr = Cotp.ParseConnectionRequest(tpdu);

        Assert.Null(cr.Cookie);
        Assert.True(cr.HasNegReq);
        Assert.Equal(RdpNegProtocol.Ssl, cr.RequestedProtocols);
    }

    [Fact]
    public void BuildConnectionConfirm_MatchesExpectedBytes()
    {
        var cc = Cotp.BuildConnectionConfirm(RdpNegProtocol.Ssl);
        // TPKT(4) + X.224 CC header(7) + rdpNegRsp(8): selectedProtocol = PROTOCOL_SSL.
        var expected = Hex("03 00 00 13 0e d0 00 00 00 00 00 02 00 08 00 01 00 00 00");
        Assert.Equal(expected, cc);
    }

    [Fact]
    public void BuildConnectionConfirmFailure_MatchesExpectedBytes()
    {
        var cc = Cotp.BuildConnectionConfirmFailure(RdpNegFailureCode.SslRequiredByServer);
        var expected = Hex("03 00 00 13 0e d0 00 00 00 00 00 03 00 08 00 01 00 00 00");
        Assert.Equal(expected, cc);
    }

    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));
}

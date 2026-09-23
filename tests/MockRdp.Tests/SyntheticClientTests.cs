using System.Buffers.Binary;
using MockRdp.Mcs;
using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using MockRdp.X224;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// End-to-end M1 checks: the in-process conformance client performs X.224 negotiation and
/// the TLS handshake against a real listener. Verifies the whole M1 path without needing mstsc.
/// </summary>
public class SyntheticClientTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    [Fact]
    public async Task Client_NegotiatesSsl_AndCompletesTlsHandshake()
    {
        using var server = new MockServerFixture();
        await using var client = new RdpTestClient();
        var ct = Timeout;

        await client.ConnectAsync(server.Endpoint, ct);
        await client.SendConnectionRequestAsync(RdpNegProtocol.Ssl | RdpNegProtocol.Hybrid, "Cookie: mstshash=test", ct);

        var neg = await client.ReadConnectionConfirmAsync(ct);
        Assert.False(neg.IsFailure);
        Assert.Equal(RdpNegProtocol.Ssl, neg.SelectedProtocol);

        await client.UpgradeToTlsAsync(ct: ct);
        // Reaching here means AuthenticateAsClientAsync succeeded — TLS is up. M1 verified.
    }

    [Fact]
    public async Task Client_WithoutSsl_FallsBackToStandardRdpSecurity()
    {
        // A client that does not offer TLS (a policy-locked mstsc, or FreeRDP `/sec:rdp`) is
        // accepted onto the Standard RDP Security layer: the server selects PROTOCOL_RDP.
        using var server = new MockServerFixture();
        await using var client = new RdpTestClient();
        var ct = Timeout;

        await client.ConnectAsync(server.Endpoint, ct);
        await client.SendConnectionRequestAsync(RdpNegProtocol.Rdp, ct: ct);

        var neg = await client.ReadConnectionConfirmAsync(ct);
        Assert.False(neg.IsFailure);
        Assert.Equal(RdpNegProtocol.Rdp, neg.SelectedProtocol);
    }

    [Fact]
    public async Task Client_WithoutSsl_IsRejected_WhenStandardRdpSecurityDisabled()
    {
        // TLS-only posture: with Standard RDP Security turned off, a client that does not offer
        // TLS is refused with SSL_REQUIRED_BY_SERVER (the original M1 behavior).
        using var server = new MockServerFixture(allowStandardRdpSecurity: false);
        await using var client = new RdpTestClient();
        var ct = Timeout;

        await client.ConnectAsync(server.Endpoint, ct);
        await client.SendConnectionRequestAsync(RdpNegProtocol.Rdp, ct: ct);

        var neg = await client.ReadConnectionConfirmAsync(ct);
        Assert.True(neg.IsFailure);
        Assert.Equal(RdpNegFailureCode.SslRequiredByServer, neg.FailureCode);
    }

    [Fact]
    public async Task StandardRdpSecurity_RunsAFullClearTextSession()
    {
        // b0: PROTOCOL_RDP with encryption NONE — no TLS, no Security Exchange. The full MCS
        // connect, channel join, licensing and Demand Active all run over the raw stream.
        using var server = new MockServerFixture();
        await using var client = new RdpTestClient();
        var ct = Timeout;

        ushort user = await McsClient.NegotiateThroughChannelJoinAsync(
            client, server.Endpoint, ["cliprdr", "drdynvc"], ct, useTls: false);

        await client.WriteRawAsync(McsClient.BuildClientInfo(user), ct);

        var licensing = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        Assert.Equal(0x0080, BinaryPrimitives.ReadUInt16LittleEndian(licensing.Payload)); // SEC_LICENSE_PKT

        var demandActive = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        Assert.Equal(ShareControl.DemandActive & 0x0F, ShareControl.PduType(demandActive.Payload));
    }
}

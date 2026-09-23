using System.Buffers.Binary;
using MockRdp.Mcs;
using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using MockRdp.X224;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// RDSTLS security protocol (MS-RDPBCGR 2.2.17): PROTOCOL_RDSTLS negotiation, the TLS upgrade, and the
/// Capabilities → Authentication Request → Authentication Response exchange, then MCS + activation.
/// FreeRDP can't drive RDSTLS as a client, so this synthetic client validates the server end-to-end.
/// </summary>
public class RdstlsTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    [Fact]
    public async Task Codec_RoundTripsPasswordAuthRequest()
    {
        var bytes = Rdstls.BuildPasswordAuthRequest("{3c2504f0-...}", "alice", "CONTOSO", "s3cret", "host");
        using var ms = new MemoryStream(bytes);
        var req = await Rdstls.ReadAuthRequestAsync(ms, CancellationToken.None);

        Assert.NotNull(req);
        Assert.Equal(Rdstls.RequestKind.PasswordCredentials, req!.Kind);
        Assert.Equal("alice", req.UserName);
        Assert.Equal("CONTOSO", req.Domain);
    }

    [Fact]
    public async Task Client_NegotiatesRdstls_CompletesAuth_ThenActivates()
    {
        using var server = new MockServerFixture(desktop: true);
        await using var client = new RdpTestClient();
        var ct = Timeout;

        await client.ConnectAsync(server.Endpoint, ct);
        await client.SendConnectionRequestAsync(RdpNegProtocol.RdsTls, ct: ct);
        var neg = await client.ReadConnectionConfirmAsync(ct);
        Assert.False(neg.IsFailure);
        Assert.Equal(RdpNegProtocol.RdsTls, neg.SelectedProtocol);

        await client.UpgradeToTlsAsync(ct: ct);

        // Server → RDSTLS Capabilities PDU.
        var caps = new byte[6];
        await client.Stream.ReadExactlyAsync(caps, ct);
        Assert.Equal(Rdstls.Version1, BinaryPrimitives.ReadUInt16LittleEndian(caps));
        Assert.Equal(Rdstls.TypeCapabilities, BinaryPrimitives.ReadUInt16LittleEndian(caps.AsSpan(2)));

        // Client → RDSTLS Authentication Request (password credentials).
        await client.WriteRawAsync(Rdstls.BuildPasswordAuthRequest("", "rdsuser", "DOM", "pw", "host"), ct);

        // Server → RDSTLS Authentication Response (success).
        var resp = new byte[8];
        await client.Stream.ReadExactlyAsync(resp, ct);
        Assert.Equal(Rdstls.TypeAuthResp, BinaryPrimitives.ReadUInt16LittleEndian(resp.AsSpan(2)));
        Assert.Equal(Rdstls.ResultSuccess, BinaryPrimitives.ReadUInt32LittleEndian(resp.AsSpan(4)));

        // The normal MCS connect + activation then runs over the same TLS channel (no RDP-level RC4).
        ushort user = await McsClient.McsConnectAndJoinAsync(client, ["cliprdr", "drdynvc"], ct);
        await client.WriteRawAsync(McsClient.BuildClientInfo(user), ct);

        var licensing = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        Assert.Equal(0x0080, BinaryPrimitives.ReadUInt16LittleEndian(licensing.Payload)); // SEC_LICENSE_PKT

        var demandActive = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        Assert.Equal(ShareControl.DemandActive & 0x0F, ShareControl.PduType(demandActive.Payload));
    }
}

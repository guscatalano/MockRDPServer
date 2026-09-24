using System.Buffers.Binary;
using MockRdp.Mcs;
using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using MockRdp.X224;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// RDS-AAD / Microsoft Entra auth (PROTOCOL_RDSAAD, MS-RDPBCGR 2.2.18). A real client only produces
/// an assertion after acquiring a token from Entra (browser/webview), so a synthetic client drives the
/// JSON exchange here: X.224 selects RDSAAD, TLS, the server's nonce → an rdp_assertion carrying a
/// (fake) access token → S_OK, then MCS + activation. The mock accepts the token without verifying it.
/// </summary>
public class AadTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    [Fact]
    public async Task RdsAad_SelectsProtocol_AcceptsAssertion_ThenActivates()
    {
        using var server = new MockServerFixture(desktop: true, enableRdsAad: true);
        await using var client = new RdpTestClient();
        var ct = Timeout;

        await client.ConnectAsync(server.Endpoint, ct);
        await client.SendConnectionRequestAsync(
            RdpNegProtocol.Ssl | RdpNegProtocol.RdsAad, "Cookie: mstshash=aad", ct);
        var neg = await client.ReadConnectionConfirmAsync(ct);
        Assert.False(neg.IsFailure);
        Assert.Equal(RdpNegProtocol.RdsAad, neg.SelectedProtocol);

        await client.UpgradeToTlsAsync(ct: ct);
        await AadClient.AuthenticateAsync(client, "rdsuser@contoso.com", ct);

        // Entra exchange done → the normal MCS connect + activation runs over the same TLS channel.
        ushort user = await McsClient.McsConnectAndJoinAsync(client, ["cliprdr", "drdynvc"], ct);
        await client.WriteRawAsync(McsClient.BuildClientInfo(user), ct);

        var licensing = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        Assert.Equal(0x0080, BinaryPrimitives.ReadUInt16LittleEndian(licensing.Payload)); // SEC_LICENSE_PKT

        var demand = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        Assert.Equal(ShareControl.DemandActive & 0x0F, ShareControl.PduType(demand.Payload));
    }
}

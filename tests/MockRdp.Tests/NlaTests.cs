using System.Buffers.Binary;
using MockRdp.Mcs;
using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using MockRdp.X224;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// NLA / CredSSP (PROTOCOL_HYBRID). FreeRDP/mstsc can't do NLA against a mock without valid host
/// credentials, so a synthetic client drives the exchange over SSPI (the current logon session,
/// loopback): X.224 selects HYBRID, TLS, the CredSSP TSRequest handshake with the public-key channel
/// binding, then MCS + activation. Skips gracefully where SSPI loopback isn't available (e.g. CI with
/// no interactive logon).
/// </summary>
public class NlaTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task Nla_SelectsHybrid_CompletesCredSsp_ThenActivates()
    {
        using var server = new MockServerFixture(desktop: true, enableNla: true);
        await using var client = new RdpTestClient();
        var ct = Timeout;

        await client.ConnectAsync(server.Endpoint, ct);
        await client.SendConnectionRequestAsync(
            RdpNegProtocol.Ssl | RdpNegProtocol.Hybrid, "Cookie: mstshash=test", ct);
        var neg = await client.ReadConnectionConfirmAsync(ct);
        Assert.False(neg.IsFailure);
        Assert.Equal(RdpNegProtocol.Hybrid, neg.SelectedProtocol);

        await client.UpgradeToTlsAsync(ct: ct);

        if (!await NlaClient.TryAuthenticateAsync(client, ct))
            return;   // SSPI loopback unavailable in this environment — nothing to assert.

        // CredSSP done → the normal MCS connect + activation runs over the same TLS channel.
        ushort user = await McsClient.McsConnectAndJoinAsync(client, ["cliprdr", "drdynvc"], ct);
        await client.WriteRawAsync(McsClient.BuildClientInfo(user), ct);

        var licensing = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        Assert.Equal(0x0080, BinaryPrimitives.ReadUInt16LittleEndian(licensing.Payload)); // SEC_LICENSE_PKT

        var demand = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        Assert.Equal(ShareControl.DemandActive & 0x0F, ShareControl.PduType(demand.Payload));
    }
}

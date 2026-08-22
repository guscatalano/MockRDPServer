using System.Buffers.Binary;
using MockRdp.Mcs;
using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using MockRdp.X224;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// Tier 1 conformance for M3: an in-process client completes licensing, capability exchange,
/// and finalization against the server, ending at an active session.
/// </summary>
public class ActivationConformanceTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;
    private static readonly string[] Channels = ["rdpdr", "rdpsnd", "cliprdr", "drdynvc"];

    [Fact]
    public async Task Client_CompletesLicensingCapabilitiesAndFinalization()
    {
        using var server = new MockServerFixture();
        await using var client = new RdpTestClient();
        var ct = Timeout;

        ushort user = await McsClient.NegotiateThroughChannelJoinAsync(client, server.Endpoint, Channels, ct);

        // Client Info → server replies with licensing, then Demand Active.
        await client.WriteRawAsync(McsClient.BuildClientInfo(user), ct);

        var licensing = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        Assert.Equal(0x0080, BinaryPrimitives.ReadUInt16LittleEndian(licensing.Payload)); // SEC_LICENSE_PKT

        var demandActive = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        Assert.Equal(ShareControl.DemandActive & 0x0F, ShareControl.PduType(demandActive.Payload));

        // Confirm Active + Font List → server sends its four finalization PDUs.
        await client.WriteRawAsync(McsClient.BuildConfirmActive(user, Capabilities.ShareId), ct);
        await client.WriteRawAsync(McsClient.BuildFontList(user), ct);

        var finalizationType2 = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            var pdu = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
            finalizationType2.Add(Finalization.DataPduType2(pdu.Payload));
        }

        Assert.Contains(Finalization.Pdu2Synchronize, finalizationType2);
        Assert.Contains(Finalization.Pdu2Control, finalizationType2);
        Assert.Contains(Finalization.Pdu2FontMap, finalizationType2);
    }
}

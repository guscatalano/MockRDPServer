using MockRdp.Mcs;
using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using MockRdp.X224;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// A client Shutdown Request (MS-RDPBCGR 2.2.2.2, sent when mstsc's window is closed) must be
/// answered so the client doesn't wait and get closed twice. The mock denies it (2.2.2.3), which
/// makes mstsc show its disconnect-confirmation dialog instead of hanging.
/// </summary>
public class ShutdownTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;
    private static readonly string[] Channels = ["rdpdr", "rdpsnd", "cliprdr", "drdynvc"];

    [Fact]
    public async Task ShutdownRequest_IsAnsweredWithShutdownDenied()
    {
        using var server = new MockServerFixture();
        await using var client = new RdpTestClient();
        var ct = Timeout;

        ushort user = await McsClient.NegotiateThroughChannelJoinAsync(client, server.Endpoint, Channels, ct);
        await McsClient.ActivateAsync(client, user, ct);

        // The client closes: Shutdown Request PDU (empty body) on the I/O channel.
        var shutdown = ShareControl.BuildDataPdu(Capabilities.ShareId, user, Finalization.Pdu2ShutdownRequest, default);
        await client.WriteRawAsync(McsClient.SendDataRequest(user, Gcc.IoChannelId, shutdown), ct);

        // The server replies on the I/O channel with a Shutdown Request Denied Data PDU.
        var reply = await ReadIoDataAsync(client, ct);
        Assert.Equal(Finalization.Pdu2ShutdownDenied, (byte)Finalization.DataPduType2(reply));
    }

    private static async Task<byte[]> ReadIoDataAsync(RdpTestClient client, CancellationToken ct)
    {
        while (true)
        {
            var (channelId, data) = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
            if (channelId == Gcc.IoChannelId
                && ShareControl.PduType(data) == (ShareControl.Data & 0x0F)
                && Finalization.DataPduType2(data) == Finalization.Pdu2ShutdownDenied)
                return data;
        }
    }
}

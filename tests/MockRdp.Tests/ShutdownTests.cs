using MockRdp.Mcs;
using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using MockRdp.X224;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// A client Shutdown Request (MS-RDPBCGR 2.2.2.2, sent when mstsc's window is closed) must be
/// answered by the server tearing the connection down, so the client closes in one step rather
/// than waiting and being closed twice.
/// </summary>
public class ShutdownTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;
    private static readonly string[] Channels = ["rdpdr", "rdpsnd", "cliprdr", "drdynvc"];

    [Fact]
    public async Task ShutdownRequest_IsAnsweredWithDisconnectProviderUltimatum()
    {
        using var server = new MockServerFixture();
        await using var client = new RdpTestClient();
        var ct = Timeout;

        ushort user = await McsClient.NegotiateThroughChannelJoinAsync(client, server.Endpoint, Channels, ct);
        await McsClient.ActivateAsync(client, user, ct);

        // The client closes: Shutdown Request PDU (empty body) on the I/O channel.
        var shutdown = ShareControl.BuildDataPdu(Capabilities.ShareId, user, Finalization.Pdu2ShutdownRequest, default);
        await client.WriteRawAsync(McsClient.SendDataRequest(user, Gcc.IoChannelId, shutdown), ct);

        // The server sends an MCS Disconnect Provider Ultimatum (choice 8 → 0x21 0x80) and closes.
        Assert.True(await ReceivesDisconnectAsync(client, ct));
    }

    private static async Task<bool> ReceivesDisconnectAsync(RdpTestClient client, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var payload = Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)).ToArray();
                if (payload.Length == 2 && payload[0] == 0x21 && payload[1] == 0x80)
                    return true;   // Disconnect Provider Ultimatum
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;          // connection closed without the ultimatum
        }
    }
}

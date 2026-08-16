using MockRdp.Tests.Harness;
using MockRdp.X224;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// Tier 1 conformance for M2: an in-process client walks the full MCS sequence
/// (Connect-Initial/Response, Erect Domain, Attach User, Channel Join) against the server.
/// </summary>
public class McsConformanceTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    private static readonly string[] Channels = ["rdpdr", "rdpsnd", "cliprdr", "drdynvc"];

    [Fact]
    public async Task Client_CompletesMcsConnectAndChannelJoin()
    {
        using var server = new MockServerFixture();
        await using var client = new RdpTestClient();
        var ct = Timeout;

        await client.ConnectAsync(server.Endpoint, ct);
        await client.SendConnectionRequestAsync(RdpNegProtocol.Ssl, ct: ct);
        await client.ReadConnectionConfirmAsync(ct);
        await client.UpgradeToTlsAsync(ct: ct);

        // MCS Connect-Initial → Connect-Response.
        await client.WriteRawAsync(McsClient.BuildConnectInitial(Channels), ct);
        var (ioChannel, channelIds) = McsClient.ParseConnectResponseNetwork(await client.ReadTpktPayloadAsync(ct));

        Assert.Equal(1003, ioChannel);
        Assert.Equal(new ushort[] { 1004, 1005, 1006, 1007 }, channelIds);

        // Erect Domain (no reply), then Attach User → Confirm.
        await client.WriteRawAsync(McsClient.ErectDomainRequest(), ct);
        await client.WriteRawAsync(McsClient.AttachUserRequest(), ct);
        ushort userChannel = McsClient.ParseAttachUserConfirm(await client.ReadTpktPayloadAsync(ct));
        Assert.True(userChannel >= 1001);

        // Join the user channel, the I/O channel, and every virtual channel.
        ushort[] toJoin = [userChannel, ioChannel, .. channelIds];
        foreach (var channel in toJoin)
        {
            await client.WriteRawAsync(McsClient.ChannelJoinRequest(userChannel, channel), ct);
            ushort granted = McsClient.ParseChannelJoinConfirm(await client.ReadTpktPayloadAsync(ct));
            Assert.Equal(channel, granted);
        }
    }
}

using System.Text;
using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// Tier 1 conformance for the dynamic virtual channel layer (MS-RDPEDYC / DRDYNVC): the
/// server advertises capabilities over "drdynvc", opens the configured channel once the
/// client answers, and echoes data back on it.
/// </summary>
public class DvcConformanceTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    // drdynvc is the 4th requested channel → id 1004 + 3 = 1007.
    private static readonly string[] Channels = ["rdpdr", "rdpsnd", "cliprdr", "drdynvc"];
    private const ushort DvcChannel = 1007;

    [Fact]
    public async Task Dvc_Capabilities_Create_And_Echo()
    {
        using var server = new MockServerFixture(["ECHO"]);
        await using var client = new RdpTestClient();
        var ct = Timeout;

        ushort user = await McsClient.NegotiateThroughChannelJoinAsync(client, server.Endpoint, Channels, ct);
        await McsClient.ActivateAsync(client, user, ct);

        // 1. Server opens the DVC layer with a capabilities advertisement.
        var caps = Dvc.Parse(await McsClient.ReadDvcAsync(client, DvcChannel, ct));
        Assert.Equal(Dvc.Cmd.Capabilities, caps.Cmd);
        Assert.Equal(1, caps.Version);

        // 2. Client answers with its capabilities; the server then creates the ECHO channel.
        await McsClient.SendDvcAsync(client, user, DvcChannel, Dvc.BuildCapabilities(1), ct);
        var create = Dvc.Parse(await McsClient.ReadDvcAsync(client, DvcChannel, ct));
        Assert.Equal(Dvc.Cmd.Create, create.Cmd);
        Assert.Equal("ECHO", Dvc.ChannelName(create));

        // 3. Client accepts the channel.
        await McsClient.SendDvcAsync(client, user, DvcChannel, Dvc.BuildCreateResponse(create.ChannelId, 0), ct);

        // 4. Data sent on the channel comes straight back.
        var payload = Encoding.ASCII.GetBytes("ping over dvc");
        foreach (var pdu in Dvc.BuildData(create.ChannelId, payload))
            await McsClient.SendDvcAsync(client, user, DvcChannel, pdu, ct);

        var echoed = Dvc.Parse(await McsClient.ReadDvcAsync(client, DvcChannel, ct));
        Assert.Equal(Dvc.Cmd.Data, echoed.Cmd);
        Assert.Equal(create.ChannelId, echoed.ChannelId);
        Assert.Equal(payload, echoed.Data);
    }

    [Fact]
    public async Task Dvc_OpensConfiguredChannelName()
    {
        // The channel set is configurable — e.g. RDPeek's diagnostics channel name.
        using var server = new MockServerFixture(["dvc::diag::inspector"]);
        await using var client = new RdpTestClient();
        var ct = Timeout;

        ushort user = await McsClient.NegotiateThroughChannelJoinAsync(client, server.Endpoint, Channels, ct);
        await McsClient.ActivateAsync(client, user, ct);

        Assert.Equal(Dvc.Cmd.Capabilities, Dvc.Parse(await McsClient.ReadDvcAsync(client, DvcChannel, ct)).Cmd);
        await McsClient.SendDvcAsync(client, user, DvcChannel, Dvc.BuildCapabilities(1), ct);

        var create = Dvc.Parse(await McsClient.ReadDvcAsync(client, DvcChannel, ct));
        Assert.Equal(Dvc.Cmd.Create, create.Cmd);
        Assert.Equal("dvc::diag::inspector", Dvc.ChannelName(create));
    }
}

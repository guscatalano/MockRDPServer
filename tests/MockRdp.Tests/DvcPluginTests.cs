using System.Text;
using MockRdp.Plugin;
using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// A loaded server-side DVC plugin opens its channel and round-trips data with the client — the
/// same path a real WTSVirtualChannel agent would take, verified end-to-end over the protocol.
/// </summary>
public class DvcPluginTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;
    private static readonly string[] Channels = ["rdpdr", "rdpsnd", "cliprdr", "drdynvc"];
    private const ushort DvcChannel = 1007;
    private const string PluginChannel = "MOCK::upper";

    /// <summary>A minimal plugin: read a message, write it back upper-cased. Its RunAsync loop is the
    /// same shape as a real in-session agent looping on WTSVirtualChannelRead / WTSVirtualChannelWrite.</summary>
    private sealed class UpperPlugin : IServerDvcPlugin
    {
        public string Name => "uppercase";
        public IReadOnlyList<string> Channels => [PluginChannel];

        public async Task RunAsync(IDvcChannel channel, CancellationToken ct)
        {
            byte[]? msg;
            while ((msg = await channel.ReadAsync(ct)) is not null)
            {
                var upper = Encoding.ASCII.GetString(msg).ToUpperInvariant();
                await channel.WriteAsync(Encoding.ASCII.GetBytes(upper), ct);
            }
        }
    }

    [Fact]
    public async Task Plugin_OpensChannel_And_TransformsData()
    {
        var host = DvcPluginHost.FromPlugins(new UpperPlugin());
        using var server = new MockServerFixture(dvcChannels: [], plugins: host);
        await using var client = new RdpTestClient();
        var ct = Timeout;

        ushort user = await McsClient.NegotiateThroughChannelJoinAsync(client, server.Endpoint, Channels, ct);
        await McsClient.ActivateAsync(client, user, ct);

        // DVC caps, then the server opens the plugin's channel.
        Assert.Equal(Dvc.Cmd.Capabilities, Dvc.Parse(await McsClient.ReadDvcAsync(client, DvcChannel, ct)).Cmd);
        await McsClient.SendDvcAsync(client, user, DvcChannel, Dvc.BuildCapabilities(1), ct);

        var create = Dvc.Parse(await McsClient.ReadDvcAsync(client, DvcChannel, ct));
        Assert.Equal(Dvc.Cmd.Create, create.Cmd);
        Assert.Equal(PluginChannel, Dvc.ChannelName(create));
        await McsClient.SendDvcAsync(client, user, DvcChannel, Dvc.BuildCreateResponse(create.ChannelId, 0), ct);

        // Data goes to the plugin's RunAsync loop; it writes the transformed reply back.
        var payload = Encoding.ASCII.GetBytes("hello dvc plugin");
        foreach (var pdu in Dvc.BuildData(create.ChannelId, payload))
            await McsClient.SendDvcAsync(client, user, DvcChannel, pdu, ct);

        var reply = Dvc.Parse(await McsClient.ReadDvcAsync(client, DvcChannel, ct));
        Assert.Equal(Dvc.Cmd.Data, reply.Cmd);
        Assert.Equal(create.ChannelId, reply.ChannelId);
        Assert.Equal("HELLO DVC PLUGIN", Encoding.ASCII.GetString(reply.Data));
    }
}

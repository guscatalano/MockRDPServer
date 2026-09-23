using MockRdp.Desktop;
using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// The in-session "DVC Chaos" window: clicking its controls sets the chaos state and queues a
/// force-fail, and the connection acts on it by closing the channel to the client.
/// </summary>
public class DvcChaosTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;
    private static readonly string[] Channels = ["rdpdr", "rdpsnd", "cliprdr", "drdynvc"];
    private const ushort DvcChannel = 1007;

    // Mouse click as a desktop input event (press of the left button at x,y).
    private static InputEvent Click(ushort x, ushort y) =>
        new(InputEventType.Mouse, (ushort)(Input.PtrFlagsDown | Input.PtrFlagsButton1), x, y, 0);

    [Fact]
    public void ChaosWindow_Toggle_Intensity_And_ForceFail_ProduceSignals()
    {
        // The DVC Chaos window opens as the 2nd window (after Welcome): n=1 → X=186, Y=136, W=440,
        // TitleH=30. Button rects come straight from FakeDesktop.ChaosToggle/ChaosPlus/ChaosFailRect.
        var d = new FakeDesktop(1024, 768, logon: false, vfsRoot: null) { DvcOpenNames = () => ["dvc::diag::inspector", "ECHO"] };
        d.OpenApp("DVC Chaos");

        Assert.False(d.Chaos.Enabled);

        d.OnInput(Click(277, 268));            // "Turn ON"  (ChaosToggle centre)
        Assert.True(d.Chaos.Enabled);
        Assert.Equal(25, d.Chaos.Percent);

        d.OnInput(Click(438, 268));            // "+10"      (ChaosPlus centre)
        Assert.Equal(35, d.Chaos.Percent);

        d.OnInput(Click(580, 327));            // "Fail" on the first listed channel (ChaosFailRect index 0)
        Assert.Contains("dvc::diag::inspector", d.TakeChaosKills());
    }

    [Fact]
    public async Task ForceFail_ClosesTheChannel_ToTheClient()
    {
        using var server = new MockServerFixture(dvcChannels: ["ECHO"], desktop: true);
        await using var client = new RdpTestClient();
        var ct = Timeout;

        ushort user = await McsClient.NegotiateThroughChannelJoinAsync(client, server.Endpoint, Channels, ct);
        await McsClient.ActivateAsync(client, user, ct);

        // DVC caps, then accept the channels the server opens (ECHO + Display Control in desktop mode).
        Assert.Equal(Dvc.Cmd.Capabilities, Dvc.Parse(await McsClient.ReadDvcAsync(client, DvcChannel, ct)).Cmd);
        await McsClient.SendDvcAsync(client, user, DvcChannel, Dvc.BuildCapabilities(1), ct);
        for (int i = 0; i < 2; i++)
        {
            var create = Dvc.Parse(await McsClient.ReadDvcAsync(client, DvcChannel, ct));
            Assert.Equal(Dvc.Cmd.Create, create.Cmd);
            await McsClient.SendDvcAsync(client, user, DvcChannel, Dvc.BuildCreateResponse(create.ChannelId, 0), ct);
        }

        // In-session: sign in, open DVC Chaos from the Start menu, click Fail on the first channel.
        await ClickAsync(client, 360, 425, ct);   // Sign in → desktop
        await ClickAsync(client, 10, 748, ct);     // Start
        await ClickAsync(client, 20, 628, ct);     // "DVC Chaos" (7th menu item: 412 + 6*36)
        await ClickAsync(client, 580, 327, ct);    // "Fail" on the first open channel

        // The server tears that channel down: the client receives a DVC Close.
        var closed = await ReadDvcUntilAsync(client, Dvc.Cmd.Close, ct);
        Assert.Equal(Dvc.Cmd.Close, closed.Cmd);
    }

    private static Task ClickAsync(RdpTestClient client, ushort x, ushort y, CancellationToken ct) =>
        client.WriteRawAsync(McsClient.BuildFastPathMouse((ushort)(Input.PtrFlagsDown | Input.PtrFlagsButton1), x, y), ct);

    private static async Task<Dvc.Message> ReadDvcUntilAsync(RdpTestClient client, Dvc.Cmd cmd, CancellationToken ct)
    {
        while (true)
        {
            var m = Dvc.Parse(await McsClient.ReadDvcAsync(client, DvcChannel, ct));
            if (m.Cmd == cmd) return m;
        }
    }
}

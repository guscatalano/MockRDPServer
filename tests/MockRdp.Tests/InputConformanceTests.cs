using MockRdp.Mcs;
using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using MockRdp.X224;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// Tier 1 conformance for M5: the client sends a fast-path mouse event and asserts the server
/// reacts by drawing a marker bitmap update at the cursor position.
/// </summary>
public class InputConformanceTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;
    private static readonly string[] Channels = ["rdpdr", "rdpsnd", "cliprdr", "drdynvc"];

    [Fact]
    public async Task MouseInput_DrawsMarkerAtCursor()
    {
        using var server = new MockServerFixture();
        await using var client = new RdpTestClient();
        var ct = Timeout;

        ushort user = await McsClient.NegotiateThroughChannelJoinAsync(client, server.Endpoint, Channels, ct);
        await McsClient.ActivateAsync(client, user, ct);

        // Drain the startup test pattern (one I/O-channel bitmap update per square).
        for (int i = 0; i < Graphics.TestPattern().Count; i++)
            await McsClient.ReadIoBitmapAsync(client, ct);

        // Move the mouse; the server should draw a marker at that position.
        await client.WriteRawAsync(McsClient.BuildFastPathMouse(Input.PtrFlagsMove, 200, 150), ct);

        var marker = await McsClient.ReadIoBitmapAsync(client, ct);
        var (x, y, w, h, _, pixel) = McsClient.ParseFirstBitmap(marker);

        Assert.Equal((200, 150, 16, 16), (x, y, w, h));
        Assert.Equal(Graphics.Rgb565(255, 255, 0), pixel); // yellow marker
    }
}

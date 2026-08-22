using MockRdp.Mcs;
using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using MockRdp.X224;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// Tier 1 conformance for M4: after activation, the client decodes the server's bitmap updates
/// and asserts the startup test pattern's pixels — deterministic graphics verification, no screenshot.
/// </summary>
public class GraphicsConformanceTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;
    private static readonly string[] Channels = ["rdpdr", "rdpsnd", "cliprdr", "drdynvc"];

    [Fact]
    public async Task Server_SendsTestPatternBitmapUpdates()
    {
        using var server = new MockServerFixture();
        await using var client = new RdpTestClient();
        var ct = Timeout;

        ushort user = await McsClient.NegotiateThroughChannelJoinAsync(client, server.Endpoint, Channels, ct);
        await McsClient.ActivateAsync(client, user, ct);

        // First bitmap update: a 64x64 red square at the origin.
        var (x, y, w, h, bpp, pixel) = McsClient.ParseFirstBitmap(await McsClient.ReadIoBitmapAsync(client, ct));

        Assert.Equal((0, 0, 64, 64), (x, y, w, h));
        Assert.Equal(16, bpp);
        Assert.Equal(Graphics.Rgb565(255, 0, 0), pixel);

        // Second bitmap update: a green square at x=64.
        var g = McsClient.ParseFirstBitmap(await McsClient.ReadIoBitmapAsync(client, ct));
        Assert.Equal((64, 0), (g.X, g.Y));
        Assert.Equal(Graphics.Rgb565(0, 255, 0), g.FirstPixel);
    }
}

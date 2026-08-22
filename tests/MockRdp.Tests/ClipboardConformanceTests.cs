using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// Tier 1 conformance for M6: the client completes the CLIPRDR handshake over the cliprdr
/// virtual channel and pulls the mock's served clipboard text.
/// </summary>
public class ClipboardConformanceTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    // cliprdr is the 3rd requested channel → id 1004 + 2 = 1006.
    private static readonly string[] Channels = ["rdpdr", "rdpsnd", "cliprdr", "drdynvc"];
    private const ushort CliprdrChannel = 1006;

    [Fact]
    public async Task Clipboard_HandshakesAndServesText()
    {
        using var server = new MockServerFixture();
        await using var client = new RdpTestClient();
        var ct = Timeout;

        ushort user = await McsClient.NegotiateThroughChannelJoinAsync(client, server.Endpoint, Channels, ct);
        await McsClient.ActivateAsync(client, user, ct);

        // Server opens the exchange with capabilities + monitor ready.
        Assert.Equal(Clipboard.CbClipCaps, Clipboard.ReadMsgType(await McsClient.ReadClipboardAsync(client, CliprdrChannel, ct)));
        Assert.Equal(Clipboard.CbMonitorReady, Clipboard.ReadMsgType(await McsClient.ReadClipboardAsync(client, CliprdrChannel, ct)));

        // Client announces a format; server acks and then offers its own text format.
        await McsClient.SendClipboardAsync(client, user, CliprdrChannel, Clipboard.FormatListUnicodeText(), ct);
        Assert.Equal(Clipboard.CbFormatListResponse, Clipboard.ReadMsgType(await McsClient.ReadClipboardAsync(client, CliprdrChannel, ct)));
        Assert.Equal(Clipboard.CbFormatList, Clipboard.ReadMsgType(await McsClient.ReadClipboardAsync(client, CliprdrChannel, ct)));

        // Client requests the served text; server returns it.
        await McsClient.SendClipboardAsync(client, user, CliprdrChannel, Clipboard.FormatDataRequest(13), ct);
        var response = await McsClient.ReadClipboardAsync(client, CliprdrChannel, ct);
        Assert.Equal(Clipboard.CbFormatDataResponse, Clipboard.ReadMsgType(response));
        Assert.Equal(Clipboard.ServedText, Clipboard.ReadTextResponse(response));
    }
}

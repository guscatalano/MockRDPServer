using System.Buffers.Binary;
using MockRdp.Mcs;
using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using MockRdp.X224;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// End-to-end conformance for a resolution change (MS-RDPEDISP + Deactivation-Reactivation):
/// the server opens the Display Control channel, and a client MONITOR_LAYOUT PDU drives a
/// Deactivate All → Demand Active at the requested size.
/// </summary>
public class ResolutionChangeTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;
    private static readonly string[] Channels = ["rdpdr", "rdpsnd", "cliprdr", "drdynvc"];
    private const ushort DvcChannel = 1007; // drdynvc is the 4th requested channel → 1004 + 3

    [Fact]
    public async Task MonitorLayout_DrivesDeactivateReactivateAtNewSize()
    {
        using var server = new MockServerFixture(desktop: true);
        await using var client = new RdpTestClient();
        var ct = Timeout;

        ushort user = await McsClient.NegotiateThroughChannelJoinAsync(client, server.Endpoint, Channels, ct);
        await McsClient.ActivateAsync(client, user, ct);

        // DVC capability exchange, then the server opens ECHO and the Display Control channel.
        Assert.Equal(Dvc.Cmd.Capabilities, Dvc.Parse(await McsClient.ReadDvcAsync(client, DvcChannel, ct)).Cmd);
        await McsClient.SendDvcAsync(client, user, DvcChannel, Dvc.BuildCapabilities(1), ct);

        uint displayId = 0;
        for (int i = 0; i < 2; i++)
        {
            var create = Dvc.Parse(await McsClient.ReadDvcAsync(client, DvcChannel, ct));
            Assert.Equal(Dvc.Cmd.Create, create.Cmd);
            await McsClient.SendDvcAsync(client, user, DvcChannel, Dvc.BuildCreateResponse(create.ChannelId, 0), ct);
            if (Dvc.ChannelName(create) == DisplayControl.ChannelName) displayId = create.ChannelId;
        }
        Assert.NotEqual(0u, displayId); // the Display Control channel was offered

        // The client resizes: send a MONITOR_LAYOUT PDU asking for 1280x960.
        foreach (var pdu in Dvc.BuildData(displayId, MonitorLayout(1280, 960)))
            await McsClient.SendDvcAsync(client, user, DvcChannel, pdu, ct);

        // The server performs a Deactivation-Reactivation: Deactivate All, then Demand Active
        // at the new size. (Bitmap Data PDUs on the same channel are skipped.)
        await ReadIoShareControlAsync(client, ShareControl.Deactivate & 0x0F, ct);
        var demand = await ReadIoShareControlAsync(client, ShareControl.DemandActive & 0x0F, ct);

        Assert.Equal((1280, 960), DemandActiveDesktopSize(demand));
    }

    [Fact]
    public async Task DisplaySettingsClick_DrivesServerInitiatedReactivation()
    {
        using var server = new MockServerFixture(desktop: true);
        await using var client = new RdpTestClient();
        var ct = Timeout;

        ushort user = await McsClient.NegotiateThroughChannelJoinAsync(client, server.Endpoint, Channels, ct);
        await McsClient.ActivateAsync(client, user, ct);

        // The session boots to the logon screen (no NLA). Click through it, then use the Start
        // menu to open Display settings and pick 1280x720 — a purely server-side trigger.
        await ClickAsync(client, 360, 425, ct);   // Sign in  → desktop
        await ClickAsync(client, 10, 748, ct);    // Start
        await ClickAsync(client, 20, 590, ct);    // Display settings (4th menu item)
        await ClickAsync(client, 210, 244, ct);   // resolution row: 1280x720

        // The server initiates a Deactivation-Reactivation on its own.
        await ReadIoShareControlAsync(client, ShareControl.Deactivate & 0x0F, ct);
        var demand = await ReadIoShareControlAsync(client, ShareControl.DemandActive & 0x0F, ct);
        Assert.Equal((1280, 720), DemandActiveDesktopSize(demand));
    }

    private static Task ClickAsync(RdpTestClient client, ushort x, ushort y, CancellationToken ct) =>
        client.WriteRawAsync(McsClient.BuildFastPathMouse((ushort)(Input.PtrFlagsDown | Input.PtrFlagsButton1), x, y), ct);

    private static async Task<byte[]> ReadIoShareControlAsync(RdpTestClient client, int pduType, CancellationToken ct)
    {
        while (true)
        {
            var (channelId, data) = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
            if (channelId == Gcc.IoChannelId && ShareControl.PduType(data) == pduType)
                return data;
        }
    }

    private static byte[] MonitorLayout(int width, int height)
    {
        var b = new byte[16 + 40];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), 0x00000002);     // Type = MONITOR_LAYOUT
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)b.Length); // Length
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 40);             // MonitorLayoutSize
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 1);             // NumMonitors
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 1);             // Flags = PRIMARY
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), (uint)width);   // Width
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), (uint)height);  // Height
        return b;
    }

    /// <summary>Walks a Demand Active PDU's capability sets to read the Bitmap cap's desktop size.</summary>
    private static (int Width, int Height) DemandActiveDesktopSize(byte[] pdu)
    {
        int srcLen = BinaryPrimitives.ReadUInt16LittleEndian(pdu.AsSpan(10, 2)); // lengthSourceDescriptor
        int numCaps = BinaryPrimitives.ReadUInt16LittleEndian(pdu.AsSpan(14 + srcLen, 2));
        int off = 14 + srcLen + 4; // past numberCapabilities(2) + pad(2)
        for (int i = 0; i < numCaps; i++)
        {
            int type = BinaryPrimitives.ReadUInt16LittleEndian(pdu.AsSpan(off, 2));
            int len = BinaryPrimitives.ReadUInt16LittleEndian(pdu.AsSpan(off + 2, 2));
            if (type == 2) // CAPSTYPE_BITMAP: preferredBpp,recv1,recv4,recv8,width,height,...
                return (BinaryPrimitives.ReadUInt16LittleEndian(pdu.AsSpan(off + 4 + 8, 2)),
                        BinaryPrimitives.ReadUInt16LittleEndian(pdu.AsSpan(off + 4 + 10, 2)));
            off += len;
        }
        return (0, 0);
    }
}

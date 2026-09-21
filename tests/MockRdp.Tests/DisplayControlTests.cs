using System.Buffers.Binary;
using MockRdp.Desktop;
using MockRdp.Rdp;
using Xunit;

namespace MockRdp.Tests;

/// <summary>MS-RDPEDISP (Display Control) codec + the framebuffer resize it drives.</summary>
public class DisplayControlTests
{
    private static byte[] MonitorLayout(uint flags, int width, int height, int monitorSize = 40)
    {
        var b = new byte[16 + monitorSize];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), 0x00000002);        // Type = MONITOR_LAYOUT
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)b.Length);    // Length
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)monitorSize); // MonitorLayoutSize
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 1);                // NumMonitors
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), flags);            // Flags
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), (uint)width);      // Width  (offset 12 in entry)
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(32), (uint)height);     // Height (offset 16 in entry)
        return b;
    }

    [Fact]
    public void ParseMonitorLayout_ReadsPrimarySize()
    {
        var size = DisplayControl.ParseMonitorLayout(MonitorLayout(flags: 1, 1920, 1080));
        Assert.Equal((1920, 1080), size);
    }

    [Fact]
    public void ParseMonitorLayout_ClampsAndEvensDimensions()
    {
        var big = DisplayControl.ParseMonitorLayout(MonitorLayout(flags: 1, 99999, 1081));
        Assert.Equal((DisplayControl.MaxDimension, 1080), big);   // clamped to max; 1081 → even 1080

        var small = DisplayControl.ParseMonitorLayout(MonitorLayout(flags: 1, 10, 10));
        Assert.Equal((DisplayControl.MinDimension, DisplayControl.MinDimension), small);
    }

    [Fact]
    public void ParseMonitorLayout_RejectsWrongPduType()
    {
        var b = MonitorLayout(flags: 1, 1280, 800);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0), 0x00000005); // CAPS, not MONITOR_LAYOUT
        Assert.Null(DisplayControl.ParseMonitorLayout(b));
    }

    [Fact]
    public void BuildCapsPdu_HasCapsTypeAndLength()
    {
        var caps = DisplayControl.BuildCapsPdu();
        Assert.Equal(0x00000005u, BinaryPrimitives.ReadUInt32LittleEndian(caps.AsSpan(0)));
        Assert.Equal(20u, BinaryPrimitives.ReadUInt32LittleEndian(caps.AsSpan(4)));
        Assert.Equal(20, caps.Length);
    }

    [Fact]
    public void Resize_ChangesDimensions_KeepsWindows_AndResendsFullFrame()
    {
        using var d = new FakeDesktop(1024, 768);
        d.DirtyTiles().ToList();                 // consume the initial full frame
        Assert.Empty(d.DirtyTiles());            // nothing dirty now

        int before = d.WindowCount;
        d.Resize(1280, 800);

        Assert.Equal(1280, d.Width);
        Assert.Equal(800, d.Height);
        Assert.Equal(before, d.WindowCount);     // windows survive the resize
        // A resize resets the tile grid, so the whole new framebuffer is dirty again.
        int cols = (1280 + 63) / 64, rows = (800 + 63) / 64;
        Assert.Equal(cols * rows, d.DirtyTiles().Count());
    }
}

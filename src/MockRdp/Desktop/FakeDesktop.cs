using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MockRdp.Desktop;

/// <summary>
/// A software-rendered fake Windows desktop. Phase 0: composes a static scene (wallpaper,
/// a window, a taskbar with a Start button and clock) into an ImageSharp framebuffer that
/// the RDP output path sends as bitmap-update tiles. Managed-only (no native deps).
/// </summary>
public sealed class FakeDesktop : IDisposable
{
    public int Width { get; }
    public int Height { get; }

    private readonly Image<Rgba32> _fb;
    private readonly Font? _font;
    private readonly Font? _fontSmall;

    public FakeDesktop(int width, int height)
    {
        Width = width;
        Height = height;
        _fb = new Image<Rgba32>(width, height);
        _font = TryLoadFont(15);
        _fontSmall = TryLoadFont(12);
        Render();
    }

    private static Font? TryLoadFont(float size)
    {
        foreach (var name in new[] { "Segoe UI", "Arial", "DejaVu Sans", "Liberation Sans" })
            if (SystemFonts.TryGet(name, out var family))
                return family.CreateFont(size, FontStyle.Regular);
        return SystemFonts.Families.Any() ? SystemFonts.Families.First().CreateFont(size, FontStyle.Regular) : null;
    }

    /// <summary>Composes the whole desktop into the framebuffer.</summary>
    public void Render()
    {
        const int taskbar = 44;
        int tbY = Height - taskbar;

        _fb.Mutate(ctx =>
        {
            // Wallpaper: vertical blue gradient.
            ctx.Fill(new LinearGradientBrush(
                new PointF(0, 0), new PointF(0, Height), GradientRepetitionMode.None,
                new ColorStop(0f, Color.ParseHex("103A6B")),
                new ColorStop(1f, Color.ParseHex("2B6AB0"))));

            DrawWindow(ctx, "Welcome to mock-rdp", 130, 90, 480, 320);

            // Taskbar + Start button + clock.
            Fill(ctx, "1F1F1F", 0, tbY, Width, taskbar);
            Fill(ctx, "0A7A3A", 0, tbY, 92, taskbar);
            Text(ctx, _font, "Start", 20, tbY + 12, Color.White);
            Text(ctx, _fontSmall, DateTime.Now.ToString("h:mm tt"), Width - 74, tbY + 15, Color.White);
        });
    }

    private void DrawWindow(IImageProcessingContext ctx, string title, int x, int y, int w, int h)
    {
        Fill(ctx, "F2F2F2", x, y, w, h);            // body
        Fill(ctx, "005A9E", x, y, w, 30);           // title bar
        Text(ctx, _font, title, x + 10, y + 7, Color.White);
        Fill(ctx, "C23030", x + w - 30, y, 30, 30); // close button
        Text(ctx, _font, "x", x + w - 19, y + 6, Color.White);
        Text(ctx, _fontSmall, "This is a software-rendered fake Windows desktop.", x + 14, y + 50, Color.ParseHex("202020"));
    }

    private static void Fill(IImageProcessingContext ctx, string hex, int x, int y, int w, int h) =>
        ctx.Fill(Color.ParseHex(hex), new RectangularPolygon(x, y, w, h));

    private static void Text(IImageProcessingContext ctx, Font? font, string s, float x, float y, Color color)
    {
        if (font is not null) ctx.DrawText(s, font, color, new PointF(x, y));
    }

    public void SavePng(string path) => _fb.SaveAsPng(path);

    /// <summary>The framebuffer as RGB565 tiles for RDP bitmap updates.</summary>
    public IEnumerable<(int X, int Y, int W, int H, ushort[] Pixels)> Tiles(int tile = 64)
    {
        var buf = new Rgba32[Width * Height];
        _fb.CopyPixelDataTo(buf);

        for (int ty = 0; ty < Height; ty += tile)
            for (int tx = 0; tx < Width; tx += tile)
            {
                int tw = Math.Min(tile, Width - tx);
                int th = Math.Min(tile, Height - ty);
                var px = new ushort[tw * th];
                for (int row = 0; row < th; row++)
                    for (int col = 0; col < tw; col++)
                    {
                        var p = buf[(ty + row) * Width + (tx + col)];
                        px[row * tw + col] = (ushort)(((p.R >> 3) << 11) | ((p.G >> 2) << 5) | (p.B >> 3));
                    }
                yield return (tx, ty, tw, th, px);
            }
    }

    public void Dispose() => _fb.Dispose();
}

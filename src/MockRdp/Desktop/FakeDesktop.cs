using MockRdp.Rdp;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MockRdp.Desktop;

/// <summary>Which desktop within the (fake) window station is active — mirroring Windows'
/// Default (interactive) vs. Winlogon (secure) desktops.</summary>
public enum DesktopKind { Default, Secure }

/// <summary>
/// A software-rendered fake Windows session. Owns a framebuffer and the state of the
/// interactive desktop (Start menu, a window) and the secure/Winlogon desktop, switches
/// between them, and turns client input into state changes. Phase 1: cursor is the client's
/// own; input drives clicks (Start menu, window close) and the Secure Attention Sequence
/// (Ctrl+Alt+End) flips to the secure desktop; Esc returns.
/// </summary>
public sealed class FakeDesktop : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public DesktopKind Active { get; set; } = DesktopKind.Default;
    public bool StartMenuOpen { get; set; }
    public bool WindowOpen { get; set; } = true;

    private readonly Image<Rgba32> _fb;
    private readonly Font? _font;
    private readonly Font? _small;
    private bool _ctrl, _alt;

    private const int Taskbar = 44;
    private static readonly int[] Win = [130, 90, 480, 320]; // x, y, w, h
    private const int StartW = 92;

    public FakeDesktop(int width, int height)
    {
        Width = width;
        Height = height;
        _fb = new Image<Rgba32>(width, height);
        _font = TryLoadFont(15);
        _small = TryLoadFont(12);
        Render();
    }

    private static Font? TryLoadFont(float size)
    {
        foreach (var name in new[] { "Segoe UI", "Arial", "DejaVu Sans", "Liberation Sans" })
            if (SystemFonts.TryGet(name, out var family))
                return family.CreateFont(size, FontStyle.Regular);
        return SystemFonts.Families.Any() ? SystemFonts.Families.First().CreateFont(size, FontStyle.Regular) : null;
    }

    // ── input ───────────────────────────────────────────────────────────────

    /// <summary>Applies one client input event. Returns true if the framebuffer changed.</summary>
    public bool OnInput(InputEvent ev)
    {
        switch (ev.Type)
        {
            case InputEventType.Scancode: return OnKey(ev.Code, (ev.Flags & 0x01) != 0);
            case InputEventType.Mouse:
                bool leftDown = (ev.Flags & Input.PtrFlagsDown) != 0 && (ev.Flags & Input.PtrFlagsButton1) != 0;
                return leftDown && OnClick(ev.X, ev.Y);
            default: return false;
        }
    }

    private bool OnKey(byte scancode, bool release)
    {
        switch (scancode)
        {
            case 0x1D: _ctrl = !release; return false;                 // Ctrl
            case 0x38: _alt = !release; return false;                  // Alt
            case 0x4F when !release && _ctrl && _alt:                  // End -> SAS
                return Switch(DesktopKind.Secure);
            case 0x01 when !release:                                    // Esc
                if (Active == DesktopKind.Secure) return Switch(DesktopKind.Default);
                if (StartMenuOpen) { StartMenuOpen = false; Render(); return true; }
                return false;
            default: return false;
        }
    }

    private bool OnClick(int x, int y)
    {
        if (Active == DesktopKind.Secure)
            return InRect(x, y, CancelBtn()) && Switch(DesktopKind.Default);

        int tbY = Height - Taskbar;
        if (InRect(x, y, 0, tbY, StartW, Taskbar)) { StartMenuOpen = !StartMenuOpen; Render(); return true; }
        if (StartMenuOpen) { StartMenuOpen = false; Render(); return true; } // any click dismisses (items are Phase 2)
        if (WindowOpen && InRect(x, y, Win[0] + Win[2] - 30, Win[1], 30, 30)) { WindowOpen = false; Render(); return true; }
        return false;
    }

    private bool Switch(DesktopKind kind)
    {
        if (Active == kind) return false;
        Active = kind;
        StartMenuOpen = false;
        Render();
        return true;
    }

    // ── rendering ─────────────────────────────────────────────────────────────

    public void Render()
    {
        _fb.Mutate(ctx =>
        {
            if (Active == DesktopKind.Secure) RenderSecure(ctx);
            else RenderDefault(ctx);
        });
    }

    private void RenderDefault(IImageProcessingContext ctx)
    {
        int tbY = Height - Taskbar;
        ctx.Fill(new LinearGradientBrush(new PointF(0, 0), new PointF(0, Height), GradientRepetitionMode.None,
            new ColorStop(0f, Color.ParseHex("103A6B")), new ColorStop(1f, Color.ParseHex("2B6AB0"))));

        if (WindowOpen)
        {
            Fill(ctx, "F2F2F2", Win[0], Win[1], Win[2], Win[3]);
            Fill(ctx, "005A9E", Win[0], Win[1], Win[2], 30);
            Text(ctx, _font, "Welcome to mock-rdp", Win[0] + 10, Win[1] + 7, Color.White);
            Fill(ctx, "C23030", Win[0] + Win[2] - 30, Win[1], 30, 30);
            Text(ctx, _font, "x", Win[0] + Win[2] - 19, Win[1] + 6, Color.White);
            Text(ctx, _small, "Click Start, close this window, or press Ctrl+Alt+End.", Win[0] + 14, Win[1] + 50, Color.ParseHex("202020"));
        }

        if (StartMenuOpen)
        {
            int mh = 220, my = tbY - mh;
            Fill(ctx, "2A2A2A", 0, my, 240, mh);
            string[] items = ["File Explorer", "Notepad", "Settings", "Run…"];
            for (int i = 0; i < items.Length; i++)
                Text(ctx, _font, items[i], 18, my + 18 + i * 34, Color.White);
        }

        Fill(ctx, "1F1F1F", 0, tbY, Width, Taskbar);
        Fill(ctx, StartMenuOpen ? "0E9F4E" : "0A7A3A", 0, tbY, StartW, Taskbar);
        Text(ctx, _font, "Start", 20, tbY + 12, Color.White);
        Text(ctx, _small, DateTime.Now.ToString("h:mm tt"), Width - 74, tbY + 15, Color.White);
    }

    private void RenderSecure(IImageProcessingContext ctx)
    {
        ctx.Fill(Color.ParseHex("0A0A14")); // dimmed secure background
        var (dx, dy, dw, dh) = Dialog();
        Fill(ctx, "1B1B2A", dx, dy, dw, dh);
        Fill(ctx, "3A3A55", dx, dy, dw, 34);
        Text(ctx, _font, "Windows Security", dx + 14, dy + 9, Color.White);
        Text(ctx, _small, "Secure desktop (Winlogon).", dx + 20, dy + 58, Color.ParseHex("D0D0D0"));
        Text(ctx, _small, "Apps on the interactive desktop cannot see or drive this.", dx + 20, dy + 82, Color.ParseHex("D0D0D0"));
        string[] opts = ["Lock", "Sign out", "Task Manager"];
        for (int i = 0; i < opts.Length; i++)
            Text(ctx, _small, "•  " + opts[i], dx + 24, dy + 116 + i * 24, Color.ParseHex("A8C8FF"));
        var c = CancelBtn();
        Fill(ctx, "3A3A55", c[0], c[1], c[2], c[3]);
        Text(ctx, _small, "Cancel", c[0] + 22, c[1] + 8, Color.White);
        Text(ctx, _small, "Press Esc or Cancel to return.", dx + 20, dy + dh - 26, Color.ParseHex("808080"));
    }

    private (int X, int Y, int W, int H) Dialog()
    {
        int dw = 440, dh = 250;
        return ((Width - dw) / 2, (Height - dh) / 2, dw, dh);
    }

    private int[] CancelBtn()
    {
        var (dx, dy, dw, dh) = Dialog();
        return [dx + dw - 110, dy + dh - 44, 90, 30];
    }

    private static void Fill(IImageProcessingContext ctx, string hex, int x, int y, int w, int h) =>
        ctx.Fill(Color.ParseHex(hex), new RectangularPolygon(x, y, w, h));

    private static void Text(IImageProcessingContext ctx, Font? font, string s, float x, float y, Color color)
    {
        if (font is not null) ctx.DrawText(s, font, color, new PointF(x, y));
    }

    private static bool InRect(int px, int py, int x, int y, int w, int h) =>
        px >= x && px < x + w && py >= y && py < y + h;

    private static bool InRect(int px, int py, int[] r) => InRect(px, py, r[0], r[1], r[2], r[3]);

    // ── output ────────────────────────────────────────────────────────────────

    public void SavePng(string path) => _fb.SaveAsPng(path);

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

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
/// A software-rendered fake Windows session with a tiny window manager. Owns a framebuffer,
/// a list of draggable windows, a Start menu that launches windows, and a secure/Winlogon
/// desktop reached via the secure-attention sequence. Turns client input into state changes
/// and exposes changed tiles (dirty-rect) for the RDP output path.
/// </summary>
public sealed class FakeDesktop : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public DesktopKind Active { get; set; } = DesktopKind.Default;
    public bool StartMenuOpen { get; set; }
    public int WindowCount => _windows.Count;
    public string FocusedTitle => Focused?.Title ?? "";
    public string FocusedText => Focused?.Body ?? "";

    private enum WinKind { Generic, Explorer, Notepad, Run }

    private sealed class Win
    {
        public WinKind Kind = WinKind.Generic;
        public required string Title;
        public string Body = "";
        public VfsNode? Folder;   // Explorer: the current directory
        public int X, Y, W, H;
    }

    private readonly List<Win> _windows = new();   // z-order: last = topmost
    private Win? _drag;
    private int _dragDx, _dragDy;
    private bool _ctrl, _alt, _shift;

    private Win? Focused => _windows.Count > 0 ? _windows[^1] : null;

    private readonly VfsNode _vfsRoot;
    private readonly VfsNode _vfsHome;

    private readonly Image<Rgba32> _fb;
    private readonly Font? _font;
    private readonly Font? _small;
    private ulong[]? _tileHash;

    private const int Taskbar = 44;
    private const int TitleH = 30;
    private const int CloseW = 30;
    private const int StartW = 92;
    private static readonly string[] MenuItems = ["File Explorer", "Notepad", "Settings", "Run…"];
    private const int MenuW = 240;
    private const int MenuRow = 36;

    public FakeDesktop(int width, int height)
    {
        Width = width;
        Height = height;
        _fb = new Image<Rgba32>(width, height);
        _font = TryLoadFont(15);
        _small = TryLoadFont(12);
        _vfsRoot = Vfs.BuildDefault();
        _vfsHome = Vfs.Home(_vfsRoot);
        _windows.Add(new Win { Title = "Welcome to mock-rdp", Body = "Click Start → File Explorer to browse C:\\, drag windows, Ctrl+Alt+End for the secure desktop.", X = 130, Y = 90, W = 520, H = 300 });
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
            case InputEventType.Scancode:
                return OnKey(ev.Code, (ev.Flags & 0x01) != 0);
            case InputEventType.Mouse:
                bool btn1 = (ev.Flags & Input.PtrFlagsButton1) != 0;
                bool down = (ev.Flags & Input.PtrFlagsDown) != 0;
                if (btn1 && down) return OnMouseDown(ev.X, ev.Y);
                if (btn1 && !down) return OnMouseUp();
                if (_drag is not null && (ev.Flags & Input.PtrFlagsMove) != 0) return OnMouseMove(ev.X, ev.Y);
                return false;
            default:
                return false;
        }
    }

    private bool OnKey(byte scancode, bool release)
    {
        switch (scancode)
        {
            case 0x1D: _ctrl = !release; return false;
            case 0x38: _alt = !release; return false;
            case Keys.LShift or Keys.RShift: _shift = !release; return false;
        }
        if (release) return false;

        if (scancode == 0x4F && _ctrl && _alt) return Switch(DesktopKind.Secure);
        if (scancode == 0x01) // Esc
        {
            if (Active == DesktopKind.Secure) return Switch(DesktopKind.Default);
            if (StartMenuOpen) { StartMenuOpen = false; Render(); return true; }
            return false;
        }
        if (Active == DesktopKind.Secure) return false;

        // Text into the focused editable window.
        var f = Focused;
        if (f is null || (f.Kind != WinKind.Notepad && f.Kind != WinKind.Run)) return false;

        if (scancode == Keys.Backspace)
        {
            if (f.Body.Length == 0) return false;
            f.Body = f.Body[..^1];
            Render();
            return true;
        }
        if (scancode == Keys.Enter)
        {
            if (f.Kind == WinKind.Run) { RunCommand(f); return true; }
            f.Body += "\r\n";
            Render();
            return true;
        }
        if (Keys.ScancodeToChar(scancode, _shift) is { } ch)
        {
            f.Body += ch;
            Render();
            return true;
        }
        return false;
    }

    private void RunCommand(Win run)
    {
        var raw = run.Body.Trim();
        _windows.Remove(run);
        switch (raw.ToLowerInvariant())
        {
            case "explorer" or "explorer.exe": Launch("File Explorer"); break;
            case "notepad" or "notepad.exe": Launch("Notepad"); break;
            case "cmd" or "cmd.exe" or "powershell":
                Open(new Win { Title = raw, Body = "Microsoft Windows [fake]\r\n\r\nC:\\Users\\rdpuser> ", W = 480, H = 260 });
                break;
            default:
                Open(new Win { Title = "Run", Body = raw.Length == 0 ? "Type a program name, then Enter." : $"Windows cannot find '{raw}'.", W = 420, H = 140 });
                break;
        }
        Render();
    }

    private bool OnMouseDown(int x, int y)
    {
        if (Active == DesktopKind.Secure)
            return InRect(x, y, CancelBtn()) && Switch(DesktopKind.Default);

        int tbY = Height - Taskbar;
        if (InRect(x, y, 0, tbY, StartW, Taskbar)) { StartMenuOpen = !StartMenuOpen; Render(); return true; }

        if (StartMenuOpen)
        {
            int my = tbY - MenuItems.Length * MenuRow - 12;
            for (int i = 0; i < MenuItems.Length; i++)
                if (InRect(x, y, 0, my + i * MenuRow + 6, MenuW, MenuRow))
                {
                    Launch(MenuItems[i]);
                    StartMenuOpen = false;
                    Render();
                    return true;
                }
            StartMenuOpen = false;
            Render();
            return true;
        }

        // Topmost window first.
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            var w = _windows[i];
            if (InRect(x, y, w.X + w.W - CloseW, w.Y, CloseW, TitleH)) { _windows.RemoveAt(i); Render(); return true; }
            if (InRect(x, y, w.X, w.Y, w.W, TitleH)) // title bar → raise + drag
            {
                Raise(i);
                _drag = w; _dragDx = x - w.X; _dragDy = y - w.Y;
                return false; // no visual change yet
            }
            if (InRect(x, y, w.X, w.Y, w.W, w.H))
            {
                bool raised = Raise(i);
                bool acted = w.Kind == WinKind.Explorer && ExplorerClick(w, x, y);
                if (raised || acted) { Render(); return true; }
                return false;
            }
        }
        return false;
    }

    /// <summary>A click in a File Explorer window: navigate into a folder, up via "..", or
    /// open a file into Notepad.</summary>
    private bool ExplorerClick(Win w, int x, int y)
    {
        if (w.Folder is null) return false;
        int listTop = w.Y + TitleH + 30;
        const int rowH = 22;
        if (y < listTop) return false;
        int row = (y - listTop) / rowH;

        bool hasParent = w.Folder.Parent is not null;
        if (hasParent && row == 0) { w.Folder = w.Folder.Parent; return true; }

        int idx = row - (hasParent ? 1 : 0);
        if (idx < 0 || idx >= w.Folder.Children.Count) return false;
        var entry = w.Folder.Children[idx];
        if (entry.IsDir) { w.Folder = entry; return true; }
        OpenNotepad(entry.Name, entry.Text);
        return true;
    }

    private bool OnMouseMove(int x, int y)
    {
        if (_drag is null) return false;
        _drag.X = Math.Clamp(x - _dragDx, -_drag.W + 80, Width - 80);
        _drag.Y = Math.Clamp(y - _dragDy, 0, Height - Taskbar - TitleH);
        Render();
        return true;
    }

    private bool OnMouseUp()
    {
        bool wasDragging = _drag is not null;
        _drag = null;
        return wasDragging;   // final position already rendered during move; report a change to flush
    }

    private bool Raise(int index)
    {
        if (index == _windows.Count - 1) return false;
        var w = _windows[index];
        _windows.RemoveAt(index);
        _windows.Add(w);
        return true;
    }

    private void Launch(string app)
    {
        switch (app)
        {
            case "File Explorer": Open(new Win { Kind = WinKind.Explorer, Title = "File Explorer", Folder = _vfsHome, W = 480, H = 320 }); break;
            case "Notepad": Open(new Win { Kind = WinKind.Notepad, Title = "Untitled — Notepad", W = 420, H = 300 }); break;
            case "Settings": Open(new Win { Title = "Settings", Body = "Settings.", W = 420, H = 240 }); break;
            default: Open(new Win { Kind = WinKind.Run, Title = "Run", W = 420, H = 160 }); break;
        }
    }

    private void OpenNotepad(string name, string text) =>
        Open(new Win { Kind = WinKind.Notepad, Title = name + " — Notepad", Body = text, W = 460, H = 320 });

    private void Open(Win w)
    {
        int n = _windows.Count;
        w.X = 160 + n * 26;
        w.Y = 110 + n * 26;
        _windows.Add(w);
    }

    private bool Switch(DesktopKind kind)
    {
        if (Active == kind) return false;
        Active = kind;
        StartMenuOpen = false;
        _drag = null;
        Render();
        return true;
    }

    // ── rendering ─────────────────────────────────────────────────────────────

    public void Render() => _fb.Mutate(ctx =>
    {
        if (Active == DesktopKind.Secure) RenderSecure(ctx);
        else RenderDefault(ctx);
    });

    private void RenderDefault(IImageProcessingContext ctx)
    {
        int tbY = Height - Taskbar;
        ctx.Fill(new LinearGradientBrush(new PointF(0, 0), new PointF(0, Height), GradientRepetitionMode.None,
            new ColorStop(0f, Color.ParseHex("103A6B")), new ColorStop(1f, Color.ParseHex("2B6AB0"))));

        foreach (var w in _windows) DrawWindow(ctx, w);

        if (StartMenuOpen)
        {
            int mh = MenuItems.Length * MenuRow + 12;
            int my = tbY - mh;
            Fill(ctx, "2A2A2A", 0, my, MenuW, mh);
            for (int i = 0; i < MenuItems.Length; i++)
                Text(ctx, _font, MenuItems[i], 18, my + i * MenuRow + 10, Color.White);
        }

        Fill(ctx, "1F1F1F", 0, tbY, Width, Taskbar);
        Fill(ctx, StartMenuOpen ? "0E9F4E" : "0A7A3A", 0, tbY, StartW, Taskbar);
        Text(ctx, _font, "Start", 20, tbY + 12, Color.White);
        Text(ctx, _small, DateTime.Now.ToString("h:mm tt"), Width - 74, tbY + 15, Color.White);
    }

    private void DrawWindow(IImageProcessingContext ctx, Win w)
    {
        Fill(ctx, "F2F2F2", w.X, w.Y, w.W, w.H);
        Fill(ctx, "005A9E", w.X, w.Y, w.W, TitleH);
        Text(ctx, _font, w.Title, w.X + 10, w.Y + 7, Color.White);
        Fill(ctx, "C23030", w.X + w.W - CloseW, w.Y, CloseW, TitleH);
        Text(ctx, _font, "x", w.X + w.W - 19, w.Y + 6, Color.White);

        bool focused = ReferenceEquals(w, Focused);
        switch (w.Kind)
        {
            case WinKind.Explorer: DrawExplorer(ctx, w); break;
            case WinKind.Notepad: DrawNotepad(ctx, w, focused); break;
            case WinKind.Run: DrawRun(ctx, w, focused); break;
            default: Text(ctx, _small, w.Body, w.X + 14, w.Y + TitleH + 18, Color.ParseHex("202020")); break;
        }
    }

    private void DrawRun(IImageProcessingContext ctx, Win w, bool focused)
    {
        Text(ctx, _small, "Type the name of a program and press Enter:", w.X + 14, w.Y + TitleH + 14, Color.ParseHex("202020"));
        Fill(ctx, "FFFFFF", w.X + 14, w.Y + TitleH + 40, w.W - 28, 26);
        Text(ctx, _small, w.Body + (focused ? "_" : ""), w.X + 20, w.Y + TitleH + 45, Color.ParseHex("101010"));
        Text(ctx, _small, "Try: explorer, notepad, cmd", w.X + 14, w.Y + TitleH + 82, Color.ParseHex("707070"));
    }

    private void DrawExplorer(IImageProcessingContext ctx, Win w)
    {
        if (w.Folder is null) return;
        Fill(ctx, "E4E4E4", w.X, w.Y + TitleH, w.W, 24);
        Text(ctx, _small, w.Folder.Path, w.X + 12, w.Y + TitleH + 5, Color.ParseHex("303030"));

        int yy = w.Y + TitleH + 30;
        void Row(string name, bool dir)
        {
            if (yy + 22 > w.Y + w.H - 4) return;
            Fill(ctx, dir ? "E8C24A" : "B7B7B7", w.X + 14, yy + 5, 14, 11);
            Text(ctx, _small, name, w.X + 36, yy + 3, Color.ParseHex("101010"));
            yy += 22;
        }

        if (w.Folder.Parent is not null) Row("..", true);
        foreach (var child in w.Folder.Children) Row(child.Name, child.IsDir);
    }

    private void DrawNotepad(IImageProcessingContext ctx, Win w, bool focused)
    {
        Fill(ctx, "FFFFFF", w.X + 6, w.Y + TitleH + 6, w.W - 12, w.H - TitleH - 12);
        var lines = w.Body.Replace("\r\n", "\n").Split('\n');
        int yy = w.Y + TitleH + 12;
        for (int i = 0; i < lines.Length; i++)
        {
            if (yy > w.Y + w.H - 18) break;
            string line = lines[i];
            if (focused && i == lines.Length - 1) line += "_"; // caret
            Text(ctx, _small, line, w.X + 14, yy, Color.ParseHex("101010"));
            yy += 16;
        }
    }

    private void RenderSecure(IImageProcessingContext ctx)
    {
        ctx.Fill(Color.ParseHex("0A0A14"));
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

    /// <summary>Tiles whose pixels changed since the previous call (all tiles on first call).</summary>
    public IEnumerable<(int X, int Y, int W, int H, ushort[] Pixels)> DirtyTiles(int tile = 64)
    {
        int cols = (Width + tile - 1) / tile;
        int rows = (Height + tile - 1) / tile;
        _tileHash ??= new ulong[cols * rows];

        var buf = new Rgba32[Width * Height];
        _fb.CopyPixelDataTo(buf);

        int idx = 0;
        for (int ty = 0; ty < Height; ty += tile)
            for (int tx = 0; tx < Width; tx += tile, idx++)
            {
                int tw = Math.Min(tile, Width - tx);
                int th = Math.Min(tile, Height - ty);
                var px = new ushort[tw * th];
                ulong hash = 1469598103934665603UL;
                for (int row = 0; row < th; row++)
                    for (int col = 0; col < tw; col++)
                    {
                        var p = buf[(ty + row) * Width + (tx + col)];
                        ushort v = (ushort)(((p.R >> 3) << 11) | ((p.G >> 2) << 5) | (p.B >> 3));
                        px[row * tw + col] = v;
                        hash = (hash ^ v) * 1099511628211UL;
                    }
                if (hash != _tileHash[idx])
                {
                    _tileHash[idx] = hash;
                    yield return (tx, ty, tw, th, px);
                }
            }
    }

    public void Dispose() => _fb.Dispose();
}

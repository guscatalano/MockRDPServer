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
public enum DesktopKind { Default, Secure, Logon }

/// <summary>
/// A software-rendered fake Windows session with a tiny window manager. Owns a framebuffer,
/// a list of draggable windows, a Start menu that launches windows, and a secure/Winlogon
/// desktop reached via the secure-attention sequence. Turns client input into state changes
/// and exposes changed tiles (dirty-rect) for the RDP output path.
/// </summary>
public sealed class FakeDesktop : IDisposable
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public DesktopKind Active { get; set; } = DesktopKind.Default;
    public bool StartMenuOpen { get; set; }
    public int WindowCount => _windows.Count;
    public string FocusedTitle => Focused?.Title ?? "";
    public string FocusedText => Focused?.Body ?? "";
    public bool IsDragging => _drag is not null;

    private enum WinKind { Generic, Explorer, Notepad, Run, Stats, Display }

    private sealed class Win
    {
        public int Id;
        public WinKind Kind = WinKind.Generic;
        public required string Title;
        public string Body = "";
        public VfsNode? Folder;                                   // Explorer: local VFS directory
        public string? ClientPath;                                // Explorer: \\tsclient path (non-null = client mode)
        public List<(string Name, bool IsDir)>? ClientEntries;    // client listing
        public bool Loading;                                      // waiting on an rdpdr result
        public int Scroll;                                        // Explorer: first visible row
        public int X, Y, W, H;
    }

    private const int RowH = 22;

    private int _nextWinId = 1;

    /// <summary>Set by the host to browse the client's redirected files over rdpdr:
    /// OnClientList(winId, path) requests a listing, OnClientOpen(winId, path) reads a file.</summary>
    public Action<int, string>? OnClientList;
    public Action<int, string>? OnClientOpen;

    /// <summary>Set by the host to feed the "Connection Info" window: live (label, value) rows
    /// describing the RDP connection (state, channels, redirected drives, …).</summary>
    public Func<IReadOnlyList<(string Label, string Value)>>? ConnectionStats;

    /// <summary>True while a Connection Info window is open, so the host can tick faster and keep
    /// the live stats (uptime, DVC state) fresh.</summary>
    public bool WantsLiveTick => _windows.Any(w => w.Kind == WinKind.Stats);

    /// <summary>A resolution the user picked in Display settings. The host reads and clears it and
    /// applies it as a server-initiated Deactivation-Reactivation (no client MONITOR_LAYOUT).</summary>
    private (int W, int H)? _requestedResize;
    public (int W, int H)? TakeRequestedResize() { var r = _requestedResize; _requestedResize = null; return r; }

    /// <summary>The resolutions offered by the Display settings window.</summary>
    private static readonly (int W, int H)[] Resolutions =
        [(1024, 768), (1280, 720), (1280, 800), (1600, 900), (1920, 1080)];

    private const string TsClient = "\\\\tsclient";

    private readonly List<Win> _windows = new();   // z-order: last = topmost
    private Win? _drag;
    private Win? _scrollbarDrag;                    // Explorer whose scrollbar thumb is being dragged
    private int _dragDx, _dragDy;
    private bool _ctrl, _alt, _shift;

    private Win? Focused => _windows.Count > 0 ? _windows[^1] : null;

    private readonly VfsNode _vfsRoot;
    private readonly VfsNode _vfsHome;

    private Image<Rgba32> _fb;
    private Image<Rgba32> _wallpaper;   // pre-rendered gradient (recomputing it per frame is slow)
    private readonly Font? _font;
    private readonly Font? _small;
    private ulong[]? _tileHash;

    private const int Taskbar = 44;
    private const int TitleH = 30;
    private const int CloseW = 30;
    private const int StartW = 92;
    private static readonly string[] MenuItems = ["File Explorer", "Notepad", "Connection Info", "Display", "Settings", "Run…"];
    private const int MenuW = 240;
    private const int MenuRow = 36;

    private string _logonUser = "rdpuser";
    private string _logonPassword = "";
    private bool _logonFocusUser;   // false = password field focused

    /// <summary>The user name entered at the logon screen (used for the Save Session Info PDU).</summary>
    public string LogonUser => _logonUser;

    /// <summary>Set true for one poll when the user just signed in, so the host can send the
    /// Server Save Session Info ("logon") PDU. The host reads and clears it.</summary>
    public bool JustSignedIn { get; set; }

    public FakeDesktop(int width, int height, bool logon = false)
    {
        Width = width;
        Height = height;
        Active = logon ? DesktopKind.Logon : DesktopKind.Default;
        _fb = new Image<Rgba32>(width, height);
        _wallpaper = BuildWallpaper(width, height);
        _font = TryLoadFont(15);
        _small = TryLoadFont(12);
        _vfsRoot = Vfs.BuildDefault();
        _vfsHome = Vfs.Home(_vfsRoot);
        _windows.Add(new Win { Id = _nextWinId++, Title = "Welcome to mock-rdp", Body = "Open File Explorer to browse C:\\ or \\\\tsclient (your files), drag windows, Ctrl+Alt+End for the secure desktop.", X = 130, Y = 90, W = 560, H = 300 });
        Render();
    }

    private static Image<Rgba32> BuildWallpaper(int width, int height)
    {
        var img = new Image<Rgba32>(width, height);
        img.Mutate(ctx => ctx.Fill(new LinearGradientBrush(
            new PointF(0, 0), new PointF(0, height), GradientRepetitionMode.None,
            new ColorStop(0f, Color.ParseHex("103A6B")), new ColorStop(1f, Color.ParseHex("2B6AB0")))));
        return img;
    }

    /// <summary>Changes the framebuffer size (a resolution change / Deactivation-Reactivation).
    /// Rebuilds the wallpaper, keeps windows on-screen, and forces a full-frame resend.</summary>
    public void Resize(int width, int height)
    {
        if (width == Width && height == Height) return;
        Width = width;
        Height = height;
        _fb.Dispose();
        _wallpaper.Dispose();
        _fb = new Image<Rgba32>(width, height);
        _wallpaper = BuildWallpaper(width, height);
        _tileHash = null;                       // grid size changed → next DirtyTiles resends everything
        _drag = null;
        _scrollbarDrag = null;
        foreach (var w in _windows)
        {
            w.X = Math.Clamp(w.X, -w.W + 80, Math.Max(0, width - 80));
            w.Y = Math.Clamp(w.Y, 0, Math.Max(0, height - Taskbar - TitleH));
        }
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
                if ((ev.Flags & 0x0200) != 0) return OnWheel(ev.Flags);   // PTRFLAGS_WHEEL
                bool btn1 = (ev.Flags & Input.PtrFlagsButton1) != 0;
                bool down = (ev.Flags & Input.PtrFlagsDown) != 0;
                if (btn1 && down) return OnMouseDown(ev.X, ev.Y);
                if (btn1 && !down) return OnMouseUp();
                if ((_drag is not null || _scrollbarDrag is not null) && (ev.Flags & Input.PtrFlagsMove) != 0) return OnMouseMove(ev.X, ev.Y);
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

        if (Active == DesktopKind.Logon)
        {
            if (scancode == Keys.Enter) return SignIn();           // any credentials accepted
            if (scancode == 0x0F) { _logonFocusUser = !_logonFocusUser; return true; } // Tab
            if (scancode == Keys.Backspace)
            {
                if (_logonFocusUser) { if (_logonUser.Length == 0) return false; _logonUser = _logonUser[..^1]; }
                else { if (_logonPassword.Length == 0) return false; _logonPassword = _logonPassword[..^1]; }
                return true;
            }
            if (Keys.ScancodeToChar(scancode, _shift) is { } lc)
            {
                if (_logonFocusUser) _logonUser += lc; else _logonPassword += lc;
                return true;
            }
            return false;
        }

        // mstsc maps Ctrl+Alt+End → Ctrl+Alt+Del on the wire (Del = 0x53); accept End (0x4F) too.
        if ((scancode == 0x53 || scancode == 0x4F) && _ctrl && _alt) return Switch(DesktopKind.Secure);
        if (scancode == 0x01) // Esc
        {
            if (Active == DesktopKind.Secure) return Switch(DesktopKind.Default);
            if (StartMenuOpen) { StartMenuOpen = false; return true; }
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
            return true;
        }
        if (scancode == Keys.Enter)
        {
            if (f.Kind == WinKind.Run) { RunCommand(f); return true; }
            f.Body += "\r\n";
            return true;
        }
        if (Keys.ScancodeToChar(scancode, _shift) is { } ch)
        {
            f.Body += ch;
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
    }

    private bool OnMouseDown(int x, int y)
    {
        if (Active == DesktopKind.Logon)
        {
            if (InRect(x, y, SignInBtn())) return SignIn();
            if (InRect(x, y, LogonField(true))) { _logonFocusUser = true; return true; }
            if (InRect(x, y, LogonField(false))) { _logonFocusUser = false; return true; }
            return false;
        }
        if (Active == DesktopKind.Secure)
            return InRect(x, y, CancelBtn()) && Switch(DesktopKind.Default);

        int tbY = Height - Taskbar;
        if (InRect(x, y, 0, tbY, StartW, Taskbar)) { StartMenuOpen = !StartMenuOpen; return true; }

        if (StartMenuOpen)
        {
            int my = tbY - MenuItems.Length * MenuRow - 12;
            for (int i = 0; i < MenuItems.Length; i++)
                if (InRect(x, y, 0, my + i * MenuRow + 6, MenuW, MenuRow))
                {
                    Launch(MenuItems[i]);
                    StartMenuOpen = false;
                    return true;
                }
            StartMenuOpen = false;
            return true;
        }

        // Topmost window first.
        for (int i = _windows.Count - 1; i >= 0; i--)
        {
            var w = _windows[i];
            if (InRect(x, y, w.X + w.W - CloseW, w.Y, CloseW, TitleH)) { _windows.RemoveAt(i); return true; }
            if (InRect(x, y, w.X, w.Y, w.W, TitleH)) // title bar → raise + drag
            {
                Raise(i);
                _drag = w; _dragDx = x - w.X; _dragDy = y - w.Y;
                return true; // raised
            }
            if (InRect(x, y, w.X, w.Y, w.W, w.H))
            {
                bool raised = Raise(i);
                if (ScrollbarHit(w, x, y)) { _scrollbarDrag = w; ScrollbarSetFromY(w, y); return true; }
                bool acted = (w.Kind == WinKind.Explorer && ExplorerClick(w, x, y))
                          || (w.Kind == WinKind.Display && DisplayClick(w, x, y));
                return raised || acted;
            }
        }
        return false;
    }

    /// <summary>A click in a File Explorer window: navigate into a folder, up via "..", or
    /// open a file into Notepad.</summary>
    private bool ExplorerClick(Win w, int x, int y)
    {
        int listTop = w.Y + TitleH + 30;
        if (y < listTop) return false;
        int row = (y - listTop) / RowH + w.Scroll;

        if (w.ClientPath is not null) return ClientClick(w, row);
        if (w.Folder is null) return false;

        // Local rows: [ \\tsclient ] [ .. ? ] [ children ]
        if (row == 0)
        {
            w.ClientPath = TsClient; w.ClientEntries = null; w.Loading = true; w.Scroll = 0;
            OnClientList?.Invoke(w.Id, TsClient);
            return true;
        }
        int r = row - 1;
        bool hasParent = w.Folder.Parent is not null;
        if (hasParent && r == 0) { w.Folder = w.Folder.Parent; w.Scroll = 0; return true; }
        int idx = r - (hasParent ? 1 : 0);
        if (idx < 0 || idx >= w.Folder.Children.Count) return false;
        var entry = w.Folder.Children[idx];
        if (entry.IsDir) { w.Folder = entry; w.Scroll = 0; return true; }
        OpenNotepad(entry.Name, entry.Text);
        return true;
    }

    private bool ClientClick(Win w, int row)
    {
        // Client rows: [ .. ] [ entries ]
        if (row == 0)
        {
            var up = ClientParent(w.ClientPath!);
            w.Scroll = 0;
            if (up is null) { w.ClientPath = null; w.ClientEntries = null; w.Loading = false; return true; } // back to local C:
            w.ClientPath = up; w.ClientEntries = null; w.Loading = true;
            OnClientList?.Invoke(w.Id, up);
            return true;
        }
        int idx = row - 1;
        if (w.ClientEntries is null || idx < 0 || idx >= w.ClientEntries.Count) return false;
        var e = w.ClientEntries[idx];
        var child = w.ClientPath!.TrimEnd('\\') + "\\" + e.Name;
        if (e.IsDir) { w.ClientPath = child; w.ClientEntries = null; w.Loading = true; w.Scroll = 0; OnClientList?.Invoke(w.Id, child); return true; }
        OnClientOpen?.Invoke(w.Id, child);
        return true;
    }

    private bool OnMouseMove(int x, int y)
    {
        if (_scrollbarDrag is { } sw) { ScrollbarSetFromY(sw, y); return true; }
        if (_drag is null) return false;
        _drag.X = Math.Clamp(x - _dragDx, -_drag.W + 80, Width - 80);
        _drag.Y = Math.Clamp(y - _dragDy, 0, Height - Taskbar - TitleH);
        return true;
    }

    /// <summary>The Explorer scrollbar track (x, top, height, total rows, visible rows), or null
    /// when the list fits and no scrollbar is shown.</summary>
    private static (int SbX, int Top, int TrackH, int Total, int Visible)? Scrollbar(Win w)
    {
        if (w.Kind != WinKind.Explorer) return null;
        int visible = VisibleRows(w), total = RowCount(w);
        if (total <= visible) return null;
        int listTop = w.Y + TitleH + 30;
        return (w.X + w.W - 10, listTop, visible * RowH, total, visible);
    }

    private static bool ScrollbarHit(Win w, int x, int y) =>
        Scrollbar(w) is { } sb && x >= sb.SbX - 4 && x <= sb.SbX + 10 && y >= sb.Top && y <= sb.Top + sb.TrackH;

    private static void ScrollbarSetFromY(Win w, int y)
    {
        if (Scrollbar(w) is not { } sb) return;
        int max = sb.Total - sb.Visible;
        double frac = (double)(y - sb.Top) / sb.TrackH;
        w.Scroll = Math.Clamp((int)Math.Round(frac * max), 0, max);
    }

    private bool OnWheel(ushort flags)
    {
        var w = Focused;
        if (w is null || w.Kind != WinKind.Explorer) return false;
        int delta = (flags & 0x0100) != 0 ? 3 : -3;   // PTRFLAGS_WHEEL_NEGATIVE → scroll down
        int max = Math.Max(0, RowCount(w) - VisibleRows(w));
        int ns = Math.Clamp(w.Scroll + delta, 0, max);
        if (ns == w.Scroll) return false;
        w.Scroll = ns;
        return true;
    }

    private static int RowCount(Win w)
    {
        if (w.ClientPath is not null) return 1 + (w.ClientEntries?.Count ?? 0);          // ".." + entries
        if (w.Folder is null) return 0;
        return 1 + (w.Folder.Parent is not null ? 1 : 0) + w.Folder.Children.Count;      // \\tsclient + ".." + children
    }

    private static int VisibleRows(Win w) => Math.Max(1, (w.H - TitleH - 34) / RowH);

    private bool OnMouseUp()
    {
        bool wasDragging = _drag is not null || _scrollbarDrag is not null;
        _drag = null;
        _scrollbarDrag = null;
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
            case "Connection Info": Open(new Win { Kind = WinKind.Stats, Title = "Connection Info", W = 540, H = 340 }); break;
            case "Display": Open(new Win { Kind = WinKind.Display, Title = "Display settings", W = 320, H = 290 }); break;
            case "Settings": Open(new Win { Title = "Settings", Body = "Settings.", W = 420, H = 240 }); break;
            default: Open(new Win { Kind = WinKind.Run, Title = "Run", W = 420, H = 160 }); break;
        }
    }

    private void OpenNotepad(string name, string text) =>
        Open(new Win { Kind = WinKind.Notepad, Title = name + " — Notepad", Body = text, W = 460, H = 320 });

    private void Open(Win w)
    {
        int n = _windows.Count;
        w.Id = _nextWinId++;
        w.X = 160 + n * 26;
        w.Y = 110 + n * 26;
        _windows.Add(w);
    }

    private Win? FindWin(int id) => _windows.FirstOrDefault(w => w.Id == id);

    /// <summary>Host delivers a client (\\tsclient) directory listing for a window.</summary>
    public bool DeliverClientList(int winId, string clientPath, IReadOnlyList<(string Name, bool IsDir)> entries)
    {
        var w = FindWin(winId);
        if (w is null) return false;
        w.ClientPath = clientPath;
        w.ClientEntries = new List<(string, bool)>(entries);
        w.Loading = false;
        return true;
    }

    /// <summary>Host delivers a client file's text; opens it in Notepad.</summary>
    public void DeliverClientOpen(string name, string text) => OpenNotepad(name, text);

    private static string? ClientParent(string path)
    {
        var segs = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return segs.Length <= 1 ? null : "\\\\" + string.Join('\\', segs[..^1]);
    }

    private bool Switch(DesktopKind kind)
    {
        if (Active == kind) return false;
        Active = kind;
        StartMenuOpen = false;
        _drag = null;
        _scrollbarDrag = null;
        return true;
    }

    // ── rendering ─────────────────────────────────────────────────────────────

    public void Render() => _fb.Mutate(ctx =>
    {
        switch (Active)
        {
            case DesktopKind.Secure: RenderSecure(ctx); break;
            case DesktopKind.Logon: RenderLogon(ctx); break;
            default: RenderDefault(ctx); break;
        }
    });

    private void RenderDefault(IImageProcessingContext ctx)
    {
        int tbY = Height - Taskbar;
        ctx.DrawImage(_wallpaper, 1f); // cached gradient

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
            case WinKind.Stats: DrawStats(ctx, w); break;
            case WinKind.Display: DrawDisplay(ctx, w); break;
            default: Text(ctx, _small, w.Body, w.X + 14, w.Y + TitleH + 18, Color.ParseHex("202020")); break;
        }

        // Black window border (drawn last so it sits on top of the content).
        ctx.Draw(Color.Black, 2f, new RectangularPolygon(w.X, w.Y, w.W, w.H));
    }

    private const int DisplayRowH = 34;

    private static int DisplayRowsTop(Win w) => w.Y + TitleH + 34;

    /// <summary>A click in the Display settings window: pick a resolution → request a server-initiated
    /// Deactivation-Reactivation to that size.</summary>
    private bool DisplayClick(Win w, int x, int y)
    {
        int top = DisplayRowsTop(w);
        if (y < top || x < w.X + 12 || x > w.X + w.W - 12) return false;
        int row = (y - top) / DisplayRowH;
        if (row < 0 || row >= Resolutions.Length) return false;
        var (rw, rh) = Resolutions[row];
        if (rw == Width && rh == Height) return false;   // already at this size
        _requestedResize = (rw, rh);
        return true;
    }

    private void DrawDisplay(IImageProcessingContext ctx, Win w)
    {
        Fill(ctx, "FFFFFF", w.X + 6, w.Y + TitleH + 6, w.W - 12, w.H - TitleH - 12);
        Text(ctx, _small, "Screen resolution (applied by the server):", w.X + 14, w.Y + TitleH + 10, Color.ParseHex("505050"));
        int top = DisplayRowsTop(w);
        for (int i = 0; i < Resolutions.Length; i++)
        {
            var (rw, rh) = Resolutions[i];
            bool current = rw == Width && rh == Height;
            int ry = top + i * DisplayRowH;
            Fill(ctx, current ? "0E639C" : "EDEDED", w.X + 12, ry, w.W - 24, DisplayRowH - 6);
            Text(ctx, _small, $"{rw} × {rh}" + (current ? "   (current)" : ""),
                w.X + 24, ry + 6, current ? Color.White : Color.ParseHex("101010"));
        }
    }

    private void DrawStats(IImageProcessingContext ctx, Win w)
    {
        Fill(ctx, "FFFFFF", w.X + 6, w.Y + TitleH + 6, w.W - 12, w.H - TitleH - 12);
        var rows = ConnectionStats?.Invoke();
        if (rows is null || rows.Count == 0)
        {
            Text(ctx, _small, "No connection data.", w.X + 16, w.Y + TitleH + 16, Color.ParseHex("808080"));
            return;
        }
        int yy = w.Y + TitleH + 18;
        int labelX = w.X + 18, valueX = w.X + 176;
        foreach (var (label, value) in rows)
        {
            Text(ctx, _small, label, labelX, yy, Color.ParseHex("606060"));
            Text(ctx, _small, value, valueX, yy, Color.ParseHex("101010"));
            yy += 32;
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
        Fill(ctx, "E4E4E4", w.X, w.Y + TitleH, w.W, 24);
        int listTop = w.Y + TitleH + 30;
        int visible = VisibleRows(w);
        int idx = 0, drawn = 0;
        void Row(string name, string iconHex)
        {
            if (idx++ < w.Scroll || drawn >= visible) return;
            int yy = listTop + drawn * RowH;
            Fill(ctx, iconHex, w.X + 14, yy + 5, 14, 11);
            Text(ctx, _small, name, w.X + 36, yy + 3, Color.ParseHex("101010"));
            drawn++;
        }

        if (w.ClientPath is not null)
        {
            Text(ctx, _small, w.ClientPath, w.X + 12, w.Y + TitleH + 5, Color.ParseHex("303030"));
            if (w.Loading) { Text(ctx, _small, "Loading…", w.X + 16, listTop + 3, Color.ParseHex("606060")); return; }
            Row("..", "E8C24A");
            if (w.ClientEntries is not null)
                foreach (var (name, isDir) in w.ClientEntries) Row(name, isDir ? "E8C24A" : "B7B7B7");
        }
        else if (w.Folder is not null)
        {
            Text(ctx, _small, w.Folder.Path, w.X + 12, w.Y + TitleH + 5, Color.ParseHex("303030"));
            Row("\\\\tsclient  (this RDP client)", "4A80E8");
            if (w.Folder.Parent is not null) Row("..", "E8C24A");
            foreach (var child in w.Folder.Children) Row(child.Name, child.IsDir ? "E8C24A" : "B7B7B7");
        }

        // Scrollbar (track + thumb) when the list overflows.
        int total = RowCount(w);
        if (total > visible)
        {
            int sbX = w.X + w.W - 10, trackH = visible * RowH;
            Fill(ctx, "D8D8D8", sbX, listTop, 8, trackH);
            int thumbH = Math.Max(20, trackH * visible / total);
            int thumbY = listTop + (trackH - thumbH) * w.Scroll / Math.Max(1, total - visible);
            Fill(ctx, "9A9A9A", sbX, thumbY, 8, thumbH);
        }
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

    private bool SignIn()   // any credentials accepted
    {
        JustSignedIn = true;                  // host will emit the Save Session Info PDU
        return Switch(DesktopKind.Default) || true;   // always redraw (logon -> desktop)
    }

    private static (int X, int Y, int W, int H) LogonBox(int width, int height)
    {
        int bw = 420, bh = 280;
        return ((width - bw) / 2, (height - bh) / 2, bw, bh);
    }

    private int[] LogonField(bool user)
    {
        var (bx, by, bw, _) = LogonBox(Width, Height);
        return [bx + 24, by + (user ? 66 : 120), bw - 48, 26];
    }

    private int[] SignInBtn()
    {
        var (bx, by, _, _) = LogonBox(Width, Height);
        return [bx + 24, by + 168, 130, 34];
    }

    private void RenderLogon(IImageProcessingContext ctx)
    {
        ctx.Fill(Color.ParseHex("0B2A4A"));
        var (bx, by, bw, bh) = LogonBox(Width, Height);
        Fill(ctx, "12385F", bx, by, bw, bh);
        Text(ctx, _font, "Sign in", bx + 24, by + 18, Color.White);

        Text(ctx, _small, "User name", bx + 24, by + 50, Color.ParseHex("A8BCD4"));
        var uf = LogonField(true);
        Fill(ctx, "FFFFFF", uf[0], uf[1], uf[2], uf[3]);
        Text(ctx, _small, _logonUser + (_logonFocusUser ? "_" : ""), uf[0] + 6, uf[1] + 5, Color.ParseHex("101010"));

        Text(ctx, _small, "Password", bx + 24, by + 104, Color.ParseHex("A8BCD4"));
        var pf = LogonField(false);
        Fill(ctx, "FFFFFF", pf[0], pf[1], pf[2], pf[3]);
        Text(ctx, _small, new string('•', _logonPassword.Length) + (_logonFocusUser ? "" : "_"), pf[0] + 6, pf[1] + 5, Color.ParseHex("101010"));

        var b = SignInBtn();
        Fill(ctx, "2A6AB0", b[0], b[1], b[2], b[3]);
        Text(ctx, _small, "Sign in", b[0] + 34, b[1] + 9, Color.White);
        Text(ctx, _small, "Mock logon — any credentials are accepted. (No NLA; sign in at the desktop.)", bx + 24, by + bh - 26, Color.ParseHex("7C90A8"));
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

    public void Dispose() { _fb.Dispose(); _wallpaper.Dispose(); }
}

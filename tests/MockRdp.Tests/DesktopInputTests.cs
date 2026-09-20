using MockRdp.Desktop;
using MockRdp.Rdp;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// The fake desktop's input → state logic (headless — no RDP, no framebuffer assertions):
/// the Secure Attention Sequence flips to the secure desktop, and clicks drive the Start
/// menu and the window close button.
/// </summary>
public class DesktopInputTests
{
    private static InputEvent Key(byte scancode, bool release = false) =>
        new(InputEventType.Scancode, (ushort)(release ? 1 : 0), 0, 0, scancode);

    private static InputEvent LeftClick(ushort x, ushort y) =>
        new(InputEventType.Mouse, (ushort)(Input.PtrFlagsDown | Input.PtrFlagsButton1), x, y, 0);

    private static void Type(FakeDesktop d, string s)
    {
        var map = new Dictionary<char, byte>
        {
            ['h'] = 0x23, ['i'] = 0x17, ['n'] = 0x31, ['o'] = 0x18, ['t'] = 0x14,
            ['e'] = 0x12, ['p'] = 0x19, ['a'] = 0x1E, ['d'] = 0x20,
        };
        foreach (var c in s) d.OnInput(Key(map[c]));
    }

    [Fact]
    public void CtrlAltEnd_SwitchesToSecure_EscReturns()
    {
        using var d = new FakeDesktop(1024, 768);
        Assert.Equal(DesktopKind.Default, d.Active);

        d.OnInput(Key(0x1D)); // Ctrl down
        d.OnInput(Key(0x38)); // Alt down
        Assert.True(d.OnInput(Key(0x4F))); // End -> SAS (state changed)
        Assert.Equal(DesktopKind.Secure, d.Active);

        Assert.True(d.OnInput(Key(0x01))); // Esc returns
        Assert.Equal(DesktopKind.Default, d.Active);
    }

    [Fact]
    public void End_WithoutCtrlAlt_DoesNotSwitch()
    {
        using var d = new FakeDesktop(1024, 768);
        Assert.False(d.OnInput(Key(0x4F)));
        Assert.Equal(DesktopKind.Default, d.Active);
    }

    [Fact]
    public void ClickStart_TogglesMenu()
    {
        using var d = new FakeDesktop(1024, 768);
        Assert.True(d.OnInput(LeftClick(10, 748))); // Start button
        Assert.True(d.StartMenuOpen);
        Assert.True(d.OnInput(LeftClick(10, 748))); // toggle closed
        Assert.False(d.StartMenuOpen);
    }

    [Fact]
    public void ClickClose_ClosesWindow()
    {
        using var d = new FakeDesktop(1024, 768);
        Assert.Equal(1, d.WindowCount);
        Assert.True(d.OnInput(LeftClick(670, 105))); // the Welcome window's close button (x=130, w=560)
        Assert.Equal(0, d.WindowCount);
    }

    [Fact]
    public void StartMenu_LaunchesWindow_AndDragMovesIt()
    {
        using var d = new FakeDesktop(1024, 768);
        Assert.Equal(1, d.WindowCount);

        d.OnInput(LeftClick(10, 748));               // open Start menu
        Assert.True(d.OnInput(LeftClick(20, 724 - 160 + 10))); // click the first menu item
        Assert.Equal(2, d.WindowCount);              // a window launched

        // Drag the launched window by its title bar (press → move → release).
        d.OnInput(LeftClick(300, 146));              // press on title bar of the launched window
        var moved = d.OnInput(new InputEvent(InputEventType.Mouse, Input.PtrFlagsMove, 340, 200, 0));
        Assert.True(moved);                          // dragging re-renders
    }

    [Fact]
    public void Explorer_BrowsesFolder_AndOpensFileInNotepad()
    {
        using var d = new FakeDesktop(1024, 768);
        d.OnInput(LeftClick(10, 748));               // Start
        d.OnInput(LeftClick(20, 574));               // File Explorer (opens at the user's home)
        Assert.Equal(2, d.WindowCount);

        // Rows now: \\tsclient(196), ..(218), Documents(240) — click Documents, then readme.txt.
        d.OnInput(LeftClick(250, 250));              // navigate into Documents (a folder → no new window)
        Assert.Equal(2, d.WindowCount);
        d.OnInput(LeftClick(250, 250));              // open readme.txt (a file → Notepad opens)
        Assert.Equal(3, d.WindowCount);
    }

    [Fact]
    public void Keyboard_TypesIntoNotepad()
    {
        using var d = new FakeDesktop(1024, 768);
        d.OnInput(LeftClick(10, 748)); d.OnInput(LeftClick(20, 620)); // Start → Notepad
        Type(d, "hi");
        Assert.Equal("hi", d.FocusedText);
    }

    [Fact]
    public void Run_TypeProgramName_Launches()
    {
        using var d = new FakeDesktop(1024, 768);
        d.OnInput(LeftClick(10, 748)); d.OnInput(LeftClick(20, 690)); // Start → Run…
        Type(d, "notepad");
        d.OnInput(Key(0x1C));                         // Enter
        Assert.Contains("Notepad", d.FocusedTitle);   // Run launched Notepad
    }
}

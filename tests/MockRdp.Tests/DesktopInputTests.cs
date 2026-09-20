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
        Assert.True(d.WindowOpen);
        Assert.True(d.OnInput(LeftClick(590, 100))); // window close button
        Assert.False(d.WindowOpen);
    }
}

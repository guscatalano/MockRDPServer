using System.Runtime.InteropServices;

namespace RdpAxClient;

/// <summary>
/// Auto-accepts the RDP client's per-connection "certificate not trusted" warning dialog by
/// clicking its Yes button. This is transient UI handling only — it persists nothing (no cert
/// store or registry changes), analogous to FreeRDP's /cert:ignore, so a self-signed test
/// server can be reached headlessly.
/// </summary>
internal static class Native
{
    private const uint BM_CLICK = 0x00F5;
    private const int CertWarningYesButtonId = 14004; // "&Yes" on the Remote Desktop cert warning

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowTitle);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>If the cert-trust warning is showing, clicks Yes. Returns true if it was clicked.</summary>
    public static bool AcceptCertWarningIfPresent()
    {
        var dialog = FindWindow("#32770", "Remote Desktop Connection");
        if (dialog == IntPtr.Zero) return false;

        IntPtr yesButton = IntPtr.Zero;
        EnumChildWindows(dialog, (hwnd, _) =>
        {
            if (GetDlgCtrlID(hwnd) == CertWarningYesButtonId) { yesButton = hwnd; return false; }
            return true;
        }, IntPtr.Zero);

        if (yesButton == IntPtr.Zero) return false;
        SendMessage(yesButton, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        return true;
    }
}

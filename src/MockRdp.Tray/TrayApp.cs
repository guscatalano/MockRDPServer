using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Text;
using System.Windows.Forms;
using Microsoft.Extensions.Logging.Abstractions;
using MockRdp.Server;
using MockRdp.Transport;

namespace MockRdp.Tray;

/// <summary>
/// A system-tray command &amp; control for the mock RDP server: hosts the server in-process and
/// launches Remote Desktop against it with a chosen preset (resolution, drive redirection).
/// </summary>
internal sealed class TrayApp : IDisposable
{
    private const int Port = 33389;

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private ToolStripMenuItem _statusItem = null!;
    private ToolStripMenuItem _toggleItem = null!;

    private RdpListener? _listener;
    private CancellationTokenSource? _cts;

    private bool Running => _listener is not null;

    private sealed record Preset(string Label, int Width, int Height, bool DriveRedirect);

    private static readonly Preset[] Presets =
    [
        new("Default — 1024 × 768", 1024, 768, false),
        new("1280 × 800", 1280, 800, false),
        new("1440 × 900", 1440, 900, false),
        new("1920 × 1080", 1920, 1080, false),
        new("1024 × 768 + drive redirect", 1024, 768, true),
    ];

    public TrayApp()
    {
        _icon = new NotifyIcon { Icon = MakeIcon(), Visible = true };
        BuildMenu();
        _icon.ContextMenuStrip = _menu;
        _icon.DoubleClick += (_, _) => Connect(Presets[0]);
        StartServer();
        Balloon("Mock RDP is running on 127.0.0.1:" + Port + ". Right-click the tray icon to connect.");
    }

    private void BuildMenu()
    {
        _statusItem = new ToolStripMenuItem("Server: starting…") { Enabled = false };
        _menu.Items.Add(_statusItem);

        _toggleItem = new ToolStripMenuItem("Stop server", null, (_, _) => Toggle());
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(new ToolStripSeparator());

        var connect = new ToolStripMenuItem("Connect Remote Desktop");
        foreach (var preset in Presets)
            connect.DropDownItems.Add(new ToolStripMenuItem(preset.Label, null, (_, _) => Connect(preset)));
        _menu.Items.Add(connect);

        _menu.Items.Add(new ToolStripMenuItem("Save .rdp to Desktop", null, (_, _) => SaveRdpToDesktop()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));
    }

    // ── server lifecycle ──────────────────────────────────────────────────

    private void StartServer()
    {
        if (Running) return;
        var cert = CertProvider.CreateSelfSigned();
        _cts = new CancellationTokenSource();
        _listener = new RdpListener(IPAddress.Loopback, Port, cert, NullLoggerFactory.Instance,
            dvcChannels: null, rdpdrReads: null, dvcBehaviors: null,
            rdpdrLists: null, rdpdrWrites: null, desktop: true, logon: true);
        _listener.Start();
        _ = _listener.AcceptLoopAsync(_cts.Token);
        UpdateStatus();
    }

    private void StopServer()
    {
        _cts?.Cancel();
        _listener?.Dispose();
        _listener = null;
        _cts = null;
        UpdateStatus();
    }

    private void Toggle()
    {
        if (Running) StopServer();
        else StartServer();
    }

    private void UpdateStatus()
    {
        _statusItem.Text = Running ? $"Server: running on :{Port}" : "Server: stopped";
        _toggleItem.Text = Running ? "Stop server" : "Start server";
        _icon.Text = Running ? $"Mock RDP — running on :{Port}" : "Mock RDP — stopped";
    }

    // ── client ────────────────────────────────────────────────────────────

    private void Connect(Preset preset)
    {
        if (!Running) StartServer();
        try
        {
            var rdp = WriteRdp(preset);
            Process.Start(new ProcessStartInfo("mstsc.exe", $"\"{rdp}\"") { UseShellExecute = true });
            if (preset.DriveRedirect)
                Balloon("mstsc will warn about sharing your drives — click Connect to allow \\\\tsclient.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't launch Remote Desktop:\n" + ex.Message, "Mock RDP",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string Rdp(Preset p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"full address:s:127.0.0.1:{Port}");
        sb.AppendLine("authentication level:i:2");
        sb.AppendLine("enablecredsspsupport:i:0");
        sb.AppendLine("prompt for credentials:i:0");
        sb.AppendLine("screen mode id:i:1");
        sb.AppendLine($"desktopwidth:i:{p.Width}");
        sb.AppendLine($"desktopheight:i:{p.Height}");
        sb.AppendLine("dynamic resolution:i:1");
        sb.AppendLine("smart sizing:i:0");
        if (p.DriveRedirect) sb.AppendLine("drivestoredirect:s:*");
        return sb.ToString();
    }

    private static string WriteRdp(Preset p)
    {
        var path = Path.Combine(Path.GetTempPath(), "mock-rdp-tray.rdp");
        File.WriteAllText(path, Rdp(p), Encoding.ASCII);   // mstsc wants ASCII
        return path;
    }

    private void SaveRdpToDesktop()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "mock-rdp.rdp");
        File.WriteAllText(path, Rdp(Presets[0]), Encoding.ASCII);
        Balloon("Saved " + path + " — double-click it to connect any time.");
    }

    // ── plumbing ──────────────────────────────────────────────────────────

    private void Balloon(string text) =>
        _icon.ShowBalloonTip(4000, "Mock RDP", text, ToolTipIcon.Info);

    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(Color.FromArgb(0x0E, 0x63, 0x9C));
            g.FillRectangle(fill, 1, 1, 14, 14);
            using var font = new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("R", font, Brushes.White, 3, 3);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void Exit()
    {
        StopServer();
        _icon.Visible = false;
        Application.Exit();
    }

    public void Dispose()
    {
        StopServer();
        _icon.Dispose();
        _menu.Dispose();
    }
}

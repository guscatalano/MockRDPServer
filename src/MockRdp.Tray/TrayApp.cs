using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using MockRdp.Server;
using MockRdp.Transport;

namespace MockRdp.Tray;

/// <summary>
/// A system-tray command &amp; control for the mock RDP server: hosts the server in-process and
/// launches Remote Desktop against it with a chosen resolution and a set of redirections.
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

    private readonly ActivityLog _activityLog = new();   // captures the server log for the monitor
    private LogWindow? _logWindow;

    private bool Running => _listener is not null;

    private sealed record Preset(string Label, int Width, int Height);

    private static readonly Preset[] Presets =
    [
        new("Default — 1024 × 768", 1024, 768),
        new("1280 × 800", 1280, 800),
        new("1440 × 900", 1440, 900),
        new("1920 × 1080", 1920, 1080),
    ];

    // Which resources the generated .rdp asks mstsc to redirect. All on by default.
    private sealed class Redir
    {
        public bool Drives = true, Clipboard = true, Printers = true, SmartCards = true, ComPorts = true, Audio = true;
    }

    private readonly Redir _redir = new();
    private bool _bootToDesktop;   // skip the fake logon screen and boot straight to the desktop

    private static string CertPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MockRdp", "dev-cert.pfx");

    public TrayApp()
    {
        _icon = new NotifyIcon { Icon = MakeIcon(), Visible = true };
        BuildMenu();
        _icon.ContextMenuStrip = _menu;
        _icon.DoubleClick += (_, _) => Connect(Presets[0]);
        StartServer();
        Balloon($"Mock RDP is running on 127.0.0.1:{Port}. Right-click the tray icon to connect.");
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

        // Redirections included in the .rdp — each is a live checkbox, all ticked by default.
        var redir = new ToolStripMenuItem("Redirections (written to the .rdp)");
        void AddRedir(string label, Func<bool> get, Action<bool> set)
        {
            var item = new ToolStripMenuItem(label) { CheckOnClick = true, Checked = get() };
            item.CheckedChanged += (_, _) => set(item.Checked);
            redir.DropDownItems.Add(item);
        }
        AddRedir(@"Drives (\\tsclient)", () => _redir.Drives, v => _redir.Drives = v);
        AddRedir("Clipboard", () => _redir.Clipboard, v => _redir.Clipboard = v);
        AddRedir("Printers", () => _redir.Printers, v => _redir.Printers = v);
        AddRedir("Smart cards", () => _redir.SmartCards, v => _redir.SmartCards = v);
        AddRedir("COM ports", () => _redir.ComPorts, v => _redir.ComPorts = v);
        AddRedir("Audio", () => _redir.Audio, v => _redir.Audio = v);
        _menu.Items.Add(redir);

        var boot = new ToolStripMenuItem("Start at the desktop (skip logon)") { CheckOnClick = true, Checked = _bootToDesktop };
        boot.ToolTipText = "Off: mstsc lands on the mock logon screen. On: it boots straight to the desktop.\n"
                         + "(The mock is TLS-only and cannot do NLA — real NLA needs your password, so it's not offered.)";
        boot.CheckedChanged += (_, _) => { _bootToDesktop = boot.Checked; RestartServer(); };
        _menu.Items.Add(boot);

        _menu.Items.Add(new ToolStripMenuItem("Activity log…", null, (_, _) => ShowLog()));
        _menu.Items.Add(new ToolStripMenuItem("Save .rdp to Desktop", null, (_, _) => SaveRdpToDesktop()));
        _menu.Items.Add(new ToolStripMenuItem("Trust server certificate (one-time)", null, (_, _) => TrustCert()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));
    }

    // ── server lifecycle ──────────────────────────────────────────────────

    private void StartServer()
    {
        if (Running) return;
        // A stable dev cert (persisted) so its thumbprint doesn't change each launch — then pin it
        // per-server in the registry so headless clients (mstscax) connect without a cert prompt.
        var cert = CertProvider.GetOrCreatePersistent(CertPath);
        PinServerCert(cert.GetCertHash());
        _cts = new CancellationTokenSource();
        // Open ECHO (for the DVC Console demo) plus RDPeek's diagnostics channels, so a registered
        // RDPeek plugin connects and its Hello handshake is answered by the built-in diag responder.
        _listener = new RdpListener(IPAddress.Loopback, Port, cert, _activityLog,
            dvcChannels: ["ECHO", "dvc::diag::inspector", "dvc::diag::files"], rdpdrReads: null, dvcBehaviors: null,
            rdpdrLists: null, rdpdrWrites: null, desktop: true, logon: true, desktopDirect: _bootToDesktop);
        _listener.Start();
        _ = _listener.AcceptLoopAsync(_cts.Token);
        UpdateStatus();
    }

    /// <summary>Pins the server cert's SHA-1 hash per-server in the RDP client registry, so mstsc/
    /// mstscax accept it for 127.0.0.1/localhost without prompting (no global Root-store trust).</summary>
    private static void PinServerCert(byte[] certHash)
    {
        foreach (var server in new[] { "127.0.0.1", "localhost", $"127.0.0.1:{Port}", $"localhost:{Port}" })
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(
                    $@"Software\Microsoft\Terminal Server Client\Servers\{server}");
                k?.SetValue("CertHash", certHash, RegistryValueKind.Binary);
            }
            catch { /* best-effort */ }
        }
    }

    private void RestartServer()
    {
        if (!Running) return;   // will pick up the new setting next time it starts
        StopServer();
        StartServer();
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
            if (_redir.Drives)
                Balloon("mstsc will warn about sharing your resources — click Connect to allow them.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't launch Remote Desktop:\n" + ex.Message, "Mock RDP",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private string Rdp(Preset p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"full address:s:127.0.0.1:{Port}");
        sb.AppendLine("authentication level:i:2");
        sb.AppendLine("enablecredsspsupport:i:0");   // mock is TLS-only — never request NLA/CredSSP
        sb.AppendLine("prompt for credentials:i:0");
        sb.AppendLine("screen mode id:i:1");
        sb.AppendLine($"desktopwidth:i:{p.Width}");
        sb.AppendLine($"desktopheight:i:{p.Height}");
        sb.AppendLine("dynamic resolution:i:1");
        sb.AppendLine("smart sizing:i:0");
        if (_redir.Drives) sb.AppendLine("drivestoredirect:s:*");
        sb.AppendLine($"redirectclipboard:i:{(_redir.Clipboard ? 1 : 0)}");
        sb.AppendLine($"redirectprinters:i:{(_redir.Printers ? 1 : 0)}");
        sb.AppendLine($"redirectsmartcards:i:{(_redir.SmartCards ? 1 : 0)}");
        sb.AppendLine($"redirectcomports:i:{(_redir.ComPorts ? 1 : 0)}");
        sb.AppendLine($"audiomode:i:{(_redir.Audio ? 0 : 2)}");   // 0 = play on this computer (redirect), 2 = do not play
        return sb.ToString();
    }

    private string WriteRdp(Preset p)
    {
        var path = Path.Combine(Path.GetTempPath(), "mock-rdp-tray.rdp");
        File.WriteAllText(path, Rdp(p), Encoding.ASCII);   // mstsc wants ASCII
        return path;
    }

    /// <summary>Installs the (stable) server cert into the user's Trusted Root store so headless
    /// clients — the hosted mstscax control — connect without a cert warning. One-time: the cert is
    /// persistent, so its thumbprint doesn't change across launches. Windows shows one confirm prompt.</summary>
    private void TrustCert()
    {
        try
        {
            using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(CertPath);
            using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                System.Security.Cryptography.X509Certificates.StoreName.Root,
                System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
            store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite);
            store.Add(cert);
            store.Close();
            Balloon("Server certificate trusted. Headless clients (the Bootstrap) now connect without a prompt.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't trust the certificate:\n" + ex.Message, "Mock RDP",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Open (or focus) the live activity monitor showing the server's log stream.</summary>
    private void ShowLog()
    {
        if (_logWindow is null || _logWindow.IsDisposed)
        {
            _logWindow = new LogWindow(_activityLog);
            _logWindow.FormClosed += (_, _) => _logWindow = null;
            _logWindow.Show();
        }
        else
        {
            if (_logWindow.WindowState == FormWindowState.Minimized) _logWindow.WindowState = FormWindowState.Normal;
            _logWindow.Activate();
            _logWindow.BringToFront();
        }
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
        _logWindow?.Close();
        _activityLog.Dispose();
        _icon.Dispose();
        _menu.Dispose();
    }
}

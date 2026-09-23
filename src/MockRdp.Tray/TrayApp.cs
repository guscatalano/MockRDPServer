using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Text;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MockRdp.Rdp;
using MockRdp.Server;
using MockRdp.Transport;

namespace MockRdp.Tray;

/// <summary>
/// A system-tray command &amp; control for the mock RDP server: hosts the server in-process, launches
/// Remote Desktop against it, and exposes the common knobs (port, LAN bind, DVC channels, desktop,
/// redirections, auto-connect, start-at-logon) as menu toggles. Settings persist in the registry so
/// the tray comes back the way you left it.
/// </summary>
internal sealed class TrayApp : IDisposable
{
    private const string SettingsKey = @"Software\MockRdp\Tray";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "MockRdpTray";
    private static readonly string[] DefaultChannels = ["ECHO", "dvc::diag::inspector", "dvc::diag::files"];

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private ToolStripMenuItem _statusItem = null!;
    private ToolStripMenuItem _toggleItem = null!;

    private RdpListener? _listener;
    private CancellationTokenSource? _cts;

    private readonly ActivityLog _activityLog = new();
    private readonly Desktop.VfsNode _sharedVfs = Desktop.Vfs.BuildDefault();
    private LogWindow? _logWindow;
    private FileBrowserWindow? _filesWindow;

    // ── settings (persisted) ───────────────────────────────────────────────
    private int _port = 33389;
    private bool _bindAny;                 // false = 127.0.0.1 only; true = 0.0.0.0 (LAN)
    private bool _bootToDesktop;           // skip the fake logon screen
    private bool _autoConnect;             // launch mstsc automatically when the server starts
    private LogLevel _logLevel = LogLevel.Information;
    private uint _encMethod = MockRdp.Rdp.StandardSecurity.Method128Bit;  // preferred RC4 method for PROTOCOL_RDP
    private bool _encHigh;                  // ENCRYPTION_LEVEL_HIGH (encrypt both directions)
    private bool _nla;                      // NLA / CredSSP (validated by SSPI against this host)
    private readonly HashSet<string> _channels = new(DefaultChannels, StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pluginPaths = new();   // server-side DVC plugin DLLs to load
    private ToolStripMenuItem _pluginStatus = null!;
    private string _freeRdpPath = "";                     // remembered wfreerdp/xfreerdp path
    private readonly Redir _redir = new();

    private bool Running => _listener is not null;

    private sealed record Preset(string Label, int Width, int Height);

    private static readonly Preset[] Presets =
    [
        new("Default — 1024 × 768", 1024, 768),
        new("1280 × 800", 1280, 800),
        new("1440 × 900", 1440, 900),
        new("1920 × 1080", 1920, 1080),
    ];

    private static readonly int[] PortPresets = [3389, 3390, 33389, 33390];
    private static readonly string[] KnownChannels = ["ECHO", "dvc::diag::inspector", "dvc::diag::files"];

    private sealed class Redir
    {
        public bool Drives = true, Clipboard = true, Printers = true, SmartCards = true, ComPorts = true, Audio = true;
    }

    private static string CertPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MockRdp", "dev-cert.pfx");

    public TrayApp()
    {
        LoadSettings();
        _activityLog.MinLevel = _logLevel;
        _icon = new NotifyIcon { Icon = Branding.MakeIcon(16), Visible = true };
        BuildMenu();
        _icon.ContextMenuStrip = _menu;
        _icon.DoubleClick += (_, _) => Connect(Presets[0]);
        StartServer();
        Balloon($"Mock RDP is running on {(_bindAny ? "0.0.0.0" : "127.0.0.1")}:{_port}. Right-click the tray icon to connect.");
    }

    // ── menu ────────────────────────────────────────────────────────────────

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

        // FreeRDP path: works where a managed mstsc is forced to the legacy RDP security layer and
        // can't offer TLS — FreeRDP isn't bound by that policy and speaks TLS to the mock.
        var free = new ToolStripMenuItem("Connect with FreeRDP");
        foreach (var preset in Presets)
            free.DropDownItems.Add(new ToolStripMenuItem(preset.Label, null, (_, _) => ConnectFreeRdp(preset)));
        _menu.Items.Add(free);

        // Port — presets plus a custom entry.
        var portMenu = new ToolStripMenuItem("Port");
        foreach (var p in PortPresets)
        {
            var item = new ToolStripMenuItem(p.ToString()) { CheckOnClick = false, Checked = p == _port, Tag = p };
            item.Click += (_, _) => SetPort((int)item.Tag);
            portMenu.DropDownItems.Add(item);
        }
        portMenu.DropDownItems.Add(new ToolStripSeparator());
        portMenu.DropDownItems.Add(new ToolStripMenuItem("Custom…", null, (_, _) =>
        {
            var np = PromptPort(_port);
            if (np is int v) SetPort(v);
        }));
        _menu.Items.Add(portMenu);

        AddToggle("Listen on all interfaces (LAN)", () => _bindAny, v => { _bindAny = v; Save(); RestartServer(); UpdateStatus(); },
            "Off: 127.0.0.1 only. On: 0.0.0.0, so other machines on your network can connect.");

        // DVC channels the server opens.
        var dvc = new ToolStripMenuItem("DVC channels");
        foreach (var ch in KnownChannels)
        {
            var item = new ToolStripMenuItem(ch) { CheckOnClick = true, Checked = _channels.Contains(ch) };
            item.CheckedChanged += (_, _) =>
            {
                if (item.Checked) _channels.Add(ch); else _channels.Remove(ch);
                Save();
                RestartServer();
            };
            dvc.DropDownItems.Add(item);
        }
        _menu.Items.Add(dvc);

        // Server-side DVC plugins — load a DLL implementing IServerDvcPlugin (MockRdp.Plugin).
        var pluginMenu = new ToolStripMenuItem("Server-side DVC plugin");
        _pluginStatus = new ToolStripMenuItem(PluginStatusText()) { Enabled = false };
        pluginMenu.DropDownItems.Add(_pluginStatus);
        pluginMenu.DropDownItems.Add(new ToolStripMenuItem("Load plugin DLL…", null, (_, _) => LoadPluginDialog()));
        pluginMenu.DropDownItems.Add(new ToolStripMenuItem("Clear plugins", null, (_, _) => ClearPlugins()));
        _menu.Items.Add(pluginMenu);

        var redir = new ToolStripMenuItem("Redirections (written to the .rdp)");
        void AddRedir(string label, Func<bool> get, Action<bool> set)
        {
            var item = new ToolStripMenuItem(label) { CheckOnClick = true, Checked = get() };
            item.CheckedChanged += (_, _) => { set(item.Checked); Save(); };
            redir.DropDownItems.Add(item);
        }
        AddRedir(@"Drives (\\tsclient)", () => _redir.Drives, v => _redir.Drives = v);
        AddRedir("Clipboard", () => _redir.Clipboard, v => _redir.Clipboard = v);
        AddRedir("Printers", () => _redir.Printers, v => _redir.Printers = v);
        AddRedir("Smart cards", () => _redir.SmartCards, v => _redir.SmartCards = v);
        AddRedir("COM ports", () => _redir.ComPorts, v => _redir.ComPorts = v);
        AddRedir("Audio", () => _redir.Audio, v => _redir.Audio = v);
        _menu.Items.Add(redir);

        AddToggle("Start at the desktop (skip logon)", () => _bootToDesktop, v => { _bootToDesktop = v; Save(); RestartServer(); },
            "Off: mstsc lands on the mock logon screen. On: it boots straight to the desktop.");

        AddToggle("Auto-connect when the server starts", () => _autoConnect, v => { _autoConnect = v; Save(); },
            "Launch Remote Desktop automatically whenever the server starts.");

        AddToggle("Start Mock RDP at logon", IsRunAtLogon, SetRunAtLogon,
            "Add/remove this tray app from the Windows startup (Run) key.");

        // Log level — how much the Activity log captures. Lowering it surfaces Debug/Trace the
        // server emits; radio-style so exactly one is ticked.
        var logLevel = new ToolStripMenuItem("Log level");
        foreach (var lvl in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error })
        {
            var item = new ToolStripMenuItem(lvl.ToString()) { Checked = lvl == _logLevel, Tag = lvl };
            item.Click += (_, _) => SetLogLevel((LogLevel)item.Tag);
            logLevel.DropDownItems.Add(item);
        }
        _menu.Items.Add(logLevel);

        // Security — the RDP security layer offered to clients. TLS and RDSTLS are always on; these
        // control Standard RDP Security (PROTOCOL_RDP) and NLA (PROTOCOL_HYBRID).
        var security = new ToolStripMenuItem("Security");
        var encMenu = new ToolStripMenuItem("RDP encryption (PROTOCOL_RDP)");
        foreach (var (label, method) in new (string, uint)[]
                 {
                     ("128-bit RC4", MockRdp.Rdp.StandardSecurity.Method128Bit),
                     ("56-bit RC4", MockRdp.Rdp.StandardSecurity.Method56Bit),
                     ("40-bit RC4", MockRdp.Rdp.StandardSecurity.Method40Bit),
                     ("None (clear)", 0u),
                 })
        {
            var item = new ToolStripMenuItem(label) { Checked = method == _encMethod, Tag = method };
            item.Click += (_, _) => SetEncMethod((uint)item.Tag);
            encMenu.DropDownItems.Add(item);
        }
        security.DropDownItems.Add(encMenu);
        AddToggleTo(security, "High encryption (encrypt both directions)", () => _encHigh,
            v => { _encHigh = v; Save(); RestartServer(); },
            "Standard RDP Security level. Off = Low (client→server only); On = High (both ways).");
        AddToggleTo(security, "NLA / CredSSP (validated against this PC)", () => _nla,
            v => { _nla = v; Save(); RestartServer(); },
            "Accept PROTOCOL_HYBRID (NLA). Credentials are validated by Windows against THIS machine — "
            + "use a local account. Off: a HYBRID request is downgraded to plain TLS.");
        _menu.Items.Add(security);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Activity log…", null, (_, _) => ShowLog()));
        _menu.Items.Add(new ToolStripMenuItem("Browse server files…", null, (_, _) => ShowFiles()));
        _menu.Items.Add(new ToolStripMenuItem("Save .rdp to Desktop", null, (_, _) => SaveRdpToDesktop()));
        _menu.Items.Add(new ToolStripMenuItem("Trust server certificate (one-time)", null, (_, _) => TrustCert()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Exit()));
    }

    private void AddToggle(string label, Func<bool> get, Action<bool> set, string? tip = null) =>
        AddToggleTo(null, label, get, set, tip);

    /// <summary>Adds a checkable toggle to <paramref name="parent"/>'s dropdown, or to the root menu
    /// when <paramref name="parent"/> is null.</summary>
    private void AddToggleTo(ToolStripMenuItem? parent, string label, Func<bool> get, Action<bool> set, string? tip = null)
    {
        var item = new ToolStripMenuItem(label) { CheckOnClick = true, Checked = get() };
        if (tip is not null) item.ToolTipText = tip;
        item.CheckedChanged += (_, _) => set(item.Checked);
        (parent?.DropDownItems ?? _menu.Items).Add(item);
    }

    private void SetEncMethod(uint method)
    {
        if (method == _encMethod) return;
        _encMethod = method;
        Save();
        foreach (ToolStripItem top in _menu.Items)
            if (top is ToolStripMenuItem { Text: "Security" } sec)
                foreach (ToolStripItem d in sec.DropDownItems)
                    if (d is ToolStripMenuItem { Text: "RDP encryption (PROTOCOL_RDP)" } em)
                        foreach (ToolStripItem mi in em.DropDownItems)
                            if (mi is ToolStripMenuItem { Tag: uint tm } item) item.Checked = tm == method;
        RestartServer();
    }

    private void SetPort(int port)
    {
        if (port == _port || port < 1 || port > 65535) return;
        _port = port;
        Save();
        // refresh the checkmarks in the Port submenu
        foreach (var top in _menu.Items)
            if (top is ToolStripMenuItem { Text: "Port" } pm)
                foreach (var d in pm.DropDownItems)
                    if (d is ToolStripMenuItem { Tag: int tp } pi) pi.Checked = tp == _port;
        RestartServer();
        UpdateStatus();
        Balloon($"Port set to {_port}.");
    }

    private string PluginStatusText() => _pluginPaths.Count == 0
        ? "No plugin loaded"
        : $"{_pluginPaths.Count} DLL(s): {string.Join(", ", _pluginPaths.Select(Path.GetFileName))}";

    private void LoadPluginDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Plugin DLL (*.dll)|*.dll",
            Title = "Load a server-side DVC plugin (implements IServerDvcPlugin)",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        if (!_pluginPaths.Contains(dlg.FileName, StringComparer.OrdinalIgnoreCase))
            _pluginPaths.Add(dlg.FileName);
        _pluginStatus.Text = PluginStatusText();
        Save();
        RestartServer();
        Balloon($"Loaded {Path.GetFileName(dlg.FileName)} — its channels open on the next connection.");
    }

    private void ClearPlugins()
    {
        if (_pluginPaths.Count == 0) return;
        _pluginPaths.Clear();
        _pluginStatus.Text = PluginStatusText();
        Save();
        RestartServer();
    }

    private void SetLogLevel(LogLevel level)
    {
        _logLevel = level;
        _activityLog.MinLevel = level;
        Save();
        foreach (var top in _menu.Items)
            if (top is ToolStripMenuItem { Text: "Log level" } lm)
                foreach (var d in lm.DropDownItems)
                    if (d is ToolStripMenuItem { Tag: LogLevel tl } li) li.Checked = tl == level;
    }

    private static int? PromptPort(int current)
    {
        using var f = new Form
        {
            Text = "Mock RDP — port",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(248, 96),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var label = new Label { Text = "Listen on port:", Left = 14, Top = 14, AutoSize = true };
        var num = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = current, Left = 16, Top = 36, Width = 210 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 68, Top = 64, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 150, Top = 64, Width = 75 };
        f.Controls.AddRange([label, num, ok, cancel]);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? (int)num.Value : null;
    }

    // ── server lifecycle ──────────────────────────────────────────────────

    private void StartServer()
    {
        if (Running) return;
        var cert = CertProvider.GetOrCreatePersistent(CertPath);
        PinServerCert(cert.GetCertHash(), _port);
        _cts = new CancellationTokenSource();
        var channels = _channels.Count > 0 ? _channels.ToArray() : DefaultChannels;
        var plugins = _pluginPaths.Count > 0
            ? DvcPluginHost.Load(_pluginPaths, _activityLog.CreateLogger("DvcPlugins"))
            : null;
        _listener = new RdpListener(_bindAny ? IPAddress.Any : IPAddress.Loopback, _port, cert, _activityLog,
            dvcChannels: channels, rdpdrReads: null, dvcBehaviors: null,
            rdpdrLists: null, rdpdrWrites: null, desktop: true, logon: true, desktopDirect: _bootToDesktop,
            vfsRoot: _sharedVfs, plugins: plugins,
            preferredRdpEncryption: _encMethod, rdpHighEncryption: _encHigh, enableNla: _nla);
        _listener.Start();
        _ = _listener.AcceptLoopAsync(_cts.Token);
        UpdateStatus();
        if (_autoConnect) Connect(Presets[0]);
    }

    /// <summary>Pins the server cert's SHA-1 hash per-server so mstsc/mstscax accept it without a
    /// prompt (no global Root-store trust).</summary>
    private static void PinServerCert(byte[] certHash, int port)
    {
        foreach (var server in new[] { "127.0.0.1", "localhost", $"127.0.0.1:{port}", $"localhost:{port}" })
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
        if (!Running) return;
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
        string where = $"{(_bindAny ? "0.0.0.0" : "127.0.0.1")}:{_port}";
        _statusItem.Text = Running ? $"Server: running on {where}" : "Server: stopped";
        _toggleItem.Text = Running ? "Stop server" : "Start server";
        _icon.Text = Running ? $"Mock RDP — {where}" : "Mock RDP — stopped";
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

    private void ConnectFreeRdp(Preset p)
    {
        if (!Running) StartServer();
        var exe = ResolveFreeRdp();
        if (exe is null) return;

        // /sec:tls forces the TLS security layer (mstsc-on-a-managed-box can't); /cert:ignore accepts
        // the self-signed dev cert; NLA is off under /sec:tls.
        var args = $"/v:127.0.0.1:{_port} /cert:ignore /sec:tls /w:{p.Width} /h:{p.Height}";
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            Balloon($"Launched FreeRDP → 127.0.0.1:{_port} (TLS).");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't launch FreeRDP:\n" + ex.Message, "Mock RDP",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Find FreeRDP: a remembered path, then PATH, then ask the user to locate it.</summary>
    private string? ResolveFreeRdp()
    {
        if (!string.IsNullOrEmpty(_freeRdpPath) && File.Exists(_freeRdpPath)) return _freeRdpPath;

        foreach (var name in new[] { "wfreerdp.exe", "wfreerdp3.exe", "xfreerdp.exe", "xfreerdp3.exe" })
        {
            var found = FindOnPath(name);
            if (found is not null) { _freeRdpPath = found; Save(); return found; }
        }

        var ask = MessageBox.Show(
            "FreeRDP (wfreerdp.exe) wasn't found on PATH. Locate it now?\n\n" +
            "Get a portable Windows build from github.com/FreeRDP/FreeRDP/releases.",
            "Mock RDP — FreeRDP", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes) return null;

        using var dlg = new OpenFileDialog
        {
            Filter = "FreeRDP (wfreerdp*.exe;xfreerdp*.exe)|wfreerdp*.exe;xfreerdp*.exe|Executables (*.exe)|*.exe",
            Title = "Locate FreeRDP (wfreerdp.exe)",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return null;
        _freeRdpPath = dlg.FileName;
        Save();
        return _freeRdpPath;
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var full = Path.Combine(dir.Trim(), exe); if (File.Exists(full)) return full; }
            catch { /* bad PATH entry */ }
        }
        return null;
    }

    private string Rdp(Preset p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"full address:s:127.0.0.1:{_port}");
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
        sb.AppendLine($"audiomode:i:{(_redir.Audio ? 0 : 2)}");
        return sb.ToString();
    }

    private string WriteRdp(Preset p)
    {
        var path = Path.Combine(Path.GetTempPath(), "mock-rdp-tray.rdp");
        File.WriteAllText(path, Rdp(p), Encoding.ASCII);
        return path;
    }

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
            Balloon("Server certificate trusted. Headless clients now connect without a prompt.");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't trust the certificate:\n" + ex.Message, "Mock RDP",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

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

    private void ShowFiles()
    {
        if (_filesWindow is null || _filesWindow.IsDisposed)
        {
            _filesWindow = new FileBrowserWindow(_sharedVfs);
            _filesWindow.FormClosed += (_, _) => _filesWindow = null;
            _filesWindow.Show();
        }
        else
        {
            if (_filesWindow.WindowState == FormWindowState.Minimized) _filesWindow.WindowState = FormWindowState.Normal;
            _filesWindow.Activate();
            _filesWindow.BringToFront();
        }
    }

    private void SaveRdpToDesktop()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "mock-rdp.rdp");
        File.WriteAllText(path, Rdp(Presets[0]), Encoding.ASCII);
        Balloon("Saved " + path + " — double-click it to connect any time.");
    }

    // ── start-at-logon (Run key) ────────────────────────────────────────────

    private static bool IsRunAtLogon()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string;
        }
        catch { return false; }
    }

    private void SetRunAtLogon(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (k is null) return;
            if (on) k.SetValue(RunValue, $"\"{Application.ExecutablePath}\"");
            else k.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Couldn't change the startup setting:\n" + ex.Message, "Mock RDP",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ── settings persistence ────────────────────────────────────────────────

    private void LoadSettings()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(SettingsKey);
            if (k is null) return;
            if (k.GetValue("Port") is int p && p is > 0 and < 65536) _port = p;
            _bindAny = (k.GetValue("BindAny") as int?) == 1;
            _bootToDesktop = (k.GetValue("BootToDesktop") as int?) == 1;
            _autoConnect = (k.GetValue("AutoConnect") as int?) == 1;
            if (k.GetValue("LogLevel") is int ll && Enum.IsDefined((LogLevel)ll)) _logLevel = (LogLevel)ll;
            if (k.GetValue("EncMethod") is int em) _encMethod = (uint)em;
            _encHigh = (k.GetValue("EncHigh") as int?) == 1;
            _nla = (k.GetValue("Nla") as int?) == 1;
            if (k.GetValue("Channels") is string csv && csv.Length > 0)
            {
                _channels.Clear();
                foreach (var c in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    _channels.Add(c);
            }
            if (k.GetValue("Plugins") is string plugins && plugins.Length > 0)
                _pluginPaths.AddRange(plugins.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (k.GetValue("FreeRdpPath") is string frp) _freeRdpPath = frp;
            if (k.GetValue("Redir") is int r)
            {
                _redir.Drives = (r & 1) != 0; _redir.Clipboard = (r & 2) != 0; _redir.Printers = (r & 4) != 0;
                _redir.SmartCards = (r & 8) != 0; _redir.ComPorts = (r & 16) != 0; _redir.Audio = (r & 32) != 0;
            }
        }
        catch { /* defaults are fine */ }
    }

    private void Save()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(SettingsKey);
            if (k is null) return;
            k.SetValue("Port", _port, RegistryValueKind.DWord);
            k.SetValue("BindAny", _bindAny ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("BootToDesktop", _bootToDesktop ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("AutoConnect", _autoConnect ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("LogLevel", (int)_logLevel, RegistryValueKind.DWord);
            k.SetValue("EncMethod", (int)_encMethod, RegistryValueKind.DWord);
            k.SetValue("EncHigh", _encHigh ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("Nla", _nla ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("Channels", string.Join(",", _channels), RegistryValueKind.String);
            k.SetValue("Plugins", string.Join(";", _pluginPaths), RegistryValueKind.String);
            k.SetValue("FreeRdpPath", _freeRdpPath, RegistryValueKind.String);
            int r = (_redir.Drives ? 1 : 0) | (_redir.Clipboard ? 2 : 0) | (_redir.Printers ? 4 : 0)
                  | (_redir.SmartCards ? 8 : 0) | (_redir.ComPorts ? 16 : 0) | (_redir.Audio ? 32 : 0);
            k.SetValue("Redir", r, RegistryValueKind.DWord);
        }
        catch { /* best-effort */ }
    }

    // ── plumbing ──────────────────────────────────────────────────────────

    private void Balloon(string text) =>
        _icon.ShowBalloonTip(4000, "Mock RDP", text, ToolTipIcon.Info);

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
        _filesWindow?.Close();
        _activityLog.Dispose();
        _icon.Dispose();
        _menu.Dispose();
    }
}

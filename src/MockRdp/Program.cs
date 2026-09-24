using System.Net;
using Microsoft.Extensions.Logging;
using MockRdp.Rdp;
using MockRdp.Server;
using MockRdp.Transport;

// Minimal arg parsing: --port <n>, --log-level <trace|debug|info|warn|error>, --bind <ip>,
// --dvc <name[,name...]> (dynamic virtual channels the server opens; default "ECHO"),
// --log-file <path> (also write a flushed log to a file, for test harnesses).
int port = 3389;
var logLevel = LogLevel.Information;
var bind = IPAddress.Any;
string? certOut = null;
string[]? dvcChannels = null;
string[]? rdpdrReads = null;
string[]? rdpdrLists = null;
string[]? rdpdrWrites = null;
string? logFile = null;
var dvcReplies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
var dvcFaults = new Dictionary<string, Dvc.Fault>(StringComparer.OrdinalIgnoreCase);
var dvcBridges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
bool desktop = args.Contains("--desktop");
bool logon = args.Contains("--logon");

// No arguments = someone double-clicked the exe (test harnesses always pass --port/etc.). Hand off
// to the system-tray app, which serves on launch and puts every feature in its right-click menu —
// no cmdline, no cert prompt, no hand-written .rdp. Falls through to the console server if the tray
// isn't deployed next to this exe.
if (args.Length == 0)
{
    var trayExe = Path.Combine(AppContext.BaseDirectory, "MockRdpTray.exe");
    if (File.Exists(trayExe))
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(trayExe) { UseShellExecute = true });
        return;
    }
}

// --screenshot <path>: render one desktop frame to PNG and exit (no server).
int ssIdx = Array.IndexOf(args, "--screenshot");
if (ssIdx >= 0 && ssIdx + 1 < args.Length)
{
    using var shot = new MockRdp.Desktop.FakeDesktop(Capabilities.DesktopWidth, Capabilities.DesktopHeight, args.Contains("--logon"));
    if (args.Contains("--secure")) shot.Active = MockRdp.Desktop.DesktopKind.Secure;
    if (args.Contains("--start-menu")) shot.StartMenuOpen = true;
    if (args.Contains("--stats"))
    {
        shot.ConnectionStats = () =>
        [
            ("State", "Active"),
            ("Client address", "127.0.0.1:52193"),
            ("Client user", @"MOCK\rdpuser"),
            ("Security", "TLS (no NLA)"),
            ("Resolution", "1024 × 768 @ 16bpp"),
            ("Uptime", "03:12"),
            ("Share id", "0x000103EA"),
            ("Static channels", "rdpdr, rdpsnd, cliprdr, drdynvc"),
            ("Clipboard", "on (channel 1006)"),
            ("Drive redir (rdpdr)", "on (channel 1004)"),
            ("Dynamic VC", "on (channel 1007, v3)"),
            ("Open DVCs", "ECHO #1"),
            ("Redirected drives", "C:, D:"),
        ];
        shot.OpenApp("Connection Info");
    }
    if (args.Contains("--scroll"))
    {
        InputEvent Click(ushort x, ushort y) => new(InputEventType.Mouse, (ushort)(Input.PtrFlagsDown | Input.PtrFlagsButton1), x, y, 0);
        InputEvent Move(ushort x, ushort y) => new(InputEventType.Mouse, Input.PtrFlagsMove, x, y, 0);
        InputEvent Up(ushort x, ushort y) => new(InputEventType.Mouse, Input.PtrFlagsButton1, x, y, 0);
        shot.OpenApp("File Explorer");
        shot.OnInput(Click(250, 226)); shot.OnInput(Click(250, 226)); // .. → Users → C:
        shot.OnInput(Click(250, 248)); shot.OnInput(Click(250, 248)); // Windows → System32 (overflows)
        if (!args.Contains("--top"))
        {
            shot.OnInput(Click(656, 210));                            // grab the scrollbar
            shot.OnInput(Move(656, 350)); shot.OnInput(Up(656, 350)); // drag it down
        }
    }
    if (args.Contains("--display"))
    {
        shot.OpenApp("Display");
    }
    if (args.Contains("--tsclient"))
    {
        // Simulate browsing \\tsclient with no redirected drives (delivers an empty listing).
        shot.OnClientList = (winId, path) => shot.DeliverClientList(winId, path, new List<(string, bool)>());
        InputEvent Click(ushort x, ushort y) => new(InputEventType.Mouse, (ushort)(Input.PtrFlagsDown | Input.PtrFlagsButton1), x, y, 0);
        shot.OpenApp("File Explorer");
        shot.OnInput(Click(210, 196));                              // click the \\tsclient row (top)
    }
    if (args.Contains("--dvcmon"))
    {
        shot.DvcChannels = () => ["input", "graphics", "rdpdr", "cliprdr", "drdynvc", "APP", "DisplayControl", "ECHO"];
        shot.DvcTraffic = () =>
        [
            new("20:31:04", false, "drdynvc", 12, "server capabilities v1"),
            new("20:31:04", true, "drdynvc", 12, "client capabilities v3"),
            new("20:31:04", false, "ECHO", 11, "create request (id 1)"),
            new("20:31:04", true, "ECHO", 10, "create OK (id 1)"),
            new("20:31:04", false, "DisplayControl", 20, "caps PDU"),
            new("20:31:06", false, "graphics", 4820, "bitmap update · 6 tiles"),
            new("20:31:07", true, "input", 22, "move · 1 ev"),
            new("20:31:07", true, "input", 22, "click · 1 ev"),
            new("20:31:08", true, "cliprdr", 24, "msgType 0x0002"),
            new("20:31:18", true, "DisplayControl", 56, "monitor layout 1280×800"),
            new("20:31:22", false, "APP", 13, "console: \"hello world\""),
        ];
        shot.OpenApp("Channel Monitor");
        if (args.Contains("--filter-app"))   // click the "APP" channel in the left pane (row 2)
            shot.OnInput(new(InputEventType.Mouse, (ushort)(Input.PtrFlagsDown | Input.PtrFlagsButton1), 250, 224, 0));
    }
    if (args.Contains("--dvcapp"))
    {
        InputEvent K(byte sc) => new(InputEventType.Scancode, 0, 0, 0, sc);
        shot.OpenApp("DVC Console");
        foreach (var sc in new byte[] { 0x23, 0x17, 0x1F }) shot.OnInput(K(sc)); // type "his" into the message
    }
    if (args.Contains("--demo"))
    {
        // Synthetic clicks: open File Explorer, browse into Documents, open readme.txt in Notepad.
        InputEvent Click(ushort x, ushort y) => new(InputEventType.Mouse, (ushort)(Input.PtrFlagsDown | Input.PtrFlagsButton1), x, y, 0);
        shot.OpenApp("File Explorer");
        shot.OnInput(Click(250, 220)); // into Documents
        shot.OnInput(Click(250, 220)); // open readme.txt → Notepad
    }
    if (args.Contains("--demo-run"))
    {
        InputEvent K(byte sc) => new(InputEventType.Scancode, 0, 0, 0, sc);
        shot.OpenApp("Run…");
        foreach (var sc in new byte[] { 0x31, 0x18, 0x14, 0x12, 0x19, 0x1E, 0x20 }) shot.OnInput(K(sc)); // "notepad"
    }
    shot.Render();
    shot.SavePng(args[ssIdx + 1]);
    Console.WriteLine($"Wrote desktop screenshot to {args[ssIdx + 1]}");
    return;
}

static (string Channel, string Value) SplitEq(string arg)
{
    int eq = arg.IndexOf('=');
    return eq < 0 ? (arg, "") : (arg[..eq], arg[(eq + 1)..]);
}

var pluginPaths = new List<string>();
for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[++i]); break;
        case "--plugin": pluginPaths.Add(args[++i]); break;
        case "--bind": bind = IPAddress.Parse(args[++i]); break;
        case "--cert-out": certOut = args[++i]; break;
        case "--log-file": logFile = args[++i]; break;
        case "--dvc": dvcChannels = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
        case "--rdpdr-read": rdpdrReads = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
        case "--rdpdr-list": rdpdrLists = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
        case "--rdpdr-write": rdpdrWrites = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
        case "--dvc-reply":  { var (ch, v) = SplitEq(args[++i]); dvcReplies[ch] = File.ReadAllBytes(v); break; }
        case "--dvc-fault":  { var (ch, v) = SplitEq(args[++i]); dvcFaults[ch] = Enum.Parse<Dvc.Fault>(v, ignoreCase: true); break; }
        case "--dvc-bridge": { var (ch, v) = SplitEq(args[++i]); dvcBridges[ch] = v; break; }
        case "--log-level":
            logLevel = args[++i].ToLowerInvariant() switch
            {
                "trace" => LogLevel.Trace,
                "debug" => LogLevel.Debug,
                "info" or "information" => LogLevel.Information,
                "warn" or "warning" => LogLevel.Warning,
                "error" => LogLevel.Error,
                _ => LogLevel.Information,
            };
            break;
    }
}

using var loggerFactory = LoggerFactory.Create(b =>
{
    b.SetMinimumLevel(logLevel)
     .AddSimpleConsole(o =>
     {
         o.SingleLine = false;
         o.TimestampFormat = "HH:mm:ss.fff ";
     });
    if (logFile is not null)
        b.AddProvider(new MockRdp.Util.FileLoggerProvider(logFile));
});

var cert = CertProvider.CreateSelfSigned();
if (certOut is not null)
{
    File.WriteAllBytes(certOut, cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));
    loggerFactory.CreateLogger("Program").LogInformation("Exported server certificate to {Path}", certOut);
}
Dictionary<string, Dvc.Behavior>? dvcBehaviors = null;
if (dvcReplies.Count > 0 || dvcFaults.Count > 0)
{
    dvcBehaviors = new(StringComparer.OrdinalIgnoreCase);
    foreach (var ch in dvcReplies.Keys.Union(dvcFaults.Keys))
        dvcBehaviors[ch] = new Dvc.Behavior
        {
            Reply = dvcReplies.GetValueOrDefault(ch),
            Fault = dvcFaults.GetValueOrDefault(ch),
        };
}

var pluginHost = pluginPaths.Count > 0
    ? MockRdp.Rdp.DvcPluginHost.Load(pluginPaths, loggerFactory.CreateLogger("DvcPlugins"))
    : null;

// --enc <128|56|40|none>: Standard RDP Security method to prefer when a PROTOCOL_RDP client offers
// it (default 128-bit RC4, the only fully MAC-verified method). `none` accepts PROTOCOL_RDP but
// runs in the clear. 40/56-bit connect and decrypt correctly but are not strictly MAC-verified.
uint preferredEnc = MockRdp.Rdp.StandardSecurity.Method128Bit;
int encIdx = Array.IndexOf(args, "--enc");
if (encIdx >= 0 && encIdx + 1 < args.Length)
    preferredEnc = args[encIdx + 1].ToLowerInvariant() switch
    {
        "40" => MockRdp.Rdp.StandardSecurity.Method40Bit,
        "56" => MockRdp.Rdp.StandardSecurity.Method56Bit,
        "128" => MockRdp.Rdp.StandardSecurity.Method128Bit,
        "none" => 0u,
        _ => MockRdp.Rdp.StandardSecurity.Method128Bit,
    };

// --enc-level <low|high>: Standard RDP Security level (default low). HIGH encrypts server→client too.
bool highEnc = false;
int lvlIdx = Array.IndexOf(args, "--enc-level");
if (lvlIdx >= 0 && lvlIdx + 1 < args.Length)
    highEnc = args[lvlIdx + 1].Equals("high", StringComparison.OrdinalIgnoreCase);

using var listener = new RdpListener(bind, port, cert, loggerFactory, dvcChannels, rdpdrReads, dvcBehaviors,
    rdpdrLists, rdpdrWrites, desktop, logon, desktopDirect: args.Contains("--no-logon"),
    dvcBridges: dvcBridges.Count > 0 ? dvcBridges : null,
    plugins: pluginHost, preferredRdpEncryption: preferredEnc, rdpHighEncryption: highEnc,
    enableNla: args.Contains("--nla"), enableRdsAad: args.Contains("--aad"));
listener.Start();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    await listener.AcceptLoopAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // graceful shutdown
}

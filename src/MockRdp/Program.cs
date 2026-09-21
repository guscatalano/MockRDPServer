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
bool desktop = args.Contains("--desktop");
bool logon = args.Contains("--logon");

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
        InputEvent Click(ushort x, ushort y) => new(InputEventType.Mouse, (ushort)(Input.PtrFlagsDown | Input.PtrFlagsButton1), x, y, 0);
        shot.OnInput(Click(10, 748)); shot.OnInput(Click(20, 555)); // Start → Connection Info
    }
    if (args.Contains("--scroll"))
    {
        InputEvent Click(ushort x, ushort y) => new(InputEventType.Mouse, (ushort)(Input.PtrFlagsDown | Input.PtrFlagsButton1), x, y, 0);
        InputEvent Move(ushort x, ushort y) => new(InputEventType.Mouse, Input.PtrFlagsMove, x, y, 0);
        InputEvent Up(ushort x, ushort y) => new(InputEventType.Mouse, Input.PtrFlagsButton1, x, y, 0);
        shot.OnInput(Click(10, 748)); shot.OnInput(Click(20, 484));  // Start → File Explorer
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
        InputEvent Click(ushort x, ushort y) => new(InputEventType.Mouse, (ushort)(Input.PtrFlagsDown | Input.PtrFlagsButton1), x, y, 0);
        shot.OnInput(Click(10, 748)); shot.OnInput(Click(20, 590)); // Start → Display
    }
    if (args.Contains("--dvcmon"))
    {
        shot.DvcTraffic = () =>
        [
            "20:31:04 → drdynvc · 12B · server capabilities v1",
            "20:31:04 ← drdynvc · 12B · client capabilities v3",
            "20:31:04 → ECHO · 11B · create request (id 1)",
            "20:31:04 ← ECHO · 10B · create OK (id 1)",
            "20:31:04 → DisplayControl · 45B · create request (id 2)",
            "20:31:04 ← DisplayControl · 10B · create OK (id 2)",
            "20:31:04 → DisplayControl · 20B · caps PDU",
            "20:31:18 ← DisplayControl · 56B · monitor layout 1280×800",
            "20:31:22 ← ECHO · 13B · data",
            "20:31:22 → ECHO · 13B · echo",
        ];
        InputEvent Click(ushort x, ushort y) => new(InputEventType.Mouse, (ushort)(Input.PtrFlagsDown | Input.PtrFlagsButton1), x, y, 0);
        shot.OnInput(Click(10, 748)); shot.OnInput(Click(20, 625)); // Start → DVC Monitor
    }
    if (args.Contains("--demo"))
    {
        // Synthetic clicks: open File Explorer, browse into Documents, open readme.txt in Notepad.
        InputEvent Click(ushort x, ushort y) => new(InputEventType.Mouse, (ushort)(Input.PtrFlagsDown | Input.PtrFlagsButton1), x, y, 0);
        shot.OnInput(Click(10, 748)); shot.OnInput(Click(20, 574)); // Start → File Explorer
        shot.OnInput(Click(250, 220)); // into Documents
        shot.OnInput(Click(250, 220)); // open readme.txt → Notepad
    }
    if (args.Contains("--demo-run"))
    {
        InputEvent Click(ushort x, ushort y) => new(InputEventType.Mouse, (ushort)(Input.PtrFlagsDown | Input.PtrFlagsButton1), x, y, 0);
        InputEvent K(byte sc) => new(InputEventType.Scancode, 0, 0, 0, sc);
        shot.OnInput(Click(10, 748)); shot.OnInput(Click(20, 690));   // Start → Run…
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

for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[++i]); break;
        case "--bind": bind = IPAddress.Parse(args[++i]); break;
        case "--cert-out": certOut = args[++i]; break;
        case "--log-file": logFile = args[++i]; break;
        case "--dvc": dvcChannels = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
        case "--rdpdr-read": rdpdrReads = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
        case "--rdpdr-list": rdpdrLists = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
        case "--rdpdr-write": rdpdrWrites = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
        case "--dvc-reply":  { var (ch, v) = SplitEq(args[++i]); dvcReplies[ch] = File.ReadAllBytes(v); break; }
        case "--dvc-fault":  { var (ch, v) = SplitEq(args[++i]); dvcFaults[ch] = Enum.Parse<Dvc.Fault>(v, ignoreCase: true); break; }
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

using var listener = new RdpListener(bind, port, cert, loggerFactory, dvcChannels, rdpdrReads, dvcBehaviors,
    rdpdrLists, rdpdrWrites, desktop, logon);
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

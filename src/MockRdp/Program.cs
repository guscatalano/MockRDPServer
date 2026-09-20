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

// --screenshot <path>: render one desktop frame to PNG and exit (no server).
int ssIdx = Array.IndexOf(args, "--screenshot");
if (ssIdx >= 0 && ssIdx + 1 < args.Length)
{
    using var shot = new MockRdp.Desktop.FakeDesktop(Capabilities.DesktopWidth, Capabilities.DesktopHeight);
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
    rdpdrLists, rdpdrWrites, desktop);
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

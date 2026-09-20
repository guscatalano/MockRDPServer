using System.Net;
using Microsoft.Extensions.Logging;
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
string? logFile = null;

for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[++i]); break;
        case "--bind": bind = IPAddress.Parse(args[++i]); break;
        case "--cert-out": certOut = args[++i]; break;
        case "--log-file": logFile = args[++i]; break;
        case "--dvc": dvcChannels = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
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
using var listener = new RdpListener(bind, port, cert, loggerFactory, dvcChannels);
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

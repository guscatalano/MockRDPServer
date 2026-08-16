using System.Net;
using Microsoft.Extensions.Logging;
using MockRdp.Server;
using MockRdp.Transport;

// Minimal arg parsing: --port <n>, --log-level <trace|debug|info|warn|error>, --bind <ip>.
int port = 3389;
var logLevel = LogLevel.Information;
var bind = IPAddress.Any;

for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[++i]); break;
        case "--bind": bind = IPAddress.Parse(args[++i]); break;
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

using var loggerFactory = LoggerFactory.Create(b => b
    .SetMinimumLevel(logLevel)
    .AddSimpleConsole(o =>
    {
        o.SingleLine = false;
        o.TimestampFormat = "HH:mm:ss.fff ";
    }));

var cert = CertProvider.CreateSelfSigned();
using var listener = new RdpListener(bind, port, cert, loggerFactory);
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

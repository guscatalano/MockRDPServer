using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace MockRdp.Server;

/// <summary>Accepts TCP connections and hands each to an <see cref="RdpConnection"/>.</summary>
public sealed class RdpListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _cert;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;

    public RdpListener(IPAddress address, int port, X509Certificate2 cert, ILoggerFactory loggerFactory)
    {
        _listener = new TcpListener(address, port);
        _cert = cert;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger("RdpListener");
    }

    /// <summary>The bound port. Valid after <see cref="Start"/> (useful when binding to port 0 in tests).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _log.LogInformation("Mock RDP server listening on {Endpoint}", _listener.LocalEndpoint);
    }

    public async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
        var log = _loggerFactory.CreateLogger($"Conn[{endpoint}]");
        log.LogInformation("Connection accepted.");
        try
        {
            using (client)
            {
                var conn = new RdpConnection(client, _cert, log);
                await conn.RunAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Unhandled connection error.");
        }
        finally
        {
            log.LogInformation("Connection closed.");
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        _cert.Dispose();
    }
}

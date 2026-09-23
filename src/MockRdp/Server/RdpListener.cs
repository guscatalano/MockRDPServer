using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MockRdp.Rdp;

namespace MockRdp.Server;

/// <summary>Accepts TCP connections and hands each to an <see cref="RdpConnection"/>.</summary>
public sealed class RdpListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _cert;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly string[]? _dvcChannels;
    private readonly string[]? _rdpdrReads;
    private readonly Dictionary<string, Dvc.Behavior>? _dvcBehaviors;
    private readonly string[]? _rdpdrLists;
    private readonly string[]? _rdpdrWrites;
    private readonly bool _desktop;
    private readonly Desktop.VfsNode? _vfsRoot;
    private readonly Dictionary<string, string>? _dvcBridges;
    private readonly bool _logon;
    private readonly bool _desktopDirect;
    private readonly Rdp.DvcPluginHost? _plugins;
    private readonly bool _allowStandardRdpSecurity;
    private readonly uint _preferredRdpEncryption;

    public RdpListener(IPAddress address, int port, X509Certificate2 cert, ILoggerFactory loggerFactory,
        string[]? dvcChannels = null, string[]? rdpdrReads = null,
        Dictionary<string, Dvc.Behavior>? dvcBehaviors = null,
        string[]? rdpdrLists = null, string[]? rdpdrWrites = null, bool desktop = false, bool logon = false,
        bool desktopDirect = false, Desktop.VfsNode? vfsRoot = null,
        Dictionary<string, string>? dvcBridges = null,
        Rdp.DvcPluginHost? plugins = null,
        bool allowStandardRdpSecurity = true,
        uint preferredRdpEncryption = Rdp.StandardSecurity.Method128Bit)
    {
        _allowStandardRdpSecurity = allowStandardRdpSecurity;
        _preferredRdpEncryption = preferredRdpEncryption;
        _vfsRoot = vfsRoot;
        _dvcBridges = dvcBridges;
        _plugins = plugins;
        _listener = new TcpListener(address, port);
        _cert = cert;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger("RdpListener");
        _dvcChannels = dvcChannels;
        _rdpdrReads = rdpdrReads;
        _dvcBehaviors = dvcBehaviors;
        _rdpdrLists = rdpdrLists;
        _rdpdrWrites = rdpdrWrites;
        _desktop = desktop;
        _logon = logon;
        _desktopDirect = desktopDirect;
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
                var conn = new RdpConnection(client, _cert, log, _dvcChannels, _rdpdrReads, _dvcBehaviors,
                    _rdpdrLists, _rdpdrWrites, _desktop, _logon, _desktopDirect, _vfsRoot, _dvcBridges, _plugins,
                    _allowStandardRdpSecurity, _preferredRdpEncryption);
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

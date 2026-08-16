using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using MockRdp.Server;
using MockRdp.Transport;

namespace MockRdp.Tests.Harness;

/// <summary>Starts a mock RDP server on a loopback ephemeral port for the duration of a test.</summary>
public sealed class MockServerFixture : IDisposable
{
    private readonly RdpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public MockServerFixture()
    {
        var cert = CertProvider.CreateSelfSigned();
        _listener = new RdpListener(IPAddress.Loopback, 0, cert, NullLoggerFactory.Instance);
        _listener.Start();
        _ = _listener.AcceptLoopAsync(_cts.Token);
    }

    public IPEndPoint Endpoint => new(IPAddress.Loopback, _listener.Port);

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Dispose();
        _cts.Dispose();
    }
}

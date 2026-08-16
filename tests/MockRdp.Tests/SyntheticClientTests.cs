using MockRdp.Tests.Harness;
using MockRdp.X224;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// End-to-end M1 checks: the in-process conformance client performs X.224 negotiation and
/// the TLS handshake against a real listener. Verifies the whole M1 path without needing mstsc.
/// </summary>
public class SyntheticClientTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    [Fact]
    public async Task Client_NegotiatesSsl_AndCompletesTlsHandshake()
    {
        using var server = new MockServerFixture();
        await using var client = new RdpTestClient();
        var ct = Timeout;

        await client.ConnectAsync(server.Endpoint, ct);
        await client.SendConnectionRequestAsync(RdpNegProtocol.Ssl | RdpNegProtocol.Hybrid, "Cookie: mstshash=test", ct);

        var neg = await client.ReadConnectionConfirmAsync(ct);
        Assert.False(neg.IsFailure);
        Assert.Equal(RdpNegProtocol.Ssl, neg.SelectedProtocol);

        await client.UpgradeToTlsAsync(ct: ct);
        // Reaching here means AuthenticateAsClientAsync succeeded — TLS is up. M1 verified.
    }

    [Fact]
    public async Task Client_WithoutSsl_ReceivesNegotiationFailure()
    {
        using var server = new MockServerFixture();
        await using var client = new RdpTestClient();
        var ct = Timeout;

        await client.ConnectAsync(server.Endpoint, ct);
        await client.SendConnectionRequestAsync(RdpNegProtocol.Rdp, ct: ct);

        var neg = await client.ReadConnectionConfirmAsync(ct);
        Assert.True(neg.IsFailure);
        Assert.Equal(RdpNegFailureCode.SslRequiredByServer, neg.FailureCode);
    }
}

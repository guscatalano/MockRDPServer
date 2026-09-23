using MockRdp.Rdp;
using Xunit;

namespace MockRdp.Tests;

/// <summary>CredSSP (NLA) wire-structure round-trips: the TSRequest envelope and TSCredentials.</summary>
public class CredSspTests
{
    [Fact]
    public void TSRequest_RoundTrips_AllFields()
    {
        var req = new CredSsp.TSRequest
        {
            Version = 6,
            NegoToken = [1, 2, 3, 4, 5],
            PubKeyAuth = [9, 8, 7],
            ClientNonce = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
        };

        var decoded = CredSsp.Decode(CredSsp.Encode(req));

        Assert.Equal(6, decoded.Version);
        Assert.Equal(req.NegoToken, decoded.NegoToken);
        Assert.Equal(req.PubKeyAuth, decoded.PubKeyAuth);
        Assert.Equal(req.ClientNonce, decoded.ClientNonce);
        Assert.Null(decoded.AuthInfo);
    }

    [Fact]
    public void TSRequest_RoundTrips_AuthInfoAndErrorCode()
    {
        var req = new CredSsp.TSRequest { Version = 3, AuthInfo = [0xAA, 0xBB], ErrorCode = 0x0005 };
        var decoded = CredSsp.Decode(CredSsp.Encode(req));

        Assert.Equal(3, decoded.Version);
        Assert.Equal(req.AuthInfo, decoded.AuthInfo);
        Assert.Equal(0x0005, decoded.ErrorCode);
        Assert.Null(decoded.NegoToken);
    }

    [Fact]
    public void TSCredentials_RoundTripsDomainAndUser()
    {
        var creds = CredSsp.EncodePasswordCredentials("CONTOSO", "alice", "s3cret");
        var (domain, user) = CredSsp.DecodePasswordCredentials(creds);

        Assert.Equal("CONTOSO", domain);
        Assert.Equal("alice", user);
    }
}

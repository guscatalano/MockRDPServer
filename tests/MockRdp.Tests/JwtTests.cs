using System.Text;
using System.Text.Json;
using MockRdp.Rdp;
using Xunit;

namespace MockRdp.Tests;

/// <summary>The RDS-AAD path decodes (without verifying) the client's JWT to surface its identity.</summary>
public class JwtTests
{
    private static string MakeJwt(object payload)
    {
        static string B64Url(byte[] b) =>
            Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var body = B64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{header}.{body}.{B64Url([1, 2, 3, 4])}";   // fake signature — never checked
    }

    [Fact]
    public void DecodeClaims_ExtractsPayload_AndPrefersUpn()
    {
        var token = MakeJwt(new { upn = "alice@contoso.com", name = "Alice", aud = "rdp" });
        var claims = Jwt.DecodeClaims(token);

        Assert.Equal("alice@contoso.com", claims["upn"]);
        Assert.Equal("Alice", claims["name"]);
        Assert.Equal("alice@contoso.com", Jwt.UserFromClaims(claims));
    }

    [Fact]
    public void UserFromClaims_FallsBackThroughClaimOrder()
    {
        var claims = Jwt.DecodeClaims(MakeJwt(new { preferred_username = "bob@contoso.com", sub = "xyz" }));
        Assert.Equal("bob@contoso.com", Jwt.UserFromClaims(claims));
    }

    [Fact]
    public void DecodeClaims_ReturnsEmpty_ForOpaqueToken()
    {
        Assert.Empty(Jwt.DecodeClaims("not-a-jwt"));
        Assert.Empty(Jwt.DecodeClaims(""));
        Assert.Equal("", Jwt.UserFromClaims(Jwt.DecodeClaims("opaque.token")));
    }
}

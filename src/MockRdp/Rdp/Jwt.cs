using System.Text;
using System.Text.Json;

namespace MockRdp.Rdp;

/// <summary>
/// Minimal JWT reader for the RDS-AAD (Entra) "fake" auth path: decodes the payload claims of a JWT
/// access token WITHOUT validating its signature. The mock is a test host — it does not (and cannot)
/// verify the token against Entra ID, it only surfaces who the client claims to be.
/// </summary>
public static class Jwt
{
    /// <summary>Decodes the payload (2nd segment) of a JWT into its claims. Returns an empty map if the
    /// token is not a well-formed JWT, so a caller can accept an opaque token gracefully.</summary>
    public static IReadOnlyDictionary<string, string> DecodeClaims(string token)
    {
        var empty = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(token)) return empty;

        var parts = token.Split('.');
        if (parts.Length < 2) return empty;

        try
        {
            using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var claims = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                claims[p.Name] = p.Value.ValueKind == JsonValueKind.String
                    ? p.Value.GetString() ?? ""
                    : p.Value.GetRawText();
            return claims;
        }
        catch
        {
            return empty;
        }
    }

    /// <summary>The best available user identity from a token's claims: upn → preferred_username →
    /// unique_name → email → name → sub. Empty string if none present.</summary>
    public static string UserFromClaims(IReadOnlyDictionary<string, string> claims)
    {
        foreach (var key in new[] { "upn", "preferred_username", "unique_name", "email", "name", "sub" })
            if (claims.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
                return v;
        return "";
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}

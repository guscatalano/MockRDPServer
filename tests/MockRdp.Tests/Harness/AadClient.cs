using System.Text;
using System.Text.Json;

namespace MockRdp.Tests.Harness;

/// <summary>
/// Client side of RDS-AAD / Entra auth for tests (MS-RDPBCGR 5.4.5.4): reads the server's nonce PDU
/// and sends an Authentication Request whose "rdp_assertion" JWT carries a (fake) Entra access token
/// with the user's UPN. TLS must already be up on <paramref name="client"/>.
/// </summary>
public static class AadClient
{
    public static async Task AuthenticateAsync(RdpTestClient client, string upn, CancellationToken ct)
    {
        // 1. Server Nonce PDU.
        using var nonceDoc = JsonDocument.Parse(await ReadJsonAsync(client, ct));
        string nonce = nonceDoc.RootElement.GetProperty("ts_nonce").GetString()!;

        // 2. Build the (fake) Entra access token and the rdp_assertion that wraps it.
        string accessToken = MakeJwt(new { upn, aud = "rdp", iss = "https://sts.windows.net/mock/" });
        string assertion = MakeJwt(new { ts = "1700000000", at = accessToken, u = "127.0.0.1", nonce });

        // 3. Authentication Request PDU.
        await WriteJsonAsync(client, $$"""{"rdp_assertion":"{{assertion}}"}""", ct);

        // 4. Authentication Result PDU — must be S_OK ("0").
        using var resultDoc = JsonDocument.Parse(await ReadJsonAsync(client, ct));
        string result = resultDoc.RootElement.GetProperty("authentication_result").GetString()!;
        if (result != "0") throw new InvalidOperationException($"RDS-AAD auth failed: {result}");
    }

    private static string MakeJwt(object payload)
    {
        static string B64Url(byte[] b) =>
            Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT","kid":"mock"}"""));
        var body = B64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{header}.{body}.{B64Url([1, 2, 3, 4])}";   // fake signature — the mock doesn't verify it
    }

    private static Task WriteJsonAsync(RdpTestClient client, string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var buf = new byte[body.Length + 1];   // trailing NUL, per the wire convention
        body.CopyTo(buf, 0);
        return client.WriteRawAsync(buf, ct);
    }

    private static async Task<string> ReadJsonAsync(RdpTestClient client, CancellationToken ct)
    {
        var acc = new List<byte>(4096);
        var chunk = new byte[8192];
        while (true)
        {
            int n = await client.Stream.ReadAsync(chunk, ct);
            if (n == 0) break;
            for (int i = 0; i < n; i++)
            {
                if (chunk[i] == 0) return Encoding.UTF8.GetString(acc.ToArray());
                acc.Add(chunk[i]);
            }
        }
        return Encoding.UTF8.GetString(acc.ToArray());
    }
}

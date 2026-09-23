using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using MockRdp.Rdp;

namespace MockRdp.Tests.Harness;

/// <summary>
/// Client side of NLA / CredSSP for tests. Uses Windows SSPI (the current logon session, loopback) to
/// drive the mock's NLA server end-to-end without a real RDP client — the mirror of the server's
/// <c>RunNlaAsync</c>. The TLS upgrade must already be done on <paramref name="client"/>.
/// </summary>
public static class NlaClient
{
    /// <summary>Runs the full CredSSP client exchange. Returns false if SSPI can't authenticate in this
    /// environment (e.g. no logon session), so a caller can skip rather than fail.</summary>
    public static async Task<bool> TryAuthenticateAsync(RdpTestClient client, CancellationToken ct)
    {
        byte[] pubKey = client.ServerPublicKeyInfo();
        using var nego = new NegotiateAuthentication(new NegotiateAuthenticationClientOptions
        {
            Package = "NTLM",
            Credential = CredentialCache.DefaultNetworkCredentials,
            TargetName = "HOST/localhost",
            RequiredProtectionLevel = ProtectionLevel.EncryptAndSign,
        });

        // 1. NTLM NEGOTIATE (Type 1).
        byte[]? token = nego.GetOutgoingBlob(ReadOnlySpan<byte>.Empty, out var status);
        if (status != NegotiateAuthenticationStatusCode.ContinueNeeded) return false;
        await WriteAsync(client, new CredSsp.TSRequest { Version = 6, NegoToken = token }, ct);

        // 2. Read the CHALLENGE (Type 2); produce AUTHENTICATE (Type 3), completing client auth.
        var challenge = await ReadAsync(client, ct);
        token = nego.GetOutgoingBlob(challenge!.NegoToken!, out status);
        if (!nego.IsAuthenticated) return false;

        // 3. Send Type 3 + the sealed client public-key binding hash + nonce.
        var nonce = RandomNumberGenerator.GetBytes(32);
        var clientHash = BindingHash("CredSSP Client-To-Server Binding Hash\0", nonce, pubKey);
        await WriteAsync(client, new CredSsp.TSRequest
        {
            Version = 6, NegoToken = token, PubKeyAuth = Wrap(nego, clientHash), ClientNonce = nonce,
        }, ct);

        // 4. Read and verify the server's binding hash.
        var serverResp = await ReadAsync(client, ct);
        var expected = BindingHash("CredSSP Server-To-Client Binding Hash\0", nonce, pubKey);
        if (!Unwrap(nego, serverResp!.PubKeyAuth!).AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("Server public-key binding hash mismatch.");

        // 5. Deliver the credentials (TSCredentials), encrypted.
        var creds = CredSsp.EncodePasswordCredentials("TESTDOM", "rdsuser", "pw");
        await WriteAsync(client, new CredSsp.TSRequest { Version = 6, AuthInfo = Wrap(nego, creds) }, ct);
        return true;
    }

    private static byte[] BindingHash(string magic, byte[] nonce, byte[] pubKey)
    {
        var m = Encoding.ASCII.GetBytes(magic);
        var buf = new byte[m.Length + nonce.Length + pubKey.Length];
        m.CopyTo(buf, 0);
        nonce.CopyTo(buf, m.Length);
        pubKey.CopyTo(buf, m.Length + nonce.Length);
        return SHA256.HashData(buf);
    }

    private static byte[] Wrap(NegotiateAuthentication nego, byte[] data)
    {
        var w = new ArrayBufferWriter<byte>();
        nego.Wrap(data, w, requestEncryption: true, out _);
        return w.WrittenSpan.ToArray();
    }

    private static byte[] Unwrap(NegotiateAuthentication nego, byte[] data)
    {
        var w = new ArrayBufferWriter<byte>();
        nego.Unwrap(data, w, out _);
        return w.WrittenSpan.ToArray();
    }

    private static Task WriteAsync(RdpTestClient client, CredSsp.TSRequest req, CancellationToken ct) =>
        client.WriteRawAsync(CredSsp.Encode(req), ct);

    private static async Task<CredSsp.TSRequest?> ReadAsync(RdpTestClient client, CancellationToken ct)
    {
        var head = new byte[2];
        await client.Stream.ReadExactlyAsync(head, ct);
        var header = new List<byte> { head[0], head[1] };
        int len;
        if ((head[1] & 0x80) == 0) len = head[1];
        else
        {
            int n = head[1] & 0x7F;
            var lb = new byte[n];
            await client.Stream.ReadExactlyAsync(lb, ct);
            header.AddRange(lb);
            len = 0;
            foreach (var b in lb) len = (len << 8) | b;
        }
        var content = new byte[len];
        await client.Stream.ReadExactlyAsync(content, ct);
        return CredSsp.Decode(header.Concat(content).ToArray());
    }
}

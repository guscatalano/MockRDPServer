using System.Buffers.Binary;
using System.Text;
using MockRdp.Util;

namespace MockRdp.Rdp;

/// <summary>
/// RDSTLS security protocol PDUs (MS-RDPBCGR 2.2.17), exchanged over an established TLS channel when
/// PROTOCOL_RDSTLS is negotiated. RDSTLS is the security protocol used for RD Gateway / redirection
/// reconnects: after TLS, the server advertises a Capabilities PDU, the client sends an Authentication
/// Request carrying credentials (password or an auto-reconnect cookie), and the server replies with an
/// Authentication Response. The mock is a test host and accepts any credentials.
///
/// PDUs are NOT TPKT-framed — each is a self-describing little-endian structure sent raw over TLS.
/// </summary>
public static class Rdstls
{
    public const ushort Version1 = 0x0001;
    public const ushort TypeCapabilities = 0x0001;
    public const ushort TypeAuthReq = 0x0002;
    public const ushort TypeAuthResp = 0x0003;
    public const ushort DataPasswordCreds = 0x0001;
    public const ushort DataAutoReconnectCookie = 0x0002;
    public const uint ResultSuccess = 0x00000000;

    public enum RequestKind { PasswordCredentials, AutoReconnectCookie }

    /// <summary>A parsed RDSTLS Authentication Request. Password/cookie bytes are not retained.</summary>
    public sealed record AuthRequest(RequestKind Kind, string UserName, string Domain);

    /// <summary>RDSTLS Capabilities PDU (2.2.17.1): Version, PduType, SupportedVersions.</summary>
    public static byte[] BuildCapabilities()
    {
        var w = new ByteWriter();
        w.WriteUInt16LE(Version1);
        w.WriteUInt16LE(TypeCapabilities);
        w.WriteUInt16LE(Version1);   // SupportedVersions
        return w.ToArray();
    }

    /// <summary>RDSTLS Authentication Request PDU with password credentials (2.2.17.2). Used by a
    /// redirection client (and by the test harness). Strings are UTF-16LE with a trailing NUL.</summary>
    public static byte[] BuildPasswordAuthRequest(string redirectionGuid, string user, string domain,
        string password, string serverName)
    {
        var w = new ByteWriter();
        w.WriteUInt16LE(Version1);
        w.WriteUInt16LE(TypeAuthReq);
        w.WriteUInt16LE(DataPasswordCreds);
        WriteField(w, redirectionGuid);
        WriteField(w, user);
        WriteField(w, domain);
        WriteField(w, password);
        WriteField(w, serverName);
        return w.ToArray();
    }

    private static void WriteField(ByteWriter w, string s)
    {
        var bytes = Encoding.Unicode.GetBytes(s + "\0");
        w.WriteUInt16LE((ushort)bytes.Length);
        w.WriteBytes(bytes);
    }

    /// <summary>RDSTLS Authentication Response PDU (2.2.17.4): Version, PduType, ResultCode.</summary>
    public static byte[] BuildAuthResponse(uint resultCode)
    {
        var w = new ByteWriter();
        w.WriteUInt16LE(Version1);
        w.WriteUInt16LE(TypeAuthResp);
        w.WriteUInt32LE(resultCode);
        return w.ToArray();
    }

    /// <summary>Reads and parses one Authentication Request PDU (2.2.17.2 / 2.2.17.3) from the stream.
    /// Returns null if the PDU is not a recognised Authentication Request. Throws on a truncated stream
    /// (EndOfStreamException) — the caller treats that as a closed connection.</summary>
    public static async Task<AuthRequest?> ReadAuthRequestAsync(Stream stream, CancellationToken ct)
    {
        ushort version = await ReadUInt16Async(stream, ct);
        ushort pduType = await ReadUInt16Async(stream, ct);
        ushort dataType = await ReadUInt16Async(stream, ct);
        if (version != Version1 || pduType != TypeAuthReq) return null;

        if (dataType == DataPasswordCreds)
        {
            _ = await ReadFieldAsync(stream, ct);            // RedirectionGuid
            string user = Decode(await ReadFieldAsync(stream, ct));
            string domain = Decode(await ReadFieldAsync(stream, ct));
            _ = await ReadFieldAsync(stream, ct);            // Password (never retained)
            _ = await ReadFieldAsync(stream, ct);            // ServerName
            return new AuthRequest(RequestKind.PasswordCredentials, user, domain);
        }

        if (dataType == DataAutoReconnectCookie)
        {
            var sid = new byte[4];
            await stream.ReadExactlyAsync(sid, ct);          // SessionId
            _ = await ReadFieldAsync(stream, ct);            // AutoReconnectCookie
            return new AuthRequest(RequestKind.AutoReconnectCookie, "", "");
        }

        return null;
    }

    // A length-prefixed field: UInt16LE length, then that many bytes.
    private static async Task<byte[]> ReadFieldAsync(Stream stream, CancellationToken ct)
    {
        int len = await ReadUInt16Async(stream, ct);
        var buf = new byte[len];
        if (len > 0) await stream.ReadExactlyAsync(buf, ct);
        return buf;
    }

    private static async Task<ushort> ReadUInt16Async(Stream stream, CancellationToken ct)
    {
        var b = new byte[2];
        await stream.ReadExactlyAsync(b, ct);
        return BinaryPrimitives.ReadUInt16LittleEndian(b);
    }

    // RDSTLS strings are UTF-16LE; the length field counts bytes and may include a trailing NUL.
    private static string Decode(byte[] utf16le)
    {
        string s = Encoding.Unicode.GetString(utf16le);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }
}

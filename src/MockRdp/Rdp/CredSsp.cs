using System.Formats.Asn1;
using System.Text;

namespace MockRdp.Rdp;

/// <summary>
/// CredSSP (MS-CSSP) wire structures for NLA: the TSRequest envelope exchanged over TLS during
/// PROTOCOL_HYBRID authentication, and the TSCredentials it finally delivers. DER via the BCL's
/// <see cref="AsnReader"/>/<see cref="AsnWriter"/>; the NTLM tokens and sealing are handled by SSPI.
/// </summary>
public static class CredSsp
{
    private static readonly Asn1Tag T0 = new(TagClass.ContextSpecific, 0, isConstructed: true);
    private static readonly Asn1Tag T1 = new(TagClass.ContextSpecific, 1, isConstructed: true);
    private static readonly Asn1Tag T2 = new(TagClass.ContextSpecific, 2, isConstructed: true);
    private static readonly Asn1Tag T3 = new(TagClass.ContextSpecific, 3, isConstructed: true);
    private static readonly Asn1Tag T4 = new(TagClass.ContextSpecific, 4, isConstructed: true);
    private static readonly Asn1Tag T5 = new(TagClass.ContextSpecific, 5, isConstructed: true);

    /// <summary>A CredSSP TSRequest (MS-CSSP 2.2.1). Only the fields the mock uses are modelled; a
    /// single nego token is carried (RDP never sends more than one).</summary>
    public sealed record TSRequest
    {
        public int Version { get; init; } = 6;
        public byte[]? NegoToken { get; init; }
        public byte[]? AuthInfo { get; init; }
        public byte[]? PubKeyAuth { get; init; }
        public int? ErrorCode { get; init; }
        public byte[]? ClientNonce { get; init; }
    }

    public static byte[] Encode(TSRequest r)
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence(T0)) w.WriteInteger(r.Version);

            if (r.NegoToken is not null)
                using (w.PushSequence(T1))            // negoTokens [1] NegoData
                using (w.PushSequence())              //   SEQUENCE OF
                using (w.PushSequence())              //     SEQUENCE
                using (w.PushSequence(T0))            //       negoToken [0]
                    w.WriteOctetString(r.NegoToken);

            if (r.AuthInfo is not null)
                using (w.PushSequence(T2)) w.WriteOctetString(r.AuthInfo);
            if (r.PubKeyAuth is not null)
                using (w.PushSequence(T3)) w.WriteOctetString(r.PubKeyAuth);
            if (r.ErrorCode is not null)
                using (w.PushSequence(T4)) w.WriteInteger(r.ErrorCode.Value);
            if (r.ClientNonce is not null)
                using (w.PushSequence(T5)) w.WriteOctetString(r.ClientNonce);
        }
        return w.Encode();
    }

    public static TSRequest Decode(ReadOnlyMemory<byte> der)
    {
        var outer = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();

        int version = (int)outer.ReadSequence(T0).ReadInteger();
        byte[]? nego = null, authInfo = null, pubKeyAuth = null, nonce = null;
        int? errorCode = null;

        while (outer.HasData)
        {
            var tag = outer.PeekTag();
            if (tag == T1)
            {
                var item = outer.ReadSequence(T1).ReadSequence().ReadSequence();   // NegoData → first token
                nego = item.ReadSequence(T0).ReadOctetString();
            }
            else if (tag == T2) authInfo = outer.ReadSequence(T2).ReadOctetString();
            else if (tag == T3) pubKeyAuth = outer.ReadSequence(T3).ReadOctetString();
            else if (tag == T4) errorCode = (int)outer.ReadSequence(T4).ReadInteger();
            else if (tag == T5) nonce = outer.ReadSequence(T5).ReadOctetString();
            else outer.ReadEncodedValue();
        }

        return new TSRequest
        {
            Version = version, NegoToken = nego, AuthInfo = authInfo,
            PubKeyAuth = pubKeyAuth, ErrorCode = errorCode, ClientNonce = nonce,
        };
    }

    /// <summary>Decodes TSCredentials → TSPasswordCreds (MS-CSSP 2.2.1.2). Returns (domain, user); the
    /// password is intentionally ignored.</summary>
    public static (string Domain, string User) DecodePasswordCredentials(byte[] tsCredentials)
    {
        var creds = new AsnReader(tsCredentials, AsnEncodingRules.DER).ReadSequence();
        _ = creds.ReadSequence(T0).ReadInteger();                 // credType
        var inner = creds.ReadSequence(T1).ReadOctetString();

        var pw = new AsnReader(inner, AsnEncodingRules.DER).ReadSequence();
        string domain = Utf16(pw.ReadSequence(T0).ReadOctetString());
        string user = Utf16(pw.ReadSequence(T1).ReadOctetString());
        return (domain, user);
    }

    /// <summary>Builds a TSCredentials carrying TSPasswordCreds — used by the test client.</summary>
    public static byte[] EncodePasswordCredentials(string domain, string user, string password)
    {
        var inner = new AsnWriter(AsnEncodingRules.DER);
        using (inner.PushSequence())
        {
            using (inner.PushSequence(T0)) inner.WriteOctetString(Encoding.Unicode.GetBytes(domain));
            using (inner.PushSequence(T1)) inner.WriteOctetString(Encoding.Unicode.GetBytes(user));
            using (inner.PushSequence(T2)) inner.WriteOctetString(Encoding.Unicode.GetBytes(password));
        }
        var creds = inner.Encode();

        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())
        {
            using (w.PushSequence(T0)) w.WriteInteger(1);       // credType = 1 (password)
            using (w.PushSequence(T1)) w.WriteOctetString(creds);
        }
        return w.Encode();
    }

    private static string Utf16(byte[] b)
    {
        string s = Encoding.Unicode.GetString(b);
        int nul = s.IndexOf('\0');
        return nul >= 0 ? s[..nul] : s;
    }
}

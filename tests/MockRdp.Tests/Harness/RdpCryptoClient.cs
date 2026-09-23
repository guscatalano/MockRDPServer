using System.Security.Cryptography;
using MockRdp.Mcs;
using MockRdp.Rdp;
using MockRdp.Util;

namespace MockRdp.Tests.Harness;

/// <summary>
/// Client-side Standard RDP Security for tests — mirrors what mstsc/FreeRDP do so the mock's server
/// crypto (b1) can be validated end-to-end in CI without a real client. Parses the server's SC_SECURITY,
/// encrypts the client random for the Security Exchange PDU, and encrypts+MACs subsequent PDUs on the
/// single client→server RC4 stream.
/// </summary>
public sealed class RdpCryptoClient
{
    private const ushort SecEncrypt = 0x0008;
    private const ushort SecInfoPkt = 0x0040;
    private const ushort SecExchangePkt = 0x0001;
    private const ushort SecSecureChecksum = 0x0800;

    private readonly byte[] _macKey;
    private readonly byte[] _modLE, _expLE;
    private readonly StandardSecurity.Rc4 _rc4;
    private uint _count;
    private readonly StandardSecurity.Rc4 _decRc4;   // server→client (HIGH)
    private uint _decCount;

    public byte[] ClientRandom { get; } = RandomNumberGenerator.GetBytes(32);
    public byte[] ServerRandom { get; }

    public RdpCryptoClient(ReadOnlySpan<byte> connectResponseTpdu, uint method)
    {
        var (serverRandom, cert) = McsClient.ParseServerSecurity(connectResponseTpdu);
        ServerRandom = serverRandom;
        (_modLE, _expLE) = StandardSecurity.ParsePublicKeyFromProprietaryCert(cert);

        var (mac, clientEncrypt, clientDecrypt) = StandardSecurity.DeriveKeys(ClientRandom, ServerRandom, method);
        _macKey = mac;
        int keyLen = method == StandardSecurity.Method128Bit ? 16 : 8;
        _rc4 = new StandardSecurity.Rc4(clientEncrypt.AsSpan(0, keyLen));
        _decRc4 = new StandardSecurity.Rc4(clientDecrypt.AsSpan(0, keyLen));
    }

    /// <summary>Decrypts a server→client Send Data payload. At ENCRYPTION_LEVEL_HIGH the body is RC4
    /// encrypted behind an 8-byte MAC; at LOW the security header is present but unencrypted. Returns
    /// the inner PDU (header stripped) and the security-header flags.</summary>
    public (byte[] Pdu, ushort Flags) DecryptServerPdu(ReadOnlySpan<byte> sendDataPayload)
    {
        ushort flags = (ushort)(sendDataPayload[0] | sendDataPayload[1] << 8);
        if ((flags & SecEncrypt) == 0)
            return (sendDataPayload[4..].ToArray(), flags);   // LOW: basic header, no encryption
        var body = sendDataPayload[12..].ToArray();
        _decRc4.Process(body);
        _decCount++;
        return (body, flags);
    }

    /// <summary>The Security Exchange PDU: the RSA-encrypted client random (MS-RDPBCGR 2.2.1.10).</summary>
    public byte[] SecurityExchange(ushort userId)
    {
        var encLE = StandardSecurity.RsaRawEncrypt(_modLE, _expLE, ClientRandom);
        var padded = new byte[_modLE.Length];
        Array.Copy(encLE, padded, Math.Min(encLE.Length, padded.Length));   // little-endian, zero-padded

        var w = new ByteWriter();
        w.WriteUInt16LE(SecExchangePkt);
        w.WriteUInt16LE(0);
        w.WriteUInt32LE((uint)(padded.Length + 8));   // length includes the 8-byte trailing pad
        w.WriteBytes(padded);
        w.WriteBytes(new byte[8]);
        return McsClient.SendDataRequest(userId, Gcc.IoChannelId, w.AsSpan());
    }

    /// <summary>Encrypts and MACs a PDU payload (salted secure-checksum MAC), advancing the RC4 stream.
    /// <paramref name="extraFlags"/> adds e.g. SEC_INFO_PKT for the Client Info PDU.</summary>
    public byte[] Encrypt(ushort userId, ushort channelId, ReadOnlySpan<byte> data, ushort extraFlags = 0)
    {
        var buf = data.ToArray();
        var mac = new byte[8];
        StandardSecurity.ComputeSaltedMac(_macKey, buf, _count, mac);
        _count++;
        _rc4.Process(buf);

        var w = new ByteWriter();
        w.WriteUInt16LE((ushort)(SecEncrypt | SecSecureChecksum | extraFlags));
        w.WriteUInt16LE(0);
        w.WriteBytes(mac);
        w.WriteBytes(buf);
        return McsClient.SendDataRequest(userId, channelId, w.AsSpan());
    }

    public byte[] EncryptClientInfo(ushort userId) =>
        Encrypt(userId, Gcc.IoChannelId, McsClient.BuildInfoPacketData(), SecInfoPkt);
}

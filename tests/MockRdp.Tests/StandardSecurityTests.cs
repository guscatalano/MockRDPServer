using System.Numerics;
using System.Text;
using MockRdp.Rdp;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// Unit checks for the Standard RDP Security crypto core (b1). RC4 and the raw-RSA client-random
/// exchange are validated against known vectors / round-trips here; the key-schedule, MAC and the
/// proprietary-certificate signature get their authoritative check against real FreeRDP.
/// </summary>
public class StandardSecurityTests
{
    [Fact]
    public void Rc4_MatchesKnownAnswerVector()
    {
        // Classic RC4 test vector: key "Key", plaintext "Plaintext" -> BBF316E8D940AF0AD3.
        var buf = Encoding.ASCII.GetBytes("Plaintext");
        new StandardSecurity.Rc4("Key"u8).Process(buf);
        Assert.Equal("BBF316E8D940AF0AD3", Convert.ToHexString(buf));

        // And decryption is the same keystream applied again.
        new StandardSecurity.Rc4("Key"u8).Process(buf);
        Assert.Equal("Plaintext", Encoding.ASCII.GetString(buf));
    }

    [Fact]
    public void RawRsa_ClientRandomRoundTrips()
    {
        var key = StandardSecurity.ServerRsaKey.Generate(2048);

        var clientRandom = new byte[32];
        new Random(1234).NextBytes(clientRandom);

        // Client side: encrypt the random as a little-endian integer with the server's public key.
        var n = new BigInteger(key.ModulusLE, isUnsigned: true, isBigEndian: false);
        var e = new BigInteger(key.PublicExponentLE, isUnsigned: true, isBigEndian: false);
        var m = new BigInteger(clientRandom, isUnsigned: true, isBigEndian: false);
        var c = BigInteger.ModPow(m, e, n);
        var cipherLE = c.ToByteArray(isUnsigned: true, isBigEndian: false);

        var recovered = key.DecryptClientRandom(cipherLE);
        Assert.Equal(clientRandom, recovered.AsSpan(0, 32).ToArray());
    }

    [Fact]
    public void ProprietaryCertificate_HasExpectedStructure()
    {
        var key = StandardSecurity.ServerRsaKey.Generate(2048);
        var cert = StandardSecurity.BuildProprietaryCertificate(key);

        // dwVersion=1, then the RSA1 public-key blob at offset 16.
        Assert.Equal(1u, BitConverter.ToUInt32(cert, 0));
        Assert.Equal("RSA1", Encoding.ASCII.GetString(cert, 16, 4));

        // The public-key blob length is the field at offset 14; the modulus follows the 20-byte
        // RSA_PUBLIC_KEY header (magic+keylen+bitlen+datalen+exp) inside it.
        ushort pubLen = BitConverter.ToUInt16(cert, 14);
        Assert.Equal((uint)key.BitLength, BitConverter.ToUInt32(cert, 24)); // bitlen field

        // Signature blob follows the public-key blob: type BB_RSA_SIGNATURE_BLOB (0x0008), 72 bytes.
        int sigTypeOff = 16 + pubLen;
        Assert.Equal(0x0008, BitConverter.ToUInt16(cert, sigTypeOff));
        Assert.Equal(72, BitConverter.ToUInt16(cert, sigTypeOff + 2));
    }

    [Fact]
    public void Mac_IsDeterministicAndKeyDependent()
    {
        var macKey = new byte[16];
        new Random(7).NextBytes(macKey);
        var data = Encoding.ASCII.GetBytes("the quick brown fox");

        var a = new byte[8];
        var b = new byte[8];
        StandardSecurity.ComputeMac(macKey, data, a);
        StandardSecurity.ComputeMac(macKey, data, b);
        Assert.Equal(a, b);                       // deterministic

        macKey[0] ^= 0xFF;
        var c = new byte[8];
        StandardSecurity.ComputeMac(macKey, data, c);
        Assert.NotEqual(a, c);                    // depends on the key
    }

    [Fact]
    public void SessionKeys_DecryptVerify_RoundTripsAClientEncryptedPdu()
    {
        // Server and a mirror "client" derive keys from the same randoms; the mirror encrypts+MACs a
        // body the way a real client would, and the server's DecryptVerify recovers and validates it.
        var clientRandom = new byte[32];
        var serverRandom = new byte[32];
        new Random(1).NextBytes(clientRandom);
        new Random(2).NextBytes(serverRandom);

        var server = new StandardSecurity.SessionKeys(clientRandom, serverRandom, StandardSecurity.Method128Bit);
        var mirror = new ClientMirror(clientRandom, serverRandom);

        var plaintext = Encoding.ASCII.GetBytes("Client Info PDU payload");
        var (cipher, mac) = mirror.EncryptMac(plaintext);

        Assert.True(server.DecryptVerify(cipher, mac));
        Assert.Equal(plaintext, cipher);          // decrypted in place back to the original

        // A corrupted MAC is rejected (fresh keys — RC4 is stateful).
        var server2 = new StandardSecurity.SessionKeys(clientRandom, serverRandom, StandardSecurity.Method128Bit);
        var (cipher2, mac2) = new ClientMirror(clientRandom, serverRandom).EncryptMac(plaintext);
        mac2[0] ^= 0xFF;
        Assert.False(server2.DecryptVerify(cipher2, mac2));
    }

    [Fact]
    public void SessionKeys_DecryptVerify_HandlesSaltedMacWithPerPacketCount()
    {
        // FreeRDP/mstsc use the secure-checksum (salted) MAC, which folds in a per-packet counter.
        // Two packets on the same stream must each verify with the counter advancing 0, 1, ...
        var clientRandom = new byte[32];
        var serverRandom = new byte[32];
        new Random(3).NextBytes(clientRandom);
        new Random(4).NextBytes(serverRandom);

        var server = new StandardSecurity.SessionKeys(clientRandom, serverRandom, StandardSecurity.Method128Bit);
        var mirror = new ClientMirror(clientRandom, serverRandom);

        for (uint count = 0; count < 3; count++)
        {
            var plaintext = Encoding.ASCII.GetBytes($"salted packet {count}");
            var (cipher, mac) = mirror.EncryptSaltedMac(plaintext, count);
            Assert.True(server.DecryptVerify(cipher, mac, salted: true));
            Assert.Equal(plaintext, cipher);
        }
    }

    /// <summary>Minimal client-side mirror of the key schedule: derives the client's encrypt key the
    /// same way the server derives its decrypt key, so a round-trip test needs no real client.</summary>
    private sealed class ClientMirror
    {
        private readonly byte[] _macKey;
        private readonly StandardSecurity.Rc4 _rc4;

        public ClientMirror(byte[] clientRandom, byte[] serverRandom)
        {
            var keys = DeriveClientEncrypt(clientRandom, serverRandom);
            _macKey = keys.mac;
            _rc4 = new StandardSecurity.Rc4(keys.enc);
        }

        public (byte[] cipher, byte[] mac) EncryptMac(byte[] plaintext)
        {
            var mac = new byte[8];
            StandardSecurity.ComputeMac(_macKey, plaintext, mac);   // MAC over plaintext
            var cipher = (byte[])plaintext.Clone();
            _rc4.Process(cipher);
            return (cipher, mac);
        }

        public (byte[] cipher, byte[] mac) EncryptSaltedMac(byte[] plaintext, uint count)
        {
            var mac = new byte[8];
            StandardSecurity.ComputeSaltedMac(_macKey, plaintext, count, mac);
            var cipher = (byte[])plaintext.Clone();
            _rc4.Process(cipher);
            return (cipher, mac);
        }

        // Mirror of StandardSecurity's private derivation for 128-bit, client (encrypt) side.
        private static (byte[] mac, byte[] enc) DeriveClientEncrypt(byte[] cr, byte[] sr)
        {
            byte[] Salted(byte[] secret, byte[] pad)
            {
                using var sha1 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA1);
                sha1.AppendData(pad); sha1.AppendData(secret); sha1.AppendData(cr); sha1.AppendData(sr);
                var s = sha1.GetHashAndReset();
                using var md5 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
                md5.AppendData(secret); md5.AppendData(s);
                return md5.GetHashAndReset()[..16];
            }
            var pre = new byte[48];
            Array.Copy(cr, 0, pre, 0, 24); Array.Copy(sr, 0, pre, 24, 24);
            var master = Salted(pre, "A"u8.ToArray()).Concat(Salted(pre, "BB"u8.ToArray())).Concat(Salted(pre, "CCC"u8.ToArray())).ToArray();
            var blob = Salted(master, "X"u8.ToArray()).Concat(Salted(master, "YY"u8.ToArray())).Concat(Salted(master, "ZZZ"u8.ToArray())).ToArray();
            var mac = blob[..16];
            using var md = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
            md.AppendData(blob.AsSpan(32, 16)); md.AppendData(cr); md.AppendData(sr);  // client encrypt key
            var enc = md.GetHashAndReset()[..16];
            return (mac, enc);
        }
    }
}

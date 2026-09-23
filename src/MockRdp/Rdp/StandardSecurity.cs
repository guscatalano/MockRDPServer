using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace MockRdp.Rdp;

/// <summary>
/// Standard RDP Security crypto (MS-RDPBCGR §5.3): the legacy, pre-TLS security layer used when a
/// client negotiates <c>PROTOCOL_RDP</c>. This is the server half — build the proprietary server
/// certificate, decrypt the client random from the Security Exchange PDU, derive the RC4 session
/// keys, and verify/produce the non-FIPS MAC.
///
/// b1 implements RC4 (40/56/128-bit) + MAC. FIPS (3DES) is b2. The whole thing is deliberately
/// self-contained and free of connection state so it can be unit-tested without a live session.
/// </summary>
public static class StandardSecurity
{
    // Encryption methods (SC_SECURITY.encryptionMethod bitmask, MS-RDPBCGR 2.2.1.4.3).
    public const uint MethodNone = 0x00;
    public const uint Method40Bit = 0x01;
    public const uint Method128Bit = 0x02;
    public const uint Method56Bit = 0x08;
    public const uint MethodFips = 0x10;

    // Encryption levels (SC_SECURITY.encryptionLevel).
    public const uint LevelNone = 0;
    public const uint LevelLow = 1;
    public const uint LevelClientCompatible = 2;
    public const uint LevelHigh = 3;
    public const uint LevelFips = 4;

    // Security header flags (MS-RDPBCGR 2.2.8.1.1.2.1).
    public const ushort SecExchangePkt = 0x0001;
    public const ushort SecEncrypt = 0x0008;
    public const ushort SecInfoPkt = 0x0040;
    public const ushort SecLicensePkt = 0x0080;
    public const ushort SecSecureChecksum = 0x0800;  // salted MAC (2.2.8.1.1.2.3)

    private static readonly byte[] Pad1 = Enumerable.Repeat((byte)0x36, 40).ToArray();
    private static readonly byte[] Pad2 = Enumerable.Repeat((byte)0x5C, 48).ToArray();

    // The RC4 methods the server can actually speak. FIPS (3DES) is not yet implemented, so it is
    // never selected even if a client offers it — the server falls back to the strongest RC4.
    private const uint SupportedMethods = Method40Bit | Method56Bit | Method128Bit;

    /// <summary>Chooses the encryption method given what the client offered and the server's preference.
    /// Uses the preferred method when it is supported and offered; otherwise falls back to the strongest
    /// supported RC4 method offered. 0 = none (run in the clear).</summary>
    public static uint ChooseMethod(uint offered, uint preferred)
    {
        if ((preferred & SupportedMethods) != 0 && (offered & preferred) != 0) return preferred;
        if ((offered & Method128Bit) != 0) return Method128Bit;
        if ((offered & Method56Bit) != 0) return Method56Bit;
        if ((offered & Method40Bit) != 0) return Method40Bit;
        return 0;
    }

    public static string MethodName(uint m) => m switch
    {
        Method40Bit => "40-bit RC4",
        Method128Bit => "128-bit RC4",
        Method56Bit => "56-bit RC4",
        MethodFips => "FIPS (3DES)",
        _ => "none",
    };

    // ---- Terminal Services well-known signing key (MS-RDPBCGR 5.3.3.1.1) -------------------------
    // Clients validate a Proprietary Server Certificate's signature against this public key, so the
    // server must sign with the matching private exponent. All little-endian, 64-byte (512-bit).
    private static readonly byte[] TsskModulus =
    [
        0x3d, 0x3a, 0x5e, 0xbd, 0x72, 0x43, 0x3e, 0xc9, 0x4d, 0xbb, 0xc1, 0x1e, 0x4a, 0xba, 0x5f, 0xcb,
        0x3e, 0x88, 0x20, 0x87, 0xef, 0xf5, 0xc1, 0xe2, 0xd7, 0xb7, 0x6b, 0x9a, 0xf2, 0x52, 0x45, 0x95,
        0xce, 0x63, 0x65, 0x6b, 0x58, 0x3a, 0xfe, 0xef, 0x7c, 0xe7, 0xbf, 0xfe, 0x3d, 0xf6, 0x5c, 0x7d,
        0x6c, 0x5e, 0x06, 0x09, 0x1a, 0xf5, 0x61, 0xbb, 0x20, 0x93, 0x09, 0x5f, 0x05, 0x6d, 0xea, 0x87,
    ];
    private static readonly byte[] TsskPrivateExponent =
    [
        0x87, 0xa7, 0x19, 0x32, 0xda, 0x11, 0x87, 0x55, 0x58, 0x00, 0x16, 0x16, 0x25, 0x65, 0x68, 0xf8,
        0x24, 0x3e, 0xe6, 0xfa, 0xe9, 0x67, 0x49, 0x94, 0xcf, 0x92, 0xcc, 0x33, 0x99, 0xe8, 0x08, 0x60,
        0x17, 0x9a, 0x12, 0x9f, 0x24, 0xdd, 0xb1, 0x24, 0x99, 0xc7, 0x3a, 0xb8, 0x0a, 0x7b, 0x0d, 0xdd,
        0x35, 0x07, 0x79, 0x17, 0x0b, 0x51, 0x9b, 0xb3, 0xc7, 0x10, 0x01, 0x13, 0xe7, 0x3f, 0xf3, 0x5f,
    ];

    /// <summary>RC4 stream cipher (not in the BCL). Keyed once; <see cref="Process"/> XORs in place.</summary>
    public sealed class Rc4
    {
        private readonly byte[] _s = new byte[256];
        private int _i, _j;

        public Rc4(ReadOnlySpan<byte> key)
        {
            for (int k = 0; k < 256; k++) _s[k] = (byte)k;
            int j = 0;
            for (int k = 0; k < 256; k++)
            {
                j = (j + _s[k] + key[k % key.Length]) & 0xff;
                (_s[k], _s[j]) = (_s[j], _s[k]);
            }
        }

        public void Process(Span<byte> buf)
        {
            for (int n = 0; n < buf.Length; n++)
            {
                _i = (_i + 1) & 0xff;
                _j = (_j + _s[_i]) & 0xff;
                (_s[_i], _s[_j]) = (_s[_j], _s[_i]);
                buf[n] ^= _s[(_s[_i] + _s[_j]) & 0xff];
            }
        }
    }

    /// <summary>An RSA key pair the server advertises in its proprietary certificate and uses to
    /// decrypt the client random. RDP encrypts/decrypts the random with raw RSA over little-endian
    /// integers (no PKCS padding), so we do the modular exponentiation ourselves.</summary>
    public sealed class ServerRsaKey
    {
        private readonly BigInteger _modulus;
        private readonly BigInteger _privateExponent;
        public byte[] ModulusLE { get; }      // little-endian, no sign byte
        public byte[] PublicExponentLE { get; }
        public int BitLength { get; }

        public ServerRsaKey(RSAParameters p)
        {
            // RSAParameters are big-endian; RDP wants little-endian.
            ModulusLE = Reverse(p.Modulus!);
            PublicExponentLE = Reverse(p.Exponent!);
            _modulus = ToPositive(p.Modulus!);
            _privateExponent = ToPositive(p.D!);
            BitLength = p.Modulus!.Length * 8;
        }

        /// <summary>Generates a fresh server exchange key. 2048-bit is universally accepted and the
        /// proprietary-cert signature is a separate fixed 512-bit key, so size here is unconstrained.</summary>
        public static ServerRsaKey Generate(int bits = 2048)
        {
            using var rsa = RSA.Create(bits);
            return new ServerRsaKey(rsa.ExportParameters(true));
        }

        /// <summary>Decrypts the encryptedClientRandom from a Security Exchange PDU. The ciphertext is
        /// little-endian; result is the little-endian client random, of which the first 32 bytes matter.</summary>
        public byte[] DecryptClientRandom(ReadOnlySpan<byte> encryptedLE)
        {
            var c = ToPositive(Reverse(encryptedLE.ToArray()));
            var m = BigInteger.ModPow(c, _privateExponent, _modulus);
            var le = m.ToByteArray(isUnsigned: true, isBigEndian: false); // little-endian
            var outp = new byte[Math.Max(le.Length, 32)];
            Array.Copy(le, outp, le.Length);
            return outp;
        }
    }

    /// <summary>The RC4 session keys and MAC key derived from the two randoms (MS-RDPBCGR 5.3.5.1),
    /// plus the per-direction RC4 state and the 4096-packet key-update bookkeeping.</summary>
    public sealed class SessionKeys
    {
        private readonly byte[] _macKey;             // 16 bytes, used for every MAC
        private readonly byte[] _initialDecryptKey;  // client->server key, pre-salt (for key update)
        private readonly int _rc4KeyLen;
        private readonly uint _method;
        private byte[] _decryptKey;
        private Rc4 _decryptRc4;
        private int _decryptUses;          // resets every 4096 packets (key-update cadence)
        private uint _decryptChecksumCount; // monotonic; salts the secure-checksum MAC
        private readonly byte[] _initialEncryptKey;  // server→client (level HIGH); pre-salt
        private byte[] _encryptKey;
        private Rc4 _encryptRc4;
        private int _encryptUses;
        private uint _encryptChecksumCount;

        public SessionKeys(byte[] clientRandom, byte[] serverRandom, uint method)
        {
            _method = method;
            _rc4KeyLen = method == Method128Bit ? 16 : 8;

            // premaster = first 24 bytes of each random.
            var pre = new byte[48];
            Array.Copy(clientRandom, 0, pre, 0, 24);
            Array.Copy(serverRandom, 0, pre, 24, 24);

            var master = new byte[48];
            SaltedHash(pre, "A"u8, clientRandom, serverRandom, master.AsSpan(0, 16));
            SaltedHash(pre, "BB"u8, clientRandom, serverRandom, master.AsSpan(16, 16));
            SaltedHash(pre, "CCC"u8, clientRandom, serverRandom, master.AsSpan(32, 16));

            var sessionBlob = new byte[48];
            SaltedHash(master, "X"u8, clientRandom, serverRandom, sessionBlob.AsSpan(0, 16));
            SaltedHash(master, "YY"u8, clientRandom, serverRandom, sessionBlob.AsSpan(16, 16));
            SaltedHash(master, "ZZZ"u8, clientRandom, serverRandom, sessionBlob.AsSpan(32, 16));

            // MS-RDPBCGR 5.3.5.1: MACKey = blob[0..16], client *decrypt* key = FinalHash(blob[16..32]),
            // client *encrypt* key = FinalHash(blob[32..48]). The server decrypts client→server with
            // the client's *encrypt* key, so we derive from the third 128 bits.
            _macKey = sessionBlob[0..16];
            _initialDecryptKey = FinalHash(sessionBlob.AsSpan(32, 16), clientRandom, serverRandom);
            _decryptKey = (byte[])_initialDecryptKey.Clone();
            Salt(_decryptKey, method);
            _decryptRc4 = new Rc4(_decryptKey.AsSpan(0, _rc4KeyLen));

            // Encrypt direction (server→client, used at ENCRYPTION_LEVEL_HIGH): the server encrypts with
            // the client's *decrypt* key = FinalHash(blob[16..32]).
            _initialEncryptKey = FinalHash(sessionBlob.AsSpan(16, 16), clientRandom, serverRandom);
            _encryptKey = (byte[])_initialEncryptKey.Clone();
            Salt(_encryptKey, method);
            _encryptRc4 = new Rc4(_encryptKey.AsSpan(0, _rc4KeyLen));
        }

        /// <summary>Encrypts an outbound (server→client) PDU body in place and returns its 8-byte MAC —
        /// the mirror of <see cref="DecryptVerify"/>, used at ENCRYPTION_LEVEL_HIGH.</summary>
        public byte[] EncryptSign(Span<byte> body, bool salted = true)
        {
            if (_encryptUses == 4096) { UpdateEncryptKey(); _encryptUses = 0; }
            _encryptUses++;
            var mac = new byte[8];
            if (salted) ComputeSaltedMac(_macKey, body, _encryptChecksumCount, mac);
            else ComputeMac(_macKey, body, mac);
            _encryptChecksumCount++;
            _encryptRc4.Process(body);
            return mac;
        }

        private void UpdateEncryptKey()
        {
            var next = UpdatedKey(_initialEncryptKey, _encryptKey, _rc4KeyLen);
            Salt(next, _method);
            _encryptKey = next;
            _encryptRc4 = new Rc4(_encryptKey.AsSpan(0, _rc4KeyLen));
        }

        /// <summary>Decrypts an inbound (client→server) encrypted PDU body in place and verifies its
        /// 8-byte MAC. <paramref name="salted"/> selects the secure-checksum MAC (SEC_SECURE_CHECKSUM /
        /// FASTPATH_INPUT_SECURE_CHECKSUM), which folds in a per-packet count. Returns false on MAC
        /// mismatch. Updates the RC4 key every 4096 packets (MS-RDPBCGR 5.3.7).</summary>
        public bool DecryptVerify(Span<byte> body, ReadOnlySpan<byte> mac, bool salted = false)
        {
            if (_decryptUses == 4096) { UpdateKey(); _decryptUses = 0; }
            _decryptUses++;
            _decryptRc4.Process(body);
            Span<byte> computed = stackalloc byte[8];
            if (salted) ComputeSaltedMac(_macKey, body, _decryptChecksumCount, computed);
            else ComputeMac(_macKey, body, computed);
            _decryptChecksumCount++;
            return computed.SequenceEqual(mac);
        }

        private void UpdateKey()
        {
            // MS-RDPBCGR 5.3.7: newKey = hash(initialKey, currentKey); RC4 it with itself; re-salt.
            var next = UpdatedKey(_initialDecryptKey, _decryptKey, _rc4KeyLen);
            Salt(next, _method);
            _decryptKey = next;
            _decryptRc4 = new Rc4(_decryptKey.AsSpan(0, _rc4KeyLen));
        }
    }

    // ---- Proprietary server certificate (MS-RDPBCGR 2.2.1.4.3.1.1 / signing 5.3.3.1.1) ----------

    /// <summary>Builds a Proprietary Server Certificate (RDP_SERVER_CERTIFICATE, version 1) wrapping
    /// <paramref name="key"/>'s public part and signed by the well-known Terminal Services key.</summary>
    public static byte[] BuildProprietaryCertificate(ServerRsaKey key)
    {
        int modLen = key.ModulusLE.Length;
        // RSA_PUBLIC_KEY (2.2.1.4.3.1.1.1): "RSA1", keylen=mod+8, bitlen, datalen=bitlen/8-1, exp, mod+pad.
        var pub = new List<byte>();
        pub.AddRange("RSA1"u8.ToArray());
        AddU32(pub, (uint)(modLen + 8));
        AddU32(pub, (uint)key.BitLength);
        AddU32(pub, (uint)(key.BitLength / 8 - 1));
        var exp = new byte[4];
        Array.Copy(key.PublicExponentLE, exp, Math.Min(4, key.PublicExponentLE.Length));
        pub.AddRange(exp);
        pub.AddRange(key.ModulusLE);
        pub.AddRange(new byte[8]);   // 8 bytes of zero padding after the modulus

        // The signature covers dwVersion..PublicKeyBlob. Assemble that prefix, then sign its MD5.
        var cert = new List<byte>();
        AddU32(cert, 0x00000001);        // dwVersion = 1 (proprietary)
        AddU32(cert, 0x00000001);        // dwSigAlgId = RSA
        AddU32(cert, 0x00000001);        // dwKeyAlgId = RSA
        AddU16(cert, 0x0006);            // wPublicKeyBlobType = BB_RSA_KEY_BLOB
        AddU16(cert, (ushort)pub.Count); // wPublicKeyBlobLen
        cert.AddRange(pub);

        var signature = SignProprietary(cert.ToArray());   // 64 bytes
        AddU16(cert, 0x0008);            // wSignatureBlobType = BB_RSA_SIGNATURE_BLOB
        AddU16(cert, (ushort)(signature.Length + 8)); // wSignatureBlobLen (sig + 8 pad)
        cert.AddRange(signature);
        cert.AddRange(new byte[8]);      // 8 bytes of zero padding after the signature
        return cert.ToArray();
    }

    /// <summary>Signs a proprietary certificate: MD5 the cert prefix, wrap the digest in the fixed
    /// RDP padding block, then raw-RSA with the Terminal Services private key (5.3.3.1.1).</summary>
    private static byte[] SignProprietary(byte[] certPrefix)
    {
        var hash = MD5.HashData(certPrefix);            // 16 bytes
        // Padded block (64 bytes, little-endian): hash || 0x00 || 0xFF*(45) || 0x01.
        var block = new byte[64];
        Array.Copy(hash, block, 16);
        block[16] = 0x00;
        for (int i = 17; i < 63; i++) block[i] = 0xFF;
        block[63] = 0x01;

        var m = ToPositive(Reverse(block));             // treat block as a little-endian integer
        var n = ToPositive(TsskModulus.Reverse().ToArray());
        var d = ToPositive(TsskPrivateExponent.Reverse().ToArray());
        var sig = BigInteger.ModPow(m, d, n);
        var le = sig.ToByteArray(isUnsigned: true, isBigEndian: false);
        var outp = new byte[64];
        Array.Copy(le, outp, Math.Min(le.Length, 64));  // little-endian, zero-padded to 64
        return outp;
    }

    // ---- Client-side helpers (symmetry; used by tests and any client-side crypto) ---------------

    /// <summary>Derives the MAC key and both directional RC4 keys from the two randoms (MS-RDPBCGR
    /// 5.3.5.1). The server decrypts client→server with <c>clientEncryptKey</c>; a client encrypts with
    /// it. RC4 keys are already salted/truncated for 40/56-bit; the MAC key is left full-length.</summary>
    public static (byte[] MacKey, byte[] ClientEncryptKey, byte[] ClientDecryptKey) DeriveKeys(
        byte[] clientRandom, byte[] serverRandom, uint method)
    {
        var pre = new byte[48];
        Array.Copy(clientRandom, 0, pre, 0, 24);
        Array.Copy(serverRandom, 0, pre, 24, 24);

        var master = new byte[48];
        SaltedHash(pre, "A"u8, clientRandom, serverRandom, master.AsSpan(0, 16));
        SaltedHash(pre, "BB"u8, clientRandom, serverRandom, master.AsSpan(16, 16));
        SaltedHash(pre, "CCC"u8, clientRandom, serverRandom, master.AsSpan(32, 16));

        var blob = new byte[48];
        SaltedHash(master, "X"u8, clientRandom, serverRandom, blob.AsSpan(0, 16));
        SaltedHash(master, "YY"u8, clientRandom, serverRandom, blob.AsSpan(16, 16));
        SaltedHash(master, "ZZZ"u8, clientRandom, serverRandom, blob.AsSpan(32, 16));

        var mac = blob[0..16];
        var cEnc = FinalHash(blob.AsSpan(32, 16), clientRandom, serverRandom); Salt(cEnc, method);
        var cDec = FinalHash(blob.AsSpan(16, 16), clientRandom, serverRandom); Salt(cDec, method);
        return (mac, cEnc, cDec);
    }

    /// <summary>Extracts the RSA public key (little-endian modulus + exponent) from a Proprietary Server
    /// Certificate, so a client can encrypt its client random for the Security Exchange PDU.</summary>
    public static (byte[] ModulusLE, byte[] ExponentLE) ParsePublicKeyFromProprietaryCert(byte[] cert)
    {
        // dwVersion(4) dwSigAlg(4) dwKeyAlg(4) wPubType(2) wPubLen(2), then the RSA_PUBLIC_KEY blob:
        // magic(4) keylen(4) bitlen(4) datalen(4) pubExp(4) modulus(keylen = modBytes+8 pad).
        const int pub = 16;
        uint keylen = BinaryPrimitives.ReadUInt32LittleEndian(cert.AsSpan(pub + 4, 4));
        var exponent = cert.AsSpan(pub + 16, 4).ToArray();
        var modulus = cert.AsSpan(pub + 20, (int)keylen - 8).ToArray();   // drop the 8-byte pad
        return (modulus, exponent);
    }

    /// <summary>Raw RSA (no padding) over little-endian integers — how RDP encrypts the client random
    /// with the server's public key (MS-RDPBCGR 5.3.4.1). Returns the little-endian ciphertext.</summary>
    public static byte[] RsaRawEncrypt(byte[] modulusLE, byte[] exponentLE, byte[] dataLE)
    {
        var n = new BigInteger(modulusLE, isUnsigned: true, isBigEndian: false);
        var e = new BigInteger(exponentLE, isUnsigned: true, isBigEndian: false);
        var m = new BigInteger(dataLE, isUnsigned: true, isBigEndian: false);
        return BigInteger.ModPow(m, e, n).ToByteArray(isUnsigned: true, isBigEndian: false);
    }

    // ---- MAC + key-schedule primitives ----------------------------------------------------------

    /// <summary>Non-FIPS MAC (MS-RDPBCGR 2.2.8.1.1.2.2): first 8 bytes of
    /// MD5(MACKey ‖ Pad2 ‖ SHA1(MACKey ‖ Pad1 ‖ len ‖ data)).</summary>
    public static void ComputeMac(ReadOnlySpan<byte> macKey, ReadOnlySpan<byte> data, Span<byte> dest8)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(len, data.Length);

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(macKey);
        sha1.AppendData(Pad1);
        sha1.AppendData(len);
        sha1.AppendData(data);
        var sha1Digest = sha1.GetHashAndReset();

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(macKey);
        md5.AppendData(Pad2);
        md5.AppendData(sha1Digest);
        var md5Digest = md5.GetHashAndReset();
        md5Digest.AsSpan(0, 8).CopyTo(dest8);
    }

    /// <summary>Salted (secure-checksum) MAC (MS-RDPBCGR 2.2.8.1.1.2.3): as <see cref="ComputeMac"/> but
    /// the inner SHA1 also folds in a 32-bit little-endian encryption counter after the data.</summary>
    public static void ComputeSaltedMac(ReadOnlySpan<byte> macKey, ReadOnlySpan<byte> data,
        uint count, Span<byte> dest8)
    {
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(len, data.Length);
        Span<byte> cnt = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(cnt, count);

        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(macKey);
        sha1.AppendData(Pad1);
        sha1.AppendData(len);
        sha1.AppendData(data);
        sha1.AppendData(cnt);
        var sha1Digest = sha1.GetHashAndReset();

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(macKey);
        md5.AppendData(Pad2);
        md5.AppendData(sha1Digest);
        md5.GetHashAndReset().AsSpan(0, 8).CopyTo(dest8);
    }

    // SaltedHash(secret, pad, r1, r2) = MD5(secret ‖ SHA1(pad ‖ secret ‖ r1 ‖ r2)).
    private static void SaltedHash(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> pad,
        ReadOnlySpan<byte> r1, ReadOnlySpan<byte> r2, Span<byte> dest16)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(pad);
        sha1.AppendData(secret);
        sha1.AppendData(r1);
        sha1.AppendData(r2);
        var s = sha1.GetHashAndReset();

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(secret);
        md5.AppendData(s);
        md5.GetHashAndReset().AsSpan(0, 16).CopyTo(dest16);
    }

    // FinalHash(k) = MD5(k ‖ r1 ‖ r2), 16 bytes.
    private static byte[] FinalHash(ReadOnlySpan<byte> k, ReadOnlySpan<byte> r1, ReadOnlySpan<byte> r2)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(k);
        md5.AppendData(r1);
        md5.AppendData(r2);
        return md5.GetHashAndReset()[..16];
    }

    // UpdatedKey (5.3.7): tmp = MD5(start ‖ Pad2 ‖ SHA1(start ‖ Pad1 ‖ current)); RC4(tmp) over tmp.
    private static byte[] UpdatedKey(byte[] startKey, byte[] currentKey, int keyLen)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        sha1.AppendData(startKey.AsSpan(0, keyLen));
        sha1.AppendData(Pad1.AsSpan(0, 40));
        sha1.AppendData(currentKey.AsSpan(0, keyLen));
        var s = sha1.GetHashAndReset();

        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(startKey.AsSpan(0, keyLen));
        md5.AppendData(Pad2.AsSpan(0, 48));
        md5.AppendData(s);
        var tmp = md5.GetHashAndReset()[..16];

        var rc4 = new Rc4(tmp.AsSpan(0, keyLen));
        rc4.Process(tmp.AsSpan(0, keyLen));
        // 40/56-bit re-salting is applied by the caller path via Salt(); 128-bit needs none.
        return tmp;
    }

    // Weaken the leading key bytes for 40/56-bit RC4 (128-bit is untouched).
    private static void Salt(byte[] key, uint method)
    {
        if (method == Method40Bit) { key[0] = 0xD1; key[1] = 0x26; key[2] = 0x9E; }
        else if (method == Method56Bit) { key[0] = 0xD1; }
    }

    private static byte[] Reverse(byte[] b) { var c = (byte[])b.Clone(); Array.Reverse(c); return c; }

    // Interpret a big-endian byte array as a non-negative BigInteger.
    private static BigInteger ToPositive(byte[] bigEndian) =>
        new(bigEndian, isUnsigned: true, isBigEndian: true);

    private static void AddU16(List<byte> l, ushort v) { l.Add((byte)v); l.Add((byte)(v >> 8)); }
    private static void AddU32(List<byte> l, uint v)
    { l.Add((byte)v); l.Add((byte)(v >> 8)); l.Add((byte)(v >> 16)); l.Add((byte)(v >> 24)); }
}

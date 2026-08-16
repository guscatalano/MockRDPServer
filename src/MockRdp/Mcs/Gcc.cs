using System.Buffers.Binary;
using MockRdp.Util;

namespace MockRdp.Mcs;

/// <summary>
/// GCC Conference Create (T.124) as embedded in the MCS Connect PDUs, plus the RDP
/// "basic settings" data blocks (MS-RDPBCGR 2.2.1.3 / 2.2.1.4). Only what the mock needs:
/// read the client's requested virtual-channel count, and write the server data blocks.
/// </summary>
public static class Gcc
{
    // RDP channel numbering: I/O channel is fixed at 1003; virtual channels follow.
    public const ushort IoChannelId = 1003;
    public const ushort FirstVirtualChannelId = 1004;

    // Server-data block header types (TS_UD_HEADER type field), little-endian on the wire.
    private const ushort ScCore = 0x0C01;
    private const ushort ScSecurity = 0x0C02;
    private const ushort ScNet = 0x0C03;
    private const ushort CsNet = 0xC003; // client network data (what we parse)

    // Client-to-server / server-to-client H.221 nonstandard keys.
    private static readonly byte[] ClientKeyDuca = "Duca"u8.ToArray();
    private static readonly byte[] ConnectDataPrefix = [0x00, 0x05, 0x00, 0x14, 0x7C, 0x00, 0x01];
    // ConferenceCreateResponse fixed fields (T.124 PER), matching what RDP servers emit:
    //   ConnectGCCPDU choice = conferenceCreateResponse = 14
    //   nodeID (integer16, offset 1001) = 76 0a  →  0x79F3
    //   tag (INTEGER 1)                 = 01 01
    //   result (ENUMERATED success)     = 00
    //   number of UserData sets (1)     = 01
    //   UserData choice (h221NonStd)    = c0
    //   octet-string length determinant = 00  (fixed-size 4, so PER writes 0)
    //   server H.221 key "McDn"         = 4d 63 44 6e
    private static readonly byte[] CcrHeader =
        [0x14, 0x76, 0x0A, 0x01, 0x01, 0x00, 0x01, 0xC0, 0x00, 0x4D, 0x63, 0x44, 0x6E];

    /// <summary>
    /// Reads the number of virtual channels the client requested, from the Client Network Data
    /// (CS_NET) block inside the GCC Conference Create Request user data. Returns 0 if absent.
    /// </summary>
    public static int ReadRequestedChannelCount(ReadOnlySpan<byte> connectInitialUserData)
    {
        // Locate the client blocks by finding the "Duca" H.221 key, then the PER length, then
        // walk the TS_UD blocks looking for CS_NET.
        int keyIdx = IndexOf(connectInitialUserData, ClientKeyDuca);
        if (keyIdx < 0) return 0;
        int pos = keyIdx + ClientKeyDuca.Length;
        _ = Asn1.ReadPerLength(connectInitialUserData, ref pos); // total user-data length

        while (pos + 4 <= connectInitialUserData.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(connectInitialUserData.Slice(pos, 2));
            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(connectInitialUserData.Slice(pos + 2, 2));
            if (len < 4) break;
            if (type == CsNet && pos + 8 <= connectInitialUserData.Length)
                return (int)BinaryPrimitives.ReadUInt32LittleEndian(connectInitialUserData.Slice(pos + 4, 4));
            pos += len;
        }
        return 0;
    }

    /// <summary>
    /// Builds the GCC Conference Create Response user data (server core/network/security blocks),
    /// advertising the I/O channel plus <paramref name="channelCount"/> virtual channels
    /// (1004, 1005, …). This is the OCTET STRING placed in the MCS Connect-Response userData.
    /// </summary>
    public static byte[] BuildConferenceCreateResponse(int channelCount, uint selectedProtocol)
    {
        var blocks = new ByteWriter();
        WriteServerCore(blocks, selectedProtocol);
        WriteServerNetwork(blocks, channelCount);
        WriteServerSecurity(blocks);

        var ccr = new ByteWriter();
        ccr.WriteBytes(CcrHeader);
        Asn1.WritePerLength(ccr, blocks.Length);
        ccr.WriteBytes(blocks.AsSpan());

        var gcc = new ByteWriter();
        gcc.WriteBytes(ConnectDataPrefix);
        Asn1.WritePerLength(gcc, ccr.Length);
        gcc.WriteBytes(ccr.AsSpan());
        return gcc.ToArray();
    }

    private static void WriteServerCore(ByteWriter w, uint selectedProtocol)
    {
        w.WriteUInt16LE(ScCore);
        w.WriteUInt16LE(12);
        w.WriteUInt32LE(0x00080004);      // version = RDP 5.x
        w.WriteUInt32LE(selectedProtocol); // clientRequestedProtocols echoed back
    }

    private static void WriteServerNetwork(ByteWriter w, int channelCount)
    {
        bool pad = (channelCount & 1) == 1;
        int len = 8 + channelCount * 2 + (pad ? 2 : 0);
        w.WriteUInt16LE(ScNet);
        w.WriteUInt16LE((ushort)len);
        w.WriteUInt16LE(IoChannelId);
        w.WriteUInt16LE((ushort)channelCount);
        for (int i = 0; i < channelCount; i++)
            w.WriteUInt16LE((ushort)(FirstVirtualChannelId + i));
        if (pad) w.WriteUInt16LE(0);
    }

    private static void WriteServerSecurity(ByteWriter w)
    {
        // TLS ("enhanced security") is in effect, so RDP-level encryption is NONE.
        w.WriteUInt16LE(ScSecurity);
        w.WriteUInt16LE(12);
        w.WriteUInt32LE(0); // encryptionMethod = 0
        w.WriteUInt32LE(0); // encryptionLevel  = 0 (ENCRYPTION_LEVEL_NONE)
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }
}

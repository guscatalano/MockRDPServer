using System.Buffers.Binary;
using System.Text;
using MockRdp.Mcs;
using MockRdp.Rdp;
using MockRdp.Util;
using MockRdp.X224;

namespace MockRdp.Tests.Harness;

/// <summary>
/// Client-side MCS helpers for the Tier 1 conformance test: builds a Connect-Initial the
/// server can parse, parses the server's Connect-Response network data, and builds/parses
/// the domain PDUs. Intentionally minimal — it exercises the server, not full T.124.
/// </summary>
public static class McsClient
{
    /// <summary>Builds a TPKT-framed MCS Connect-Initial requesting the given virtual channels.</summary>
    public static byte[] BuildConnectInitial(params string[] channels)
    {
        // Client Network Data (CS_NET): channelCount + 12-byte CHANNEL_DEF entries.
        var net = new ByteWriter();
        net.WriteUInt16LE(0xC003);
        net.WriteUInt16LE((ushort)(8 + channels.Length * 12));
        net.WriteUInt32LE((uint)channels.Length);
        Span<byte> field = stackalloc byte[8];
        foreach (var name in channels)
        {
            field.Clear();
            Encoding.ASCII.GetBytes(name).AsSpan(0, Math.Min(name.Length, 7)).CopyTo(field);
            net.WriteBytes(field);
            net.WriteUInt32LE(0); // options
        }

        // GCC user data: ConnectData prefix + client H.221 key "Duca" + PER length + blocks.
        var gcc = new ByteWriter();
        gcc.WriteBytes([0x00, 0x05, 0x00, 0x14, 0x7C, 0x00, 0x01]);
        gcc.WriteBytes("Duca"u8);
        Asn1.WritePerLength(gcc, net.Length);
        gcc.WriteBytes(net.AsSpan());

        // MCS Connect-Initial (BER, [APPLICATION 101]).
        byte[] dp = [0x02, 0x01, 0x22, 0x02, 0x01, 0x02, 0x02, 0x01, 0x00, 0x02, 0x01, 0x01,
                     0x02, 0x01, 0x00, 0x02, 0x01, 0x01, 0x02, 0x03, 0x00, 0xFF, 0xFF, 0x02, 0x01, 0x02];
        var body = new ByteWriter();
        body.WriteBytes([0x04, 0x01, 0x01]); // callingDomainSelector
        body.WriteBytes([0x04, 0x01, 0x01]); // calledDomainSelector
        body.WriteBytes([0x01, 0x01, 0xFF]); // upwardFlag = TRUE
        foreach (var _ in new[] { 0, 1, 2 })  // target / minimum / maximum parameters
        {
            body.WriteUInt8(0x30);
            Asn1.WriteBerLength(body, dp.Length);
            body.WriteBytes(dp);
        }
        body.WriteUInt8(0x04); // userData OCTET STRING
        Asn1.WriteBerLength(body, gcc.Length);
        body.WriteBytes(gcc.AsSpan());

        var pdu = new ByteWriter();
        pdu.WriteUInt8(0x7F);
        pdu.WriteUInt8(0x65);
        Asn1.WriteBerLength(pdu, body.Length);
        pdu.WriteBytes(body.AsSpan());
        return Cotp.BuildDataTpdu(pdu.AsSpan());
    }

    /// <summary>Parses the Server Network Data (SC_NET) from a Connect-Response TPDU.</summary>
    public static (ushort IoChannel, ushort[] Channels) ParseConnectResponseNetwork(ReadOnlySpan<byte> tpdu)
    {
        var mcs = Cotp.StripDataTpdu(tpdu);
        if (mcs.Length < 3 || mcs[0] != 0x7F || mcs[1] != 0x66)
            throw new FormatException("Expected MCS Connect-Response (tag 7f 66).");

        int mcdn = IndexOf(mcs, "McDn"u8);
        if (mcdn < 0) throw new FormatException("Server GCC key 'McDn' not found.");
        int pos = mcdn + 4;
        _ = Asn1.ReadPerLength(mcs, ref pos); // server user-data length

        while (pos + 4 <= mcs.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(mcs.Slice(pos, 2));
            ushort len = BinaryPrimitives.ReadUInt16LittleEndian(mcs.Slice(pos + 2, 2));
            if (len < 4) break;
            if (type == 0x0C03)
            {
                ushort io = BinaryPrimitives.ReadUInt16LittleEndian(mcs.Slice(pos + 4, 2));
                int count = BinaryPrimitives.ReadUInt16LittleEndian(mcs.Slice(pos + 6, 2));
                var ids = new ushort[count];
                for (int i = 0; i < count; i++)
                    ids[i] = BinaryPrimitives.ReadUInt16LittleEndian(mcs.Slice(pos + 8 + i * 2, 2));
                return (io, ids);
            }
            pos += len;
        }
        throw new FormatException("SC_NET block not found in Connect-Response.");
    }

    public static byte[] ErectDomainRequest() => Cotp.BuildDataTpdu([0x04, 0x01, 0x00, 0x01, 0x00]);

    public static byte[] AttachUserRequest() => Cotp.BuildDataTpdu([0x28]);

    public static ushort ParseAttachUserConfirm(ReadOnlySpan<byte> tpdu)
    {
        var mcs = Cotp.StripDataTpdu(tpdu);
        if (mcs[0] != 0x2E) throw new FormatException("Expected Attach User Confirm (0x2e).");
        return (ushort)(BinaryPrimitives.ReadUInt16BigEndian(mcs.Slice(2, 2)) + 1001);
    }

    public static byte[] ChannelJoinRequest(ushort userId, ushort channelId)
    {
        var w = new ByteWriter();
        w.WriteUInt8(0x38);
        w.WriteUInt16BE((ushort)(userId - 1001)); // initiator = UserId (offset from 1001)
        w.WriteUInt16BE(channelId);
        return Cotp.BuildDataTpdu(w.AsSpan());
    }

    public static ushort ParseChannelJoinConfirm(ReadOnlySpan<byte> tpdu)
    {
        var mcs = Cotp.StripDataTpdu(tpdu);
        if (mcs[0] != 0x3E) throw new FormatException("Expected Channel Join Confirm (0x3e).");
        // 3e, result(1), initiator(2), requested(2), channelId(2) → granted id at offset 6.
        return BinaryPrimitives.ReadUInt16BigEndian(mcs.Slice(6, 2));
    }

    /// <summary>Wraps an RDP payload in an MCS Send Data Request (client → server) on a channel.</summary>
    public static byte[] SendDataRequest(ushort userId, ushort channelId, ReadOnlySpan<byte> payload)
    {
        var w = new ByteWriter();
        w.WriteUInt8(0x64);
        w.WriteUInt16BE((ushort)(userId - 1001));
        w.WriteUInt16BE(channelId);
        w.WriteUInt8(0x70);
        Asn1.WritePerLength(w, payload.Length);
        w.WriteBytes(payload);
        return Cotp.BuildDataTpdu(w.AsSpan());
    }

    /// <summary>Minimal Client Info PDU (empty auto-logon fields) on the I/O channel.</summary>
    public static byte[] BuildClientInfo(ushort userId)
    {
        var info = new ByteWriter();
        info.WriteUInt16LE(0x0040); // Security Header flags = SEC_INFO_PKT
        info.WriteUInt16LE(0x0000);
        info.WriteUInt32LE(0);      // CodePage
        info.WriteUInt32LE(0);      // flags
        for (int i = 0; i < 5; i++) info.WriteUInt16LE(0); // cbDomain..cbWorkingDir
        for (int i = 0; i < 5; i++) info.WriteUInt16LE(0); // empty (null) strings
        return SendDataRequest(userId, Gcc.IoChannelId, info.AsSpan());
    }

    /// <summary>Minimal Confirm Active PDU (no capability sets — the mock does not parse them).</summary>
    public static byte[] BuildConfirmActive(ushort userId, uint shareId)
    {
        ReadOnlySpan<byte> source = "RDP\0"u8;
        var body = new ByteWriter();
        body.WriteUInt32LE(shareId);
        body.WriteUInt16LE(Gcc.ServerChannelId); // originatorId
        body.WriteUInt16LE((ushort)source.Length);
        body.WriteUInt16LE(4);                   // lengthCombinedCapabilities (numCaps + pad)
        body.WriteBytes(source);
        body.WriteUInt16LE(0);                   // numberCapabilities
        body.WriteUInt16LE(0);                   // pad
        var pdu = ShareControl.Wrap(ShareControl.ConfirmActive, userId, body.AsSpan());
        return SendDataRequest(userId, Gcc.IoChannelId, pdu);
    }

    /// <summary>Font List Data PDU — the finalization trigger that makes the server respond.</summary>
    public static byte[] BuildFontList(ushort userId)
    {
        var d = new ByteWriter();
        d.WriteUInt16LE(0);       // numberFonts
        d.WriteUInt16LE(0);       // totalNumFonts
        d.WriteUInt16LE(0x0003);  // listFlags = FIRST | LAST
        d.WriteUInt16LE(50);      // entrySize
        var pdu = Finalization.BuildDataPdu(Finalization.Pdu2FontList, d.AsSpan());
        return SendDataRequest(userId, Gcc.IoChannelId, pdu);
    }

    /// <summary>Completes licensing, capability exchange and finalization for an already-joined client.</summary>
    public static async Task ActivateAsync(RdpTestClient client, ushort user, CancellationToken ct)
    {
        await client.WriteRawAsync(BuildClientInfo(user), ct);
        await client.ReadTpktPayloadAsync(ct); // licensing
        await client.ReadTpktPayloadAsync(ct); // Demand Active
        await client.WriteRawAsync(BuildConfirmActive(user, Capabilities.ShareId), ct);
        await client.WriteRawAsync(BuildFontList(user), ct);
        for (int i = 0; i < 4; i++) await client.ReadTpktPayloadAsync(ct); // server finalization
    }

    /// <summary>Parses the first rectangle of a Bitmap Update PDU (share control payload).</summary>
    public static (int X, int Y, int Width, int Height, int Bpp, ushort FirstPixel) ParseFirstBitmap(ReadOnlySpan<byte> pdu)
    {
        static ushort U16(ReadOnlySpan<byte> b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(p, 2));
        // Layout after share control(6) + share data header(12) = offset 18:
        // updateType(18) numberRectangles(20) destLeft(22) destTop(24) destRight(26)
        // destBottom(28) width(30) height(32) bpp(34) flags(36) bitmapLength(38) pixels(40).
        return (U16(pdu, 22), U16(pdu, 24), U16(pdu, 30), U16(pdu, 32), U16(pdu, 34), U16(pdu, 40));
    }

    /// <summary>Drives a client through negotiation, TLS, MCS connect and channel join; returns the user channel.</summary>
    public static async Task<ushort> NegotiateThroughChannelJoinAsync(
        RdpTestClient client, System.Net.IPEndPoint endpoint, string[] channels, CancellationToken ct)
    {
        await client.ConnectAsync(endpoint, ct);
        await client.SendConnectionRequestAsync(RdpNegProtocol.Ssl, ct: ct);
        await client.ReadConnectionConfirmAsync(ct);
        await client.UpgradeToTlsAsync(ct: ct);

        await client.WriteRawAsync(BuildConnectInitial(channels), ct);
        var (io, ids) = ParseConnectResponseNetwork(await client.ReadTpktPayloadAsync(ct));

        await client.WriteRawAsync(ErectDomainRequest(), ct);
        await client.WriteRawAsync(AttachUserRequest(), ct);
        ushort user = ParseAttachUserConfirm(await client.ReadTpktPayloadAsync(ct));

        ushort[] toJoin = [user, io, .. ids];
        foreach (var ch in toJoin)
        {
            await client.WriteRawAsync(ChannelJoinRequest(user, ch), ct);
            await client.ReadTpktPayloadAsync(ct);
        }
        return user;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }
}

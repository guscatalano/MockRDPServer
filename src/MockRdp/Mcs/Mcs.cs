using System.Buffers.Binary;
using MockRdp.Util;
using MockRdp.X224;

namespace MockRdp.Mcs;

/// <summary>MCS domain PDU kinds we care about, identified by the first (choice) byte.</summary>
public enum McsDomainPdu
{
    ErectDomainRequest,   // 0x04
    AttachUserRequest,    // 0x28
    ChannelJoinRequest,   // 0x38
    SendDataRequest,      // 0x64 — first appears with the Client Info PDU (M3)
    Other,
}

/// <summary>
/// MCS (T.125) PDUs as used by RDP: the BER-encoded Connect-Initial/Response and the
/// PER-encoded domain PDUs (Erect Domain, Attach User, Channel Join). See MS-RDPBCGR 2.2.1.3–2.2.1.8.
/// </summary>
public static class McsPdu
{
    // DomainParameters returned to the client (MS-RDPBCGR 4.1.4 values): 8 INTEGERs.
    private static readonly byte[] DomainParameters =
    [
        0x02, 0x01, 0x22,             // maxChannelIds = 34
        0x02, 0x01, 0x03,             // maxUserIds = 3
        0x02, 0x01, 0x00,             // maxTokenIds = 0
        0x02, 0x01, 0x01,             // numPriorities = 1
        0x02, 0x01, 0x00,             // minThroughput = 0
        0x02, 0x01, 0x01,             // maxHeight = 1
        0x02, 0x03, 0x00, 0xFF, 0xF8, // maxMCSPDUsize = 65528
        0x02, 0x01, 0x02,             // protocolVersion = 2
    ];

    /// <summary>Extracts the userData OCTET STRING (the GCC data) from an MCS Connect-Initial PDU.</summary>
    public static ReadOnlySpan<byte> ReadConnectInitialUserData(ReadOnlySpan<byte> mcs)
    {
        if (mcs.Length < 2 || mcs[0] != 0x7F || mcs[1] != 0x65)
            throw new FormatException("Expected MCS Connect-Initial (tag 7f 65).");
        int pos = 2;
        int outerLen = Asn1.ReadBerLength(mcs, ref pos);
        int end = Math.Min(pos + outerLen, mcs.Length);

        ReadOnlySpan<byte> userData = default;
        while (pos < end)
        {
            byte tag = mcs[pos++];
            int len = Asn1.ReadBerLength(mcs, ref pos);
            if (pos + len > mcs.Length) break;
            if (tag == 0x04) userData = mcs.Slice(pos, len); // last OCTET STRING = userData
            pos += len;
        }
        return userData;
    }

    /// <summary>Builds the TPKT-framed MCS Connect-Response with an embedded GCC Conference Create Response.</summary>
    public static byte[] BuildConnectResponse(int channelCount, uint selectedProtocol)
    {
        byte[] gcc = Gcc.BuildConferenceCreateResponse(channelCount, selectedProtocol);

        var content = new ByteWriter();
        content.WriteBytes([0x0A, 0x01, 0x00]); // result = rt-successful (ENUMERATED 0)
        content.WriteBytes([0x02, 0x01, 0x00]); // calledConnectId = 0
        content.WriteUInt8(0x30);               // domainParameters SEQUENCE
        Asn1.WriteBerLength(content, DomainParameters.Length);
        content.WriteBytes(DomainParameters);
        content.WriteUInt8(0x04);               // userData OCTET STRING
        Asn1.WriteBerLength(content, gcc.Length);
        content.WriteBytes(gcc);

        var pdu = new ByteWriter();
        pdu.WriteUInt8(0x7F);                    // [APPLICATION 102] Connect-Response
        pdu.WriteUInt8(0x66);
        Asn1.WriteBerLength(pdu, content.Length);
        pdu.WriteBytes(content.AsSpan());

        return Cotp.BuildDataTpdu(pdu.AsSpan());
    }

    /// <summary>Classifies an MCS domain PDU by its leading choice byte.</summary>
    public static McsDomainPdu ClassifyDomainPdu(ReadOnlySpan<byte> mcs) => mcs.Length == 0 ? McsDomainPdu.Other : mcs[0] switch
    {
        0x04 => McsDomainPdu.ErectDomainRequest,
        0x28 => McsDomainPdu.AttachUserRequest,
        0x38 => McsDomainPdu.ChannelJoinRequest,
        0x64 => McsDomainPdu.SendDataRequest,
        _ => McsDomainPdu.Other,
    };

    /// <summary>Attach User Confirm granting <paramref name="userChannelId"/> to the client.</summary>
    public static byte[] BuildAttachUserConfirm(ushort userChannelId)
    {
        // 0x2E = attachUserConfirm choice with initiator present; result 0; initiator = UserId
        // (PER constrained INTEGER 1001.., encoded as value-1001 in 2 octets).
        var w = new ByteWriter();
        w.WriteUInt8(0x2E);
        w.WriteUInt8(0x00);
        w.WriteUInt16BE((ushort)(userChannelId - 1001));
        return Cotp.BuildDataTpdu(w.AsSpan());
    }

    /// <summary>Parses a Channel Join Request into (initiator, requested channel).</summary>
    public static (ushort Initiator, ushort ChannelId) ParseChannelJoinRequest(ReadOnlySpan<byte> mcs)
    {
        ushort initiator = BinaryPrimitives.ReadUInt16BigEndian(mcs.Slice(1, 2));
        ushort channelId = BinaryPrimitives.ReadUInt16BigEndian(mcs.Slice(3, 2));
        return (initiator, channelId);
    }

    /// <summary>Channel Join Confirm granting the requested channel.</summary>
    public static byte[] BuildChannelJoinConfirm(ushort initiator, ushort channelId)
    {
        // 0x3E = channelJoinConfirm choice with channelId present; result 0; then initiator,
        // requested, and granted channelId (ChannelId = raw 2-octet PER integer).
        var w = new ByteWriter();
        w.WriteUInt8(0x3E);
        w.WriteUInt8(0x00);
        w.WriteUInt16BE(initiator);
        w.WriteUInt16BE(channelId); // requested
        w.WriteUInt16BE(channelId); // channelId (granted)
        return Cotp.BuildDataTpdu(w.AsSpan());
    }
}

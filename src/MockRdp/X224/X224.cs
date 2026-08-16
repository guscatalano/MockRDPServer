using System.Buffers.Binary;
using System.Text;
using MockRdp.Framing;
using MockRdp.Util;

namespace MockRdp.X224;

/// <summary>Security protocols the client may request / the server may select (MS-RDPBCGR 2.2.1.1.1).</summary>
[Flags]
public enum RdpNegProtocol : uint
{
    Rdp = 0x00000000,       // standard RDP security
    Ssl = 0x00000001,       // TLS 1.x
    Hybrid = 0x00000002,    // CredSSP (NLA)
    RdsTls = 0x00000004,
    HybridEx = 0x00000008,
    RdsAad = 0x00000010,
}

/// <summary>Failure codes for the RDP Negotiation Failure PDU (MS-RDPBCGR 2.2.1.2.2).</summary>
public enum RdpNegFailureCode : uint
{
    SslRequiredByServer = 0x00000001,
    SslNotAllowedByServer = 0x00000002,
    SslCertNotOnServer = 0x00000003,
    InconsistentFlags = 0x00000004,
    HybridRequiredByServer = 0x00000005,
    SslWithUserAuthRequiredByServer = 0x00000006,
}

/// <summary>Parsed X.224 Connection Request (MS-RDPBCGR 2.2.1.1).</summary>
public sealed record X224ConnectionRequest(
    string? Cookie,
    bool HasNegReq,
    RdpNegProtocol RequestedProtocols,
    byte NegReqFlags);

/// <summary>
/// X.224 (ISO 8073 / COTP class 0) connection PDUs as used by RDP, plus the
/// RDP negotiation structures carried inside them (MS-RDPBCGR 2.2.1.1 / 2.2.1.2).
/// </summary>
public static class Cotp
{
    private const byte TpduConnectionRequest = 0xE0; // CR, low nibble = credit (0)
    private const byte TpduConnectionConfirm = 0xD0; // CC
    private const byte TypeRdpNegReq = 0x01;
    private const byte TypeRdpNegRsp = 0x02;
    private const byte TypeRdpNegFailure = 0x03;
    private const byte TypeRdpCorrelationInfo = 0x06;
    private const ushort NegPayloadLength = 8; // all three neg structures are 8 bytes

    /// <summary>Parses an X.224 CR TPDU (the bytes after the TPKT header).</summary>
    public static X224ConnectionRequest ParseConnectionRequest(ReadOnlySpan<byte> tpdu)
    {
        if (tpdu.Length < 7)
            throw new FormatException("X.224 CR TPDU too short.");

        int li = tpdu[0];
        byte code = tpdu[1];
        if ((code & 0xF0) != TpduConnectionRequest)
            throw new FormatException($"Expected CR TPDU, got code 0x{code:X2}.");

        // Fixed part after LI: code(1) dst-ref(2) src-ref(2) class(1) = 6 bytes.
        int tpduEnd = Math.Min(li + 1, tpdu.Length);
        if (tpduEnd < 7) throw new FormatException("X.224 CR TPDU length indicator too small.");
        var variable = tpdu[7..tpduEnd];

        string? cookie = null;
        int pos = 0;

        // Optional routing token / cookie: an ASCII string terminated by CR LF.
        // Distinguished from the binary neg structures, which start with a known type byte.
        if (variable.Length > 0 && variable[0] != TypeRdpNegReq && variable[0] != TypeRdpCorrelationInfo)
        {
            int crlf = IndexOfCrLf(variable);
            if (crlf >= 0)
            {
                cookie = Encoding.ASCII.GetString(variable[..crlf]);
                pos = crlf + 2;
            }
        }

        bool hasNeg = false;
        var protocols = RdpNegProtocol.Rdp;
        byte negFlags = 0;
        if (variable.Length - pos >= NegPayloadLength && variable[pos] == TypeRdpNegReq)
        {
            hasNeg = true;
            negFlags = variable[pos + 1];
            protocols = (RdpNegProtocol)BinaryPrimitives.ReadUInt32LittleEndian(variable.Slice(pos + 4, 4));
        }

        return new X224ConnectionRequest(cookie, hasNeg, protocols, negFlags);
    }

    /// <summary>Builds a TPKT-framed CC TPDU carrying an RDP Negotiation Response.</summary>
    public static byte[] BuildConnectionConfirm(RdpNegProtocol selectedProtocol, byte negRspFlags = 0)
    {
        var w = new ByteWriter();
        WriteCcHeader(w);
        w.WriteUInt8(TypeRdpNegRsp);
        w.WriteUInt8(negRspFlags);
        w.WriteUInt16LE(NegPayloadLength);
        w.WriteUInt32LE((uint)selectedProtocol);
        return Tpkt.Wrap(w.AsSpan());
    }

    /// <summary>Builds a TPKT-framed CC TPDU carrying an RDP Negotiation Failure.</summary>
    public static byte[] BuildConnectionConfirmFailure(RdpNegFailureCode failureCode)
    {
        var w = new ByteWriter();
        WriteCcHeader(w);
        w.WriteUInt8(TypeRdpNegFailure);
        w.WriteUInt8(0);
        w.WriteUInt16LE(NegPayloadLength);
        w.WriteUInt32LE((uint)failureCode);
        return Tpkt.Wrap(w.AsSpan());
    }

    private static void WriteCcHeader(ByteWriter w)
    {
        // LI counts everything after itself: fixed 6 bytes + 8-byte neg structure = 14.
        w.WriteUInt8(6 + NegPayloadLength);
        w.WriteUInt8(TpduConnectionConfirm);
        w.WriteUInt16BE(0); // dst-ref
        w.WriteUInt16BE(0); // src-ref
        w.WriteUInt8(0);    // class option
    }

    /// <summary>Wraps a payload in an X.224 Data TPDU (02 f0 80), the framing used for all
    /// post-connection PDUs (MCS, security, capabilities, …).</summary>
    public static byte[] BuildDataTpdu(ReadOnlySpan<byte> payload)
    {
        var packet = new byte[3 + payload.Length];
        packet[0] = 0x02; // LI
        packet[1] = 0xF0; // DT TPDU code
        packet[2] = 0x80; // EOT
        payload.CopyTo(packet.AsSpan(3));
        return Tpkt.Wrap(packet);
    }

    /// <summary>Strips the 3-byte X.224 Data TPDU header, returning the inner payload.</summary>
    public static ReadOnlySpan<byte> StripDataTpdu(ReadOnlySpan<byte> tpdu)
    {
        if (tpdu.Length < 3 || tpdu[1] != 0xF0)
            throw new FormatException("Expected an X.224 Data TPDU (code 0xF0).");
        return tpdu[3..];
    }

    private static int IndexOfCrLf(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 1 < data.Length; i++)
            if (data[i] == 0x0D && data[i + 1] == 0x0A)
                return i;
        return -1;
    }
}

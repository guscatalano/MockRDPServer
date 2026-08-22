using MockRdp.Util;

namespace MockRdp.Rdp;

/// <summary>
/// RDP licensing (MS-RDPELE). The mock issues a "valid client" license error immediately,
/// which tells the client no licensing exchange is required, and it proceeds to capabilities.
/// </summary>
public static class Licensing
{
    // Security Header flag marking a licensing PDU.
    private const ushort SecLicensePkt = 0x0080;

    // LICENSE_ERROR_MESSAGE fields.
    private const uint StatusValidClient = 0x00000007;
    private const uint StNoTransition = 0x00000002;
    private const ushort BbErrorBlob = 0x0004;

    /// <summary>Builds the RDP payload (security header + licensing PDU) for a valid-client license error.</summary>
    public static byte[] BuildValidClient()
    {
        var w = new ByteWriter();

        // Basic Security Header (present on licensing PDUs even under TLS).
        w.WriteUInt16LE(SecLicensePkt);
        w.WriteUInt16LE(0x0000); // flagsHi

        // Licensing preamble: bMsgType = ERROR_ALERT (0xFF), version 3.0, wMsgSize.
        w.WriteUInt8(0xFF);
        w.WriteUInt8(0x03);
        w.WriteUInt16LE(16); // preamble (4) + LICENSE_ERROR_MESSAGE (12)

        // LICENSE_ERROR_MESSAGE.
        w.WriteUInt32LE(StatusValidClient);
        w.WriteUInt32LE(StNoTransition);
        w.WriteUInt16LE(BbErrorBlob);
        w.WriteUInt16LE(0x0000); // empty blob

        return w.ToArray();
    }
}

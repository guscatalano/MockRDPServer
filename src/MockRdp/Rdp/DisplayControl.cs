using System.Buffers.Binary;
using MockRdp.Util;

namespace MockRdp.Rdp;

/// <summary>
/// The Display Control Virtual Channel (MS-RDPEDISP): a dynamic virtual channel named
/// <c>Microsoft::Windows::RDS::DisplayControl</c> over which the client asks the server to change
/// the session resolution. The server sends a CAPS PDU when the channel opens; the client then
/// sends a MONITOR_LAYOUT PDU (e.g. when its window is resized) and the server responds with a
/// Deactivation-Reactivation Sequence at the new size.
/// </summary>
public static class DisplayControl
{
    public const string ChannelName = "Microsoft::Windows::RDS::DisplayControl";

    private const uint PduTypeMonitorLayout = 0x00000002;  // DISPLAYCONTROL_PDU_TYPE_MONITOR_LAYOUT
    private const uint PduTypeCaps = 0x00000005;           // DISPLAYCONTROL_PDU_TYPE_CAPS
    private const uint MonitorPrimary = 0x00000001;        // DISPLAYCONTROL_MONITOR_PRIMARY

    // MS-RDPEDISP resolution constraints (2.2.2.2.1): even, and within these bounds.
    public const int MinDimension = 200;
    public const int MaxDimension = 8192;

    /// <summary>DISPLAYCONTROL_CAPS_PDU (2.2.2.1): advertises how many monitors / how large an area
    /// the server will accept. Sent as the channel's first message.</summary>
    public static byte[] BuildCapsPdu(uint maxNumMonitors = 1, uint maxAreaFactorA = 8192, uint maxAreaFactorB = 8192)
    {
        var w = new ByteWriter();
        w.WriteUInt32LE(PduTypeCaps);      // Header.Type
        w.WriteUInt32LE(20);               // Header.Length (8-byte header + 12 bytes)
        w.WriteUInt32LE(maxNumMonitors);
        w.WriteUInt32LE(maxAreaFactorA);
        w.WriteUInt32LE(maxAreaFactorB);
        return w.ToArray();
    }

    /// <summary>Parses a DISPLAYCONTROL_MONITOR_LAYOUT_PDU (2.2.2.2) and returns the primary monitor's
    /// requested (Width, Height), clamped and made even per the spec, or null if it is not that PDU.</summary>
    public static (int Width, int Height)? ParseMonitorLayout(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 16) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(pdu) != PduTypeMonitorLayout) return null;

        uint layoutSize = BinaryPrimitives.ReadUInt32LittleEndian(pdu.Slice(8, 4));   // bytes per monitor entry
        uint num = BinaryPrimitives.ReadUInt32LittleEndian(pdu.Slice(12, 4));
        if (layoutSize < 40 || num == 0) return null;

        (int W, int H)? first = null;
        int off = 16;
        for (uint i = 0; i < num && off + 40 <= pdu.Length; i++, off += (int)layoutSize)
        {
            var m = pdu.Slice(off, 40);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(0, 4));
            int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(12, 4));   // DISPLAYCONTROL_MONITOR_LAYOUT.Width
            int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(m.Slice(16, 4));  // .Height
            var size = (Clamp(width), Clamp(height));
            first ??= size;
            if ((flags & MonitorPrimary) != 0) return size;
        }
        return first;
    }

    private static int Clamp(int v)
    {
        v = Math.Clamp(v, MinDimension, MaxDimension);
        return v & ~1;   // widths/heights must be even
    }
}

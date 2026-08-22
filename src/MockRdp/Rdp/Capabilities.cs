using MockRdp.Mcs;
using MockRdp.Util;

namespace MockRdp.Rdp;

/// <summary>
/// Builds the server's Demand Active PDU and its capability sets (MS-RDPBCGR 2.2.7 / 2.2.1.13.1).
/// The set is deliberately small: enough for a client to accept the share and start a session
/// that receives bitmap updates (no drawing orders — the client falls back to bitmaps).
/// </summary>
public static class Capabilities
{
    public const uint ShareId = 0x000103EA;
    public const int DesktopWidth = 1024;
    public const int DesktopHeight = 768;
    public const int BitsPerPixel = 16;

    // Capability set type identifiers.
    private const ushort CapGeneral = 1;
    private const ushort CapBitmap = 2;
    private const ushort CapOrder = 3;
    private const ushort CapPointer = 8;
    private const ushort CapShare = 9;
    private const ushort CapColorCache = 10;
    private const ushort CapInput = 13;
    private const ushort CapFont = 14;
    private const ushort CapVirtualChannel = 20;

    /// <summary>Builds the RDP payload for a Demand Active PDU (to be wrapped in an MCS Send Data Indication).</summary>
    public static byte[] BuildDemandActive()
    {
        var caps = new ByteWriter();
        int count = 0;
        WriteGeneral(caps); count++;
        WriteBitmap(caps); count++;
        WriteOrder(caps); count++;
        WritePointer(caps); count++;
        WriteInput(caps); count++;
        WriteVirtualChannel(caps); count++;
        WriteShare(caps); count++;
        WriteColorCache(caps); count++;
        WriteFont(caps); count++;

        ReadOnlySpan<byte> source = "RDP\0"u8;
        var body = new ByteWriter();
        body.WriteUInt32LE(ShareId);
        body.WriteUInt16LE((ushort)source.Length);          // lengthSourceDescriptor
        body.WriteUInt16LE((ushort)(4 + caps.Length));      // lengthCombinedCapabilities
        body.WriteBytes(source);
        body.WriteUInt16LE((ushort)count);                  // numberCapabilities
        body.WriteUInt16LE(0);                              // pad2octets
        body.WriteBytes(caps.AsSpan());
        body.WriteUInt32LE(0);                              // sessionId

        return ShareControl.Wrap(ShareControl.DemandActive, Gcc.ServerChannelId, body.AsSpan());
    }

    private static void WriteCap(ByteWriter w, ushort type, ReadOnlySpan<byte> data)
    {
        w.WriteUInt16LE(type);
        w.WriteUInt16LE((ushort)(4 + data.Length));
        w.WriteBytes(data);
    }

    private static void WriteGeneral(ByteWriter caps)
    {
        var d = new ByteWriter();
        d.WriteUInt16LE(1);       // osMajorType = WINDOWS
        d.WriteUInt16LE(3);       // osMinorType = WINDOWS_NT
        d.WriteUInt16LE(0x0200);  // protocolVersion
        d.WriteUInt16LE(0);       // pad
        d.WriteUInt16LE(0);       // generalCompressionTypes
        d.WriteUInt16LE(0x0401);  // extraFlags: FASTPATH_OUTPUT | NO_BITMAP_COMPRESSION_HDR
        d.WriteUInt16LE(0);       // updateCapabilityFlag
        d.WriteUInt16LE(0);       // remoteUnshareFlag
        d.WriteUInt16LE(0);       // generalCompressionLevel
        d.WriteUInt8(0);          // refreshRectSupport
        d.WriteUInt8(0);          // suppressOutputSupport
        WriteCap(caps, CapGeneral, d.AsSpan());
    }

    private static void WriteBitmap(ByteWriter caps)
    {
        var d = new ByteWriter();
        d.WriteUInt16LE(BitsPerPixel);   // preferredBitsPerPixel
        d.WriteUInt16LE(1);              // receive1BitPerPixel
        d.WriteUInt16LE(1);              // receive4BitsPerPixel
        d.WriteUInt16LE(1);              // receive8BitsPerPixel
        d.WriteUInt16LE(DesktopWidth);
        d.WriteUInt16LE(DesktopHeight);
        d.WriteUInt16LE(0);              // pad
        d.WriteUInt16LE(1);              // desktopResizeFlag
        d.WriteUInt16LE(1);              // bitmapCompressionFlag (MUST be 1)
        d.WriteUInt8(0);                 // highColorFlags
        d.WriteUInt8(0);                 // drawingFlags
        d.WriteUInt16LE(1);              // multipleRectangleSupport (MUST be 1)
        d.WriteUInt16LE(0);              // pad
        WriteCap(caps, CapBitmap, d.AsSpan());
    }

    private static void WriteOrder(ByteWriter caps)
    {
        var d = new ByteWriter();
        d.WriteBytes(new byte[16]);      // terminalDescriptor
        d.WriteUInt32LE(0);              // pad4octetsA
        d.WriteUInt16LE(1);              // desktopSaveXGranularity
        d.WriteUInt16LE(20);             // desktopSaveYGranularity
        d.WriteUInt16LE(0);              // pad
        d.WriteUInt16LE(1);              // maximumOrderLevel
        d.WriteUInt16LE(0);              // numberFonts
        d.WriteUInt16LE(0x0022);         // orderFlags: NEGOTIATEORDERSUPPORT | SOLIDPATTERNBRUSHONLY
        d.WriteBytes(new byte[32]);      // orderSupport (all unsupported → client uses bitmap updates)
        d.WriteUInt16LE(0);              // textFlags
        d.WriteUInt16LE(0);              // orderSupportExFlags
        d.WriteUInt32LE(0);              // pad4octetsB
        d.WriteUInt32LE(0x00038400);     // desktopSaveSize
        d.WriteUInt16LE(0);              // pad
        d.WriteUInt16LE(0);              // pad
        d.WriteUInt16LE(0);              // textANSICodePage
        d.WriteUInt16LE(0);              // pad
        WriteCap(caps, CapOrder, d.AsSpan());
    }

    private static void WritePointer(ByteWriter caps)
    {
        var d = new ByteWriter();
        d.WriteUInt16LE(1);   // colorPointerFlag
        d.WriteUInt16LE(20);  // colorPointerCacheSize
        d.WriteUInt16LE(20);  // pointerCacheSize
        WriteCap(caps, CapPointer, d.AsSpan());
    }

    private static void WriteInput(ByteWriter caps)
    {
        var d = new ByteWriter();
        d.WriteUInt16LE(0x000D);      // inputFlags: SCANCODES | MOUSEX | FASTPATH_INPUT
        d.WriteUInt16LE(0);           // pad
        d.WriteUInt32LE(0x00000409);  // keyboardLayout (US)
        d.WriteUInt32LE(4);           // keyboardType
        d.WriteUInt32LE(0);           // keyboardSubType
        d.WriteUInt32LE(12);          // keyboardFunctionKey
        d.WriteBytes(new byte[64]);   // imeFileName
        WriteCap(caps, CapInput, d.AsSpan());
    }

    private static void WriteVirtualChannel(ByteWriter caps)
    {
        var d = new ByteWriter();
        d.WriteUInt32LE(0);      // flags: VCCAPS_NO_COMPR
        d.WriteUInt32LE(1600);   // VCChunkSize
        WriteCap(caps, CapVirtualChannel, d.AsSpan());
    }

    private static void WriteShare(ByteWriter caps)
    {
        var d = new ByteWriter();
        d.WriteUInt16LE(Gcc.ServerChannelId); // nodeId
        d.WriteUInt16LE(0);                   // pad
        WriteCap(caps, CapShare, d.AsSpan());
    }

    private static void WriteColorCache(ByteWriter caps)
    {
        var d = new ByteWriter();
        d.WriteUInt16LE(6);  // colorTableCacheSize
        d.WriteUInt16LE(0);  // pad
        WriteCap(caps, CapColorCache, d.AsSpan());
    }

    private static void WriteFont(ByteWriter caps)
    {
        var d = new ByteWriter();
        d.WriteUInt16LE(0x0001); // fontSupportFlags: FONTSUPPORT_FONTLIST
        d.WriteUInt16LE(0);      // pad
        WriteCap(caps, CapFont, d.AsSpan());
    }
}

using System.Buffers.Binary;
using System.Text;
using MockRdp.Util;

namespace MockRdp.Rdp;

/// <summary>
/// Clipboard virtual channel extension (MS-RDPECLIP), carried over the static "cliprdr" channel.
/// The mock advertises capabilities, signals monitor-ready, acknowledges the client's format
/// lists, offers a fixed Unicode-text format, and serves that text on request.
/// </summary>
public static class Clipboard
{
    // CLIPRDR_HEADER msgType values.
    public const ushort CbMonitorReady = 0x0001;
    public const ushort CbFormatList = 0x0002;
    public const ushort CbFormatListResponse = 0x0003;
    public const ushort CbFormatDataRequest = 0x0004;
    public const ushort CbFormatDataResponse = 0x0005;
    public const ushort CbClipCaps = 0x0007;

    private const ushort CbResponseOk = 0x0001;
    private const uint CfUnicodeText = 13; // CF_UNICODETEXT

    /// <summary>The text the mock serves when the client requests clipboard data.</summary>
    public const string ServedText = "Hello from mock-rdp clipboard";

    private static byte[] Pdu(ushort msgType, ushort msgFlags, ReadOnlySpan<byte> data)
    {
        var w = new ByteWriter();
        w.WriteUInt16LE(msgType);
        w.WriteUInt16LE(msgFlags);
        w.WriteUInt32LE((uint)data.Length);
        w.WriteBytes(data);
        return w.ToArray();
    }

    /// <summary>Clipboard Capabilities PDU (one general set, short format names).</summary>
    public static byte[] ClipboardCapabilities()
    {
        var caps = new ByteWriter();
        caps.WriteUInt16LE(1);       // cCapabilitiesSets
        caps.WriteUInt16LE(0);       // pad
        caps.WriteUInt16LE(0x0001);  // CB_CAPSTYPE_GENERAL
        caps.WriteUInt16LE(12);      // lengthCapability
        caps.WriteUInt32LE(2);       // version = CB_CAPS_VERSION_2
        caps.WriteUInt32LE(0);       // generalFlags = 0 (short format names)
        return Pdu(CbClipCaps, 0, caps.AsSpan());
    }

    public static byte[] MonitorReady() => Pdu(CbMonitorReady, 0, default);

    public static byte[] FormatListResponseOk() => Pdu(CbFormatListResponse, CbResponseOk, default);

    /// <summary>Format List (short names) offering a single CF_UNICODETEXT format.</summary>
    public static byte[] FormatListUnicodeText()
    {
        var d = new ByteWriter();
        d.WriteUInt32LE(CfUnicodeText);
        d.WriteBytes(new byte[32]); // empty 32-byte format name
        return Pdu(CbFormatList, 0, d.AsSpan());
    }

    /// <summary>Format Data Response carrying the served text as null-terminated UTF-16.</summary>
    public static byte[] FormatDataResponseText(string text)
    {
        var d = new ByteWriter();
        d.WriteBytes(Encoding.Unicode.GetBytes(text));
        d.WriteUInt16LE(0); // null terminator
        return Pdu(CbFormatDataResponse, CbResponseOk, d.AsSpan());
    }

    /// <summary>Format Data Request PDU asking for a specific clipboard format.</summary>
    public static byte[] FormatDataRequest(uint formatId)
    {
        var d = new ByteWriter();
        d.WriteUInt32LE(formatId);
        return Pdu(CbFormatDataRequest, 0, d.AsSpan());
    }

    public static ushort ReadMsgType(ReadOnlySpan<byte> pdu) =>
        pdu.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(pdu[..2]) : (ushort)0;

    /// <summary>Decodes the text from a Format Data Response payload (UTF-16, trimming a trailing null).</summary>
    public static string ReadTextResponse(ReadOnlySpan<byte> pdu)
    {
        // CLIPRDR_HEADER is 8 bytes; the data follows.
        int dataLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(pdu.Slice(4, 4));
        var data = pdu.Slice(8, Math.Min(dataLen, pdu.Length - 8));
        string s = Encoding.Unicode.GetString(data);
        return s.TrimEnd('\0');
    }
}

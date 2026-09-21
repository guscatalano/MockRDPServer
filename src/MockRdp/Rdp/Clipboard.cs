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
    public const ushort CbFilecontentsRequest = 0x0008;
    public const ushort CbFilecontentsResponse = 0x0009;

    private const ushort CbResponseOk = 0x0001;
    private const uint CfUnicodeText = 13; // CF_UNICODETEXT

    // Capability generalFlags.
    private const uint CbUseLongFormatNames = 0x00000002;
    private const uint CbStreamFileclipEnabled = 0x00000004;

    // CLIPRDR_FILECONTENTS_REQUEST.dwFlags.
    private const uint FileContentsSize = 0x00000001;
    private const uint FileContentsRange = 0x00000002;

    // FILEDESCRIPTORW.flags and .fileAttributes.
    private const uint FdAttributes = 0x00000004;
    private const uint FdFilesize = 0x00000040;
    private const uint FileAttributeNormal = 0x00000080;

    /// <summary>The clipboard format id the mock assigns to the "FileGroupDescriptorW" format.</summary>
    public const uint FileGroupDescriptorId = 0x0000C001;

    /// <summary>The text the mock serves when the client requests clipboard data.</summary>
    public const string ServedText = "Hello from mock-rdp clipboard";

    /// <summary>The virtual file the mock offers on its clipboard (paste it into the client's Explorer).</summary>
    public const string ServedFileName = "mock-rdp.txt";
    public static readonly byte[] ServedFileBytes = Encoding.ASCII.GetBytes(
        "This file was copied from the mock RDP server's clipboard.\r\n\r\n" +
        "It demonstrates clipboard file transfer (MS-RDPECLIP FileContents):\r\n" +
        "the server offered FileGroupDescriptorW, and your RDP client fetched\r\n" +
        "the descriptor and these bytes when you pasted.\r\n");

    /// <summary>Result of parsing a Filecontents Request: which file, size-vs-range, and the range.</summary>
    public readonly record struct FileContentsReq(uint StreamId, uint Index, bool WantSize, ulong Position, uint Length);

    /// <summary>One file's essentials parsed from a FILEGROUPDESCRIPTORW (client → mock paste).</summary>
    public readonly record struct FileDescriptor(string Name, long Size);

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
        caps.WriteUInt32LE(CbUseLongFormatNames | CbStreamFileclipEnabled); // long names + file clipboard
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

    /// <summary>Format List (long names) offering both CF_UNICODETEXT and a FileGroupDescriptorW file.</summary>
    public static byte[] FormatListWithFile()
    {
        var d = new ByteWriter();
        d.WriteUInt32LE(CfUnicodeText);
        d.WriteUInt16LE(0);                                   // empty long format name (UTF-16 NUL)
        d.WriteUInt32LE(FileGroupDescriptorId);
        d.WriteBytes(Encoding.Unicode.GetBytes("FileGroupDescriptorW"));
        d.WriteUInt16LE(0);                                   // NUL terminator
        return Pdu(CbFormatList, 0, d.AsSpan());
    }

    /// <summary>Format Data Response carrying a FILEGROUPDESCRIPTORW for a single file.</summary>
    public static byte[] FormatDataResponseFileList(string fileName, long size)
    {
        var d = new ByteWriter();
        d.WriteUInt32LE(1);                                   // cItems
        // FILEDESCRIPTORW (592 bytes)
        d.WriteUInt32LE(FdAttributes | FdFilesize);          // flags
        d.WriteBytes(new byte[32]);                          // reserved1
        d.WriteUInt32LE(FileAttributeNormal);                // fileAttributes
        d.WriteBytes(new byte[16]);                          // reserved2
        d.WriteBytes(new byte[8]);                           // lastWriteTime
        d.WriteUInt32LE((uint)(size >> 32));                 // fileSizeHigh
        d.WriteUInt32LE((uint)(size & 0xFFFFFFFF));          // fileSizeLow
        var name = new byte[520];                            // fileName[260] WCHAR, NUL-terminated
        var nb = Encoding.Unicode.GetBytes(fileName);
        Array.Copy(nb, name, Math.Min(nb.Length, 518));
        d.WriteBytes(name);
        return Pdu(CbFormatDataResponse, CbResponseOk, d.AsSpan());
    }

    /// <summary>Filecontents Response carrying the requested bytes (or the 8-byte file size).</summary>
    public static byte[] FilecontentsResponse(uint streamId, ReadOnlySpan<byte> data)
    {
        var d = new ByteWriter();
        d.WriteUInt32LE(streamId);
        d.WriteBytes(data);
        return Pdu(CbFilecontentsResponse, CbResponseOk, d.AsSpan());
    }

    /// <summary>Reads the requested format id from a Format Data Request payload.</summary>
    public static uint ReadFormatDataRequestId(ReadOnlySpan<byte> pdu) =>
        pdu.Length >= 12 ? BinaryPrimitives.ReadUInt32LittleEndian(pdu.Slice(8, 4)) : 0;

    /// <summary>Parses a Filecontents Request PDU (streamId, file index, size-vs-range, position, length).</summary>
    public static FileContentsReq ReadFilecontentsRequest(ReadOnlySpan<byte> pdu)
    {
        var d = pdu[8..];   // past the 8-byte CLIPRDR_HEADER
        uint streamId = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(0, 4));
        uint lindex = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4, 4));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(8, 4));
        uint posLow = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(12, 4));
        uint posHigh = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(16, 4));
        uint cb = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(20, 4));
        return new FileContentsReq(streamId, lindex, (flags & FileContentsSize) != 0, ((ulong)posHigh << 32) | posLow, cb);
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

    // ── inbound (client → mock) file paste ────────────────────────────────────

    /// <summary>Filecontents <b>Range</b> Request: ask the client for <paramref name="length"/> bytes
    /// of file <paramref name="index"/> starting at <paramref name="position"/> (mock is the requester).</summary>
    public static byte[] FilecontentsRangeRequest(uint streamId, uint index, ulong position, uint length)
    {
        var d = new ByteWriter();
        d.WriteUInt32LE(streamId);
        d.WriteUInt32LE(index);
        d.WriteUInt32LE(FileContentsRange);
        d.WriteUInt32LE((uint)(position & 0xFFFFFFFF));
        d.WriteUInt32LE((uint)(position >> 32));
        d.WriteUInt32LE(length);
        return Pdu(CbFilecontentsRequest, 0, d.AsSpan());
    }

    /// <summary>Scans a client Format List (long names) for a "FileGroupDescriptorW" entry and returns
    /// the format id the client assigned it, or 0 if the client isn't offering files.</summary>
    public static uint ParseFormatListFileId(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 8) return 0;
        int dataLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(pdu.Slice(4, 4));
        int end = Math.Min(8 + dataLen, pdu.Length);
        int pos = 8;
        while (pos + 4 <= end)
        {
            uint formatId = BinaryPrimitives.ReadUInt32LittleEndian(pdu.Slice(pos, 4));
            pos += 4;
            // Long format name: UTF-16, NUL-terminated.
            int nameStart = pos;
            while (pos + 2 <= end && !(pdu[pos] == 0 && pdu[pos + 1] == 0)) pos += 2;
            var name = Encoding.Unicode.GetString(pdu.Slice(nameStart, pos - nameStart));
            pos += 2; // skip the UTF-16 NUL
            if (name.Equals("FileGroupDescriptorW", StringComparison.OrdinalIgnoreCase))
                return formatId;
        }
        return 0;
    }

    /// <summary>Parses the first file's name + size from a FILEGROUPDESCRIPTORW Format Data Response.</summary>
    public static FileDescriptor? ReadFileGroupDescriptor(ReadOnlySpan<byte> pdu)
    {
        // 8-byte header, then cItems(4), then FILEDESCRIPTORW[0] (592 bytes). Within the whole PDU:
        //   fileSizeHigh @ 76, fileSizeLow @ 80, fileName[260 WCHAR] @ 84.
        if (pdu.Length < 84 + 2) return null;
        uint high = BinaryPrimitives.ReadUInt32LittleEndian(pdu.Slice(76, 4));
        uint low = BinaryPrimitives.ReadUInt32LittleEndian(pdu.Slice(80, 4));
        long size = ((long)high << 32) | low;
        int nameBytes = Math.Min(520, pdu.Length - 84);
        var name = Encoding.Unicode.GetString(pdu.Slice(84, nameBytes)).TrimEnd('\0');
        // Take just the leaf name (descriptors may carry relative paths with backslashes).
        int slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        return new FileDescriptor(name, size);
    }

    /// <summary>Extracts the file bytes from a Filecontents Response (past the streamId).</summary>
    public static byte[] ReadFilecontentsResponseData(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length < 12) return Array.Empty<byte>();
        int dataLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(pdu.Slice(4, 4));
        int total = Math.Min(dataLen, pdu.Length - 8);   // streamId(4) + bytes
        return total <= 4 ? Array.Empty<byte>() : pdu.Slice(12, total - 4).ToArray();
    }
}

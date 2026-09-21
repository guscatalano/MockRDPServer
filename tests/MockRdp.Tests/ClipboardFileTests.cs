using System.Buffers.Binary;
using System.Text;
using MockRdp.Rdp;
using Xunit;

namespace MockRdp.Tests;

/// <summary>Clipboard file transfer (MS-RDPECLIP): the FileGroupDescriptorW offer, the file
/// descriptor response, and the Filecontents request/response the client uses to fetch bytes.</summary>
public class ClipboardFileTests
{
    [Fact]
    public void FormatList_OffersFileGroupDescriptor()
    {
        var pdu = Clipboard.FormatListWithFile();
        Assert.Equal(Clipboard.CbFormatList, Clipboard.ReadMsgType(pdu));
        var body = Encoding.Unicode.GetString(pdu, 8, pdu.Length - 8);
        Assert.Contains("FileGroupDescriptorW", body);
    }

    [Fact]
    public void FileDescriptorResponse_CarriesSizeAndName()
    {
        var pdu = Clipboard.FormatDataResponseFileList("mock-rdp.txt", 123);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(pdu.AsSpan(8, 4)));   // cItems
        Assert.Equal(123u, BinaryPrimitives.ReadUInt32LittleEndian(pdu.AsSpan(80, 4))); // fileSizeLow
        Assert.Equal("mock-rdp.txt", Encoding.Unicode.GetString(pdu, 84, "mock-rdp.txt".Length * 2));
    }

    [Fact]
    public void FilecontentsRequest_ParsesSizeAndRange()
    {
        var size = Clipboard.ReadFilecontentsRequest(FileContentsRequest(streamId: 5, wantSize: true, pos: 0, len: 0));
        Assert.Equal(5u, size.StreamId);
        Assert.True(size.WantSize);

        var range = Clipboard.ReadFilecontentsRequest(FileContentsRequest(streamId: 9, wantSize: false, pos: 128, len: 64));
        Assert.False(range.WantSize);
        Assert.Equal(128ul, range.Position);
        Assert.Equal(64u, range.Length);
    }

    [Fact]
    public void FilecontentsResponse_EchoesStreamAndData()
    {
        var resp = Clipboard.FilecontentsResponse(7, new byte[] { 1, 2, 3 });
        Assert.Equal(Clipboard.CbFilecontentsResponse, Clipboard.ReadMsgType(resp));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(resp.AsSpan(8, 4)));
        Assert.Equal(new byte[] { 1, 2, 3 }, resp.AsSpan(12, 3).ToArray());
    }

    // ── inbound (client → mock) paste path ────────────────────────────────────

    [Fact]
    public void FilecontentsRangeRequest_RoundTrips()
    {
        var pdu = Clipboard.FilecontentsRangeRequest(streamId: 1, index: 0, position: 128, length: 64);
        Assert.Equal(Clipboard.CbFilecontentsRequest, Clipboard.ReadMsgType(pdu));
        var req = Clipboard.ReadFilecontentsRequest(pdu);
        Assert.Equal(1u, req.StreamId);
        Assert.False(req.WantSize);          // it's a range request, not a size request
        Assert.Equal(128ul, req.Position);
        Assert.Equal(64u, req.Length);
    }

    [Fact]
    public void ParseFormatListFileId_FindsFileGroupDescriptor()
    {
        // A client-style long-name format list: a text format, then FileGroupDescriptorW at 0xC123.
        var w = new List<byte>();
        void U16(ushort v) => w.AddRange(BitConverter.GetBytes(v));
        void U32(uint v) => w.AddRange(BitConverter.GetBytes(v));

        var body = new List<byte>();
        void BU32(uint v) => body.AddRange(BitConverter.GetBytes(v));
        void BName(string s) { body.AddRange(Encoding.Unicode.GetBytes(s)); body.AddRange(new byte[] { 0, 0 }); }
        BU32(13); BName("");                    // CF_UNICODETEXT, empty name
        BU32(0xC123); BName("FileGroupDescriptorW");
        U16(Clipboard.CbFormatList); U16(0); U32((uint)body.Count);
        w.AddRange(body);

        Assert.Equal(0xC123u, Clipboard.ParseFormatListFileId(w.ToArray()));
    }

    [Fact]
    public void ParseFormatListFileId_ZeroWhenNoFile()
    {
        Assert.Equal(0u, Clipboard.ParseFormatListFileId(Clipboard.FormatListUnicodeText()));
    }

    [Fact]
    public void ReadFileGroupDescriptor_RoundTripsNameAndSize()
    {
        var pdu = Clipboard.FormatDataResponseFileList("paste me.txt", 4096);
        var fd = Clipboard.ReadFileGroupDescriptor(pdu);
        Assert.NotNull(fd);
        Assert.Equal("paste me.txt", fd!.Value.Name);
        Assert.Equal(4096, fd.Value.Size);
    }

    [Fact]
    public void ReadFilecontentsResponseData_ExtractsBytes()
    {
        var payload = new byte[] { 9, 8, 7, 6, 5 };
        var resp = Clipboard.FilecontentsResponse(3, payload);
        Assert.Equal(payload, Clipboard.ReadFilecontentsResponseData(resp));
    }

    private static byte[] FileContentsRequest(uint streamId, bool wantSize, ulong pos, uint len)
    {
        var w = new List<byte>();
        void U16(ushort v) => w.AddRange(BitConverter.GetBytes(v));
        void U32(uint v) => w.AddRange(BitConverter.GetBytes(v));
        U16(Clipboard.CbFilecontentsRequest); U16(0); U32(28);       // CLIPRDR_HEADER
        U32(streamId); U32(0); U32(wantSize ? 1u : 2u);             // streamId, lindex, dwFlags
        U32((uint)(pos & 0xFFFFFFFF)); U32((uint)(pos >> 32)); U32(len); U32(0);
        return w.ToArray();
    }
}

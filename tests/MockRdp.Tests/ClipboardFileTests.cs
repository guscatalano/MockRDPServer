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

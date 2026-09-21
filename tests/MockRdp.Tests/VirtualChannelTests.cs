using System.Buffers.Binary;
using MockRdp.Desktop;
using MockRdp.Rdp;
using Xunit;

namespace MockRdp.Tests;

/// <summary>Static virtual-channel chunking (large clipboard file transfers) and the on-the-fly
/// generated-file content used to demo transfer progress.</summary>
public class VirtualChannelTests
{
    private const uint First = 0x1, Last = 0x2;

    [Fact]
    public void WrapChunked_SmallPayload_IsSingleFirstLastChunk()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var chunks = VirtualChannel.WrapChunked(data).ToList();
        Assert.Single(chunks);
        Assert.Equal(First | Last, BinaryPrimitives.ReadUInt32LittleEndian(chunks[0].AsSpan(4, 4)));
        Assert.Equal(data, chunks[0].AsSpan(8).ToArray());
    }

    [Fact]
    public void WrapChunked_LargePayload_SplitsAndReassembles()
    {
        var data = new byte[4000];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 31 + 7);

        var chunks = VirtualChannel.WrapChunked(data, chunkSize: 1590).ToList();
        Assert.Equal(3, chunks.Count);

        // Flags: FIRST on the first only, LAST on the last only; total length in every header.
        Assert.Equal(First, BinaryPrimitives.ReadUInt32LittleEndian(chunks[0].AsSpan(4, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(chunks[1].AsSpan(4, 4)));
        Assert.Equal(Last, BinaryPrimitives.ReadUInt32LittleEndian(chunks[^1].AsSpan(4, 4)));
        foreach (var c in chunks)
            Assert.Equal((uint)data.Length, BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(0, 4)));

        var reassembled = chunks.SelectMany(c => c.Skip(8)).ToArray();
        Assert.Equal(data, reassembled);
    }

    [Fact]
    public void FillGenerated_IsDeterministicAcrossRanges()
    {
        var whole = new byte[300];
        Vfs.FillGenerated(whole, 0);

        var slice = new byte[100];
        Vfs.FillGenerated(slice, 150);          // same bytes whether generated whole or in a range
        Assert.Equal(whole.AsSpan(150, 100).ToArray(), slice);
    }
}

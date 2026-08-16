using MockRdp.Framing;
using Xunit;

namespace MockRdp.Tests;

public class FramingTests
{
    [Fact]
    public void Wrap_PrependsCorrectHeader()
    {
        var packet = Tpkt.Wrap([0xAA, 0xBB, 0xCC]);
        Assert.Equal(new byte[] { 0x03, 0x00, 0x00, 0x07, 0xAA, 0xBB, 0xCC }, packet);
    }

    [Fact]
    public void ReadLength_RoundTripsWithWrap()
    {
        var payload = new byte[500];
        var packet = Tpkt.Wrap(payload);
        Assert.Equal(504, Tpkt.ReadLength(packet.AsSpan(0, 4)));
    }

    [Fact]
    public void ReadLength_RejectsNonTpkt()
    {
        Assert.Throws<FormatException>(() => Tpkt.ReadLength([0x04, 0x00, 0x00, 0x05]));
    }
}

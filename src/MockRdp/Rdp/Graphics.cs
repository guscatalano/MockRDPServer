using MockRdp.Mcs;
using MockRdp.Util;

namespace MockRdp.Rdp;

/// <summary>
/// Bitmap graphics output (MS-RDPBCGR 2.2.9.1.1.3.1.2). The mock sends uncompressed 16bpp
/// (RGB565) Bitmap Update PDUs — the simplest output path every RDP client accepts.
///
/// Note on size: each PDU rides in one MCS Send Data Indication whose PER length is at most
/// 16383 bytes, so one 64x64 tile (8192 bytes of pixels) per PDU stays safely under the limit.
/// </summary>
public static class Graphics
{
    private const ushort UpdateTypeBitmap = 0x0001;
    public const int TileSize = 64;

    /// <summary>A solid-colour square to draw at (x, y).</summary>
    public readonly record struct Square(int X, int Y, int Size, ushort Color);

    /// <summary>Packs 8-bit RGB into a 16-bit RGB565 pixel.</summary>
    public static ushort Rgb565(byte r, byte g, byte b) =>
        (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    /// <summary>Builds a Bitmap Update PDU payload drawing one solid-colour square.</summary>
    public static byte[] BuildSolidSquare(Square square)
    {
        int pixels = square.Size * square.Size;
        var bitmap = new ByteWriter(pixels * 2);
        for (int i = 0; i < pixels; i++)
            bitmap.WriteUInt16LE(square.Color); // solid colour → row order is irrelevant

        var upd = new ByteWriter();
        upd.WriteUInt16LE(UpdateTypeBitmap);
        upd.WriteUInt16LE(1); // numberRectangles
        upd.WriteUInt16LE((ushort)square.X);                       // destLeft
        upd.WriteUInt16LE((ushort)square.Y);                       // destTop
        upd.WriteUInt16LE((ushort)(square.X + square.Size - 1));   // destRight
        upd.WriteUInt16LE((ushort)(square.Y + square.Size - 1));   // destBottom
        upd.WriteUInt16LE((ushort)square.Size);                    // width
        upd.WriteUInt16LE((ushort)square.Size);                    // height
        upd.WriteUInt16LE(16);                                     // bitsPerPixel
        upd.WriteUInt16LE(0);                                      // flags = uncompressed
        upd.WriteUInt16LE((ushort)(pixels * 2));                   // bitmapLength
        upd.WriteBytes(bitmap.AsSpan());

        return ShareControl.BuildDataPdu(Capabilities.ShareId, Gcc.ServerChannelId,
            Finalization.Pdu2Update, upd.AsSpan());
    }

    /// <summary>A deterministic row of eight colour squares, used as the startup test pattern.</summary>
    public static IReadOnlyList<Square> TestPattern()
    {
        ushort[] colors =
        [
            Rgb565(255, 0, 0),     // red
            Rgb565(0, 255, 0),     // green
            Rgb565(0, 0, 255),     // blue
            Rgb565(255, 255, 0),   // yellow
            Rgb565(0, 255, 255),   // cyan
            Rgb565(255, 0, 255),   // magenta
            Rgb565(255, 255, 255), // white
            Rgb565(128, 128, 128), // gray
        ];
        var squares = new Square[colors.Length];
        for (int i = 0; i < colors.Length; i++)
            squares[i] = new Square(i * TileSize, 0, TileSize, colors[i]);
        return squares;
    }
}

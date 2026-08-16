using System.Text;

namespace MockRdp.Util;

/// <summary>Classic offset / hex / ASCII dump for logging raw PDUs at Trace level.</summary>
public static class HexDump
{
    public static string Format(ReadOnlySpan<byte> data, int bytesPerLine = 16)
    {
        if (data.Length == 0) return "<empty>";
        var sb = new StringBuilder();
        for (int off = 0; off < data.Length; off += bytesPerLine)
        {
            sb.Append(off.ToString("x4")).Append("  ");
            int lineLen = Math.Min(bytesPerLine, data.Length - off);
            for (int i = 0; i < bytesPerLine; i++)
            {
                if (i < lineLen) sb.Append(data[off + i].ToString("x2")).Append(' ');
                else sb.Append("   ");
                if (i == bytesPerLine / 2 - 1) sb.Append(' ');
            }
            sb.Append(' ');
            for (int i = 0; i < lineLen; i++)
            {
                byte b = data[off + i];
                sb.Append(b >= 0x20 && b < 0x7f ? (char)b : '.');
            }
            if (off + bytesPerLine < data.Length) sb.Append('\n');
        }
        return sb.ToString();
    }
}

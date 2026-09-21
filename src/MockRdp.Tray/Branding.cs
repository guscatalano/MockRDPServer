using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MockRdp.Tray;

/// <summary>
/// The mock's logo, drawn as a vector with GDI+ so it stays crisp at any size (no baked asset).
/// The motif: a blue "screen" (rounded, gradient) with a title bar and a green "connected" dot,
/// with a small cascaded window inside it — a remote-desktop session. Used for the tray icon and
/// every tray window, and rendered to a PNG for the README (tray `--logo`).
/// </summary>
internal static class Branding
{
    public static Icon MakeIcon(int size)
    {
        using var bmp = MakeBitmap(size);
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static void SavePng(string path, int size)
    {
        using var bmp = MakeBitmap(size);
        bmp.Save(path, ImageFormat.Png);
    }

    public static Bitmap MakeBitmap(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);

        float S = size;
        float radius = S * 0.16f;
        var body = new RectangleF(S * 0.06f, S * 0.12f, S * 0.88f, S * 0.76f);

        // Screen body: diagonal blue gradient.
        using (var path = Rounded(body, radius))
        using (var grad = new LinearGradientBrush(body, Color.FromArgb(0x1F, 0x8A, 0xCC), Color.FromArgb(0x0A, 0x4C, 0x79), 55f))
            g.FillPath(grad, path);

        // Title bar (rounded top only).
        var bar = new RectangleF(body.X, body.Y, body.Width, body.Height * 0.24f);
        using (var barPath = RoundedTop(bar, radius))
        using (var barBrush = new SolidBrush(Color.FromArgb(0x2C, 0xA0, 0xE6)))
            g.FillPath(barBrush, barPath);

        // "Connected" dot at the title bar's right.
        float dot = S * 0.095f;
        using (var green = new SolidBrush(Color.FromArgb(0x4A, 0xD6, 0x72)))
            g.FillEllipse(green, body.Right - dot * 1.9f, bar.Top + bar.Height * 0.5f - dot * 0.5f, dot, dot);

        // A cascaded "remote window" inside the screen: outline + its own title line.
        float pw = MathF.Max(1f, S * 0.045f);
        using (var pen = new Pen(Color.FromArgb(235, 255, 255, 255), pw) { LineJoin = LineJoin.Round })
        {
            var win = new RectangleF(body.X + body.Width * 0.22f, bar.Bottom + body.Height * 0.14f,
                                     body.Width * 0.50f, body.Height * 0.42f);
            using (var wp = Rounded(win, S * 0.05f)) g.DrawPath(pen, wp);
            g.DrawLine(pen, win.X, win.Y + win.Height * 0.30f, win.Right, win.Y + win.Height * 0.30f);
        }

        return bmp;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2f;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static GraphicsPath RoundedTop(RectangleF r, float radius)
    {
        float d = radius * 2f;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
        p.CloseFigure();
        return p;
    }
}

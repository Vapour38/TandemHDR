using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace TandemHdr.Services;

/// <summary>Draws the app's tandem discs for the notification toast. The side that matches
/// the live mode keeps its full brightness and the other falls to a darker version of its
/// own hue, so the pair still reads as one object. The geometry is the same as
/// scripts/make-icon.ps1, which keeps the toast and app.ico in one family.</summary>
internal static class DiscIcon
{
    private const string SdrLitFrom = "#DCE7F0", SdrLitTo = "#A5B9CA";
    private const string SdrDimFrom = "#6E7C89", SdrDimTo = "#4E5A65";
    private const string HdrLitFrom = "#3FA9FF", HdrLitTo = "#0E63C8";
    private const string HdrDimFrom = "#3A5872", HdrDimTo = "#26405A";

    // The toast draws its logo at roughly 48 px, and more on a scaled display. 256 covers
    // every scale and still costs a few kilobytes.
    private const int ImageSize = 256;

    /// <summary>Writes the artwork for a state and returns its path. A toast reads its logo
    /// from a file, so the picture cannot stay in memory. It goes to the temp directory
    /// rather than a folder of the app's own: the two files are rebuilt on demand, nothing
    /// reads them afterwards, and Windows clears the directory itself. Rewritten on every
    /// call, which costs a few milliseconds and rules out a stale picture.</summary>
    public static string EnsureImage(bool hdr)
    {
        string folder = Path.Combine(Path.GetTempPath(), "Tandem HDR");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, hdr ? "hdr.png" : "sdr.png");
        using var bmp = Render(hdr);
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    private static Bitmap Render(bool hdr)
    {
        var bmp = new Bitmap(ImageSize, ImageSize, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);

        // app.ico draws the pair across 90% of its square. The toast box is only 48 px, so
        // the same shape is scaled up to almost touch the sides. The proportions do not move.
        const float Fill = 0.98f / 0.90f;
        float r = ImageSize * 0.29f * Fill;
        float lx = ImageSize * (0.5f - 0.16f * Fill);
        float rx = ImageSize * (0.5f + 0.16f * Fill);
        float cy = ImageSize * 0.5f;
        var left = new RectangleF(lx - r, cy - r, r * 2, r * 2);
        var right = new RectangleF(rx - r, cy - r, r * 2, r * 2);

        FillDisc(g, left, hdr ? SdrDimFrom : SdrLitFrom, hdr ? SdrDimTo : SdrLitTo);
        FillDisc(g, right, hdr ? HdrLitFrom : HdrDimFrom, hdr ? HdrLitTo : HdrDimTo);

        // The lens where the discs overlap: an arc of each circle between the two crossing
        // points. a is half the centre distance, so h is the lens half-height.
        float a = (rx - lx) / 2;
        float h = (float)Math.Sqrt(r * r - a * a);
        float phi = (float)(Math.Atan2(h, a) * 180 / Math.PI);

        using var lens = new GraphicsPath();
        lens.AddArc(left, -phi, phi * 2);
        lens.AddArc(right, 180 - phi, phi * 2);
        lens.CloseFigure();
        using var lensBrush = new SolidBrush(Color.FromArgb(214, 244, 250, 255));
        g.FillPath(lensBrush, lens);

        return bmp;
    }

    private static void FillDisc(Graphics g, RectangleF bounds, string from, string to)
    {
        using var brush = new LinearGradientBrush(
            new PointF(bounds.Left, bounds.Top), new PointF(bounds.Right, bounds.Bottom),
            ColorTranslator.FromHtml(from), ColorTranslator.FromHtml(to));
        g.FillEllipse(brush, bounds);
    }
}

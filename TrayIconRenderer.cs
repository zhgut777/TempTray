using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace TempTray;

internal static class TrayIconRenderer
{
    // Узкий полужирный шрифт из состава Windows: цифры выходят выше при той же ширине значка
    private const string DefaultFont = "Bahnschrift SemiBold SemiConden";

    /// <summary>Подпись значка: только число, без знака градуса.</summary>
    public static string Label(float? temp) => temp is float value ? Math.Round(value).ToString("0") : "--";

    /// <summary>Рисует температуру на цветной плашке со скруглёнными углами.</summary>
    public static Bitmap Draw(string text, Color textColor, Color backColor, int size, string? fontName = null)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Значок в трее всегда квадратный, поэтому плашка занимает всю его ширину
        int height = (int)MathF.Round(size * 0.86f), top = (size - height) / 2;
        var plate = new Rectangle(0, top, size, height);
        using (GraphicsPath shape = RoundedRect(plate, size * 0.2f))
        using (var back = new SolidBrush(backColor))
            g.FillPath(back, shape);

        int padX = Math.Max(1, (int)MathF.Round(size * 0.05f)), padY = Math.Max(1, (int)MathF.Round(size * 0.1f));
        int maxWidth = plate.Width - 2 * padX, maxHeight = plate.Height - 2 * padY;

        var (family, style) = Font(fontName ?? DefaultFont);
        using (family)
        {
            // Подбираем самый крупный кегль, при котором число ещё помещается на плашке.
            // Текст рисуется штатным растеризатором с привязкой к пикселям — так края цифр остаются чёткими.
            for (float em = size * 1.4f; em >= 5f; em -= 0.5f)
            {
                using Bitmap glyphs = RenderText(text, family, style, em, textColor, out Rectangle ink);
                if (ink.Width > maxWidth || ink.Height > maxHeight)
                    continue;
                int x = plate.X + (plate.Width - ink.Width + 1) / 2 - ink.X;
                int y = plate.Y + (plate.Height - ink.Height + 1) / 2 - ink.Y;
                g.DrawImageUnscaled(glyphs, x, y);
                break;
            }
        }
        return bitmap;
    }

    /// <summary>Рисует текст на прозрачном холсте и возвращает границы закрашенных пикселей.</summary>
    private static Bitmap RenderText(string text, FontFamily family, FontStyle style, float em, Color color, out Rectangle ink)
    {
        const int margin = 3;
        int width = (int)(em * text.Length) + margin * 2, height = (int)(em * 1.7f) + margin * 2;
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        using (var font = new Font(family, em, style, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(color))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawString(text, font, brush, margin, margin, StringFormat.GenericTypographic);
        }

        // Точки читаем разом из памяти: GetPixel на каждую точку в сотни раз медленнее
        var pixels = new int[width * height];
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        int left = width, right = -1, topInk = height, bottom = -1;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if ((uint)pixels[y * width + x] >> 24 < 48)
                continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            topInk = Math.Min(topInk, y);
            bottom = Math.Max(bottom, y);
        }
        ink = right < 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, topInk, right + 1, bottom + 1);
        return bitmap;
    }

    private static (FontFamily Family, FontStyle Style) Font(string name)
    {
        try
        {
            var family = new FontFamily(name);
            return (family, family.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold);
        }
        catch (ArgumentException)
        {
            return (new FontFamily("Segoe UI"), FontStyle.Bold);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static int SmallIconSize => Math.Max(16, Native.GetSystemMetrics(Native.SM_CXSMICON));
}

#if DEBUG
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace TempTray;

/// <summary>
/// Рисует значок программы (градусник на тёмно-зелёной плитке) и собирает из него app.ico.
/// Запускается вручную: TempTray.exe --makeicon app.ico; готовый файл лежит в проекте.
/// </summary>
internal static class AppIcon
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    public static void Save(string file)
    {
        var frames = new List<byte[]>();
        foreach (int size in Sizes)
        {
            using Bitmap bitmap = Draw(size);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            frames.Add(stream.ToArray());
        }

        using var output = new BinaryWriter(File.Create(file));
        output.Write((short)0);
        output.Write((short)1);
        output.Write((short)frames.Count);
        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            output.Write((byte)(Sizes[i] == 256 ? 0 : Sizes[i]));
            output.Write((byte)(Sizes[i] == 256 ? 0 : Sizes[i]));
            output.Write((byte)0);
            output.Write((byte)0);
            output.Write((short)1);
            output.Write((short)32);
            output.Write(frames[i].Length);
            output.Write(offset);
            offset += frames[i].Length;
        }
        foreach (byte[] frame in frames)
            output.Write(frame);
    }

    /// <summary>Все размеры на одном листе, мелкие увеличены — чтобы оценить значок глазами.</summary>
    public static void SavePreview(string file)
    {
        using var sheet = new Bitmap(1100, 300);
        using var g = Graphics.FromImage(sheet);
        g.Clear(Color.FromArgb(0x2C, 0x2C, 0x2C));
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        int x = 16;
        foreach (int size in new[] { 256, 64, 48, 32, 24, 16 })
        {
            using Bitmap icon = Draw(size);
            int zoom = size >= 64 ? 1 : size >= 32 ? 3 : 6;
            g.DrawImage(icon, x, 16, size * zoom, size * zoom);
            x += size * zoom + 24;
        }
        sheet.Save(file, ImageFormat.Png);
    }

    private static Bitmap Draw(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float k = size / 256f;
        g.ScaleTransform(k, k);

        // Плитка
        using (GraphicsPath tile = RoundedRect(new RectangleF(8, 8, 240, 240), 56))
        using (var fill = new LinearGradientBrush(new PointF(0, 8), new PointF(0, 248), Color.FromArgb(0x24, 0x55, 0x30), Color.FromArgb(0x12, 0x2E, 0x1A)))
            g.FillPath(fill, tile);

        // На мелких размерах градусник крупнее и без шкалы, иначе он сливается в пятно
        bool small = size < 40;
        float cx = small ? 128 : 112;
        float half = small ? 22 : 17, top = small ? 44 : 46, bulbY = small ? 178 : 180, bulb = small ? 46 : 38;
        float stroke = small ? 20 : 13;

        float dy = MathF.Sqrt(bulb * bulb - half * half);
        float joinY = bulbY - dy;
        float angle = MathF.Atan2(-dy, half) * 180f / MathF.PI;
        using (var outline = new GraphicsPath())
        {
            outline.AddLine(cx - half, joinY, cx - half, top + half);
            outline.AddArc(cx - half, top, half * 2, half * 2, 180, 180);
            outline.AddLine(cx + half, top + half, cx + half, joinY);
            outline.AddArc(cx - bulb, bulbY - bulb, bulb * 2, bulb * 2, angle, 360 - 2 * (90 + angle) + 180 - 180);
            outline.CloseFigure();
            using var glass = new Pen(Color.FromArgb(0xF4, 0xFF, 0xF4), stroke) { LineJoin = LineJoin.Round };
            g.DrawPath(glass, outline);
        }

        // Столбик
        var mercury = Color.FromArgb(0x7D, 0xF5, 0x7D);
        float core = bulb - stroke / 2 - (small ? 7 : 6), column = small ? 9 : 7, level = small ? 96 : 100;
        using (var liquid = new SolidBrush(mercury))
        {
            g.FillEllipse(liquid, cx - core, bulbY - core, core * 2, core * 2);
            using GraphicsPath stem = RoundedRect(new RectangleF(cx - column, level, column * 2, bulbY - level), column);
            g.FillPath(liquid, stem);
        }

        if (!small)
        {
            using var tick = new Pen(Color.FromArgb(0xD0, 0xF4, 0xFF, 0xF4), 11) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(tick, 158, 70, 192, 70);
            g.DrawLine(tick, 158, 104, 180, 104);
            g.DrawLine(tick, 158, 138, 192, 138);
        }
        return bitmap;
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
}
#endif

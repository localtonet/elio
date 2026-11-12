using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Elio.Desktop;

/// <summary>The app's icon, drawn rather than shipped: a prompt and a rising sun on a rounded square.</summary>
internal static class AppIcon
{
    private const int Size = 64;
    private const int Corner = 14;
    private const uint Background = 0xFFFFB020;
    private const uint Mark = 0xFF0E1116;

    public static WindowIcon Create()
    {
        uint[] pixels = new uint[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (!InsideRoundedSquare(x, y)) continue;
                pixels[(y * Size) + x] = OnMark(x, y) ? Mark : Background;
            }
        }

        // The icon keeps the bitmap, so it is not disposed here.
        var bitmap = new WriteableBitmap(new PixelSize(Size, Size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (ILockedFramebuffer buffer = bitmap.Lock())
        {
            for (int y = 0; y < Size; y++)
                System.Runtime.InteropServices.Marshal.Copy((int[])(object)pixels, y * Size, buffer.Address + (y * buffer.RowBytes), Size);
        }

        return new WindowIcon(bitmap);
    }

    private static bool InsideRoundedSquare(int x, int y)
    {
        int cx = Math.Clamp(x, Corner, Size - 1 - Corner);
        int cy = Math.Clamp(y, Corner, Size - 1 - Corner);
        int dx = x - cx;
        int dy = y - cy;
        return (dx * dx) + (dy * dy) <= Corner * Corner;
    }

    // The same shapes as build/make-icons.py: the ">", the three rays, the half sun and the line under it.
    private static bool OnMark(int x, int y) =>
        Near(x, y, 17, 22, 27, 32, 2.6) || Near(x, y, 27, 32, 17, 42, 2.6)
        || Near(x, y, 43, 19, 43, 23, 1.5) || Near(x, y, 34.5, 22.5, 37, 25, 1.5) || Near(x, y, 51.5, 22.5, 49, 25, 1.5)
        || (y <= 37 && ((x - 43) * (x - 43)) + ((y - 37) * (y - 37)) <= 81)
        || (x is >= 33 and <= 53 && y is >= 40 and <= 44);

    private static bool Near(double x, double y, double ax, double ay, double bx, double by, double reach)
    {
        double dx = bx - ax, dy = by - ay;
        double along = Math.Clamp((((x - ax) * dx) + ((y - ay) * dy)) / ((dx * dx) + (dy * dy)), 0, 1);
        double px = x - ax - (along * dx), py = y - ay - (along * dy);
        return (px * px) + (py * py) <= reach * reach;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Elio.Core;

namespace Elio.UI;

/// <summary>
/// Shows a text as a QR code. Always dark on white with a white margin, whatever the theme: that is what a
/// camera reads best, and a code in the theme's colors would not be read at all on a dark window.
/// </summary>
public sealed class QrView : Control
{
    // The standard asks for four light squares around the code, which is what lets a reader find its edge.
    private const int QuietSquares = 4;

    private readonly bool[,] _squares;

    public QrView(string text, double side = 196)
    {
        _squares = QrCode.Encode(text);
        Width = Height = side;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        int size = _squares.GetLength(0);
        double side = Math.Min(Bounds.Width, Bounds.Height);
        context.DrawRectangle(Brushes.White, null, new RoundedRect(new Rect(0, 0, side, side), 8));

        // Whole pixels for each square, so that neighbors meet without a hairline of white between them.
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double square = Math.Floor(side * scale / (size + (2 * QuietSquares))) / scale;
        double start = (side - (square * size)) / 2;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (!_squares[y, x]) continue;

                // Runs of dark squares in a row are drawn as one bar, for the same reason.
                int run = 1;
                while (x + run < size && _squares[y, x + run]) run++;
                context.DrawRectangle(Brushes.Black, null, new Rect(start + (x * square), start + (y * square), square * run, square));
                x += run - 1;
            }
        }
    }
}

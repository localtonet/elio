using Avalonia;
using Avalonia.Controls;

namespace Elio.UI;

/// <summary>
/// Lays the tabs out in a row that never grows past the room it is given. While they fit, each tab is as wide
/// as its name needs; once they do not, the widest give way first, as in a browser, so the row stops short of
/// the window's own buttons instead of running under them.
/// </summary>
public sealed class TabStripPanel : Panel
{
    /// <summary>The space between two tabs.</summary>
    public double Spacing { get; set; } = 2;

    /// <summary>A tab is at least this wide while there is room for it.</summary>
    public double PreferredTabWidth { get; set; } = 120;

    /// <summary>However many tabs there are, none gets narrower than this: enough for its mark and its close button.</summary>
    public double SmallestTabWidth { get; set; } = 44;

    private double[] _widths = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = Children.Count;
        if (_widths.Length != count) _widths = new double[count];
        if (count == 0) return default;

        for (int i = 0; i < count; i++)
        {
            Children[i].Measure(new Size(double.PositiveInfinity, availableSize.Height));
            _widths[i] = Math.Max(PreferredTabWidth, Children[i].DesiredSize.Width);
        }

        double gaps = Spacing * (count - 1);
        if (!double.IsInfinity(availableSize.Width)) Shrink(_widths, Math.Max(0, availableSize.Width - gaps), SmallestTabWidth);

        // Each is measured again at the width it ends up with, so a long name is cut short rather than drawn over its neighbor.
        double height = 0;
        for (int i = 0; i < count; i++)
        {
            Children[i].Measure(new Size(_widths[i], availableSize.Height));
            height = Math.Max(height, Children[i].DesiredSize.Height);
        }

        return new Size(_widths.Sum() + gaps, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        for (int i = 0; i < Children.Count && i < _widths.Length; i++)
        {
            Children[i].Arrange(new Rect(x, 0, _widths[i], finalSize.Height));
            x += _widths[i] + Spacing;
        }

        return finalSize;
    }

    // Brings the widths down to what there is room for by lowering a ceiling over all of them: tabs already
    // narrower than the ceiling keep their width, and the rest share what is left equally.
    internal static void Shrink(double[] widths, double room, double smallest)
    {
        if (widths.Sum() <= room) return;

        double[] sorted = [.. widths.Order()];
        double left = room;
        double ceiling = smallest;
        for (int i = 0; i < sorted.Length; i++)
        {
            double share = left / (sorted.Length - i);
            if (sorted[i] >= share)
            {
                ceiling = Math.Max(smallest, share);
                break;
            }

            left -= sorted[i];
        }

        for (int i = 0; i < widths.Length; i++) widths[i] = Math.Min(widths[i], ceiling);
    }
}

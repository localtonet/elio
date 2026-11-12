using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Elio.Connections;

/// <summary>What one node of a tmux window layout is.</summary>
public enum TmuxLayoutKind
{
    /// <summary>A single pane.</summary>
    Pane,

    /// <summary>Children side by side, left to right, each separated by a one-cell divider. tmux writes this as {...}.</summary>
    Columns,

    /// <summary>Children stacked top to bottom, each separated by a one-cell divider. tmux writes this as [...].</summary>
    Rows,
}

/// <summary>One cell of a tmux window layout: a pane, or a split holding more cells.</summary>
public sealed class TmuxLayoutNode
{
    internal TmuxLayoutNode(TmuxLayoutKind kind, int width, int height, int x, int y, string? paneId, IReadOnlyList<TmuxLayoutNode> children)
    {
        (Kind, Width, Height, X, Y, PaneId, Children) = (kind, width, height, x, y, paneId, children);
    }

    public TmuxLayoutKind Kind { get; }

    /// <summary>Size in character cells. For a split this includes the dividers between its children.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Position in cells from the window's top left corner.</summary>
    public int X { get; }

    public int Y { get; }

    /// <summary>The pane's id, such as "%3". Null for a split.</summary>
    public string? PaneId { get; }

    /// <summary>The cells inside a split, in order. Empty for a pane.</summary>
    public IReadOnlyList<TmuxLayoutNode> Children { get; }

    /// <summary>Every pane under this node, in layout order.</summary>
    public IEnumerable<TmuxLayoutNode> Panes()
    {
        if (Kind == TmuxLayoutKind.Pane)
        {
            yield return this;
            yield break;
        }

        foreach (TmuxLayoutNode child in Children)
        {
            foreach (TmuxLayoutNode pane in child.Panes()) yield return pane;
        }
    }

    /// <summary>The pane with this id under this node, or null.</summary>
    public TmuxLayoutNode? Find(string paneId) => Panes().FirstOrDefault(p => p.PaneId == paneId);
}

/// <summary>Reads tmux layout strings such as "b25d,80x24,0,0{40x24,0,0,1,39x24,41,0,2}".</summary>
public static class TmuxLayout
{
    /// <summary>Turns a layout string into a tree.</summary>
    /// <exception cref="FormatException">The text is not a layout, or its checksum does not match.</exception>
    public static TmuxLayoutNode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int comma = text.IndexOf(',');
        if (comma != 4 || !int.TryParse(text.AsSpan(0, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int expected))
            throw new FormatException("A tmux layout starts with a four digit checksum.");

        ReadOnlySpan<char> body = text.AsSpan(5);
        if (Checksum(body) != expected) throw new FormatException("The tmux layout does not match its checksum.");

        int position = 0;
        TmuxLayoutNode root = ReadCell(body, ref position, 0);
        if (position != body.Length) throw new FormatException("Unexpected text after the tmux layout.");
        return root;
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out TmuxLayoutNode? root)
    {
        root = null;
        if (text is null) return false;

        try
        {
            root = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The checksum tmux puts in front of a layout: a 16 bit rotate-and-add over the text after the first comma.</summary>
    public static int Checksum(ReadOnlySpan<char> body)
    {
        int sum = 0;
        foreach (char c in body)
        {
            sum = (sum >> 1) + ((sum & 1) << 15);
            sum = (sum + c) & 0xffff;
        }

        return sum;
    }

    private static TmuxLayoutNode ReadCell(ReadOnlySpan<char> text, ref int position, int depth)
    {
        // A layout a user could type by hand is never this deep; this only stops a hostile one.
        if (depth > 64) throw new FormatException("The tmux layout is nested too deeply.");

        int width = ReadNumber(text, ref position);
        Expect(text, ref position, 'x');
        int height = ReadNumber(text, ref position);
        Expect(text, ref position, ',');
        int x = ReadNumber(text, ref position);
        Expect(text, ref position, ',');
        int y = ReadNumber(text, ref position);

        if (position < text.Length && text[position] is '{' or '[')
        {
            char close = text[position] == '{' ? '}' : ']';
            TmuxLayoutKind kind = close == '}' ? TmuxLayoutKind.Columns : TmuxLayoutKind.Rows;
            position++;

            var children = new List<TmuxLayoutNode>();
            while (true)
            {
                children.Add(ReadCell(text, ref position, depth + 1));
                if (position >= text.Length) throw new FormatException("The tmux layout ends inside a split.");
                if (text[position] == close) break;
                Expect(text, ref position, ',');
            }

            position++;
            return new TmuxLayoutNode(kind, width, height, x, y, null, children);
        }

        // Very old tmux leaves the pane id out. What follows the comma is then the next cell ("40x24,..."),
        // which is told apart from an id by the x after its first number.
        string? paneId = null;
        if (position < text.Length && text[position] == ',')
        {
            int probe = position + 1;
            int number = ReadNumber(text, ref probe);
            if (probe >= text.Length || text[probe] != 'x')
            {
                paneId = "%" + number.ToString(CultureInfo.InvariantCulture);
                position = probe;
            }
        }

        return new TmuxLayoutNode(TmuxLayoutKind.Pane, width, height, x, y, paneId, []);
    }

    private static int ReadNumber(ReadOnlySpan<char> text, ref int position)
    {
        int start = position;
        while (position < text.Length && char.IsAsciiDigit(text[position])) position++;
        if (position == start || position - start > 9) throw new FormatException("A number was expected in the tmux layout.");
        return int.Parse(text[start..position], NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static void Expect(ReadOnlySpan<char> text, ref int position, char wanted)
    {
        if (position >= text.Length || text[position] != wanted) throw new FormatException($"'{wanted}' was expected in the tmux layout.");
        position++;
    }
}

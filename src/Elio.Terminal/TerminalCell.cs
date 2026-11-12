namespace Elio.Terminal;

[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Faint = 4,
    Invisible = 8,
    Strikethrough = 16,
    Overline = 32,
    HasBackground = 64,
}

public enum UnderlineStyle : byte
{
    None,
    Single,
    Double,
    Curly,
    Dotted,
    Dashed,
}

public enum CursorShape
{
    Bar,
    Block,
    Underline,
    HollowBlock,
}

/// <summary>One grid position, with colors already resolved to 0xRRGGBB.</summary>
public struct TerminalCell
{
    /// <summary>The character, or 0 for an empty cell or the second half of a wide one.</summary>
    public uint Codepoint;

    /// <summary>Set instead of relying on <see cref="Codepoint"/> when several code points form one character.</summary>
    public string? Cluster;

    public uint Foreground;
    public uint Background;
    public uint UnderlineColor;
    public CellFlags Flags;
    public UnderlineStyle Underline;
}

/// <summary>What a renderer needs besides the rows themselves.</summary>
public readonly record struct TerminalFrame(
    int Columns,
    int Rows,
    uint Background,
    uint Foreground,
    uint CursorColor,
    bool CursorVisible,
    int CursorX,
    int CursorY,
    CursorShape CursorShape,
    bool CursorBlinking);

/// <summary>Receives a row that has to be drawn again. The cells are only valid during the call.</summary>
public delegate void RowVisitor(int row, ReadOnlySpan<TerminalCell> cells);

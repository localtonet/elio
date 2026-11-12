namespace Elio.Terminal;

public enum SelectionPhase
{
    Press,
    Drag,
    Release,
}

public enum MouseAction
{
    Press = 0,
    Release = 1,
    Motion = 2,
}

public enum MouseButton
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 3,
    WheelUp = 4,
    WheelDown = 5,
}

/// <summary>How to read the screen.</summary>
public enum FrameRead
{
    /// <summary>Rows changed since the last read, which are then marked as seen.</summary>
    Changes,

    /// <summary>Every row, for a renderer starting empty. Changes are then marked as seen.</summary>
    All,

    /// <summary>Every row, leaving the record of changes alone.</summary>
    Peek,
}

/// <summary>Sizes in device pixels, as programs see them.</summary>
public readonly record struct TerminalPixels(int Width, int Height, int CellWidth, int CellHeight);

/// <summary>What a program says about a task it is running, as sent with OSC 9;4.</summary>
public enum ProgressState
{
    /// <summary>Nothing is being reported, or the task is over.</summary>
    None = 0,
    Running = 1,
    Error = 2,

    /// <summary>Working, with no way to say how far along.</summary>
    Indeterminate = 3,
    Paused = 4,
}

/// <summary>How far along the running program says it is.</summary>
public readonly record struct TerminalProgress(ProgressState State, int Percent);

/// <summary>Where the screen sits in the history, in rows.</summary>
/// <param name="Total">Every row there is, history included.</param>
/// <param name="Offset">The first row on screen.</param>
/// <param name="Visible">How many rows are on screen.</param>
public readonly record struct ScrollPosition(long Total, long Offset, long Visible)
{
    /// <summary>There is history above or below what is on screen.</summary>
    public bool HasHistory => Total > Visible && Visible > 0;
}

/// <summary>Where a search stands.</summary>
/// <param name="Current">Which match is shown, counting from 1 at the newest. Zero when there is none.</param>
/// <param name="Total">How many matches there are.</param>
public readonly record struct SearchResult(int Current, int Total)
{
    /// <summary>The search was for a regular expression, and what was given is not one that can be used.</summary>
    public bool Invalid { get; init; }

    /// <summary>The regular expression took too long to match and was given up on. A simpler one may not.</summary>
    public bool TimedOut { get; init; }
}

/// <summary>A picture on screen: which one, where its top-left cell is, and how to draw it.</summary>
/// <param name="Generation">Changes when the picture's pixels do, so a drawn copy knows when it is stale.</param>
/// <param name="Layer">0 under the background, 1 under the text, 2 over the text.</param>
/// <param name="Column">The cell the picture starts in. Negative when it starts off screen.</param>
/// <param name="OffsetX">How far into that cell it starts, in device pixels.</param>
/// <param name="Width">The size to draw it at, in device pixels.</param>
/// <param name="SourceX">The part of the picture to draw, in the picture's own pixels.</param>
public readonly record struct TerminalImage(
    uint Id,
    ulong Generation,
    int Layer,
    int Z,
    int Column,
    int Row,
    int OffsetX,
    int OffsetY,
    int Width,
    int Height,
    int SourceX,
    int SourceY,
    int SourceWidth,
    int SourceHeight);

/// <summary>A picture's pixels, four bytes each: red, green, blue, and how opaque.</summary>
public sealed record ImagePixels(int Width, int Height, byte[] Rgba);

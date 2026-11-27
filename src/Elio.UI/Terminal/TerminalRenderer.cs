using Elio.Terminal;
using SkiaSharp;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Elio.UI.Terminal;

/// <summary>
/// The command line being typed, as far as the renderer is to color it: where it starts on the screen and how to color text.
/// </summary>
/// <param name="StartColumn">The column of the first character of the line, on the row it starts on.</param>
/// <param name="StartRow">That row, counted on the screen.</param>
/// <param name="Spans">Tells which parts of a line are what.</param>
/// <param name="ColorOf">The color for a kind of text as 0xRRGGBB, or null to leave it as it is.</param>
/// <param name="Version">Changes when the colors or the way of telling things apart did, so that a line painted before is painted again.</param>
internal sealed record InputHighlight(int StartColumn, int StartRow, Func<string, IReadOnlyList<HighlightSpan>> Spans, Func<HighlightKind, uint?> ColorOf, int Version);

/// <summary>
/// Draws a terminal. Each row keeps what it needs to draw itself, and only rows the engine reports
/// as changed are rebuilt. Used only on the render thread.
/// </summary>
internal sealed class TerminalRenderer(TerminalFonts fonts, bool ligatures, IReadOnlyList<(Regex Pattern, uint Color)> highlights, uint selectionColor) : IDisposable
{
    private sealed class RowLayer
    {
        public readonly List<(float X, float Width, uint Color)> Backgrounds = [];
        public readonly List<(float X, float Width, uint Color)> Highlights = [];
        public readonly List<(SKTextBlob Blob, uint Color)> Text = [];
        public readonly List<(float X, float Width, float Y, uint Color)> Lines = [];

        public void Clear()
        {
            foreach (var (blob, _) in Text) blob.Dispose();
            Text.Clear();
            Backgrounds.Clear();
            Highlights.Clear();
            Lines.Clear();
        }
    }

    private const uint HighlightAlpha = 0x70000000;

    // Matches of a search are tinted with the color of a selection, which is what the match the search is on becomes.
    private const uint SearchAlpha = 0xA0000000;

    // What the shell would be given if the suggestion were taken, drawn faint enough not to pass for what was typed.
    private const uint GhostAlpha = 0x80000000;

    // The longest command line that is colored, in characters and in rows. Beyond that it is not a line anyone is typing.
    private const int MaxInputChars = 4096;
    private const int MaxInputRows = 12;

    private readonly List<RowLayer> _rows = [];

    // What each row held when it was last read, to paint the line being typed from, and to put it back when it is not wanted.
    private readonly List<TerminalCell[]> _cells = [];
    private readonly HashSet<int> _paintedRows = [];
    private string? _paintedKey;
    private readonly SKPaint _paint = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKTextBlobBuilder _builder = new();
    private ushort[] _glyphs = new ushort[256];
    private float[] _positions = new float[256];
    private char[] _runText = new char[256];
    private char[] _rowText = new char[256];
    private bool _everRead;
    private readonly List<SearchHighlight> _searchMarks = [];
    private int _nextSearchMark;
    private uint _searchOutline;

    // Pictures on screen, and the drawable copy made of each. A copy is remade when its picture's pixels change.
    private const int TextLayer = 2;

    // An animation cannot ask to be drawn faster than a screen refreshes, nor leave the timer waiting for ever.
    private const long MinAnimationDelay = 8;
    private const long MaxAnimationDelay = 10_000;
    private readonly List<TerminalImage> _images = [];
    private readonly Dictionary<uint, (ulong Generation, SKImage Image)> _imageCache = [];
    private readonly SKPaint _imagePaint = new() { IsAntialias = true };

    public TerminalFonts Fonts => fonts;

    /// <summary>How long until an animated picture needs drawing again, as of the last draw. Null if none is playing.</summary>
    public TimeSpan? NextAnimation { get; private set; }

    /// <returns>The frame that was drawn, or null if the session has closed.</returns>
    /// <param name="ghost">A suggestion for the rest of the line being typed, drawn from the cursor on. Null for none.</param>
    /// <param name="input">The command line being typed, to be colored by what its parts are. Null for none.</param>
    /// <param name="typingAtPrompt">Whether a command line is being typed at the shell's prompt (<see cref="TerminalSession.TypingAtPrompt"/>).</param>
    public TerminalFrame? Draw(SKCanvas canvas, TerminalSession session, float width, float height, bool focused, bool cursorOn, string? ghost, InputHighlight? input = null, bool typingAtPrompt = false)
    {
        long now = Environment.TickCount64;
        NextAnimation = session.AdvanceAnimations(now) is long due ? TimeSpan.FromMilliseconds(Math.Clamp(due - now, MinAnimationDelay, MaxAnimationDelay)) : null;

        TerminalFrame? read = session.ReadFrame(BuildRow, _everRead ? FrameRead.Changes : FrameRead.All);
        if (read is not TerminalFrame frame) return null;
        _everRead = true;

        bool shown = ShowsCursor(frame, typingAtPrompt);
        PaintInput(frame, input, shown);

        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, width, height));

        _paint.Color = Opaque(frame.Background);
        canvas.DrawRect(0, 0, width, height, _paint);

        session.ReadImages(_images);
        DrawImages(canvas, session, overText: false);

        session.ReadSearchHighlights(_searchMarks);
        _nextSearchMark = 0;
        _searchOutline = frame.Foreground;

        int rows = Math.Min(frame.Rows, _rows.Count);
        for (int y = 0; y < rows; y++) DrawRow(canvas, _rows[y], y, y * fonts.CellHeight);

        DrawImages(canvas, session, overText: true);
        ForgetUnusedImages();

        using SKTextBlob? suggestion = shown && frame.CursorY < rows && ghost is { Length: > 0 } ? BuildGhost(frame, ghost) : null;
        if (suggestion is not null) DrawGhost(canvas, frame, suggestion, underCursor: false);

        if (shown && frame.CursorY < rows && (cursorOn || !frame.CursorBlinking || !focused))
        {
            DrawCursor(canvas, frame, focused);

            // A block hides the cell it is on, and the first character of the suggestion is in that cell.
            if (suggestion is not null && focused && frame.CursorShape == CursorShape.Block) DrawGhost(canvas, frame, suggestion, underCursor: true);
        }

        canvas.Restore();
        return frame;
    }

    /// <summary>
    /// Whether the cursor is drawn: when the program shows it, and also while a command line is being typed at the shell's
    /// prompt, if a program hid it and stopped or crashed before showing it again. The shell does not show it again by
    /// itself, and a line cannot be edited without seeing where the cursor is. Full-screen programs, progress bars and
    /// pickers that hide it on purpose are not at a prompt, so they keep it hidden.
    /// </summary>
    internal static bool ShowsCursor(TerminalFrame frame, bool typingAtPrompt) =>
        frame.CursorInView && (frame.CursorVisible || typingAtPrompt);

    /// <summary>Draws the pictures that go under the text, or the ones that go over it.</summary>
    private void DrawImages(SKCanvas canvas, TerminalSession session, bool overText)
    {
        float scale = (float)fonts.Scale;
        foreach (TerminalImage image in _images)
        {
            if (image.Layer >= TextLayer != overText || DrawableFor(session, image) is not SKImage drawable) continue;

            // The engine speaks in device pixels, the canvas in the units the rest of the app uses.
            float left = (image.Column * fonts.CellWidth) + (image.OffsetX / scale);
            float top = (image.Row * fonts.CellHeight) + (image.OffsetY / scale);
            var source = new SKRect(image.SourceX, image.SourceY, image.SourceX + image.SourceWidth, image.SourceY + image.SourceHeight);
            var target = new SKRect(left, top, left + (image.Width / scale), top + (image.Height / scale));
            canvas.DrawImage(drawable, source, target, new SKSamplingOptions(SKFilterMode.Linear), _imagePaint);
        }
    }

    private SKImage? DrawableFor(TerminalSession session, TerminalImage image)
    {
        if (_imageCache.TryGetValue(image.Id, out var cached) && cached.Generation == image.Generation) return cached.Image;
        if (session.ReadImagePixels(image.Id) is not ImagePixels pixels) return cached.Image;

        cached.Image?.Dispose();
        var info = new SKImageInfo(pixels.Width, pixels.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        SKImage drawable = SKImage.FromPixelCopy(info, pixels.Rgba);
        _imageCache[image.Id] = (image.Generation, drawable);
        return drawable;
    }

    private void ForgetUnusedImages()
    {
        if (_imageCache.Count == 0) return;

        foreach (uint id in _imageCache.Keys.Where(id => !_images.Exists(i => i.Id == id)).ToList())
        {
            _imageCache[id].Image.Dispose();
            _imageCache.Remove(id);
        }
    }

    private void DrawRow(SKCanvas canvas, RowLayer row, int index, float top)
    {
        foreach (var (x, cellsWidth, color) in row.Backgrounds)
        {
            _paint.Color = Opaque(color);
            canvas.DrawRect(x, top, cellsWidth, fonts.CellHeight, _paint);
        }

        foreach (var (x, cellsWidth, color) in row.Highlights)
        {
            _paint.Color = new SKColor(HighlightAlpha | color);
            canvas.DrawRect(x, top, cellsWidth, fonts.CellHeight, _paint);
        }

        DrawSearchMarks(canvas, index, top);

        foreach (var (blob, color) in row.Text)
        {
            _paint.Color = new SKColor(color);
            canvas.DrawText(blob, 0, top, _paint);
        }

        foreach (var (x, lineWidth, y, color) in row.Lines)
        {
            _paint.Color = Opaque(color);
            canvas.DrawRect(x, top + y, lineWidth, 1, _paint);
        }
    }

    /// <summary>Tints what a search matched on one row. The marks come ordered by row, as the rows are drawn.</summary>
    private void DrawSearchMarks(SKCanvas canvas, int y, float top)
    {
        while (_nextSearchMark < _searchMarks.Count && _searchMarks[_nextSearchMark].Row < y) _nextSearchMark++;

        for (; _nextSearchMark < _searchMarks.Count && _searchMarks[_nextSearchMark].Row == y; _nextSearchMark++)
        {
            SearchHighlight mark = _searchMarks[_nextSearchMark];
            float left = mark.Column * fonts.CellWidth;
            float markWidth = mark.Length * fonts.CellWidth;

            _paint.Color = new SKColor(SearchAlpha | selectionColor);
            canvas.DrawRect(left, top, markWidth, fonts.CellHeight, _paint);
            if (!mark.Current) continue;

            // The match the search is on is also the selection. The frame keeps it apart from the others
            // even after a click has taken the selection away.
            _paint.Color = Opaque(_searchOutline);
            _paint.Style = SKPaintStyle.Stroke;
            canvas.DrawRect(left + 0.5f, top + 0.5f, markWidth - 1, fonts.CellHeight - 1, _paint);
            _paint.Style = SKPaintStyle.Fill;
        }
    }

    /// <summary>Lays out a suggestion from the cursor to the end of its row, a cell for each character.</summary>
    private SKTextBlob? BuildGhost(TerminalFrame frame, string ghost)
    {
        if (_glyphs.Length < frame.Columns)
        {
            _glyphs = new ushort[frame.Columns];
            _positions = new float[frame.Columns];
            _runText = new char[frame.Columns];
        }

        SKFont? runFont = null;
        int count = 0;
        bool any = false;

        void FlushRun()
        {
            if (count == 0) return;
            SKHorizontalRunBuffer run = _builder.AllocateHorizontalRun(runFont!, count, fonts.Baseline);
            _glyphs.AsSpan(0, count).CopyTo(run.Glyphs);
            _positions.AsSpan(0, count).CopyTo(run.Positions);
            count = 0;
            any = true;
        }

        int column = frame.CursorX;
        foreach (Rune rune in ghost.EnumerateRunes())
        {
            // An accent on the character before it has no cell of its own, and is not worth shaping for a hint.
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) continue;

            int cells = IsWide(rune.Value) ? 2 : 1;
            if (column + cells > frame.Columns) break;

            if (!Rune.IsWhiteSpace(rune))
            {
                var (font, glyph) = fonts.Resolve((uint)rune.Value, false, false);
                if (font != runFont) FlushRun();
                runFont = font;
                _glyphs[count] = glyph;
                _positions[count] = column * fonts.CellWidth;
                count++;
            }

            column += cells;
        }

        FlushRun();
        return any ? _builder.Build() : null;
    }

    // Close enough for a hint: the scripts and symbols that terminals give two cells.
    private static bool IsWide(int codepoint) => codepoint is
        (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3) or (>= 0xF900 and <= 0xFAFF)
        or (>= 0xFE30 and <= 0xFE4F) or (>= 0xFF00 and <= 0xFF60) or (>= 0xFFE0 and <= 0xFFE6) or (>= 0x1F300 and <= 0x1FAFF) or (>= 0x20000 and <= 0x3FFFD);

    private void DrawGhost(SKCanvas canvas, TerminalFrame frame, SKTextBlob suggestion, bool underCursor)
    {
        float left = frame.CursorX * fonts.CellWidth;
        float top = frame.CursorY * fonts.CellHeight;
        float right = underCursor ? left + fonts.CellWidth : frame.Columns * fonts.CellWidth;

        canvas.Save();
        canvas.ClipRect(new SKRect(left, top, right, top + fonts.CellHeight));
        _paint.Color = underCursor ? Opaque(frame.Background) : new SKColor(GhostAlpha | frame.Foreground);
        canvas.DrawText(suggestion, 0, top, _paint);
        canvas.Restore();
    }

    private void DrawCursor(SKCanvas canvas, TerminalFrame frame, bool focused)
    {
        float x = frame.CursorX * fonts.CellWidth;
        float y = frame.CursorY * fonts.CellHeight;
        CursorShape shape = focused ? frame.CursorShape : CursorShape.HollowBlock;
        TerminalCell under = frame.CursorY < _cells.Count && frame.CursorX < _cells[frame.CursorY].Length ? _cells[frame.CursorY][frame.CursorX] : default;
        (uint cursor, uint text) = CursorColors(frame.CursorColor, frame.Foreground, frame.Background, under);
        _paint.Color = Opaque(cursor);

        switch (shape)
        {
            case CursorShape.Bar:
                canvas.DrawRect(x, y, Math.Max(1, fonts.CellWidth / 8), fonts.CellHeight, _paint);
                break;
            case CursorShape.Underline:
                canvas.DrawRect(x, y + fonts.CellHeight - 2, fonts.CellWidth, 2, _paint);
                break;
            case CursorShape.HollowBlock:
                _paint.Style = SKPaintStyle.Stroke;
                canvas.DrawRect(x + 0.5f, y + 0.5f, fonts.CellWidth - 1, fonts.CellHeight - 1, _paint);
                _paint.Style = SKPaintStyle.Fill;
                break;
            default:
                var cell = new SKRect(x, y, x + fonts.CellWidth, y + fonts.CellHeight);
                canvas.DrawRect(cell, _paint);

                // The character under the block is redrawn in a color that stands out from it, so it stays readable.
                canvas.Save();
                canvas.ClipRect(cell);
                _paint.Color = Opaque(text);
                foreach (var (blob, _) in _rows[frame.CursorY].Text) canvas.DrawText(blob, 0, y, _paint);
                canvas.Restore();
                break;
        }
    }

    // How far apart two colors have to be, as a distance between their red, green and blue, to tell one from the other in a cell.
    private const int DistinctColors = 48;

    /// <summary>
    /// The colors to draw the cursor in over a cell: the cursor's, and the screen's background for the character under a
    /// block. Over a cell whose own background is too like the cursor's color the cursor would vanish into it. That is
    /// the case in text a shell shows in reverse, as bash and zsh do with what was just pasted, wherever the theme's
    /// cursor has the text's color. There the cell is drawn the other way round, in its text color with its character in
    /// its background, as other terminals do.
    /// </summary>
    internal static (uint Cursor, uint Text) CursorColors(uint cursor, uint foreground, uint background, TerminalCell cell)
    {
        // Only a cell with a background of its own differs from the screen. The others may not even have colors, past the end of a line.
        bool own = (cell.Flags & CellFlags.HasBackground) != 0;
        uint under = own ? cell.Background : background;
        if (!Alike(cursor, under)) return (cursor, background);

        uint block = own && !Alike(cell.Foreground, under) ? cell.Foreground : Alike(foreground, under) ? background : foreground;
        return (block, Alike(background, block) ? under : background);
    }

    private static bool Alike(uint a, uint b)
    {
        int red = (int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF);
        int green = (int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF);
        int blue = (int)(a & 0xFF) - (int)(b & 0xFF);
        return (red * red) + (green * green) + (blue * blue) < DistinctColors * DistinctColors;
    }

    private void BuildRow(int y, ReadOnlySpan<TerminalCell> cells)
    {
        while (_rows.Count <= y) _rows.Add(new RowLayer());
        while (_cells.Count <= y) _cells.Add([]);

        if (_cells[y].Length != cells.Length) _cells[y] = new TerminalCell[cells.Length];
        cells.CopyTo(_cells[y]);

        // The row is made again as the engine has it, which is not how a line being typed was painted.
        if (_paintedRows.Remove(y)) _paintedKey = null;

        LayOut(_rows[y], cells);
    }

    private void LayOut(RowLayer row, ReadOnlySpan<TerminalCell> cells)
    {
        row.Clear();
        BuildBackgrounds(row, cells);
        if (highlights.Count > 0) BuildHighlights(row, cells);
        BuildText(row, cells);
        BuildLines(row, cells);
    }

    /// <summary>
    /// Colors the command line being typed by what its parts are. The rows it is on are laid out again from a copy of
    /// their cells with the colors changed, and only when the line, or how to color it, is not what was painted last
    /// time. Text a program has colored itself, as the shell does with a suggestion, is left alone.
    /// </summary>
    private void PaintInput(TerminalFrame frame, InputHighlight? input, bool cursorShown)
    {
        if (input is null || !cursorShown || input.StartRow < 0 || input.StartRow > frame.CursorY || frame.CursorY >= _cells.Count)
        {
            Unpaint();
            return;
        }

        // The line as text, with the cell each of its characters is in. A row that is full goes on in the next one.
        var text = new StringBuilder();
        var where = new List<(int Row, int Column)>();
        int rows = Math.Min(frame.Rows, _cells.Count);
        int column = input.StartColumn;

        for (int row = input.StartRow; row < rows && row - input.StartRow < MaxInputRows && text.Length < MaxInputChars; row++, column = 0)
        {
            TerminalCell[] cells = _cells[row];
            int last = -1;
            for (int x = cells.Length - 1; x >= 0; x--)
            {
                if (cells[x].Codepoint is 0 or ' ') continue;
                last = x;
                break;
            }

            // What was typed and then left as spaces is part of the line up to the cursor.
            int stop = row == frame.CursorY ? Math.Max(last, frame.CursorX - 1) : last;
            bool previousWide = false;
            for (int x = column; x <= stop && x < cells.Length; x++)
            {
                TerminalCell cell = cells[x];
                if (cell.Codepoint == 0 && previousWide)
                {
                    previousWide = false;
                    continue;
                }

                string piece = cell.Cluster ?? (cell.Codepoint == 0 ? " " : char.ConvertFromUtf32((int)Math.Min(cell.Codepoint, 0x10FFFF)));
                for (int k = 0; k < piece.Length; k++) where.Add((row, x));
                text.Append(piece);
                previousWide = cell.Codepoint != 0 && IsWide((int)cell.Codepoint);
            }

            bool full = last == cells.Length - 1;
            if (row >= frame.CursorY && !full) break;
        }

        if (text.Length == 0)
        {
            Unpaint();
            return;
        }

        string line = text.ToString();
        string key = $"{input.Version}|{input.StartRow}|{input.StartColumn}|{frame.Foreground:x}|{line}";
        if (key == _paintedKey) return;

        Unpaint();
        _paintedKey = key;

        Dictionary<int, TerminalCell[]>? changed = null;
        foreach (HighlightSpan span in input.Spans(line))
        {
            if (input.ColorOf(span.Kind) is not uint color) continue;

            for (int i = span.Start; i < span.Start + span.Length && i < where.Count; i++)
            {
                (int row, int x) = where[i];
                if (_cells[row][x].Foreground != frame.Foreground || (_cells[row][x].Flags & CellFlags.Faint) != 0) continue;

                changed ??= [];
                if (!changed.TryGetValue(row, out TerminalCell[]? copy)) changed[row] = copy = (TerminalCell[])_cells[row].Clone();
                copy[x].Foreground = color;
            }
        }

        if (changed is null) return;

        foreach ((int row, TerminalCell[] cells) in changed)
        {
            LayOut(_rows[row], cells);
            _paintedRows.Add(row);
        }
    }

    // Puts back the rows that were painted, as the engine gave them.
    private void Unpaint()
    {
        if (_paintedRows.Count > 0)
        {
            foreach (int row in _paintedRows)
                if (row < _rows.Count && row < _cells.Count) LayOut(_rows[row], _cells[row]);
            _paintedRows.Clear();
        }

        _paintedKey = null;
    }

    private void BuildHighlights(RowLayer row, ReadOnlySpan<TerminalCell> cells)
    {
        if (_rowText.Length < cells.Length) _rowText = new char[cells.Length];

        // One character per cell, so a match's position in the text is its column.
        Span<char> text = _rowText.AsSpan(0, cells.Length);
        bool any = false;
        for (int i = 0; i < cells.Length; i++)
        {
            uint codepoint = cells[i].Codepoint;
            text[i] = codepoint == 0 ? ' ' : codepoint > char.MaxValue ? '\uFFFD' : (char)codepoint;
            any |= codepoint is not (0 or ' ');
        }

        if (!any) return;

        foreach (var (pattern, color) in highlights)
            foreach (ValueMatch match in pattern.EnumerateMatches(text))
                row.Highlights.Add((match.Index * fonts.CellWidth, match.Length * fonts.CellWidth, color));
    }

    private void BuildBackgrounds(RowLayer row, ReadOnlySpan<TerminalCell> cells)
    {
        for (int x = 0; x < cells.Length;)
        {
            if ((cells[x].Flags & CellFlags.HasBackground) == 0)
            {
                x++;
                continue;
            }

            uint color = cells[x].Background;
            int start = x;
            while (x < cells.Length && (cells[x].Flags & CellFlags.HasBackground) != 0 && cells[x].Background == color) x++;
            row.Backgrounds.Add((start * fonts.CellWidth, (x - start) * fonts.CellWidth, color));
        }
    }

    private void BuildText(RowLayer row, ReadOnlySpan<TerminalCell> cells)
    {
        uint blobColor = 0;
        SKFont? runFont = null;
        int count = 0;
        int lastColumn = int.MinValue;
        bool textUsable = true;
        bool blobOpen = false;

        void FlushRun()
        {
            if (count == 0) return;
            if (ligatures && textUsable && count >= 2 && HasOperatorPair(_runText.AsSpan(0, count)))
                fonts.ApplyLigatures(new string(_runText, 0, count), runFont!, _glyphs.AsSpan(0, count));

            SKHorizontalRunBuffer run = _builder.AllocateHorizontalRun(runFont!, count, fonts.Baseline);
            _glyphs.AsSpan(0, count).CopyTo(run.Glyphs);
            _positions.AsSpan(0, count).CopyTo(run.Positions);
            count = 0;
            textUsable = true;
        }

        void FlushBlob()
        {
            FlushRun();
            if (!blobOpen) return;
            blobOpen = false;
            SKTextBlob? blob = _builder.Build();
            if (blob is not null) row.Text.Add((blob, blobColor));
        }

        if (_glyphs.Length < cells.Length)
        {
            _glyphs = new ushort[cells.Length];
            _positions = new float[cells.Length];
            _runText = new char[cells.Length];
        }

        for (int x = 0; x < cells.Length; x++)
        {
            ref readonly TerminalCell cell = ref cells[x];
            if (cell.Codepoint == 0 || cell.Codepoint == ' ' || (cell.Flags & CellFlags.Invisible) != 0) continue;

            bool bold = (cell.Flags & CellFlags.Bold) != 0;
            bool italic = (cell.Flags & CellFlags.Italic) != 0;
            uint alpha = (cell.Flags & CellFlags.Faint) != 0 ? 0x99000000u : 0xFF000000u;
            uint color = alpha | cell.Foreground;
            var (font, glyph) = fonts.Resolve(cell.Codepoint, bold, italic);

            if (cell.Cluster is not null)
            {
                FlushBlob();
                AddCluster(row, cell.Cluster, font, x * fonts.CellWidth, color);
                continue;
            }

            if (blobOpen && color != blobColor) FlushBlob();
            // Ligatures only form between neighbouring cells.
            if (runFont != font || (ligatures && x != lastColumn + 1)) FlushRun();

            blobOpen = true;
            lastColumn = x;
            blobColor = color;
            runFont = font;
            if (cell.Codepoint > char.MaxValue) textUsable = false;
            else _runText[count] = (char)cell.Codepoint;
            _glyphs[count] = glyph;
            _positions[count] = x * fonts.CellWidth;
            count++;
        }

        FlushBlob();
    }

    private static bool HasOperatorPair(ReadOnlySpan<char> text)
    {
        const string Operators = @"!#$%&*+-./:;<=>?@\^_|~";
        for (int i = 1; i < text.Length; i++)
            if (Operators.Contains(text[i]) && Operators.Contains(text[i - 1])) return true;
        return false;
    }

    private void AddCluster(RowLayer row, string cluster, SKFont font, float left, uint color)
    {
        var (glyphs, positions) = fonts.Shape(cluster, font);
        if (glyphs.Length == 0) return;

        SKPositionedRunBuffer run = _builder.AllocatePositionedRun(font, glyphs.Length);
        glyphs.CopyTo(run.Glyphs);
        for (int i = 0; i < positions.Length; i++) run.Positions[i] = new SKPoint(left + positions[i].X, fonts.Baseline + positions[i].Y);

        if (_builder.Build() is SKTextBlob blob) row.Text.Add((blob, color));
    }

    private void BuildLines(RowLayer row, ReadOnlySpan<TerminalCell> cells)
    {
        float underline = fonts.Baseline + fonts.UnderlineOffset;
        float strike = fonts.Baseline * 0.65f;

        for (int x = 0; x < cells.Length; x++)
        {
            ref readonly TerminalCell cell = ref cells[x];
            float left = x * fonts.CellWidth;

            if (cell.Underline != UnderlineStyle.None) row.Lines.Add((left, fonts.CellWidth, underline, cell.UnderlineColor));
            if (cell.Underline == UnderlineStyle.Double) row.Lines.Add((left, fonts.CellWidth, underline + 2, cell.UnderlineColor));
            if ((cell.Flags & CellFlags.Strikethrough) != 0) row.Lines.Add((left, fonts.CellWidth, strike, cell.Foreground));
            if ((cell.Flags & CellFlags.Overline) != 0) row.Lines.Add((left, fonts.CellWidth, 0, cell.Foreground));
        }
    }

    private static SKColor Opaque(uint rgb) => new(0xFF000000u | rgb);

    public void Dispose()
    {
        foreach (RowLayer row in _rows) row.Clear();
        foreach (var (_, image) in _imageCache.Values) image.Dispose();
        _imageCache.Clear();
        _imagePaint.Dispose();
        _paint.Dispose();
        _builder.Dispose();
    }
}

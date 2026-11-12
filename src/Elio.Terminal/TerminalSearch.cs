using System.Diagnostics;
using System.Text.RegularExpressions;
using Elio.Terminal.Native;

namespace Elio.Terminal;

/// <summary>A run of cells on one row of the screen that a search matched.</summary>
/// <param name="Row">The row on screen, counting from 0 at the top.</param>
/// <param name="Length">How many cells, which is two for each wide character.</param>
/// <param name="Current">Part of the match the search is on, which is drawn so that it stands out.</param>
public readonly record struct SearchHighlight(int Row, int Column, int Length, bool Current);

/// <summary>
/// Rows of the terminal as text, a line for each line the program wrote: rows the terminal wrapped are
/// joined back together. Remembers which cell each character came from, so that what is found in the text
/// can be shown on the grid.
/// </summary>
internal sealed class SearchText(int columns)
{
    private char[] _text = new char[4096];
    private int _length;

    // Where each line starts in the text, with one more entry at the end for where the last one stops.
    private readonly List<int> _lineStarts = [];
    private readonly List<long> _lineRows = [];

    // Only for lines where characters and cells do not line up one to one: wide characters, characters
    // made of several code points, and anything outside the basic plane. Most lines need none, and with
    // tens of thousands of rows that is most of the memory saved.
    private readonly Dictionary<int, int[]> _maps = [];

    private int[] _map = new int[256];
    private bool _open;
    private bool _plain;
    private int _lineStart;
    private long _lineRow;
    private int _rowInLine;

    public int Columns => columns;

    public int Lines => _lineRows.Count;

    public ReadOnlySpan<char> Line(int line) => _text.AsSpan(_lineStarts[line], _lineStarts[line + 1] - _lineStarts[line]);

    public void StartRow(long row)
    {
        if (_open)
        {
            _rowInLine++;
            return;
        }

        _open = true;
        _plain = true;
        _lineStart = _length;
        _lineRow = row;
        _rowInLine = 0;
    }

    public void Add(uint codepoint, int column, bool wide)
    {
        // Nearly every cell is one plain character, so that case skips the general one below.
        int cell = (_rowInLine * columns) + column;
        if (!wide && codepoint < 0xD800 && _length - _lineStart == cell) Append(codepoint == 0 ? ' ' : (char)codepoint, cell, wide: false);
        else Add(new ReadOnlySpan<uint>(in codepoint), column, wide);
    }

    /// <summary>Adds the character in one cell. An empty cell reads as a space.</summary>
    public void Add(ReadOnlySpan<uint> codepoints, int column, bool wide)
    {
        int cell = (_rowInLine * columns) + column;
        int at = _length - _lineStart;
        if (wide || at != cell || codepoints.Length != 1 || codepoints[0] > char.MaxValue) _plain = false;

        foreach (uint codepoint in codepoints)
        {
            // What a cell holds comes from the program, and not all of it is a character.
            uint value = codepoint == 0 ? ' ' : codepoint > 0x10FFFF || codepoint is >= 0xD800 and <= 0xDFFF ? '�' : codepoint;
            if (value <= char.MaxValue)
            {
                Append((char)value, cell, wide);
            }
            else
            {
                value -= 0x10000;
                Append((char)(0xD800 + (value >> 10)), cell, wide);
                Append((char)(0xDC00 + (value & 0x3FF)), cell, wide);
            }
        }
    }

    private void Append(char value, int cell, bool wide)
    {
        if (_length == _text.Length) Array.Resize(ref _text, _text.Length * 2);

        int at = _length - _lineStart;
        if (at == _map.Length) Array.Resize(ref _map, _map.Length * 2);

        _map[at] = (cell << 1) | (wide ? 1 : 0);
        _text[_length++] = value;
    }

    /// <param name="wrapped">The line goes on in the next row.</param>
    /// <param name="trim">Leave out the blanks after the last character, so that a pattern can find the end of the line.</param>
    public void EndRow(bool wrapped, bool trim)
    {
        if (wrapped || !_open) return;

        if (trim)
        {
            while (_length > _lineStart && _text[_length - 1] == ' ') _length--;
        }

        if (!_plain) _maps[_lineRows.Count] = _map.AsSpan(0, _length - _lineStart).ToArray();
        _lineStarts.Add(_lineStart);
        _lineRows.Add(_lineRow);
        _open = false;
    }

    public void Finish(bool trim)
    {
        // The rows asked for can end in the middle of a line.
        EndRow(wrapped: false, trim);
        _lineStarts.Add(_length);
    }

    /// <summary>The cell a character of a line is in, and whether it takes two.</summary>
    public (long Row, int Column, bool Wide) Cell(int line, int index)
    {
        int packed = _maps.TryGetValue(line, out int[]? map) ? map[index] : index << 1;
        int cell = packed >> 1;
        return (_lineRows[line] + (cell / columns), cell % columns, (packed & 1) != 0);
    }

    /// <summary>The first and the last cell of a piece of a line.</summary>
    public (long Row, int Column, long EndRow, int EndColumn) Cells(int line, int index, int length)
    {
        var (row, column, _) = Cell(line, index);
        var (endRow, endColumn, wide) = Cell(line, index + length - 1);
        return (row, column, endRow, wide ? Math.Min(endColumn + 1, columns - 1) : endColumn);
    }
}

// The part of a session that searches with regular expressions. The engine's own search only knows plain text.
public sealed partial class TerminalSession
{
    // A pattern is matched against one line at a time, and no line gets longer than this to answer.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // The whole search runs on the thread the user is typing on.
    private static readonly TimeSpan SearchBudget = TimeSpan.FromMilliseconds(1500);

    // Finding what to highlight happens while drawing a frame.
    private static readonly TimeSpan HighlightBudget = TimeSpan.FromMilliseconds(30);

    // A pattern like "." matches every character there is. Past this many nobody is counting.
    private const int MaxMatches = 500_000;

    // How far outside the rows asked for to follow a wrapped line.
    private const int MaxLineReach = 200;

    private readonly record struct MatchSpan(int Line, int Index, int Length);

    /// <summary>What drawing needs to know about the search. Replaced as a whole, so it can be read without waiting.</summary>
    private sealed record RegexState(Regex Regex, long CurrentRow, int CurrentColumn, int Serial);

    // Held for a whole search. Always taken before the terminal's own lock, never after.
    private readonly Lock _searchGate = new();

    // Counts everything that may have changed what the terminal holds. Guarded by the terminal's lock.
    private long _contentVersion;

    private string? _regexPattern;
    private bool _regexIgnoreCase;
    private Regex? _regex;
    private SearchText? _searchText;
    private long _searchTextVersion;
    private List<MatchSpan>? _matches;
    private bool _searchTimedOut;
    private (long Row, int Column)? _currentMatch;
    private int _regexSerial;
    private volatile RegexState? _regexState;

    // Only touched by whoever draws.
    private readonly List<SearchHighlight> _highlights = [];
    private (int Serial, long Version, long Offset, int Visible) _highlightsFor;
    private int _highlightsBrokenFor;

    /// <summary>
    /// Looks for a regular expression on the screen and in the history, and moves to one of the places it
    /// matched, the way <see cref="Find"/> does for plain text. A pattern is matched against one line at a
    /// time, with rows the terminal wrapped joined back into the line they were. Every match on screen is
    /// reported by <see cref="ReadSearchHighlights"/>, and the one the search is on also becomes the selection.
    /// </summary>
    /// <param name="pattern">A .NET regular expression. Empty ends the search.</param>
    /// <param name="older">Whether to move towards older output. The first call lands on the newest match.</param>
    /// <returns>
    /// Where the search stands. <see cref="SearchResult.Invalid"/> is set when the pattern cannot be used,
    /// and <see cref="SearchResult.TimedOut"/> when it took too long to match. Never throws for a bad pattern.
    /// </returns>
    public SearchResult FindRegex(string pattern, bool older = true, bool ignoreCase = true)
    {
        SearchResult result = FindRegexCore(pattern, older, ignoreCase);
        SignalChanged();
        return result;
    }

    private unsafe SearchResult FindRegexCore(string pattern, bool older, bool ignoreCase)
    {
        lock (_searchGate)
        {
            lock (_terminalGate)
            {
                if (_disposed != 0) return default;

                // The plain search would otherwise keep its own idea of what is selected.
                if (_search != 0) Check(Ghostty.SearchSet(_search, Ghostty.SearchOptNeedle, null));
            }

            if (pattern.Length == 0)
            {
                bool searching = _regexPattern is not null;
                ForgetRegex();
                if (searching) SelectNothing();
                return default;
            }

            if (pattern != _regexPattern || ignoreCase != _regexIgnoreCase)
            {
                ForgetRegex();
                _regexPattern = pattern;
                _regexIgnoreCase = ignoreCase;
                _regex = Compile(pattern, ignoreCase);
            }

            if (_regex is null)
            {
                SelectNothing();
                return new SearchResult(0, 0) { Invalid = true };
            }

            // Reading tens of thousands of rows is the slow part, so the text is kept for as long as it stays true.
            lock (_terminalGate)
            {
                if (_disposed != 0) return default;
                if (_searchText is null || _searchTextVersion != _contentVersion)
                {
                    Ghostty.Scrollbar bar = default;
                    if (Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataScrollbar, &bar) != Ghostty.Success) return default;

                    _searchText = ReadText(Ghostty.PointTagScreen, 0, (long)bar.Total - 1, wholeLines: false);
                    _searchTextVersion = _contentVersion;
                    _matches = null;
                    _searchTimedOut = false;
                }
            }

            // Matching does not need the terminal, so output keeps flowing while it runs.
            if (_matches is null && !_searchTimedOut)
            {
                var matches = new List<MatchSpan>();
                if (FindAll(_regex, _searchText, matches)) _matches = matches;
                else _searchTimedOut = true;
            }

            if (_matches is not List<MatchSpan> found)
            {
                _regexState = null;
                SelectNothing();
                return new SearchResult(0, 0) { TimedOut = true };
            }

            if (found.Count == 0)
            {
                _currentMatch = null;
                _regexState = new RegexState(_regex, -1, -1, ++_regexSerial);
                SelectNothing();
                return default;
            }

            int index = NextMatch(_searchText, found, _currentMatch, older);
            MatchSpan match = found[index];
            var (row, column, endRow, endColumn) = _searchText.Cells(match.Line, match.Index, match.Length);

            _currentMatch = (row, column);
            _regexState = new RegexState(_regex, row, column, ++_regexSerial);
            ShowMatch(row, column, endRow, endColumn);
            return new SearchResult(found.Count - index, found.Count);
        }
    }

    /// <summary>Called with the search lock held.</summary>
    private void ForgetRegex()
    {
        _regexPattern = null;
        _regex = null;
        _searchText = null;
        _matches = null;
        _searchTimedOut = false;
        _currentMatch = null;
        _regexState = null;
    }

    private static Regex? Compile(string pattern, bool ignoreCase)
    {
        RegexOptions options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        try
        {
            // This way of matching cannot stall whatever the pattern and the text are.
            return new Regex(pattern, options | RegexOptions.NonBacktracking, MatchTimeout);
        }
        catch (NotSupportedException)
        {
            // It cannot look ahead or behind, or refer back to a group. The usual way can, kept in check by the timeout.
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            return new Regex(pattern, options, MatchTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <returns>False if the pattern took too long.</returns>
    private static bool FindAll(Regex regex, SearchText text, List<MatchSpan> into)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            for (int line = 0; line < text.Lines; line++)
            {
                ReadOnlySpan<char> content = text.Line(line);
                if (content.IsEmpty) continue;

                foreach (ValueMatch match in regex.EnumerateMatches(content))
                {
                    // A pattern that can match nothing does so everywhere, and there is nothing to show for it.
                    if (match.Length == 0) continue;

                    into.Add(new MatchSpan(line, match.Index, match.Length));
                    if (into.Count >= MaxMatches) return true;
                }

                if (Stopwatch.GetElapsedTime(started) > SearchBudget) return false;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Which match to move to. The matches run from oldest to newest. The search remembers where it was
    /// rather than which match it was on, because new output changes how many there are.
    /// </summary>
    private static int NextMatch(SearchText text, List<MatchSpan> matches, (long Row, int Column)? current, bool older)
    {
        if (current is not var (row, column)) return older ? matches.Count - 1 : 0;

        // The first match at or after where the search was.
        int low = 0, high = matches.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            var (matchRow, matchColumn, _) = text.Cell(matches[middle].Line, matches[middle].Index);
            if (matchRow < row || (matchRow == row && matchColumn < column)) low = middle + 1;
            else high = middle;
        }

        if (older) return low > 0 ? low - 1 : matches.Count - 1;

        if (low < matches.Count)
        {
            var (matchRow, matchColumn, _) = text.Cell(matches[low].Line, matches[low].Index);
            if (matchRow == row && matchColumn == column) low++;
        }

        return low < matches.Count ? low : 0;
    }

    /// <summary>Brings a match into view and makes it the selection, so it is drawn like one and can be copied like one.</summary>
    private unsafe void ShowMatch(long row, int column, long endRow, int endColumn)
    {
        lock (_terminalGate)
        {
            if (_disposed != 0) return;

            Ghostty.Scrollbar bar = default;
            if (Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataScrollbar, &bar) == Ghostty.Success
                && (row < (long)bar.Offset || endRow >= (long)(bar.Offset + bar.Length)))
            {
                // In the middle of the screen, where what comes before and after it can be read too.
                var behavior = new Ghostty.ScrollViewport { Tag = Ghostty.ScrollViewportRow, Delta = (nint)Math.Max(0, row - ((long)bar.Length / 2)) };
                Check(Ghostty.TerminalScrollViewport(_terminal, behavior));
            }

            // Output that arrived while matching can have moved things. Then there is nothing safe to select.
            Ghostty.Selection selection = default;
            bool found = Ghostty.TerminalGridRef(_terminal, new Ghostty.Point { Tag = Ghostty.PointTagScreen, X = (ushort)column, Y = (uint)row }, &selection.Start) == Ghostty.Success
                && Ghostty.TerminalGridRef(_terminal, new Ghostty.Point { Tag = Ghostty.PointTagScreen, X = (ushort)endColumn, Y = (uint)endRow }, &selection.End) == Ghostty.Success;
            Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptSelection, found ? &selection : null));
        }
    }

    private unsafe void SelectNothing()
    {
        lock (_terminalGate)
        {
            if (_disposed != 0) return;
            Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptSelection, null));
        }
    }

    /// <summary>
    /// The matches of the current <see cref="FindRegex"/> search that are on screen, for drawing over the
    /// rows. Empty when no such search is running. Looks only at the rows on screen, and only when they or
    /// the search changed, so it is cheap enough to call for every frame. Meant to be called by whoever
    /// draws, from one thread.
    /// </summary>
    public unsafe void ReadSearchHighlights(List<SearchHighlight> into)
    {
        into.Clear();
        if (_regexState is not RegexState state) return;

        SearchText text;
        long offset;
        int visible;
        lock (_terminalGate)
        {
            if (_disposed != 0) return;

            Ghostty.Scrollbar bar = default;
            if (Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataScrollbar, &bar) != Ghostty.Success) return;

            (offset, visible) = ((long)bar.Offset, (int)bar.Length);
            var key = (state.Serial, _contentVersion, offset, visible);
            if (key == _highlightsFor)
            {
                into.AddRange(_highlights);
                return;
            }

            _highlightsFor = key;
            text = ReadText(Ghostty.PointTagScreen, offset, offset + visible - 1, wholeLines: true);
        }

        _highlights.Clear();
        if (state.Serial != _highlightsBrokenFor)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                for (int line = 0; line < text.Lines && Stopwatch.GetElapsedTime(started) < HighlightBudget; line++)
                {
                    foreach (ValueMatch match in state.Regex.EnumerateMatches(text.Line(line)))
                    {
                        if (match.Length > 0) AddHighlights(text, line, match, offset, visible, state);
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Not worth stalling every frame for. The search itself says that the pattern is too slow.
                _highlightsBrokenFor = state.Serial;
                _highlights.Clear();
            }
        }

        into.AddRange(_highlights);
    }

    private void AddHighlights(SearchText text, int line, ValueMatch match, long offset, int visible, RegexState state)
    {
        var (row, column, endRow, endColumn) = text.Cells(line, match.Index, match.Length);
        bool current = row == state.CurrentRow && column == state.CurrentColumn;

        // A match in a wrapped line can run over several rows, and some of them may be off screen.
        for (long y = Math.Max(row, offset); y <= Math.Min(endRow, offset + visible - 1); y++)
        {
            int from = y == row ? column : 0;
            int to = y == endRow ? endColumn : text.Columns - 1;
            if (to >= from) _highlights.Add(new SearchHighlight((int)(y - offset), from, to - from + 1, current));
        }
    }

    /// <summary>
    /// What is on the cursor's row to the left of the cursor, or null if that cannot be told. It is how
    /// to check that what was typed is what the shell is showing.
    /// </summary>
    public unsafe string? TextBeforeCursor()
    {
        lock (_terminalGate)
        {
            if (_disposed != 0) return null;

            ushort x = 0, y = 0;
            if (Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataCursorX, &x) != Ghostty.Success
                || Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataCursorY, &y) != Ghostty.Success) return null;

            SearchText text = ReadText(Ghostty.PointTagActive, y, y, wholeLines: false, trim: false, columnLimit: x);
            return text.Lines == 0 ? null : text.Line(0).ToString();
        }
    }

    /// <summary>
    /// Whether the cursor's row goes on after the cursor. At a prompt that is the shell offering a completion of its own,
    /// as PowerShell and fish do, drawn where a suggestion from here would go.
    /// </summary>
    public unsafe bool HasTextAfterCursor()
    {
        lock (_terminalGate)
        {
            if (_disposed != 0) return false;

            ushort x = 0, y = 0;
            if (Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataCursorX, &x) != Ghostty.Success
                || Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataCursorY, &y) != Ghostty.Success) return false;

            SearchText whole = ReadText(Ghostty.PointTagActive, y, y, wholeLines: false);
            SearchText before = ReadText(Ghostty.PointTagActive, y, y, wholeLines: false, trim: false, columnLimit: x);
            if (whole.Lines == 0) return false;

            int upToCursor = before.Lines == 0 ? 0 : before.Line(0).ToString().Length;
            return whole.Line(0).ToString().TrimEnd().Length > upToCursor;
        }
    }

    /// <summary>The cursor's column and row on the screen, counted from zero, or null once the terminal is gone.</summary>
    public unsafe (int Column, int Row)? CursorCell()
    {
        lock (_terminalGate)
        {
            if (_disposed != 0) return null;

            ushort x = 0, y = 0;
            if (Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataCursorX, &x) != Ghostty.Success
                || Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataCursorY, &y) != Ghostty.Success) return null;
            return (x, y);
        }
    }

    /// <summary>Reads rows of the terminal as text. Called with the terminal held.</summary>
    /// <param name="tag">How the rows are counted: from the top of the history, or from the top of the screen the cursor moves on.</param>
    /// <param name="wholeLines">Also read the rows before and after that belong to a line the terminal wrapped.</param>
    private unsafe SearchText ReadText(int tag, long first, long last, bool wholeLines, bool trim = true, int columnLimit = int.MaxValue)
    {
        // Asked from the engine rather than remembered: a resize on its way must not make this read past a row.
        ushort columns = 0;
        Check(Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataCols, &columns));

        var text = new SearchText(columns);
        uint* cluster = stackalloc uint[MaxCluster];
        uint codepoint = 0;
        int content = 0, wide = 0;
        int* keys = stackalloc int[] { Ghostty.GridCellDataCodepoint, Ghostty.GridCellDataContentTag, Ghostty.GridCellDataWide };
        void** values = stackalloc void*[] { &codepoint, &content, &wide };

        Ghostty.GridRef reference = default;
        if (wholeLines)
        {
            for (int reach = 0; reach < MaxLineReach && first > 0 && RowAt(tag, first - 1, &reference) && Wraps(&reference); reach++) first--;
        }

        bool wrapped = false;
        int limit = Math.Min(columns, columnLimit);
        for (long y = first; y <= last || (wholeLines && wrapped && y <= last + MaxLineReach); y++)
        {
            if (!RowAt(tag, y, &reference)) break;

            wrapped = Wraps(&reference);
            text.StartRow(y);

            for (int x = 0; x < limit; x++)
            {
                // The reference names a row of the engine's storage and a column in it, so the same one serves the whole row.
                reference.X = (ushort)x;
                ulong cell = 0;
                if (Ghostty.GridRefCell(&reference, &cell) != Ghostty.Success) break;

                // Nothing was ever written here. Much of a terminal is such cells, and they need no second look.
                if (cell == 0)
                {
                    text.Add(0, x, wide: false);
                    continue;
                }

                if (Ghostty.GridCellGetMulti(cell, 3, keys, values, null) != Ghostty.Success) break;

                // The second half of a wide character, or the gap left when one did not fit at the end of a row.
                if (wide is Ghostty.CellWideSpacerTail or Ghostty.CellWideSpacerHead) continue;

                nuint count = 0;
                if (content == Ghostty.CellContentGrapheme && Ghostty.GridRefGraphemes(&reference, cluster, MaxCluster, &count) == Ghostty.Success && count > 0)
                    text.Add(new ReadOnlySpan<uint>(cluster, (int)count), x, wide == Ghostty.CellWideWide);
                else
                    text.Add(codepoint, x, wide == Ghostty.CellWideWide);
            }

            text.EndRow(wrapped, trim);
        }

        text.Finish(trim);
        return text;
    }

    private unsafe bool RowAt(int tag, long row, Ghostty.GridRef* reference) =>
        Ghostty.TerminalGridRef(_terminal, new Ghostty.Point { Tag = tag, X = 0, Y = (uint)row }, reference) == Ghostty.Success;

    private static unsafe bool Wraps(Ghostty.GridRef* reference)
    {
        ulong row = 0;
        byte wraps = 0;
        return Ghostty.GridRefRow(reference, &row) == Ghostty.Success && Ghostty.GridRowGet(row, Ghostty.GridRowDataWrap, &wraps) == Ghostty.Success && wraps != 0;
    }
}

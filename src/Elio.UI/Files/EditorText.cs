using System.Text;
using System.Text.RegularExpressions;

namespace Elio.UI.Files;

/// <summary>A stretch of a text, by where it begins and how long it is.</summary>
internal readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>Replaces <see cref="Length"/> characters from <see cref="Start"/> with <see cref="Replacement"/>, and says what to choose afterwards.</summary>
internal sealed record TextEdit(int Start, int Length, string Replacement, int SelectionStart, int SelectionEnd);

/// <summary>What a search of the text found. Never thrown: a pattern that is no good says so here.</summary>
internal sealed record FindResult(List<TextSpan> Matches, bool Capped, bool Invalid)
{
    public static FindResult None { get; } = new([], false, false);
}

/// <summary>
/// What the editor does with text that does not depend on the screen: where a line begins, how a new line is indented,
/// what a search finds. Texts here only ever have "\n" for line endings.
/// </summary>
internal static class EditorText
{
    /// <summary>The most matches one search keeps. A text with more than this is still searched, but the count stops here.</summary>
    public const int MaxMatches = 10_000;

    private const int IndentSampleLines = 600;
    private static readonly TimeSpan PatternTimeLimit = TimeSpan.FromSeconds(1);

    /// <summary>The line and the column of a place in the text, both counted from 1.</summary>
    public static (int Line, int Column) LineColumn(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);
        ReadOnlySpan<char> before = text.AsSpan(0, index);
        int line = before.Count('\n') + 1;
        int lastBreak = before.LastIndexOf('\n');
        return (line, index - lastBreak);
    }

    /// <summary>How many lines the text has. The empty text has one.</summary>
    public static int LineCount(string text) => text.AsSpan().Count('\n') + 1;

    /// <summary>Where a line begins. A line past the end is the last one, and one before the first is the first.</summary>
    public static int IndexOfLine(string text, int line)
    {
        int index = 0;
        for (int current = 1; current < line; current++)
        {
            int next = text.IndexOf('\n', index);
            if (next < 0) return index;
            index = next + 1;
        }

        return index;
    }

    /// <summary>The place of a line and a column in the text, kept inside that line.</summary>
    public static int IndexAt(string text, int line, int column)
    {
        int start = IndexOfLine(text, line);
        int end = text.IndexOf('\n', start);
        if (end < 0) end = text.Length;
        return Math.Clamp(start + Math.Max(column, 1) - 1, start, end);
    }

    private static int LineStart(string text, int index) => index <= 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;

    private static int LineEnd(string text, int index)
    {
        int end = text.IndexOf('\n', Math.Min(index, text.Length));
        return end < 0 ? text.Length : end;
    }

    /// <summary>
    /// What Enter puts in: the line break, and the white space the line began with, as far as the cursor is past it.
    /// A line that ends in an opening bracket asks for one more level.
    /// </summary>
    public static string NewLineWithIndent(string text, int caret, string unit)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        int start = LineStart(text, caret);
        int whiteEnd = start;
        while (whiteEnd < caret && text[whiteEnd] is ' ' or '\t') whiteEnd++;

        string indent = text[start..whiteEnd];

        // The last thing before the cursor that is not a space decides whether the block goes on a level.
        int last = caret - 1;
        while (last >= start && text[last] is ' ' or '\t') last--;
        bool opens = last >= start && text[last] is '{' or '(' or '[' or ':';
        return "\n" + indent + (opens ? unit : string.Empty);
    }

    /// <summary>
    /// How the file indents: a tab where lines mostly begin with one, two spaces where lines are indented by 2, 6 and
    /// 10 as well as by 4 and 8, four spaces otherwise.
    /// </summary>
    public static string DetectIndent(string text)
    {
        int tabs = 0;
        int twos = 0;
        int fours = 0;

        int index = 0;
        for (int lines = 0; lines < IndentSampleLines && index < text.Length; lines++)
        {
            int end = LineEnd(text, index);
            if (end > index)
            {
                if (text[index] == '\t')
                {
                    tabs++;
                }
                else if (text[index] == ' ')
                {
                    int spaces = 0;
                    while (index + spaces < end && text[index + spaces] == ' ') spaces++;

                    // A line of nothing but spaces says nothing about how the file is indented.
                    if (index + spaces < end)
                    {
                        if (spaces % 4 == 2) twos++;
                        else if (spaces % 4 == 0) fours++;
                    }
                }
            }

            index = end + 1;
        }

        if (tabs > twos + fours) return "\t";

        // A few lines lined up with something above them do not make a file two-spaced.
        return twos >= 2 && twos * 3 >= fours ? "  " : "    ";
    }

    /// <summary>What Tab puts in: the unit, or the spaces up to the next tab stop.</summary>
    public static string TabInsertion(string text, int caret, string unit)
    {
        if (unit == "\t") return unit;

        caret = Math.Clamp(caret, 0, text.Length);
        int column = caret - LineStart(text, caret);
        return new string(' ', unit.Length - (column % unit.Length));
    }

    // The lines a selection touches, as the place the first begins and the place the last ends. A selection that ends
    // at the very start of a line does not touch that line.
    private static (int Start, int End) LinesTouched(string text, int selectionStart, int selectionEnd)
    {
        int start = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
        int end = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, text.Length);
        int regionEnd = end > start && text[end - 1] == '\n' ? end - 1 : end;
        return (LineStart(text, start), LineEnd(text, regionEnd));
    }

    /// <summary>Puts one more level in front of every line the selection touches.</summary>
    public static TextEdit Indent(string text, int selectionStart, int selectionEnd, string unit)
    {
        (int from, int to) = LinesTouched(text, selectionStart, selectionEnd);
        string[] lines = text[from..to].Split('\n');

        int added = 0;
        int addedBeforeStart = 0;
        int position = from;
        var result = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) result.Append('\n');

            // Empty lines are left empty rather than made of white space.
            if (lines[i].Length > 0)
            {
                result.Append(unit);
                added += unit.Length;
                if (position <= Math.Min(selectionStart, selectionEnd)) addedBeforeStart += unit.Length;
            }

            result.Append(lines[i]);
            position += lines[i].Length + 1;
        }

        int low = Math.Min(selectionStart, selectionEnd);
        int high = Math.Max(selectionStart, selectionEnd);
        // A selection that began at the start of a line keeps that, so the new indent is part of it.
        return new TextEdit(from, to - from, result.ToString(), low == from ? low : low + addedBeforeStart, high + added);
    }

    /// <summary>Takes one level off the front of every line the selection touches, where there is one to take.</summary>
    public static TextEdit Outdent(string text, int selectionStart, int selectionEnd, string unit)
    {
        (int from, int to) = LinesTouched(text, selectionStart, selectionEnd);
        string[] lines = text[from..to].Split('\n');
        int width = unit == "\t" ? 4 : unit.Length;
        int low = Math.Min(selectionStart, selectionEnd);
        int high = Math.Max(selectionStart, selectionEnd);

        int removedBeforeStart = 0;
        int removed = 0;
        int position = from;
        var result = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) result.Append('\n');

            string line = lines[i];
            int take = 0;
            if (line.StartsWith('\t'))
            {
                take = 1;
            }
            else
            {
                while (take < width && take < line.Length && line[take] == ' ') take++;
            }

            result.Append(line, take, line.Length - take);
            removed += take;

            // How much of what was taken lay before the place the selection begins, which moves that place back by as much.
            removedBeforeStart += Math.Min(take, Math.Max(0, low - position));
            position += line.Length + 1;
        }

        int newStart = Math.Max(from, low - removedBeforeStart);
        return new TextEdit(from, to - from, result.ToString(), newStart, Math.Max(newStart, high - removed));
    }

    private static Regex? PatternOf(string query, bool caseSensitive, bool regex)
    {
        if (query.Length == 0) return null;

        RegexOptions options = RegexOptions.Multiline | RegexOptions.CultureInvariant;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;
        return new Regex(regex ? query : Regex.Escape(query), options, PatternTimeLimit);
    }

    /// <summary>Every place the query is found, up to <see cref="MaxMatches"/>. A match of nothing at all is not one.</summary>
    public static FindResult Find(string text, string query, bool caseSensitive, bool regex)
    {
        if (query.Length == 0) return FindResult.None;

        var matches = new List<TextSpan>();
        try
        {
            if (!regex)
            {
                StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                int index = 0;
                while (index <= text.Length - query.Length && text.IndexOf(query, index, comparison) is int found and >= 0)
                {
                    if (matches.Count == MaxMatches) return new FindResult(matches, Capped: true, Invalid: false);
                    matches.Add(new TextSpan(found, query.Length));
                    index = found + query.Length;
                }

                return new FindResult(matches, Capped: false, Invalid: false);
            }

            Regex? pattern = PatternOf(query, caseSensitive, regex: true);
            for (Match match = pattern!.Match(text); match.Success; match = match.NextMatch())
            {
                if (match.Length == 0) continue;
                if (matches.Count == MaxMatches) return new FindResult(matches, Capped: true, Invalid: false);
                matches.Add(new TextSpan(match.Index, match.Length));
            }

            return new FindResult(matches, Capped: false, Invalid: false);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            // A pattern that is not finished yet, or one that takes forever, is something to say, not something to fall over.
            return new FindResult([], Capped: false, Invalid: true);
        }
    }

    /// <summary>What a match is to be replaced with: the replacement as it is, or with $1 and the like filled in for a pattern.</summary>
    public static string Replacement(string text, TextSpan match, string query, string replacement, bool caseSensitive, bool regex)
    {
        if (!regex) return replacement;

        try
        {
            Match found = PatternOf(query, caseSensitive, regex: true)!.Match(text, match.Start);
            return found.Success && found.Index == match.Start ? found.Result(replacement) : replacement;
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return replacement;
        }
    }

    /// <summary>The text with every match replaced, and how many there were.</summary>
    public static (string Text, int Count, bool Invalid) ReplaceAll(string text, string query, string replacement, bool caseSensitive, bool regex)
    {
        if (query.Length == 0) return (text, 0, false);

        try
        {
            Regex pattern = PatternOf(query, caseSensitive, regex)!;
            int count = 0;
            string result = pattern.Replace(text, match =>
            {
                // Empty matches are skipped here as they are when searching, so the count is the one the bar showed.
                if (match.Length == 0 && regex) return string.Empty;
                count++;
                return regex ? match.Result(replacement) : replacement;
            });
            return (count == 0 ? text : result, count, false);
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return (text, 0, true);
        }
    }

    /// <summary>The first match that begins at or after a place, going round to the first of all when there is none.</summary>
    public static int NextMatch(IReadOnlyList<TextSpan> matches, int from)
    {
        if (matches.Count == 0) return -1;

        int low = 0;
        int high = matches.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (matches[middle].Start < from) low = middle + 1;
            else high = middle;
        }

        return low < matches.Count ? low : 0;
    }

    /// <summary>The last match that begins before a place, going round to the last of all when there is none.</summary>
    public static int PreviousMatch(IReadOnlyList<TextSpan> matches, int before)
    {
        if (matches.Count == 0) return -1;

        int low = 0;
        int high = matches.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (matches[middle].Start < before) low = middle + 1;
            else high = middle;
        }

        return low > 0 ? low - 1 : matches.Count - 1;
    }
}

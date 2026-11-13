using System.Text.RegularExpressions;

namespace Elio.UI.Files;

/// <summary>How the text typed into the search bar is read.</summary>
public enum FileFilterKind
{
    /// <summary>Nothing typed: every name matches.</summary>
    None,

    /// <summary>The text is part of the name, in either case.</summary>
    Text,

    /// <summary>The text has <c>*</c> or <c>?</c> in it: the whole name has to fit, as in <c>*.log</c>.</summary>
    Wildcard,

    /// <summary>The text is a regular expression, found anywhere in the name.</summary>
    Regex,
}

/// <summary>
/// What the search bar asks of a list of names: which ones stay. Plain text finds part of a name in either case,
/// <c>*</c> and <c>?</c> turn it into a pattern for the whole name, and a regular expression is read as one. Nothing
/// here touches a control, so a folder of any size can be sorted through off the UI thread.
/// </summary>
public sealed class FileFilter
{
    // A pattern that cannot finish on a name this short is a mistake, and 100 000 names must not wait on it one by one.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private readonly string _text;
    private readonly Regex? _regex;
    private int _timedOut;

    private FileFilter(FileFilterKind kind, string text, Regex? regex, bool invalid)
    {
        Kind = kind;
        _text = text;
        _regex = regex;
        IsInvalid = invalid;
    }

    /// <summary>The filter that lets everything through.</summary>
    public static FileFilter None { get; } = new(FileFilterKind.None, string.Empty, null, invalid: false);

    public FileFilterKind Kind { get; }

    /// <summary>True when it holds nothing back, either because nothing was typed or because the text is no pattern.</summary>
    public bool IsEmpty => Kind == FileFilterKind.None || IsInvalid;

    /// <summary>The text was meant as a regular expression and is not one.</summary>
    public bool IsInvalid { get; }

    /// <summary>A name took the regular expression too long. What was decided up to then is not to be trusted.</summary>
    public bool TimedOut => Volatile.Read(ref _timedOut) != 0;

    /// <summary>Reads what was typed. Spaces around plain text and patterns are dropped, and those inside a regular expression are not.</summary>
    public static FileFilter Parse(string? text, bool regex)
    {
        if (string.IsNullOrEmpty(text)) return None;

        if (regex)
        {
            try
            {
                return new FileFilter(FileFilterKind.Regex, text, new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout), invalid: false);
            }
            catch (ArgumentException)
            {
                return new FileFilter(FileFilterKind.Regex, text, null, invalid: true);
            }
        }

        string trimmed = text.Trim();
        if (trimmed.Length == 0) return None;

        return trimmed.AsSpan().IndexOfAny('*', '?') >= 0
            ? new FileFilter(FileFilterKind.Wildcard, trimmed, null, invalid: false)
            : new FileFilter(FileFilterKind.Text, trimmed, null, invalid: false);
    }

    /// <summary>Whether a name stays in the list.</summary>
    public bool Matches(string name)
    {
        if (IsEmpty) return true;

        switch (Kind)
        {
            case FileFilterKind.Text:
                return name.Contains(_text, StringComparison.OrdinalIgnoreCase);
            case FileFilterKind.Wildcard:
                return WildcardMatches(_text, name);
            default:
                try
                {
                    return _regex!.IsMatch(name);
                }
                catch (RegexMatchTimeoutException)
                {
                    Interlocked.Exchange(ref _timedOut, 1);
                    return false;
                }
        }
    }

    /// <summary>
    /// Where in a name the text was found, to show it in bold. Only plain text and regular expressions point at a part of a
    /// name: a pattern such as <c>*.log</c> matches all of it.
    /// </summary>
    public bool TryFind(string name, out int start, out int length)
    {
        start = length = 0;
        if (IsEmpty) return false;

        if (Kind == FileFilterKind.Text)
        {
            start = name.IndexOf(_text, StringComparison.OrdinalIgnoreCase);
            length = _text.Length;
            return start >= 0;
        }

        if (Kind != FileFilterKind.Regex) return false;

        try
        {
            Match match = _regex!.Match(name);
            (start, length) = (match.Index, match.Length);
            return match.Success && match.Length > 0;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Which rows of a folder to show and how many there were to choose from.</summary>
    public readonly record struct Result(List<FileRow> Rows, int Candidates, bool TimedOut);

    /// <summary>
    /// The rows that pass, in the order they came. <see cref="Result.Candidates"/> counts those left once hidden ones are
    /// out, so that "12 of 340" compares with what could have been shown.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token was cancelled: a newer search replaced this one.</exception>
    public static Result Apply(IEnumerable<FileRow> rows, FileFilter filter, bool showHidden, CancellationToken cancellation)
    {
        var kept = new List<FileRow>();
        int candidates = 0;
        int seen = 0;

        foreach (FileRow row in rows)
        {
            // Often enough to stop soon, rarely enough not to cost more than the matching.
            if ((++seen & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            if (!showHidden && row.IsHidden) continue;

            candidates++;
            if (filter.Matches(row.Name)) kept.Add(row);

            // A regular expression that gave up on one name would do so on the next, and the rows decided so far are wrong.
            if (filter.TimedOut) return new Result([], candidates, TimedOut: true);
        }

        cancellation.ThrowIfCancellationRequested();
        return new Result(kept, candidates, TimedOut: false);
    }

    // * stands for any run of characters, ? for one. Followed on the way back to the last *, as OpenSSH and the shell do, so
    // that a pattern full of them takes time in proportion to the name and cannot get stuck.
    internal static bool WildcardMatches(string pattern, string name)
    {
        int p = 0, n = 0, star = -1, resume = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                resume = n;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || SameLetter(pattern[p], name[n])))
            {
                p++;
                n++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++resume;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool SameLetter(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}

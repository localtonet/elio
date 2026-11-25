using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Elio.Terminal;

/// <summary>A line of output that matched a rule, and what to type in reply, if the rule says.</summary>
public sealed record TriggerHit(string Line, string? Reply);

/// <summary>
/// Watches what a terminal prints for words the user asked to be told about, such as "BUILD FAILED" or "error:".
/// The output is read as plain text, without colors or cursor movement, line by line, so a match is found however
/// the program split its output into pieces. Each line that matches is reported once.
/// A rule written "pattern => text" answers instead: when the pattern shows, the text is typed, followed by Enter.
/// Such a rule also looks at the line being written, since a question like "Continue? [y/N]" waits on a line of its
/// own that has not ended.
/// </summary>
public sealed class OutputWatcher
{
    /// <summary>What separates a pattern from the reply that is typed when it shows.</summary>
    public const string ReplyArrow = " => ";

    // A line longer than this is a progress bar or a binary file, not something a person reads; only its start is looked at.
    private const int LongestLine = 4096;

    // A pattern comes from the user, but the text comes from whatever runs on the server: a match may never stall on it.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    // A reply is typed at most this often for one rule, so output that repeats its question cannot make a loop of it.
    private static readonly TimeSpan ReplyPause = TimeSpan.FromSeconds(2);

    private enum State
    {
        Text,
        Escape,
        Control,
        OperatingSystem,
        OperatingSystemEscape,
    }

    private sealed record Rule(Regex Pattern, string? Reply)
    {
        public long LastReply = long.MinValue;
    }

    private readonly List<Rule> _rules;
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly Func<long> _now;
    private State _state;
    private bool _returned;

    // Whether the line being written was answered already, so a prompt that is still on screen is answered once.
    private bool _answered;

    /// <summary>Reads the rules, one per entry. A pattern that is not a valid regular expression is taken as plain text.</summary>
    /// <param name="now">Stands in for the clock, in milliseconds, in tests.</param>
    public OutputWatcher(IEnumerable<string> rules, Func<long>? now = null)
    {
        _now = now ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
        _rules = rules
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .Select(Parse)
            .OfType<Rule>()
            .ToList();
    }

    public bool IsEmpty => _rules.Count == 0;

    private static Rule? Parse(string rule)
    {
        int arrow = rule.IndexOf(ReplyArrow, StringComparison.Ordinal);
        string pattern = arrow < 0 ? rule : rule[..arrow].Trim();
        if (pattern.Length == 0) return null;

        // A reply is one line: a line break in it would type a second command nobody wrote down as one.
        string? reply = arrow < 0 ? null : string.Concat(rule[(arrow + ReplyArrow.Length)..].Where(c => !char.IsControl(c)));
        return new Rule(Compile(pattern), reply);
    }

    private static Regex Compile(string pattern)
    {
        const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        try
        {
            return new Regex(pattern, Options | RegexOptions.NonBacktracking, MatchTimeout);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // Not a pattern this engine takes, such as one with a look-around: it is looked for as it is written.
            return new Regex(Regex.Escape(pattern), Options | RegexOptions.NonBacktracking, MatchTimeout);
        }
    }

    /// <summary>Reads a piece of output and gives what matched: lines it completed, and a question waiting on the line being written.</summary>
    public List<TriggerHit> Feed(ReadOnlySpan<byte> data)
    {
        var found = new List<TriggerHit>();
        if (IsEmpty || data.IsEmpty) return found;

        char[] chars = new char[_decoder.GetCharCount(data, flush: false)];
        int count = _decoder.GetChars(data, chars, flush: false);
        for (int i = 0; i < count; i++)
        {
            char c = chars[i];
            switch (_state)
            {
                case State.Text:
                    if (c == '\u001b') _state = State.Escape;
                    else if (c == '\n') EndLine(found);
                    else if (c == '\r') _returned = true;
                    else if (!char.IsControl(c) || c == '\t')
                    {
                        // A carriage return followed by more text, rather than a line feed, writes over the line: a progress bar.
                        if (_returned)
                        {
                            _line.Clear();
                            _answered = false;
                        }

                        _returned = false;
                        if (_line.Length < LongestLine) _line.Append(c);
                    }

                    break;
                case State.Escape:
                    _state = c switch
                    {
                        '[' => State.Control,
                        ']' => State.OperatingSystem,
                        _ => State.Text,
                    };
                    break;
                case State.Control:
                    if (c is >= '@' and <= '~') _state = State.Text;
                    break;
                case State.OperatingSystem:
                    if (c == '\u0007') _state = State.Text;
                    else if (c == '\u001b') _state = State.OperatingSystemEscape;
                    break;
                case State.OperatingSystemEscape:
                    _state = State.Text;
                    break;
            }
        }

        // A question waits on its own line, which has not ended: only rules that answer look there, once per line.
        if (!_answered && _line.Length > 0 && Match(_line.ToString(), replyOnly: true) is { } waiting)
        {
            _answered = true;
            found.Add(waiting);
        }

        return found;
    }

    private void EndLine(List<TriggerHit> found)
    {
        string line = _line.ToString();
        bool answered = _answered;
        _line.Clear();
        _returned = false;
        _answered = false;
        if (line.Length == 0) return;

        // A line that was answered while it was being written is not answered again when it ends.
        if (Match(line, replyOnly: false, skipReplies: answered) is { } hit) found.Add(hit);
    }

    private TriggerHit? Match(string line, bool replyOnly, bool skipReplies = false)
    {
        foreach (Rule rule in _rules)
        {
            if (replyOnly && rule.Reply is null) continue;
            if (skipReplies && rule.Reply is not null) continue;

            try
            {
                if (!rule.Pattern.IsMatch(line)) continue;
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (rule.Reply is not null)
            {
                long now = _now();
                if (rule.LastReply != long.MinValue && now - rule.LastReply < ReplyPause.TotalMilliseconds) continue;
                rule.LastReply = now;
            }

            return new TriggerHit(line.Trim(), rule.Reply);
        }

        return null;
    }
}

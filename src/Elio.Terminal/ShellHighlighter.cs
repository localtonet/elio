using System.Collections.Frozen;

namespace Elio.Terminal;

/// <summary>What a stretch of a command line is, so that it can be colored while it is typed.</summary>
public enum HighlightKind
{
    /// <summary>A command that is there, on the computer or among the ones the shell keeps.</summary>
    Command,

    /// <summary>A command that is not anywhere the shell would look for it.</summary>
    UnknownCommand,

    /// <summary>A command the shell itself carries out, such as <c>cd</c> or <c>export</c>.</summary>
    Builtin,

    /// <summary>A word that is part of the shell's language, such as <c>if</c>, <c>do</c> or <c>done</c>.</summary>
    Keyword,

    /// <summary>A word that starts with a dash, after a command: <c>-l</c>, <c>--color</c>.</summary>
    Option,

    /// <summary>Text in quotes, quotes included.</summary>
    String,

    /// <summary>A variable, <c>$HOME</c> or <c>${HOME}</c>, and the <c>$(</c> that starts a command inside a command.</summary>
    Variable,

    /// <summary>What joins or groups commands: <c>|</c>, <c>&amp;&amp;</c>, <c>;</c>, parentheses.</summary>
    Operator,

    /// <summary>Where input or output goes: <c>&gt;</c>, <c>2&gt;&amp;1</c>, <c>&lt;&lt;</c>.</summary>
    Redirect,

    /// <summary>From a <c>#</c> that starts a word to the end of the line.</summary>
    Comment,

    /// <summary>The <c>NAME=</c> of a variable set for one command.</summary>
    Assignment,

    /// <summary>Something that cannot be right as it stands, such as a quote that is never closed.</summary>
    Error,
}

/// <summary>A stretch of a line with something to say about it. Text that no span covers is plain.</summary>
/// <param name="Start">Where it starts, in UTF-16 code units.</param>
/// <param name="Length">How long it is, in UTF-16 code units.</param>
public readonly record struct HighlightSpan(int Start, int Length, HighlightKind Kind);

/// <summary>Whether a word can be run as a command.</summary>
public enum CommandState
{
    /// <summary>It cannot be told, so the word is left alone rather than risk marking a good command as bad.</summary>
    Unknown,
    Exists,
    Missing,
}

/// <summary>
/// Finds the parts of a command line, a few at a time as it is typed, the way zsh-syntax-highlighting does inside zsh:
/// which word is the command, which are options, strings and variables, where one command ends and the next begins.
/// </summary>
/// <remarks>
/// It reads the line from left to right once, with no look back, so a line that is typed gets the same colors for what
/// is already there as it gets for the whole line, except where later text closes something: a quote, say. It reads
/// bash, zsh and fish alike, and PowerShell well enough for what it colors. Where it cannot be sure it colors nothing:
/// a color that is wrong is worse than none.
/// </remarks>
public static class ShellHighlighter
{
    // What the shell reads as its own language where a command could stand. "in" is left out: it only means something
    // after a for, select or case, where it is told apart by where it stands.
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Keywords = Words(
        "if", "then", "else", "elif", "fi", "for", "while", "until", "do", "done", "case", "esac", "select", "function",
        "time", "{", "}", "[[", "]]", "!", "coproc");

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Builtins = Words(
        "cd", "echo", "export", "alias", "unalias", "source", ".", "set", "unset", "exit", "return", "read", "printf", "test", "[",
        "history", "jobs", "fg", "bg", "wait", "eval", "exec", "shift", "trap", "type", "ulimit", "umask", "pwd", "pushd", "popd",
        "declare", "typeset", "local", "readonly", "let", "getopts", "command", "builtin", "hash", "kill", "true", "false", ":",
        "break", "continue", "shopt", "disown", "dirs", "enable", "help", "mapfile", "readarray", "compgen", "complete", "bind",
        "caller", "logout", "suspend", "times", "fc");

    // Commands that run the next command: after them, and their options, a command stands again.
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Wrappers = Words(
        "sudo", "doas", "time", "nohup", "nice", "env", "xargs", "command", "exec", "builtin", "watch", "strace", "ionice");

    private static FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Words(params string[] words) =>
        words.ToFrozenSet(StringComparer.Ordinal).GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The parts of a line that call for a color, in order, none overlapping another.</summary>
    /// <param name="lookup">Says whether a word is a command that exists. Without it commands are neither marked good nor bad.</param>
    public static IReadOnlyList<HighlightSpan> Highlight(string line, Func<string, CommandState>? lookup = null) =>
        string.IsNullOrEmpty(line) ? [] : new Scanner(line, lookup).Run();

    private enum FrameKind : byte { Top, Paren, DollarParen, Backtick, Arith, DoubleQuote }

    // Where a word stands: first words are commands, the ones after a wrapper such as sudo may be too, the rest are arguments.
    private enum Mode : byte { Command, Args, Wrapped }

    private enum WordKind : byte { Normal, Assignment, RedirectTarget, Pattern }

    // Commands whose own words mean something by where they stand.
    private enum CommandKind : byte { Other, ForLike, Case, Function, Bracket }

    private enum DollarKind { Literal, Simple, Paren, Arith, Ansi }

    /// <summary>
    /// One thing the line is inside of: the line itself, parentheses, a command in <c>$( )</c> or backticks, arithmetic or
    /// double quotes. Each of the first four reads a command of its own and keeps what it knows about it here.
    /// </summary>
    private struct Frame
    {
        public FrameKind Kind;
        public int Start;
        public int SpanCount;

        // Double quotes: where the part of the string not yet reported begins.
        public int StringStart;

        // Arithmetic: how many parentheses of its own are open.
        public int Depth;
        public bool IsCommandArith;

        // The command being read.
        public Mode Mode;
        public CommandKind Command;
        public int ArgIndex;
        public bool OptionsEnded;
        public bool OptionSeen;
        public bool RedirectTarget;
        public bool InBracket;
        public bool CasePattern;
        public bool AfterFunctionName;
        public int LiteralParens;

        // The word being read.
        public bool InWord;
        public int WordStart;
        public bool Plain;
        public WordKind Word;
        public bool OptionCandidate;
        public bool OptionEmitted;
    }

    private sealed class Scanner(string s, Func<string, CommandState>? lookup)
    {
        private readonly int _n = s.Length;
        private readonly List<HighlightSpan> _spans = [];
        private Frame[] _stack = new Frame[16];
        private int _top;

        public List<HighlightSpan> Run()
        {
            int i = 0;
            while (i < _n)
            {
                i = _stack[_top].Kind switch
                {
                    FrameKind.DoubleQuote => StepDouble(i),
                    FrameKind.Arith => StepArith(i),
                    _ => StepCommand(i),
                };
            }

            Finish();
            return _spans;
        }

        // What is still open when the line ends. A quote that is not closed is wrong, and takes with it everything it was
        // going to color; the others are only waiting for the rest of what is being typed.
        private void Finish()
        {
            for (; _top >= 0; _top--)
            {
                ref Frame f = ref _stack[_top];
                if (f.Kind != FrameKind.DoubleQuote)
                {
                    EndWord(ref f, _n);
                    continue;
                }

                TruncateTo(f.SpanCount);
                Emit(f.Start, _n, HighlightKind.Error);
            }
        }

        private void Emit(int start, int end, HighlightKind kind)
        {
            // Spans never overlap: one that would is cut short rather than let a mistake here reach the colors.
            if (_spans.Count > 0)
            {
                HighlightSpan last = _spans[^1];
                start = Math.Max(start, last.Start + last.Length);
            }

            if (end > start) _spans.Add(new HighlightSpan(start, end - start, kind));
        }

        private void TruncateTo(int count)
        {
            if (_spans.Count > count) _spans.RemoveRange(count, _spans.Count - count);
        }

        private void Push(FrameKind kind, int start)
        {
            if (_top + 1 == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);

            _top++;
            _stack[_top] = default;
            ref Frame f = ref _stack[_top];
            f.Kind = kind;
            f.Start = start;
            f.StringStart = start;
            f.SpanCount = _spans.Count;
            f.Mode = Mode.Command;
        }

        // Leaves the frame on top, and tells the one under it how to go on.
        private void Pop(FrameKind kind, bool commandArith, int resumeAt)
        {
            _top--;
            ref Frame parent = ref _stack[_top];
            if (parent.Kind == FrameKind.DoubleQuote)
            {
                parent.StringStart = resumeAt;
                return;
            }

            if (kind != FrameKind.Paren && !commandArith) return;

            // A group or a sum has been a command, and what comes after it is not another one, unless it was the
            // () of a function's name, which a command in braces follows.
            parent.Mode = parent.AfterFunctionName ? Mode.Command : Mode.Args;
            parent.Command = CommandKind.Other;
            parent.ArgIndex = 0;
            parent.AfterFunctionName = false;
        }

        private static void ResetCommand(ref Frame f)
        {
            f.Mode = Mode.Command;
            f.Command = CommandKind.Other;
            f.ArgIndex = 0;
            f.OptionsEnded = false;
            f.OptionSeen = false;
            f.RedirectTarget = false;
            f.InBracket = false;
            f.AfterFunctionName = false;
            f.LiteralParens = 0;
        }

        #region Words

        private void StartWord(ref Frame f, int i)
        {
            f.InWord = true;
            f.WordStart = i;
            f.Plain = true;
            f.Word = WordKind.Normal;
            f.OptionCandidate = false;
            f.OptionEmitted = false;

            if (f.RedirectTarget)
            {
                f.Word = WordKind.RedirectTarget;
                return;
            }

            if (f.CasePattern)
            {
                f.Word = WordKind.Pattern;
                return;
            }

            if (f.Mode is Mode.Command or Mode.Wrapped && AssignmentEnd(i) is int equals and > 0)
            {
                f.Word = WordKind.Assignment;
                Emit(i, equals, HighlightKind.Assignment);
                return;
            }

            f.OptionCandidate = s[i] == '-' && !f.OptionsEnded && f.Mode != Mode.Command && f.Command is CommandKind.Other or CommandKind.Bracket;
        }

        // Where the = of NAME=value ends, or -1 if the word does not begin that way.
        private int AssignmentEnd(int i)
        {
            if (!IsNameStart(s[i])) return -1;

            int j = i + 1;
            while (j < _n && IsNameChar(s[j])) j++;
            if (j < _n && s[j] == '=') return j + 1;
            return j + 1 < _n && s[j] == '+' && s[j + 1] == '=' ? j + 2 : -1;
        }

        // Something that is not a plain letter of a word has begun: a quote, an expansion, an escape.
        private void MarkPiece(ref Frame f, int i)
        {
            if (!f.InWord) StartWord(ref f, i);
            f.Plain = false;
            CloseOption(ref f, i);
        }

        // An option stops where the word turns into something else, or at its =.
        private void CloseOption(ref Frame f, int i)
        {
            if (!f.OptionCandidate || f.OptionEmitted) return;

            f.OptionEmitted = true;
            if (i - f.WordStart > 1) Emit(f.WordStart, i, HighlightKind.Option);
        }

        private void EndWord(ref Frame f, int end)
        {
            if (!f.InWord) return;

            f.InWord = false;
            int start = f.WordStart;
            switch (f.Word)
            {
                case WordKind.RedirectTarget:
                    f.RedirectTarget = false;
                    return;
                case WordKind.Assignment:
                    // Another assignment or the command can follow.
                    return;
                case WordKind.Pattern:
                    if (f.Plain && Is(start, end, "esac"))
                    {
                        Emit(start, end, HighlightKind.Keyword);
                        f.CasePattern = false;
                        ResetCommand(ref f);
                        f.Mode = Mode.Args;
                    }

                    return;
            }

            if (f.OptionCandidate)
            {
                if (!f.OptionEmitted)
                {
                    f.OptionEmitted = true;
                    int length = end - start;
                    if (f.Plain && length == 2 && s[start] == '-' && s[start + 1] == '-')
                    {
                        // After -- what comes is the command itself, and no option can have taken it as its value.
                        f.OptionsEnded = true;
                        f.OptionSeen = false;
                        return;
                    }

                    if (length > 1) Emit(start, end, HighlightKind.Option);
                }

                if (f.Mode == Mode.Wrapped) f.OptionSeen = true;
                else f.ArgIndex++;
                return;
            }

            switch (f.Mode)
            {
                case Mode.Command:
                    CommandWord(ref f, start, end, suppressMissing: false);
                    break;
                case Mode.Wrapped:
                    CommandWord(ref f, start, end, suppressMissing: f.OptionSeen);
                    break;
                default:
                    ArgumentWord(ref f, start, end);
                    break;
            }
        }

        private bool Is(int start, int end, string word) =>
            end - start == word.Length && s.AsSpan(start, end - start).SequenceEqual(word);

        // A word where a command stands.
        /// <param name="suppressMissing">An option came before the word, and may have taken it as its value: sudo -u bob.</param>
        private void CommandWord(ref Frame f, int start, int end, bool suppressMissing)
        {
            f.Command = CommandKind.Other;
            f.ArgIndex = 0;
            f.OptionsEnded = false;

            // Not made of letters alone: a quote, a variable, an escape. Nothing can be said about what it will run.
            if (!f.Plain)
            {
                f.Mode = Mode.Args;
                f.OptionSeen = false;
                return;
            }

            ReadOnlySpan<char> word = s.AsSpan(start, end - start);
            if (Keywords.Contains(word))
            {
                Emit(start, end, HighlightKind.Keyword);
                ApplyKeyword(ref f, word);
                return;
            }

            bool wrapper = Wrappers.Contains(word);
            if (Builtins.Contains(word))
            {
                Emit(start, end, HighlightKind.Builtin);
                f.Mode = wrapper ? Mode.Wrapped : Mode.Args;
                f.OptionSeen = wrapper && suppressMissing;
                return;
            }

            // name() { ... } is a function being made, not a command that is not there.
            if (FollowedByEmptyParens(end))
            {
                f.AfterFunctionName = true;
                f.Mode = Mode.Command;
                return;
            }

            switch (Ask(word))
            {
                case CommandState.Exists:
                    Emit(start, end, HighlightKind.Command);
                    break;
                case CommandState.Missing when !suppressMissing:
                    Emit(start, end, HighlightKind.UnknownCommand);
                    break;
            }

            f.Mode = wrapper ? Mode.Wrapped : Mode.Args;
            f.OptionSeen = wrapper && suppressMissing;
        }

        private CommandState Ask(ReadOnlySpan<char> word)
        {
            // What cannot be the name of a command is not asked about: history expansions, job numbers, patterns.
            if (lookup is null || word.IsEmpty || word[0] is '!' or '%' or '-' || word.ContainsAny("*?[]{}~=\\")) return CommandState.Unknown;

            try
            {
                return lookup(word.ToString());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The answer is somebody else's code, and what it cannot say, nothing is made of.
                return CommandState.Unknown;
            }
        }

        private bool FollowedByEmptyParens(int end)
        {
            int j = end;
            while (j < _n && s[j] is ' ' or '\t') j++;
            if (j >= _n || s[j] != '(') return false;

            j++;
            while (j < _n && s[j] is ' ' or '\t') j++;
            return j < _n && s[j] == ')';
        }

        private static void ApplyKeyword(ref Frame f, ReadOnlySpan<char> word)
        {
            switch (word)
            {
                case "for" or "select":
                    f.Mode = Mode.Args;
                    f.Command = CommandKind.ForLike;
                    break;
                case "case":
                    f.Mode = Mode.Args;
                    f.Command = CommandKind.Case;
                    break;
                case "function":
                    f.Mode = Mode.Args;
                    f.Command = CommandKind.Function;
                    break;
                case "[[":
                    f.Mode = Mode.Args;
                    f.Command = CommandKind.Bracket;
                    f.InBracket = true;
                    break;
                case "time":
                    f.Mode = Mode.Wrapped;
                    f.OptionSeen = false;
                    break;
                case "fi" or "done" or "}" or "]]":
                    f.Mode = Mode.Args;
                    break;
                case "esac":
                    f.CasePattern = false;
                    f.Mode = Mode.Args;
                    break;
                default:
                    // if, then, else, elif, while, until, do, {, !, coproc: a command comes next.
                    f.Mode = Mode.Command;
                    break;
            }
        }

        // A word after the command.
        private void ArgumentWord(ref Frame f, int start, int end)
        {
            switch (f.Command)
            {
                case CommandKind.ForLike or CommandKind.Case when f.ArgIndex == 1 && f.Plain && Is(start, end, "in"):
                    Emit(start, end, HighlightKind.Keyword);
                    if (f.Command == CommandKind.Case) f.CasePattern = true;
                    else f.OptionsEnded = true;
                    break;
                case CommandKind.Function when f.ArgIndex == 0:
                    // function name { ... }: the name is not a command, and a group of commands follows it.
                    f.AfterFunctionName = true;
                    f.Mode = Mode.Command;
                    f.Command = CommandKind.Other;
                    return;
                case CommandKind.Bracket when f.Plain && Is(start, end, "]]"):
                    Emit(start, end, HighlightKind.Keyword);
                    f.InBracket = false;
                    f.Command = CommandKind.Other;
                    break;
            }

            f.ArgIndex++;
        }

        #endregion

        #region Stepping through the line

        // One piece of a line that is read as commands, from where it is. Gives back where to go on from.
        private int StepCommand(int i)
        {
            ref Frame f = ref _stack[_top];
            char c = s[i];
            switch (c)
            {
                case ' ' or '\t':
                    EndWord(ref f, i);
                    return i + 1;
                case '\n' or '\r':
                    EndWord(ref f, i);
                    ResetCommand(ref f);
                    return i + 1;
                case '|':
                    return Pipe(ref f, i);
                case '&':
                    return Ampersand(ref f, i);
                case ';':
                    return Semicolon(ref f, i);
                case '(':
                    return OpenParen(ref f, i);
                case ')':
                    return CloseParen(ref f, i);
                case '<' or '>':
                    return Redirect(ref f, i);
                case '#' when !f.InWord:
                    int lineEnd = s.AsSpan(i).IndexOfAny('\n', '\r');
                    int commentEnd = lineEnd < 0 ? _n : i + lineEnd;
                    Emit(i, commentEnd, HighlightKind.Comment);
                    return commentEnd;
                case '\'':
                    return SingleQuote(ref f, i);
                case '"':
                    MarkPiece(ref f, i);
                    Push(FrameKind.DoubleQuote, i);
                    return i + 1;
                case '`':
                    return Backtick(ref f, i);
                case '$':
                    return DollarInCommand(ref f, i);
                case '\\':
                    MarkPiece(ref f, i);
                    return Math.Min(i + 2, _n);
                default:
                    if (!f.InWord) StartWord(ref f, i);
                    if (c == '=') CloseOption(ref f, i);
                    return i + 1;
            }
        }

        private int SingleQuote(ref Frame f, int i)
        {
            MarkPiece(ref f, i);
            int close = s.IndexOf('\'', i + 1);
            if (close < 0)
            {
                Emit(i, _n, HighlightKind.Error);
                return _n;
            }

            Emit(i, close + 1, HighlightKind.String);
            return close + 1;
        }

        private int Backtick(ref Frame f, int i)
        {
            if (f.Kind == FrameKind.Backtick)
            {
                EndWord(ref f, i);
                Emit(i, i + 1, HighlightKind.Operator);
                Pop(FrameKind.Backtick, commandArith: false, i + 1);
                return i + 1;
            }

            MarkPiece(ref f, i);
            Emit(i, i + 1, HighlightKind.Operator);
            Push(FrameKind.Backtick, i);
            return i + 1;
        }

        private int DollarInCommand(ref Frame f, int i)
        {
            DollarKind kind = ClassifyDollar(i, inDouble: false, out int end);
            switch (kind)
            {
                case DollarKind.Literal:
                    if (!f.InWord) StartWord(ref f, i);
                    return i + 1;
                case DollarKind.Simple:
                    MarkPiece(ref f, i);
                    Emit(i, end, HighlightKind.Variable);
                    return end;
                case DollarKind.Paren:
                    MarkPiece(ref f, i);
                    Emit(i, end, HighlightKind.Variable);
                    Push(FrameKind.DollarParen, i);
                    return end;
                case DollarKind.Arith:
                    MarkPiece(ref f, i);
                    Emit(i, end, HighlightKind.Variable);
                    Push(FrameKind.Arith, i);
                    return end;
                default:
                    MarkPiece(ref f, i);
                    if (end < 0)
                    {
                        Emit(i, _n, HighlightKind.Error);
                        return _n;
                    }

                    Emit(i, end, HighlightKind.String);
                    return end;
            }
        }

        // What a $ begins. For anything else than the kinds below it is just a character.
        private DollarKind ClassifyDollar(int i, bool inDouble, out int end)
        {
            end = i + 1;
            if (i + 1 >= _n) return DollarKind.Literal;

            char next = s[i + 1];
            switch (next)
            {
                case '(':
                    if (i + 2 < _n && s[i + 2] == '(')
                    {
                        end = i + 3;
                        return DollarKind.Arith;
                    }

                    end = i + 2;
                    return DollarKind.Paren;
                case '{':
                    int depth = 0;
                    int j = i + 1;
                    for (; j < _n; j++)
                    {
                        char c = s[j];
                        if (c == '\\')
                        {
                            j++;
                        }
                        else if (c == '{')
                        {
                            depth++;
                        }
                        else if (c == '}' && --depth == 0)
                        {
                            j++;
                            break;
                        }
                    }

                    end = Math.Min(j, _n);
                    return DollarKind.Simple;
                case '\'' when !inDouble:
                    for (int k = i + 2; k < _n; k++)
                    {
                        if (s[k] == '\\')
                        {
                            k++;
                        }
                        else if (s[k] == '\'')
                        {
                            end = k + 1;
                            return DollarKind.Ansi;
                        }
                    }

                    end = -1;
                    return DollarKind.Ansi;
            }

            if (IsNameStart(next))
            {
                int j = i + 2;
                while (j < _n && IsNameChar(s[j])) j++;
                end = j;
                return DollarKind.Simple;
            }

            if (char.IsAsciiDigit(next) || next is '?' or '$' or '!' or '@' or '*' or '#' or '-')
            {
                end = i + 2;
                return DollarKind.Simple;
            }

            return DollarKind.Literal;
        }

        private int Pipe(ref Frame f, int i)
        {
            EndWord(ref f, i);
            bool doubled = i + 1 < _n && s[i + 1] == '|';
            int length = i + 1 < _n && (s[i + 1] is '|' or '&') ? 2 : 1;
            Emit(i, i + length, HighlightKind.Operator);

            // Inside [[ ]] || joins tests, and between case patterns | joins patterns: neither starts a command.
            if (f.CasePattern || (f.InBracket && doubled)) return i + length;

            ResetCommand(ref f);
            return i + length;
        }

        private int Ampersand(ref Frame f, int i)
        {
            EndWord(ref f, i);
            if (i + 1 < _n && s[i + 1] == '&')
            {
                Emit(i, i + 2, HighlightKind.Operator);
                if (!f.InBracket) ResetCommand(ref f);
                return i + 2;
            }

            if (i + 1 < _n && s[i + 1] == '>')
            {
                int end = i + 2 < _n && s[i + 2] == '>' ? i + 3 : i + 2;
                Emit(i, end, HighlightKind.Redirect);
                f.RedirectTarget = true;
                return end;
            }

            Emit(i, i + 1, HighlightKind.Operator);
            ResetCommand(ref f);
            return i + 1;
        }

        private int Semicolon(ref Frame f, int i)
        {
            EndWord(ref f, i);

            // ;; ;& and ;;& end a branch of a case, and a pattern comes next.
            int length = 1;
            if (i + 1 < _n && s[i + 1] == ';') length = i + 2 < _n && s[i + 2] == '&' ? 3 : 2;
            else if (i + 1 < _n && s[i + 1] == '&') length = 2;

            Emit(i, i + length, HighlightKind.Operator);
            ResetCommand(ref f);
            if (length > 1) f.CasePattern = true;
            return i + length;
        }

        private int OpenParen(ref Frame f, int i)
        {
            // Beside a word in the middle of a command, as in zsh's *(.), it is part of the word and not a group.
            if (f.InWord && f.Mode == Mode.Args && !f.CasePattern && f.Command != CommandKind.Function)
            {
                f.LiteralParens++;
                return i + 1;
            }

            EndWord(ref f, i);
            if (f.CasePattern || f.InBracket)
            {
                Emit(i, i + 1, HighlightKind.Operator);
                return i + 1;
            }

            bool sum = i + 1 < _n && s[i + 1] == '(' && (f.Mode == Mode.Command || (f.Mode == Mode.Args && f.Command == CommandKind.ForLike && f.ArgIndex == 0));
            if (sum)
            {
                // (( )) is arithmetic: nothing in it is a command. The parent goes on once it is closed.
                Emit(i, i + 2, HighlightKind.Operator);
                Push(FrameKind.Arith, i);
                _stack[_top].IsCommandArith = true;
                return i + 2;
            }

            Emit(i, i + 1, HighlightKind.Operator);

            // A group of commands begins only where a command could. Elsewhere, as in PowerShell's Write-Host (1 + 2),
            // the words inside are left plain.
            if (f.Mode == Mode.Command) Push(FrameKind.Paren, i);
            return i + 1;
        }

        private int CloseParen(ref Frame f, int i)
        {
            if (f.InWord && f.LiteralParens > 0)
            {
                f.LiteralParens--;
                return i + 1;
            }

            EndWord(ref f, i);
            if (f.CasePattern)
            {
                // The ) that ends a case pattern, after which commands stand.
                Emit(i, i + 1, HighlightKind.Operator);
                f.CasePattern = false;
                ResetCommand(ref f);
                return i + 1;
            }

            Emit(i, i + 1, HighlightKind.Operator);
            if (f.Kind is FrameKind.Paren or FrameKind.DollarParen) Pop(f.Kind, commandArith: false, i + 1);
            return i + 1;
        }

        private int Redirect(ref Frame f, int i)
        {
            // 2> and 2>&1: the number belongs to the redirection and is not a word of its own.
            int start = i;
            if (f.InWord && f.Plain && f.Word == WordKind.Normal && !f.OptionCandidate && AllDigits(f.WordStart, i))
            {
                start = f.WordStart;
                f.InWord = false;
            }
            else
            {
                EndWord(ref f, i);
            }

            char c = s[i];
            if (i + 1 < _n && s[i + 1] == '(' && !f.InBracket)
            {
                // <(command) and >(command) stand for a file.
                Emit(start, i + 2, HighlightKind.Operator);
                Push(FrameKind.Paren, i);
                return i + 2;
            }

            int end = i + 1;
            bool target = true;
            if (c == '>')
            {
                if (end < _n && (s[end] is '>' or '|')) end++;
                else if (end < _n && s[end] == '&') end = AfterDuplication(end + 1, out target);
            }
            else if (end < _n && s[end] == '<')
            {
                end++;
                if (end < _n && (s[end] is '<' or '-')) end++;
            }
            else if (end < _n && s[end] == '&')
            {
                end = AfterDuplication(end + 1, out target);
            }
            else if (end < _n && s[end] == '>')
            {
                end++;
            }

            Emit(start, end, HighlightKind.Redirect);
            f.RedirectTarget = target;
            return end;
        }

        // >&2 and <&- name the other end themselves. >&file does not, and a word follows.
        private int AfterDuplication(int at, out bool wordFollows)
        {
            int j = at;
            while (j < _n && char.IsAsciiDigit(s[j])) j++;
            if (j == at && j < _n && s[j] == '-') j++;

            wordFollows = j == at;
            return j;
        }

        private bool AllDigits(int start, int end)
        {
            for (int j = start; j < end; j++)
                if (!char.IsAsciiDigit(s[j])) return false;
            return end > start;
        }

        #endregion

        #region Inside double quotes and arithmetic

        private int StepDouble(int i)
        {
            ref Frame f = ref _stack[_top];
            switch (s[i])
            {
                case '"':
                    Emit(f.StringStart, i + 1, HighlightKind.String);
                    _top--;
                    return i + 1;
                case '\\':
                    return Math.Min(i + 2, _n);
                case '$':
                    return DollarInDouble(ref f, i);
                case '`':
                    // Inside backticks a quote that is still open when the closing one comes was never closed.
                    if (_top > 0 && _stack[_top - 1].Kind == FrameKind.Backtick)
                    {
                        TruncateTo(f.SpanCount);
                        Emit(f.Start, i, HighlightKind.Error);
                        _top--;
                        return i;
                    }

                    FlushString(ref f, i);
                    Emit(i, i + 1, HighlightKind.Operator);
                    Push(FrameKind.Backtick, i);
                    return i + 1;
                default:
                    return i + 1;
            }
        }

        private int DollarInDouble(ref Frame f, int i)
        {
            DollarKind kind = ClassifyDollar(i, inDouble: true, out int end);
            if (kind == DollarKind.Literal) return i + 1;

            FlushString(ref f, i);
            Emit(i, end, HighlightKind.Variable);
            switch (kind)
            {
                case DollarKind.Simple:
                    f.StringStart = end;
                    break;
                case DollarKind.Paren:
                    Push(FrameKind.DollarParen, i);
                    break;
                default:
                    Push(FrameKind.Arith, i);
                    break;
            }

            return end;
        }

        private void FlushString(ref Frame f, int i)
        {
            if (i > f.StringStart) Emit(f.StringStart, i, HighlightKind.String);
        }

        private int StepArith(int i)
        {
            ref Frame f = ref _stack[_top];
            switch (s[i])
            {
                case '(':
                    f.Depth++;
                    return i + 1;
                case ')':
                    if (f.Depth > 0)
                    {
                        f.Depth--;
                        return i + 1;
                    }

                    if (i + 1 < _n && s[i + 1] == ')')
                    {
                        Emit(i, i + 2, HighlightKind.Operator);
                        Pop(FrameKind.Arith, f.IsCommandArith, i + 2);
                        return i + 2;
                    }

                    return i + 1;
                case '$':
                    DollarKind kind = ClassifyDollar(i, inDouble: true, out int end);
                    switch (kind)
                    {
                        case DollarKind.Literal:
                            return i + 1;
                        case DollarKind.Simple:
                            Emit(i, end, HighlightKind.Variable);
                            return end;
                        case DollarKind.Paren:
                            Emit(i, end, HighlightKind.Variable);
                            Push(FrameKind.DollarParen, i);
                            return end;
                        default:
                            Emit(i, end, HighlightKind.Variable);
                            Push(FrameKind.Arith, i);
                            return end;
                    }

                default:
                    return i + 1;
            }
        }

        #endregion

        private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';

        private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
    }
}

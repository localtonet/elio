using System.Diagnostics;
using System.Text;
using Elio.Terminal;

namespace Elio.Tests;

/// <summary>
/// Coloring a command line as it is typed. Each case is a line and what the highlighter says about it, written
/// the way it reads: [kind:text] for what has a color and everything else as it is.
/// </summary>
public sealed class ShellHighlighterTests
{
    private static readonly HashSet<string> Installed =
    [
        "git", "ls", "cat", "grep", "tee", "sudo", "make", "date", "apt", "systemctl", "docker", "curl", "sort", "uniq", "find",
        "xargs", "env", "nohup", "nice", "less", "diff", "watch", "vim",
    ];

    // What a list of the computer's commands says: a name that is not installed is missing, and a path is not a name to look for.
    private static CommandState Known(string word) =>
        word.Contains('/') ? CommandState.Unknown : word == "mystery" ? CommandState.Unknown : Installed.Contains(word) ? CommandState.Exists : CommandState.Missing;

    private static readonly Dictionary<HighlightKind, string> Names = new()
    {
        [HighlightKind.Command] = "cmd",
        [HighlightKind.UnknownCommand] = "bad",
        [HighlightKind.Builtin] = "builtin",
        [HighlightKind.Keyword] = "kw",
        [HighlightKind.Option] = "opt",
        [HighlightKind.String] = "str",
        [HighlightKind.Variable] = "var",
        [HighlightKind.Operator] = "op",
        [HighlightKind.Redirect] = "redir",
        [HighlightKind.Comment] = "comment",
        [HighlightKind.Assignment] = "assign",
        [HighlightKind.Error] = "err",
    };

    private static string Render(string line, Func<string, CommandState>? lookup)
    {
        IReadOnlyList<HighlightSpan> spans = ShellHighlighter.Highlight(line, lookup);
        AssertWellFormed(line, spans);

        var text = new StringBuilder();
        int at = 0;
        foreach (HighlightSpan span in spans)
        {
            text.Append(line, at, span.Start - at);
            text.Append('[').Append(Names[span.Kind]).Append(':').Append(line, span.Start, span.Length).Append(']');
            at = span.Start + span.Length;
        }

        return text.Append(line, at, line.Length - at).ToString();
    }

    private static string Render(string line) => Render(line, Known);

    // Whatever the line, what comes back is in order, inside the line, and never says the same text twice.
    private static void AssertWellFormed(string line, IReadOnlyList<HighlightSpan> spans)
    {
        int end = 0;
        foreach (HighlightSpan span in spans)
        {
            Assert.True(span.Length > 0, $"an empty span at {span.Start}");
            Assert.True(span.Start >= end, $"spans overlap or are out of order at {span.Start}");
            end = span.Start + span.Length;
            Assert.True(end <= line.Length, $"a span ends at {end}, past the end of the line");
        }
    }

    [Theory]
    [InlineData("git status", "[cmd:git] status")]
    [InlineData("gti status", "[bad:gti] status")]
    [InlineData("echo hi", "[builtin:echo] hi")]
    [InlineData("cd /tmp", "[builtin:cd] /tmp")]
    [InlineData("mystery arg", "mystery arg")]
    [InlineData("./run.sh arg", "./run.sh arg")]
    [InlineData("  git   status  ", "  [cmd:git]   status  ")]
    [InlineData("gi", "[bad:gi]")]
    public void Commands_AreMarkedByWhetherTheyExist(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Fact]
    public void Commands_WithoutAList_AreNotMarkedGoodOrBad()
    {
        Assert.Equal("git status", Render("git status", null));
        Assert.Equal("[builtin:echo] hi", Render("echo hi", null));
    }

    [Fact]
    public void Commands_AListThatFails_SaysNothing()
    {
        static CommandState Fails(string word) => throw new InvalidOperationException();

        Assert.Equal("git status", Render("git status", Fails));
    }

    [Theory]
    [InlineData("!! ", "!! ")]
    [InlineData("!ls", "!ls")]
    [InlineData("%1", "%1")]
    [InlineData("ls*", "ls*")]
    [InlineData("~/bin/tool", "~/bin/tool")]
    [InlineData("foo-bar=baz", "foo-bar=baz")]
    public void Commands_ThatCannotBeNames_AreNotAskedAbout(string line, string expected)
    {
        var asked = new List<string>();
        CommandState Record(string word)
        {
            asked.Add(word);
            return CommandState.Missing;
        }

        Assert.Equal(expected, Render(line, Record));
        Assert.Empty(asked);
    }

    [Theory]
    [InlineData("ls -la /tmp", "[cmd:ls] [opt:-la] /tmp")]
    [InlineData("ls --color=auto", "[cmd:ls] [opt:--color]=auto")]
    [InlineData("ls -I=x", "[cmd:ls] [opt:-I]=x")]
    [InlineData("ls --", "[cmd:ls] --")]
    [InlineData("ls -- -la", "[cmd:ls] -- -la")]
    [InlineData("ls -", "[cmd:ls] -")]
    [InlineData("ls a -b", "[cmd:ls] a [opt:-b]")]
    [InlineData("ls -n\"x\"", "[cmd:ls] [opt:-n][str:\"x\"]")]
    [InlineData("ls -n$HOME", "[cmd:ls] [opt:-n][var:$HOME]")]
    [InlineData("-la", "-la")]
    public void Options_AreWordsThatStartWithADashAfterACommand(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("git commit -m \"fix: it\"", "[cmd:git] commit [opt:-m] [str:\"fix: it\"]")]
    [InlineData("echo 'a b'", "[builtin:echo] [str:'a b']")]
    [InlineData("echo \"a\" 'b' c", "[builtin:echo] [str:\"a\"] [str:'b'] c")]
    [InlineData("echo \"\"", "[builtin:echo] [str:\"\"]")]
    [InlineData("echo ''", "[builtin:echo] [str:'']")]
    [InlineData("echo a\"b c\"d", "[builtin:echo] a[str:\"b c\"]d")]
    [InlineData("echo 'it$HOME'", "[builtin:echo] [str:'it$HOME']")]
    [InlineData("echo \"a\\\"b\"", "[builtin:echo] [str:\"a\\\"b\"]")]
    [InlineData("echo $'a\\'b'", "[builtin:echo] [str:$'a\\'b']")]
    public void Strings_AreColoredWithTheirQuotes(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("echo $HOME/bin", "[builtin:echo] [var:$HOME]/bin")]
    [InlineData("echo $? $$ $1 $@ $# $! $*", "[builtin:echo] [var:$?] [var:$$] [var:$1] [var:$@] [var:$#] [var:$!] [var:$*]")]
    [InlineData("echo ${HOME:-/tmp}/x", "[builtin:echo] [var:${HOME:-/tmp}]/x")]
    [InlineData("echo ${a:-${b}}c", "[builtin:echo] [var:${a:-${b}}]c")]
    [InlineData("echo \"hello $USER and ${HOME}x\"", "[builtin:echo] [str:\"hello ][var:$USER][str: and ][var:${HOME}][str:x\"]")]
    [InlineData("echo \"$a$b\"", "[builtin:echo] [str:\"][var:$a][var:$b][str:\"]")]
    [InlineData("echo $", "[builtin:echo] $")]
    [InlineData("echo $ x", "[builtin:echo] $ x")]
    [InlineData("echo \"a $ b\"", "[builtin:echo] [str:\"a $ b\"]")]
    [InlineData("echo \\$HOME", "[builtin:echo] \\$HOME")]
    [InlineData("$EDITOR file", "[var:$EDITOR] file")]
    [InlineData("echo ${HOME", "[builtin:echo] [var:${HOME]")]
    [InlineData("$PAGER", "[var:$PAGER]")]
    public void Variables_AreColored(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("echo $(date +%F)", "[builtin:echo] [var:$(][cmd:date] +%F[op:)]")]
    [InlineData("echo \"today is $(date)\"", "[builtin:echo] [str:\"today is ][var:$(][cmd:date][op:)][str:\"]")]
    [InlineData("echo $(echo $(date))", "[builtin:echo] [var:$(][builtin:echo] [var:$(][cmd:date][op:)][op:)]")]
    [InlineData("echo $(ls | grep x)", "[builtin:echo] [var:$(][cmd:ls] [op:|] [cmd:grep] x[op:)]")]
    [InlineData("echo $(gti)", "[builtin:echo] [var:$(][bad:gti][op:)]")]
    [InlineData("echo `date`", "[builtin:echo] [op:`][cmd:date][op:`]")]
    [InlineData("echo `date | sort` x", "[builtin:echo] [op:`][cmd:date] [op:|] [cmd:sort][op:`] x")]
    [InlineData("echo \"a `date` b\"", "[builtin:echo] [str:\"a ][op:`][cmd:date][op:`][str: b\"]")]
    [InlineData("echo $(date", "[builtin:echo] [var:$(][cmd:date]")]
    [InlineData("echo `date", "[builtin:echo] [op:`][cmd:date]")]
    [InlineData("echo $(", "[builtin:echo] [var:$(]")]
    [InlineData("$(which git) status", "[var:$(][bad:which] git[op:)] status")]
    [InlineData("echo $(echo \")\")", "[builtin:echo] [var:$(][builtin:echo] [str:\")\"][op:)]")]
    public void CommandsInsideCommands_AreReadAsCommands(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("cat a.txt | grep foo | sort -u", "[cmd:cat] a.txt [op:|] [cmd:grep] foo [op:|] [cmd:sort] [opt:-u]")]
    [InlineData("make && make install || echo failed; ls", "[cmd:make] [op:&&] [cmd:make] install [op:||] [builtin:echo] failed[op:;] [cmd:ls]")]
    [InlineData("make &", "[cmd:make] [op:&]")]
    [InlineData("make & ls", "[cmd:make] [op:&] [cmd:ls]")]
    [InlineData("ls |& grep x", "[cmd:ls] [op:|&] [cmd:grep] x")]
    [InlineData("ls;ls", "[cmd:ls][op:;][cmd:ls]")]
    [InlineData("ls\nmake", "[cmd:ls]\n[cmd:make]")]
    [InlineData("ls |", "[cmd:ls] [op:|]")]
    [InlineData("ls | gti", "[cmd:ls] [op:|] [bad:gti]")]
    [InlineData("(cd /tmp && ls)", "[op:(][builtin:cd] /tmp [op:&&] [cmd:ls][op:)]")]
    [InlineData("(gti)", "[op:(][bad:gti][op:)]")]
    [InlineData("(ls) | grep x", "[op:(][cmd:ls][op:)] [op:|] [cmd:grep] x")]
    [InlineData("diff <(ls) <(ls -a)", "[cmd:diff] [op:<(][cmd:ls][op:)] [op:<(][cmd:ls] [opt:-a][op:)]")]
    [InlineData("echo (", "[builtin:echo] [op:(]")]
    [InlineData("echo (a b)", "[builtin:echo] [op:(]a b[op:)]")]
    [InlineData("ls *(.)", "[cmd:ls] *(.)")]
    public void Operators_StartANewCommand(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("ls > out.txt", "[cmd:ls] [redir:>] out.txt")]
    [InlineData("ls >out.txt", "[cmd:ls] [redir:>]out.txt")]
    [InlineData("ls >> out.txt", "[cmd:ls] [redir:>>] out.txt")]
    [InlineData("cat < in.txt", "[cmd:cat] [redir:<] in.txt")]
    [InlineData("ls 2> err.txt", "[cmd:ls] [redir:2>] err.txt")]
    [InlineData("ls 2>>err.txt", "[cmd:ls] [redir:2>>]err.txt")]
    [InlineData("ls > o 2>&1", "[cmd:ls] [redir:>] o [redir:2>&1]")]
    [InlineData("ls >&2", "[cmd:ls] [redir:>&2]")]
    [InlineData("ls 1>&2 | grep x", "[cmd:ls] [redir:1>&2] [op:|] [cmd:grep] x")]
    [InlineData("ls >&-", "[cmd:ls] [redir:>&-]")]
    [InlineData("ls >&all.txt", "[cmd:ls] [redir:>&]all.txt")]
    [InlineData("ls &> all", "[cmd:ls] [redir:&>] all")]
    [InlineData("ls &>> all", "[cmd:ls] [redir:&>>] all")]
    [InlineData("ls >| f", "[cmd:ls] [redir:>|] f")]
    [InlineData("cat <<< hi", "[cmd:cat] [redir:<<<] hi")]
    [InlineData("cat <<EOF", "[cmd:cat] [redir:<<]EOF")]
    [InlineData("cat <<-EOF", "[cmd:cat] [redir:<<-]EOF")]
    [InlineData("cat <&3", "[cmd:cat] [redir:<&3]")]
    [InlineData("cat <> f", "[cmd:cat] [redir:<>] f")]
    [InlineData("ls 12 > x", "[cmd:ls] 12 [redir:>] x")]
    [InlineData("a2>x", "[bad:a2][redir:>]x")]
    public void Redirects_AreColoredWithTheirNumber(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Fact]
    public void Redirects_TheFileAfterOneIsNotACommand_EvenBeforeTheCommand()
    {
        Assert.Equal("[redir:>] out.txt [cmd:ls]", Render("> out.txt ls"));
        Assert.Equal("[redir:2>] err.txt [cmd:ls] [opt:-a]", Render("2> err.txt ls -a"));
    }

    [Theory]
    [InlineData("ls # list files", "[cmd:ls] [comment:# list files]")]
    [InlineData("# only a comment", "[comment:# only a comment]")]
    [InlineData("#", "[comment:#]")]
    [InlineData("echo a#b", "[builtin:echo] a#b")]
    [InlineData("echo a #b", "[builtin:echo] a [comment:#b]")]
    [InlineData("echo \"#x\" # real", "[builtin:echo] [str:\"#x\"] [comment:# real]")]
    [InlineData("echo $# x", "[builtin:echo] [var:$#] x")]
    [InlineData("ls # a\nmake # b", "[cmd:ls] [comment:# a]\n[cmd:make] [comment:# b]")]
    [InlineData("ls;# c", "[cmd:ls][op:;][comment:# c]")]
    public void Comments_RunFromAHashThatStartsAWord(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("FOO=bar make", "[assign:FOO=]bar [cmd:make]")]
    [InlineData("A=1 B=\"x y\" make", "[assign:A=]1 [assign:B=][str:\"x y\"] [cmd:make]")]
    [InlineData("FOO=bar", "[assign:FOO=]bar")]
    [InlineData("FOO=", "[assign:FOO=]")]
    [InlineData("PATH+=:x ls", "[assign:PATH+=]:x [cmd:ls]")]
    [InlineData("A=$(date) make", "[assign:A=][var:$(][cmd:date][op:)] [cmd:make]")]
    [InlineData("A=$HOME make", "[assign:A=][var:$HOME] [cmd:make]")]
    [InlineData("export FOO=bar", "[builtin:export] FOO=bar")]
    [InlineData("ls FOO=bar", "[cmd:ls] FOO=bar")]
    [InlineData("1A=b ls", "1A=b ls")]
    [InlineData("env FOO=1 make", "[cmd:env] [assign:FOO=]1 [cmd:make]")]
    [InlineData("make; X=1 ls", "[cmd:make][op:;] [assign:X=]1 [cmd:ls]")]
    public void Assignments_PrecedeTheCommand(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("if [ -f x ]; then echo ok; fi", "[kw:if] [builtin:[] [opt:-f] x ][op:;] [kw:then] [builtin:echo] ok[op:;] [kw:fi]")]
    [InlineData("if git diff; then make; else ls; fi", "[kw:if] [cmd:git] diff[op:;] [kw:then] [cmd:make][op:;] [kw:else] [cmd:ls][op:;] [kw:fi]")]
    [InlineData("for f in *.txt; do cat \"$f\"; done", "[kw:for] f [kw:in] *.txt[op:;] [kw:do] [cmd:cat] [str:\"][var:$f][str:\"][op:;] [kw:done]")]
    [InlineData("for f in -a -b; do ls; done", "[kw:for] f [kw:in] -a -b[op:;] [kw:do] [cmd:ls][op:;] [kw:done]")]
    [InlineData("while true; do ls; done", "[kw:while] [builtin:true][op:;] [kw:do] [cmd:ls][op:;] [kw:done]")]
    [InlineData("until gti; do ls; done", "[kw:until] [bad:gti][op:;] [kw:do] [cmd:ls][op:;] [kw:done]")]
    [InlineData("! git diff", "[kw:!] [cmd:git] diff")]
    [InlineData("{ ls; }", "[kw:{] [cmd:ls][op:;] [kw:}]")]
    [InlineData("ls }", "[cmd:ls] }")]
    [InlineData("echo if", "[builtin:echo] if")]
    [InlineData("select x in a b; do ls; done", "[kw:select] x [kw:in] a b[op:;] [kw:do] [cmd:ls][op:;] [kw:done]")]
    [InlineData("in", "[bad:in]")]
    public void Keywords_AreColoredWhereACommandCouldStand(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("[[ -f a && -d b ]] && ls", "[kw:[[] [opt:-f] a [op:&&] [opt:-d] b [kw:]]] [op:&&] [cmd:ls]")]
    [InlineData("[[ a == b || c ]]", "[kw:[[] a == b [op:||] c [kw:]]]")]
    [InlineData("[[ ( -f a ) ]]", "[kw:[[] [op:(] [opt:-f] a [op:)] [kw:]]]")]
    [InlineData("[[ $a < b ]]; ls", "[kw:[[] [var:$a] [redir:<] b [kw:]]][op:;] [cmd:ls]")]
    public void DoubleBrackets_AreATestAndNotCommands(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("case $x in a|b) ls;; c) make;; esac", "[kw:case] [var:$x] [kw:in] a[op:|]b[op:)] [cmd:ls][op:;;] c[op:)] [cmd:make][op:;;] [kw:esac]")]
    [InlineData("case x in\n  gti) ls ;;\nesac", "[kw:case] x [kw:in]\n  gti[op:)] [cmd:ls] [op:;;]\n[kw:esac]")]
    [InlineData("case x in (a) ls;; esac", "[kw:case] x [kw:in] [op:(]a[op:)] [cmd:ls][op:;;] [kw:esac]")]
    [InlineData("case x in *) gti ;& esac", "[kw:case] x [kw:in] *[op:)] [bad:gti] [op:;&] [kw:esac]")]
    public void Case_PatternsAreNotCommands(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("function foo { ls; }", "[kw:function] foo [kw:{] [cmd:ls][op:;] [kw:}]")]
    [InlineData("function foo() { ls; }", "[kw:function] foo[op:(][op:)] [kw:{] [cmd:ls][op:;] [kw:}]")]
    [InlineData("foo() { ls; }", "foo[op:(][op:)] [kw:{] [cmd:ls][op:;] [kw:}]")]
    [InlineData("foo ( ) { ls; }", "foo [op:(] [op:)] [kw:{] [cmd:ls][op:;] [kw:}]")]
    public void Functions_NamesAreNotCommands(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("echo $((1+2))", "[builtin:echo] [var:$((]1+2[op:))]")]
    [InlineData("echo $(( (1+2)*3 ))", "[builtin:echo] [var:$((] (1+2)*3 [op:))]")]
    [InlineData("echo $(( $x + 1 ))", "[builtin:echo] [var:$((] [var:$x] + 1 [op:))]")]
    [InlineData("echo $(( $(date) + 1 ))", "[builtin:echo] [var:$((] [var:$(][cmd:date][op:)] + 1 [op:))]")]
    [InlineData("(( i++ ))", "[op:((] i++ [op:))]")]
    [InlineData("((i++)); ls", "[op:((]i++[op:))][op:;] [cmd:ls]")]
    [InlineData("for ((i=0;i<3;i++)); do ls; done", "[kw:for] [op:((]i=0;i<3;i++[op:))][op:;] [kw:do] [cmd:ls][op:;] [kw:done]")]
    [InlineData("echo $((1+2", "[builtin:echo] [var:$((]1+2")]
    public void Arithmetic_HasNoCommandsInIt(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("sudo ls -la", "[cmd:sudo] [cmd:ls] [opt:-la]")]
    [InlineData("sudo gti", "[cmd:sudo] [bad:gti]")]
    [InlineData("sudo -E ls", "[cmd:sudo] [opt:-E] [cmd:ls]")]
    [InlineData("sudo -E gti", "[cmd:sudo] [opt:-E] gti")]
    [InlineData("sudo -u bob ls", "[cmd:sudo] [opt:-u] bob ls")]
    [InlineData("sudo -u ls", "[cmd:sudo] [opt:-u] [cmd:ls]")]
    [InlineData("sudo sudo ls", "[cmd:sudo] [cmd:sudo] [cmd:ls]")]
    [InlineData("sudo -E sudo gti", "[cmd:sudo] [opt:-E] [cmd:sudo] gti")]
    [InlineData("sudo -- gti", "[cmd:sudo] -- [bad:gti]")]
    [InlineData("sudo !!", "[cmd:sudo] !!")]
    [InlineData("sudo echo hi", "[cmd:sudo] [builtin:echo] hi")]
    [InlineData("sudo ls | gti", "[cmd:sudo] [cmd:ls] [op:|] [bad:gti]")]
    [InlineData("nice -n 10 make", "[cmd:nice] [opt:-n] 10 make")]
    [InlineData("xargs -n 1 echo", "[cmd:xargs] [opt:-n] 1 echo")]
    [InlineData("xargs grep foo", "[cmd:xargs] [cmd:grep] foo")]
    [InlineData("nohup make &", "[cmd:nohup] [cmd:make] [op:&]")]
    [InlineData("command -v git", "[builtin:command] [opt:-v] [cmd:git]")]
    [InlineData("command -v gti", "[builtin:command] [opt:-v] gti")]
    [InlineData("exec ls", "[builtin:exec] [cmd:ls]")]
    [InlineData("time ls", "[kw:time] [cmd:ls]")]
    [InlineData("time -p ls", "[kw:time] [opt:-p] [cmd:ls]")]
    [InlineData("watch -n 1 ls", "[cmd:watch] [opt:-n] 1 ls")]
    [InlineData("sudo \"ls\"", "[cmd:sudo] [str:\"ls\"]")]
    public void Wrappers_LetACommandStandAgain(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("echo \"abc", "[builtin:echo] [err:\"abc]")]
    [InlineData("echo 'abc", "[builtin:echo] [err:'abc]")]
    [InlineData("echo \"", "[builtin:echo] [err:\"]")]
    [InlineData("echo '", "[builtin:echo] [err:']")]
    [InlineData("echo \"a\" \"b", "[builtin:echo] [str:\"a\"] [err:\"b]")]
    [InlineData("echo \"a $HOME", "[builtin:echo] [err:\"a $HOME]")]
    [InlineData("echo \"a $(date", "[builtin:echo] [err:\"a $(date]")]
    [InlineData("echo $(echo \"a", "[builtin:echo] [var:$(][builtin:echo] [err:\"a]")]
    [InlineData("echo $(echo 'a", "[builtin:echo] [var:$(][builtin:echo] [err:'a]")]
    [InlineData("echo `echo \"a`", "[builtin:echo] [op:`][builtin:echo] [err:\"a][op:`]")]
    [InlineData("echo $'abc", "[builtin:echo] [err:$'abc]")]
    [InlineData("echo \"a\\", "[builtin:echo] [err:\"a\\]")]
    [InlineData("echo \"a\\\"", "[builtin:echo] [err:\"a\\\"]")]
    public void Quotes_ThatAreNeverClosedAreErrors(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("echo a\\ b", "[builtin:echo] a\\ b")]
    [InlineData("\\ls", "\\ls")]
    [InlineData("echo \\\"x", "[builtin:echo] \\\"x")]
    [InlineData("echo \\", "[builtin:echo] \\")]
    [InlineData("echo \\'a", "[builtin:echo] \\'a")]
    [InlineData("echo a\\;b", "[builtin:echo] a\\;b")]
    [InlineData("echo \\#x", "[builtin:echo] \\#x")]
    [InlineData("echo \"a\\$b\"", "[builtin:echo] [str:\"a\\$b\"]")]
    [InlineData("l\\s", "l\\s")]
    public void Escapes_KeepAWordTogetherAndColorNothing(string line, string expected) =>
        Assert.Equal(expected, Render(line));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   \t ")]
    [InlineData("\n")]
    public void Blank_HasNothingToColor(string line) =>
        Assert.Empty(ShellHighlighter.Highlight(line, Known));

    [Theory]
    [InlineData("\"")]
    [InlineData("'")]
    [InlineData("$")]
    [InlineData("\\")]
    [InlineData("`")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("$(")]
    [InlineData("${")]
    [InlineData("$((")]
    [InlineData("))")]
    [InlineData("((")]
    [InlineData("<(")]
    [InlineData(";;")]
    [InlineData("&")]
    [InlineData("&&")]
    [InlineData("||")]
    [InlineData("2>")]
    [InlineData("<<")]
    [InlineData("#")]
    [InlineData("=")]
    [InlineData("a=")]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("[[")]
    [InlineData("]]")]
    [InlineData("!")]
    public void LoneSymbols_NeverBreakIt(string line) => Render(line);

    [Fact]
    public void Prefixes_KeepTheColorsTheyAlreadyHad()
    {
        string[] lines =
        [
            "git commit -m \"fix it\" && make | grep x > out 2>&1 # done",
            "FOO=bar sudo -E ls -la /tmp ; for f in *.txt; do cat \"$f\"; done",
            "echo $(date +%F) `date` ${HOME} 'a b' | sort -u >> log 2>/dev/null &",
            "if [ -f x ]; then echo ok; else ls; fi",
            "case $x in a|b) ls;; *) make;; esac",
            "echo $((1 + 2)) && (cd /tmp; ls) || gti",
        ];

        foreach (string line in lines)
        {
            IReadOnlyList<HighlightSpan> whole = ShellHighlighter.Highlight(line, Known);
            foreach (int cut in SafeCuts(line))
            {
                string prefix = line[..cut];
                IReadOnlyList<HighlightSpan> typed = ShellHighlighter.Highlight(prefix, Known);
                HighlightSpan[] expected = [.. whole.Where(span => span.Start + span.Length <= cut)];

                Assert.True(expected.SequenceEqual(typed), $"\"{prefix}\" is colored {Render(prefix)}, but as part of \"{line}\" it is {Render(line)}");
            }
        }
    }

    // After a space with no quote, group or comment open: where typing has just finished a word.
    private static IEnumerable<int> SafeCuts(string line)
    {
        bool single = false, @double = false, comment = false;
        int depth = 0, backticks = 0;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (comment) continue;
            if (single)
            {
                single = c != '\'';
                continue;
            }

            switch (c)
            {
                case '\'' when !@double:
                    single = true;
                    break;
                case '"':
                    @double = !@double;
                    break;
                case '`':
                    backticks++;
                    break;
                case '(' when i > 0 && line[i - 1] == '$':
                    depth++;
                    break;
                case ')' when depth > 0:
                    depth--;
                    break;
                case '#' when !@double && (i == 0 || line[i - 1] == ' '):
                    comment = true;
                    break;
            }

            if (c == ' ' && !single && !@double && depth == 0 && backticks % 2 == 0) yield return i + 1;
        }
    }

    [Fact]
    public void Random_LinesNeverBreakIt()
    {
        const string Alphabet = "ab -=|&;()<>{}[]$#'\"`\\\n\t!*?~12.:/ ";
        var random = new Random(20260101);
        for (int round = 0; round < 20_000; round++)
        {
            var line = new StringBuilder();
            int length = random.Next(1, 40);
            for (int i = 0; i < length; i++) line.Append(Alphabet[random.Next(Alphabet.Length)]);

            string text = line.ToString();
            IReadOnlyList<HighlightSpan> spans = ShellHighlighter.Highlight(text, Known);
            AssertWellFormed(text, spans);
        }
    }

    [Theory]
    [InlineData("(")]
    [InlineData("$(")]
    [InlineData("\"")]
    [InlineData("'")]
    [InlineData("`")]
    [InlineData("\\")]
    [InlineData("${")]
    [InlineData("$((")]
    [InlineData("((")]
    [InlineData("<(")]
    [InlineData("\"$(")]
    [InlineData("\"`")]
    [InlineData("$'")]
    [InlineData("a=")]
    [InlineData("2>")]
    [InlineData("# ")]
    [InlineData(" ")]
    [InlineData("; ")]
    [InlineData("a ")]
    [InlineData("-a ")]
    [InlineData("1")]
    [InlineData("a() ")]
    public void HugeLines_AreReadFast(string unit)
    {
        string line = string.Concat(Enumerable.Repeat(unit, 100_000 / unit.Length));
        var clock = Stopwatch.StartNew();

        IReadOnlyList<HighlightSpan> spans = ShellHighlighter.Highlight(line, Known);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed} for {unit.Length}-char unit");
        AssertWellFormed(line, spans);
    }

    [Fact]
    public void HugeLines_OfMixedPunctuation_AreReadFast()
    {
        const string Alphabet = "ab -|&;()<>{}$#'\"`\\\n=";
        var random = new Random(7);
        var line = new StringBuilder();
        for (int i = 0; i < 100_000; i++) line.Append(Alphabet[random.Next(Alphabet.Length)]);
        string text = line.ToString();
        var clock = Stopwatch.StartNew();

        IReadOnlyList<HighlightSpan> spans = ShellHighlighter.Highlight(text, Known);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        AssertWellFormed(text, spans);
    }

    [Fact]
    public void Time_GrowsInStepWithTheLine()
    {
        // Twice the line, about twice the time: a loop inside a loop would make it four times, and 100 000 characters a minute.
        TimeSpan small = Measure(40_000);
        TimeSpan large = Measure(160_000);

        Assert.True(large < small * 12 + TimeSpan.FromMilliseconds(200), $"{small} for 40 000 characters, {large} for 160 000");

        static TimeSpan Measure(int length)
        {
            string line = string.Concat(Enumerable.Repeat("git commit -m \"a $(date)\" | grep x > f; ", length / 40));
            ShellHighlighter.Highlight(line, Known);
            var clock = Stopwatch.StartNew();
            ShellHighlighter.Highlight(line, Known);
            return clock.Elapsed;
        }
    }
}

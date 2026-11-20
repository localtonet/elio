using Elio.UI.Files;

namespace Elio.Tests;

public sealed class EditorTextTests
{
    private const string Spaces = "    ";

    private static string Apply(string text, TextEdit edit) =>
        text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];

    [Theory]
    [InlineData("", 0, 1, 1)]
    [InlineData("abc", 0, 1, 1)]
    [InlineData("abc", 3, 1, 4)]
    [InlineData("abc\ndef", 4, 2, 1)]
    [InlineData("abc\ndef", 6, 2, 3)]
    [InlineData("abc\n\nxyz", 4, 2, 1)]
    [InlineData("abc\n\nxyz", 5, 3, 1)]
    [InlineData("abc\n", 4, 2, 1)]
    public void LineColumn_CountsFromOne(string text, int index, int line, int column) =>
        Assert.Equal((line, column), EditorText.LineColumn(text, index));

    [Fact]
    public void LineColumn_KeepsAnIndexOutsideTheTextInsideIt()
    {
        Assert.Equal((1, 1), EditorText.LineColumn("abc", -5));
        Assert.Equal((2, 4), EditorText.LineColumn("abc\ndef", 99));
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("a", 1)]
    [InlineData("a\n", 2)]
    [InlineData("a\nb\nc", 3)]
    public void LineCount_IsTheNumberOfBreaksPlusOne(string text, int expected) =>
        Assert.Equal(expected, EditorText.LineCount(text));

    [Theory]
    [InlineData("a\nbb\nccc", 1, 0)]
    [InlineData("a\nbb\nccc", 2, 2)]
    [InlineData("a\nbb\nccc", 3, 5)]
    [InlineData("a\nbb\nccc", 99, 5)]
    [InlineData("a\nbb\nccc", 0, 0)]
    public void IndexOfLine_IsWhereTheLineBegins_AndStaysInsideTheText(string text, int line, int expected) =>
        Assert.Equal(expected, EditorText.IndexOfLine(text, line));

    [Fact]
    public void IndexAt_StaysInsideItsLine()
    {
        Assert.Equal(2, EditorText.IndexAt("a\nbb\nccc", 2, 1));
        Assert.Equal(4, EditorText.IndexAt("a\nbb\nccc", 2, 99));
        Assert.Equal(8, EditorText.IndexAt("a\nbb\nccc", 3, 99));
    }

    [Theory]
    [InlineData("abc", 3, "\n")]
    [InlineData("    abc", 7, "\n    ")]
    [InlineData("\t\tabc", 5, "\n\t\t")]
    [InlineData("  \t abc", 7, "\n  \t ")]
    [InlineData("    abc", 2, "\n  ")]
    [InlineData("    abc\n  def", 3, "\n   ")]
    [InlineData("", 0, "\n")]
    public void NewLineWithIndent_KeepsTheWhiteSpaceTheLineBeganWith(string text, int caret, string expected) =>
        Assert.Equal(expected, EditorText.NewLineWithIndent(text, caret, Spaces));

    [Theory]
    [InlineData("if (x) {", 8, "\n    ")]
    [InlineData("    foo(", 8, "\n        ")]
    [InlineData("    items = [  ", 14, "\n        ")]
    [InlineData("def f():", 8, "\n    ")]
    [InlineData("    return x;", 13, "\n    ")]
    public void NewLineWithIndent_AddsALevelAfterAnOpeningBracket(string text, int caret, string expected) =>
        Assert.Equal(expected, EditorText.NewLineWithIndent(text, caret, Spaces));

    [Fact]
    public void NewLineWithIndent_AddsTheUnitItIsGiven() =>
        Assert.Equal("\n\t\t", EditorText.NewLineWithIndent("\tif (x) {", 9, "\t"));

    [Fact]
    public void DetectIndent_FindsTabs()
    {
        const string text = "func main() {\n\tfmt.Println(1)\n\tif x {\n\t\treturn\n\t}\n}\n";

        Assert.Equal("\t", EditorText.DetectIndent(text));
    }

    [Fact]
    public void DetectIndent_FindsFourSpaces()
    {
        const string text = "class A:\n    def f(self):\n        return 1\n    def g(self):\n        return 2\n";

        Assert.Equal("    ", EditorText.DetectIndent(text));
    }

    [Fact]
    public void DetectIndent_FindsTwoSpaces()
    {
        const string text = "services:\n  web:\n    image: x\n    ports:\n      - 80\n  db:\n    image: y\n";

        Assert.Equal("  ", EditorText.DetectIndent(text));
    }

    [Fact]
    public void DetectIndent_AFewAlignedLinesDoNotMakeItTwoSpaced()
    {
        const string text = "a:\n    b\n    c\n    d\n    e\n    f\n    g\n    h\n  i\n";

        Assert.Equal("    ", EditorText.DetectIndent(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no indent at all\nnone here\n")]
    public void DetectIndent_DefaultsToFourSpaces(string text) =>
        Assert.Equal("    ", EditorText.DetectIndent(text));

    [Theory]
    [InlineData("abc", 0, "    ")]
    [InlineData("abc", 1, "   ")]
    [InlineData("abc", 3, " ")]
    [InlineData("abcde", 5, "   ")]
    [InlineData("x\nab", 4, "  ")]
    public void TabInsertion_GoesToTheNextTabStop(string text, int caret, string expected) =>
        Assert.Equal(expected, EditorText.TabInsertion(text, caret, Spaces));

    [Fact]
    public void TabInsertion_IsATabForATabUnit() =>
        Assert.Equal("\t", EditorText.TabInsertion("abc", 2, "\t"));

    [Fact]
    public void Indent_PutsAUnitInFrontOfEveryTouchedLine()
    {
        const string text = "one\ntwo\nthree\nfour";

        TextEdit edit = EditorText.Indent(text, 5, 10, Spaces);

        Assert.Equal("one\n    two\n    three\nfour", Apply(text, edit));
        Assert.Equal(9, edit.SelectionStart);
        Assert.Equal(18, edit.SelectionEnd);
    }

    [Fact]
    public void Indent_LeavesEmptyLinesEmpty()
    {
        const string text = "a\n\nb";

        Assert.Equal("    a\n\n    b", Apply(text, EditorText.Indent(text, 0, text.Length, Spaces)));
    }

    [Fact]
    public void Indent_DoesNotTouchALineTheSelectionOnlyReachesTheStartOf()
    {
        const string text = "a\nb\nc";

        // From the start of "a" through the line break after "b": "c" is not part of it.
        Assert.Equal("    a\n    b\nc", Apply(text, EditorText.Indent(text, 0, 4, Spaces)));
    }

    [Fact]
    public void Indent_KeepsASelectionThatBeganAtALineStartAtThatLineStart()
    {
        const string text = "ab\ncd";

        TextEdit edit = EditorText.Indent(text, 0, 5, Spaces);

        Assert.Equal(0, edit.SelectionStart);
        Assert.Equal(13, edit.SelectionEnd);
    }

    [Fact]
    public void Indent_WithATabUnit()
    {
        const string text = "a\nb";

        Assert.Equal("\ta\n\tb", Apply(text, EditorText.Indent(text, 0, 3, "\t")));
    }

    [Fact]
    public void Outdent_TakesOneLevelOff()
    {
        const string text = "        a\n    b\nc";

        TextEdit edit = EditorText.Outdent(text, 0, text.Length, Spaces);

        Assert.Equal("    a\nb\nc", Apply(text, edit));
    }

    [Fact]
    public void Outdent_TakesOnlyWhatIsThere()
    {
        const string text = "  a\n b";

        Assert.Equal("a\nb", Apply(text, EditorText.Outdent(text, 0, text.Length, Spaces)));
    }

    [Fact]
    public void Outdent_TakesATab()
    {
        const string text = "\t\ta\n\tb";

        Assert.Equal("\ta\nb", Apply(text, EditorText.Outdent(text, 0, text.Length, "\t")));
    }

    [Fact]
    public void Outdent_OnACaretDoesTheLineItIsOn_AndMovesTheCaretBack()
    {
        const string text = "x\n        abc";

        TextEdit edit = EditorText.Outdent(text, 8, 8, Spaces);

        Assert.Equal("x\n    abc", Apply(text, edit));
        Assert.Equal(4, edit.SelectionStart);
        Assert.Equal(edit.SelectionStart, edit.SelectionEnd);
    }

    [Fact]
    public void Outdent_NeverMovesTheSelectionBeforeItsLine()
    {
        const string text = "ab\n    cd";

        TextEdit edit = EditorText.Outdent(text, 4, 4, Spaces);

        Assert.Equal(3, edit.SelectionStart);
        Assert.Equal(3, edit.SelectionEnd);
    }

    [Fact]
    public void Find_PlainText_IgnoresCaseUnlessToldNotTo()
    {
        FindResult any = EditorText.Find("Foo foo FOO", "foo", caseSensitive: false, regex: false);
        FindResult exact = EditorText.Find("Foo foo FOO", "foo", caseSensitive: true, regex: false);

        Assert.Equal([new TextSpan(0, 3), new TextSpan(4, 3), new TextSpan(8, 3)], any.Matches);
        Assert.Equal([new TextSpan(4, 3)], exact.Matches);
    }

    [Fact]
    public void Find_PlainText_TakesSpecialCharactersAsTheyAre()
    {
        FindResult result = EditorText.Find("a.b a*b axb", "a.b", caseSensitive: true, regex: false);

        Assert.Equal([new TextSpan(0, 3)], result.Matches);
    }

    [Fact]
    public void Find_PlainText_DoesNotOverlap()
    {
        Assert.Equal(2, EditorText.Find("aaaa", "aa", caseSensitive: true, regex: false).Matches.Count);
    }

    [Fact]
    public void Find_Regex_FindsPatterns_AndLinesAreSeparate()
    {
        FindResult result = EditorText.Find("a1\nb22\nc333", @"^\w\d+$", caseSensitive: true, regex: true);

        Assert.Equal([new TextSpan(0, 2), new TextSpan(3, 3), new TextSpan(7, 4)], result.Matches);
    }

    [Fact]
    public void Find_Regex_SkipsMatchesOfNothing()
    {
        FindResult result = EditorText.Find("abc", "x*", caseSensitive: true, regex: true);

        Assert.Empty(result.Matches);
        Assert.False(result.Invalid);
    }

    [Theory]
    [InlineData("(")]
    [InlineData("[a-")]
    [InlineData("*abc")]
    public void Find_Regex_ABadPatternIsReportedAndNeverThrows(string pattern)
    {
        FindResult result = EditorText.Find("some text", pattern, caseSensitive: true, regex: true);

        Assert.True(result.Invalid);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Find_AnEmptyQueryFindsNothing()
    {
        Assert.Empty(EditorText.Find("abc", string.Empty, caseSensitive: true, regex: false).Matches);
        Assert.Empty(EditorText.Find("abc", string.Empty, caseSensitive: true, regex: true).Matches);
    }

    [Fact]
    public void Find_StopsAtTheLimit()
    {
        string text = string.Concat(Enumerable.Repeat("x ", EditorText.MaxMatches + 500));

        FindResult plain = EditorText.Find(text, "x", caseSensitive: true, regex: false);
        FindResult pattern = EditorText.Find(text, "x", caseSensitive: true, regex: true);

        Assert.Equal(EditorText.MaxMatches, plain.Matches.Count);
        Assert.True(plain.Capped);
        Assert.Equal(EditorText.MaxMatches, pattern.Matches.Count);
        Assert.True(pattern.Capped);
    }

    [Fact]
    public void Find_JustAtTheLimitIsNotCapped()
    {
        string text = string.Concat(Enumerable.Repeat("x ", EditorText.MaxMatches));

        FindResult result = EditorText.Find(text, "x", caseSensitive: true, regex: false);

        Assert.Equal(EditorText.MaxMatches, result.Matches.Count);
        Assert.False(result.Capped);
    }

    [Fact]
    public void Find_APatternThatTakesForeverGivesUpInsteadOfHanging()
    {
        string text = new string('a', 60) + "!";
        var clock = System.Diagnostics.Stopwatch.StartNew();

        FindResult result = EditorText.Find(text, "(a+)+$", caseSensitive: true, regex: true);

        // Answered, or said it could not: it neither threw nor ran for ever.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20));
        Assert.True(result.Invalid || result.Matches.Count == 0);
    }

    [Fact]
    public void ReplaceAll_PlainText_ReplacesEveryOneAndCountsThem()
    {
        (string text, int count, bool invalid) = EditorText.ReplaceAll("a.b a.b axb", "a.b", "$1", caseSensitive: true, regex: false);

        Assert.Equal("$1 $1 axb", text);
        Assert.Equal(2, count);
        Assert.False(invalid);
    }

    [Fact]
    public void ReplaceAll_Regex_FillsInGroups()
    {
        (string text, int count, _) = EditorText.ReplaceAll("john smith\nada lovelace", @"(\w+) (\w+)", "$2, $1", caseSensitive: true, regex: true);

        Assert.Equal("smith, john\nlovelace, ada", text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void ReplaceAll_IgnoresCaseWhenAsked()
    {
        (string text, int count, _) = EditorText.ReplaceAll("Foo foo", "FOO", "x", caseSensitive: false, regex: false);

        Assert.Equal("x x", text);
        Assert.Equal(2, count);
    }

    [Fact]
    public void ReplaceAll_WithNothingFound_ReturnsTheSameText()
    {
        (string text, int count, bool invalid) = EditorText.ReplaceAll("abc", "z", "y", caseSensitive: true, regex: false);

        Assert.Equal("abc", text);
        Assert.Equal(0, count);
        Assert.False(invalid);
    }

    [Fact]
    public void ReplaceAll_ABadPatternChangesNothing()
    {
        (string text, int count, bool invalid) = EditorText.ReplaceAll("abc", "(", "y", caseSensitive: true, regex: true);

        Assert.Equal("abc", text);
        Assert.Equal(0, count);
        Assert.True(invalid);
    }

    [Fact]
    public void Replacement_ForARegexFillsInGroupsAtThatMatch()
    {
        const string text = "k1=v1 k2=v2";

        string replacement = EditorText.Replacement(text, new TextSpan(6, 5), @"(\w+)=(\w+)", "$2=$1", caseSensitive: true, regex: true);

        Assert.Equal("v2=k2", replacement);
    }

    [Fact]
    public void Replacement_ForPlainTextIsTheReplacementAsItIs() =>
        Assert.Equal("$1", EditorText.Replacement("abc", new TextSpan(0, 1), "a", "$1", caseSensitive: true, regex: false));

    [Fact]
    public void NextMatch_IsTheFirstAtOrAfterAPlace_AndWrapsRound()
    {
        TextSpan[] matches = [new(2, 1), new(10, 1), new(20, 1)];

        Assert.Equal(0, EditorText.NextMatch(matches, 0));
        Assert.Equal(0, EditorText.NextMatch(matches, 2));
        Assert.Equal(1, EditorText.NextMatch(matches, 3));
        Assert.Equal(2, EditorText.NextMatch(matches, 20));
        Assert.Equal(0, EditorText.NextMatch(matches, 21));
        Assert.Equal(-1, EditorText.NextMatch([], 0));
    }

    [Fact]
    public void PreviousMatch_IsTheLastBeforeAPlace_AndWrapsRound()
    {
        TextSpan[] matches = [new(2, 1), new(10, 1), new(20, 1)];

        Assert.Equal(2, EditorText.PreviousMatch(matches, 2));
        Assert.Equal(0, EditorText.PreviousMatch(matches, 3));
        Assert.Equal(1, EditorText.PreviousMatch(matches, 20));
        Assert.Equal(2, EditorText.PreviousMatch(matches, 99));
        Assert.Equal(-1, EditorText.PreviousMatch([], 0));
    }
}

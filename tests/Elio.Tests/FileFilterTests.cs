using System.Globalization;
using Elio.UI.Files;

namespace Elio.Tests;

/// <summary>What the search bar over a file list lets through. Plain logic: no control is created.</summary>
public sealed class FileFilterTests
{
    private static FileRow Row(string name, bool directory = false, bool hidden = false) =>
        new(name, "/x/" + name, 0, DateTime.MinValue, directory, IsLink: false, string.Empty, null, hidden);

    private static string[] Names(IEnumerable<FileRow> rows) => rows.Select(r => r.Name).ToArray();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NothingTyped_LetsEverythingThrough(string? text)
    {
        FileFilter filter = FileFilter.Parse(text, regex: false);

        Assert.True(filter.IsEmpty);
        Assert.Equal(FileFilterKind.None, filter.Kind);
        Assert.True(filter.Matches("anything"));
    }

    [Theory]
    [InlineData("log", "app.log", true)]
    [InlineData("LOG", "app.log", true)]
    [InlineData("log", "Catalog.TXT", true)]
    [InlineData("log", "readme.md", false)]
    [InlineData("app.l", "app.log", true)]
    [InlineData("my file", "My File (1).txt", true)]
    public void Matches_PlainText_FindsPartOfTheNameInEitherCase(string text, string name, bool expected) =>
        Assert.Equal(expected, FileFilter.Parse(text, regex: false).Matches(name));

    [Fact]
    public void Parse_PlainText_DropsTheSpacesAroundIt()
    {
        FileFilter filter = FileFilter.Parse("  log ", regex: false);

        Assert.Equal(FileFilterKind.Text, filter.Kind);
        Assert.True(filter.Matches("app.log"));
    }

    [Theory]
    [InlineData("*.log", "app.log", true)]
    [InlineData("*.log", "APP.LOG", true)]
    [InlineData("*.log", "app.log.bak", false)]
    [InlineData("*.log", "log", false)]
    [InlineData("app*", "application.txt", true)]
    [InlineData("app*", "my-app.txt", false)]
    [InlineData("file?.txt", "file1.txt", true)]
    [InlineData("file?.txt", "file10.txt", false)]
    [InlineData("file?.txt", "file.txt", false)]
    [InlineData("*", "whatever", true)]
    [InlineData("a*b*c", "a--b--c", true)]
    [InlineData("a*b*c", "abc", true)]
    [InlineData("a*b*c", "a--c--b", false)]
    [InlineData("*a*b*c*", "xaxbxcx", true)]
    [InlineData("report-????-*.pdf", "report-2024-final.pdf", true)]
    public void Matches_WithWildcards_FitsTheWholeName(string pattern, string name, bool expected)
    {
        FileFilter filter = FileFilter.Parse(pattern, regex: false);

        Assert.Equal(FileFilterKind.Wildcard, filter.Kind);
        Assert.Equal(expected, filter.Matches(name));
    }

    [Fact]
    public void Matches_WildcardsWithManyStars_DoNotGetStuckOnALongName()
    {
        // The kind of pattern that makes a naive matcher try every way to divide the name up.
        FileFilter filter = FileFilter.Parse("*a*a*a*a*a*a*a*a*a*b", regex: false);
        string name = new('a', 5000);

        Assert.False(filter.Matches(name));
        Assert.False(filter.TimedOut);
    }

    [Fact]
    public void Matches_InATurkishCulture_StillTreatsIAsTheSameLetterInEitherCase()
    {
        // With the culture's own rules, I and i are not each other's capital, and typing "file" would miss FILE.TXT.
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            Assert.True(FileFilter.Parse("file", regex: false).Matches("FILE.TXT"));
            Assert.True(FileFilter.Parse("FILE*", regex: false).Matches("file.txt"));
            Assert.True(FileFilter.Parse(@"^file\.", regex: true).Matches("FILE.TXT"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData(@"^app\d+\.log$", "app12.log", true)]
    [InlineData(@"^app\d+\.log$", "APP12.LOG", true)]
    [InlineData(@"^app\d+\.log$", "app.log", false)]
    [InlineData(@"\.(log|txt)$", "notes.txt", true)]
    [InlineData(@"\.(log|txt)$", "notes.md", false)]
    [InlineData(@"2024-\d\d", "backup-2024-05.tar", true)]
    public void Matches_RegularExpression_IsFoundAnywhereInTheName(string pattern, string name, bool expected)
    {
        FileFilter filter = FileFilter.Parse(pattern, regex: true);

        Assert.Equal(FileFilterKind.Regex, filter.Kind);
        Assert.False(filter.IsInvalid);
        Assert.Equal(expected, filter.Matches(name));
    }

    [Fact]
    public void Parse_RegularExpressionThatIsNotOne_IsInvalidAndHoldsNothingBack()
    {
        FileFilter filter = FileFilter.Parse("([unclosed", regex: true);

        Assert.True(filter.IsInvalid);
        Assert.True(filter.IsEmpty);
        Assert.True(filter.Matches("anything"));
    }

    [Fact]
    public void Parse_AWildcardAsARegularExpression_IsReadAsOne()
    {
        // *.log is no regular expression, and typing it in that mode is told so and not quietly read the other way.
        Assert.True(FileFilter.Parse("*.log", regex: true).IsInvalid);
    }

    [Fact]
    public void Matches_RegularExpressionThatTakesTooLong_IsReportedAndGivesUp()
    {
        FileFilter filter = FileFilter.Parse("^(a+)+$", regex: true);

        Assert.False(filter.Matches(new string('a', 60) + "!"));
        Assert.True(filter.TimedOut);
    }

    [Fact]
    public void TryFind_PlainText_GivesWhereItIs()
    {
        FileFilter filter = FileFilter.Parse("LOG", regex: false);

        Assert.True(filter.TryFind("Catalog.txt", out int start, out int length));
        Assert.Equal((4, 3), (start, length));
    }

    [Fact]
    public void TryFind_RegularExpression_GivesTheFirstMatch()
    {
        FileFilter filter = FileFilter.Parse(@"\d+", regex: true);

        Assert.True(filter.TryFind("backup-2024-05.tar", out int start, out int length));
        Assert.Equal((7, 4), (start, length));
    }

    [Fact]
    public void TryFind_WildcardsAndNothing_PointAtNoPartOfTheName()
    {
        Assert.False(FileFilter.Parse("*.log", regex: false).TryFind("a.log", out _, out _));
        Assert.False(FileFilter.None.TryFind("a.log", out _, out _));
        Assert.False(FileFilter.Parse("zip", regex: false).TryFind("a.log", out _, out _));
    }

    [Fact]
    public void Apply_KeepsTheRowsThatFit_InTheOrderTheyCame()
    {
        FileRow[] rows = [Row("b.log"), Row("a.txt"), Row("c.log"), Row("d.md")];

        FileFilter.Result result = FileFilter.Apply(rows, FileFilter.Parse("*.log", regex: false), showHidden: true, CancellationToken.None);

        Assert.Equal(["b.log", "c.log"], Names(result.Rows));
        Assert.Equal(4, result.Candidates);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public void Apply_WithoutAFilter_ReturnsEveryRow()
    {
        FileRow[] rows = [Row("a"), Row("b"), Row("c", directory: true)];

        FileFilter.Result result = FileFilter.Apply(rows, FileFilter.None, showHidden: true, CancellationToken.None);

        Assert.Equal(["a", "b", "c"], Names(result.Rows));
    }

    [Fact]
    public void Apply_HiddenRowsOut_LeavesThemOutOfBothTheListAndTheCount()
    {
        FileRow[] rows = [Row(".git", directory: true, hidden: true), Row(".env", hidden: true), Row("readme.md"), Row("a.md")];

        FileFilter.Result hidden = FileFilter.Apply(rows, FileFilter.None, showHidden: false, CancellationToken.None);
        FileFilter.Result narrowed = FileFilter.Apply(rows, FileFilter.Parse("a", regex: false), showHidden: false, CancellationToken.None);

        Assert.Equal(["readme.md", "a.md"], Names(hidden.Rows));
        Assert.Equal(2, hidden.Candidates);
        Assert.Equal(["readme.md", "a.md"], Names(narrowed.Rows));
        Assert.Equal(2, narrowed.Candidates);
    }

    [Fact]
    public void Apply_HiddenRowsIn_ShowsThem() =>
        Assert.Equal(["x", ".y"], Names(FileFilter.Apply([Row("x"), Row(".y", hidden: true)], FileFilter.None, showHidden: true, CancellationToken.None).Rows));

    [Fact]
    public void Apply_RegularExpressionThatTimesOut_GivesNothingAndSaysSo()
    {
        FileRow[] rows = [Row("fine"), Row(new string('a', 60) + "!"), Row("also fine")];

        FileFilter.Result result = FileFilter.Apply(rows, FileFilter.Parse("^(a+)+$", regex: true), showHidden: true, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Apply_Cancelled_Throws()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => FileFilter.Apply([Row("a")], FileFilter.None, showHidden: true, cancelled.Token));
    }

    [Fact]
    public void Apply_CancelledWhileGoingThroughAHugeFolder_StopsBeforeTheEnd()
    {
        using var cancel = new CancellationTokenSource();
        int visited = 0;

        IEnumerable<FileRow> Many()
        {
            for (int i = 0; i < 1_000_000; i++)
            {
                visited++;
                if (i == 5000) cancel.Cancel();
                yield return Row("file" + i);
            }
        }

        Assert.Throws<OperationCanceledException>(() => FileFilter.Apply(Many(), FileFilter.Parse("file", regex: false), showHidden: true, cancel.Token));
        Assert.InRange(visited, 5001, 7000);
    }

    [Fact]
    public void Apply_ManyThousandsOfRows_IsQuick()
    {
        FileRow[] rows = Enumerable.Range(0, 200_000).Select(i => Row($"file-{i:D6}.log")).ToArray();

        var timer = System.Diagnostics.Stopwatch.StartNew();
        FileFilter.Result plain = FileFilter.Apply(rows, FileFilter.Parse("0042", regex: false), showHidden: true, CancellationToken.None);
        FileFilter.Result pattern = FileFilter.Apply(rows, FileFilter.Parse("file-1*9.log", regex: false), showHidden: true, CancellationToken.None);
        FileFilter.Result regex = FileFilter.Apply(rows, FileFilter.Parse(@"-\d*77\.log$", regex: true), showHidden: true, CancellationToken.None);
        timer.Stop();

        Assert.Equal(rows.Count(r => r.Name.Contains("0042", StringComparison.Ordinal)), plain.Rows.Count);
        Assert.NotEmpty(plain.Rows);
        Assert.Equal(10_000, pattern.Rows.Count);
        Assert.Equal(2000, regex.Rows.Count);

        // Far more than it needs: what matters is that this is milliseconds, not the seconds that would freeze a window.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"took {timer.Elapsed}");
    }
}

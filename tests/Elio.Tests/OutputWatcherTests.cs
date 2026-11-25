using System.Text;
using Elio.Terminal;

namespace Elio.Tests;

public sealed class OutputWatcherTests
{
    private static List<string> Feed(OutputWatcher watcher, params string[] pieces) =>
        pieces.SelectMany(piece => watcher.Feed(Encoding.UTF8.GetBytes(piece))).Select(hit => hit.Line).ToList();

    private static List<TriggerHit> Hits(OutputWatcher watcher, params string[] pieces) =>
        pieces.SelectMany(piece => watcher.Feed(Encoding.UTF8.GetBytes(piece))).ToList();

    [Fact]
    public void AMatchingLine_IsReportedOnce_WhenItEnds()
    {
        var watcher = new OutputWatcher(["BUILD FAILED"]);

        Assert.Empty(Feed(watcher, "compiling...\n", "BUILD FAI"));
        Assert.Equal(["BUILD FAILED in 3s"], Feed(watcher, "LED in 3s\r\n"));
    }

    [Fact]
    public void Colors_AndTitles_AreNotPartOfTheText()
    {
        var watcher = new OutputWatcher(["error: cannot find"]);

        List<string> found = Feed(watcher, "\u001b]0;make\u0007\u001b[1;31merror:\u001b[0m cannot find symbol\n");

        Assert.Equal(["error: cannot find symbol"], found);
    }

    [Fact]
    public void Patterns_AreRegularExpressions_AndIgnoreCase()
    {
        var watcher = new OutputWatcher([@"exit code [1-9]\d*"]);

        Assert.Equal(["Process finished with EXIT CODE 137"], Feed(watcher, "Process finished with exit code 0\n", "Process finished with EXIT CODE 137\n"));
    }

    [Fact]
    public void APatternThatDoesNotCompile_IsLookedForAsWritten()
    {
        var watcher = new OutputWatcher(["[ERROR"]);

        Assert.Equal(["[ERROR] disk full"], Feed(watcher, "[ERROR] disk full\n"));
    }

    [Fact]
    public void ACarriageReturn_StartsTheLineOver_LikeAProgressBar()
    {
        var watcher = new OutputWatcher(["done"]);

        Assert.Empty(Feed(watcher, "50% done-ish\rcopying 60%\n"));
    }

    [Fact]
    public void ACharacterSplitAcrossPieces_StillMatches()
    {
        var watcher = new OutputWatcher(["café"]);
        byte[] line = Encoding.UTF8.GetBytes("café ready\n");

        var found = new List<string>();
        found.AddRange(watcher.Feed(line.AsSpan(0, 4)).Select(hit => hit.Line));
        found.AddRange(watcher.Feed(line.AsSpan(4)).Select(hit => hit.Line));

        Assert.Equal(["café ready"], found);
    }

    [Fact]
    public void NoPatterns_WatchesNothing()
    {
        var watcher = new OutputWatcher(["", "   "]);

        Assert.True(watcher.IsEmpty);
        Assert.Empty(Feed(watcher, "anything\n"));
    }

    [Fact]
    public void AVeryLongLine_IsCutAndStillChecked()
    {
        var watcher = new OutputWatcher(["^x+$"]);

        List<string> found = Feed(watcher, new string('x', 100_000) + "\n");

        Assert.Single(found);
        Assert.Equal(4096, found[0].Length);
    }

    [Fact]
    public void AReplyRule_AnswersAQuestionThatWaitsOnItsLine_Once()
    {
        long now = 0;
        var watcher = new OutputWatcher(["Continue\\? \\[y/N\\] => y"], () => now);

        List<TriggerHit> first = Hits(watcher, "Removing 3 packages.\r\n", "Continue? [y/N] ");
        Assert.Equal([new TriggerHit("Continue? [y/N]", "y")], first);

        // Still the same question on screen: not answered again, also not when its line ends.
        now += 10_000;
        Assert.Empty(Hits(watcher, " ", "y\r\n"));
    }

    [Fact]
    public void AReplyRule_IsNotTypedAgainAndAgainInALoop()
    {
        long now = 0;
        var watcher = new OutputWatcher(["again => n"], () => now);

        Assert.Single(Hits(watcher, "again\n"));
        Assert.Empty(Hits(watcher, "again\n"));
        now += 3000;
        Assert.Single(Hits(watcher, "again\n"));
    }

    [Fact]
    public void ARuleWithoutAReply_OnlyLooksAtLinesThatEnded()
    {
        var watcher = new OutputWatcher(["password"]);

        Assert.Empty(Hits(watcher, "Enter password: "));
        Assert.Equal([new TriggerHit("Enter password: ok", null)], Hits(watcher, "ok\n"));
    }

    [Fact]
    public void AReply_CannotCarryALineBreak()
    {
        var watcher = new OutputWatcher(["go => yes\nrm -rf ~"]);

        TriggerHit hit = Assert.Single(Hits(watcher, "go\n"));
        Assert.DoesNotContain('\n', hit.Reply!);
    }
}

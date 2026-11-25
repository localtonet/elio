using Elio.UI.Files;

namespace Elio.Tests;

/// <summary>Comparing the two sides of the file screen, and what synchronising them would copy.</summary>
public sealed class FolderCompareTests
{
    private static readonly DateTime Noon = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static FileRow File(string name, long size = 100, DateTime? modified = null) =>
        new(name, "/x/" + name, size, modified ?? Noon, false, false, string.Empty, null);

    private static FileRow Folder(string name) => new(name, "/x/" + name, 4096, Noon, true, false, string.Empty, null);

    private static FileRow Link(string name, bool toFolder = false) => new(name, "/x/" + name, 0, Noon, toFolder, true, string.Empty, null);

    private static FolderSnapshot Side(params FileRow[] top) => Side(true, true, ("", top));

    private static FolderSnapshot Side(bool caseSensitive, bool subfolders, params (string Folder, FileRow[] Rows)[] folders) => new()
    {
        Folders = folders.ToDictionary(f => f.Folder, f => (IReadOnlyList<FileRow>)f.Rows, StringComparer.Ordinal),
        CaseSensitive = caseSensitive,
        Subfolders = subfolders,
    };

    private static FolderComparison Compare(FolderSnapshot left, FolderSnapshot right) => FolderComparison.Compare(left, right);

    [Fact]
    public void TheSameFiles_HaveNoDifferences()
    {
        FolderComparison comparison = Compare(Side(File("a.txt"), File("b.txt", 5)), Side(File("a.txt"), File("b.txt", 5)));

        Assert.Empty(comparison.Items);
        Assert.Equal(2, comparison.Same);
        Assert.Empty(comparison.MarksFor(left: true));
    }

    [Fact]
    public void ANewerFile_IsNewerOnItsSide_AndOlderOnTheOther()
    {
        FolderComparison comparison = Compare(Side(File("a.txt", 100, Noon.AddMinutes(5))), Side(File("a.txt", 100, Noon)));

        Assert.Equal(CompareState.LeftNewer, Assert.Single(comparison.Items).State);
        Assert.Equal(FileMark.Newer, comparison.MarksFor(left: true)["a.txt"]);
        Assert.Equal(FileMark.Older, comparison.MarksFor(left: false)["a.txt"]);
        Assert.Equal(new CompareTally(0, 1, 0, 0), comparison.Tally(left: true));
        Assert.Equal(new CompareTally(0, 0, 1, 0), comparison.Tally(left: false));
    }

    [Fact]
    public void AMissingFile_IsMarkedOnlyWhereItIs()
    {
        FolderComparison comparison = Compare(Side(File("left.txt")), Side(File("right.txt")));

        Assert.Equal([CompareState.LeftOnly, CompareState.RightOnly], comparison.Items.Select(i => i.State));
        Assert.Equal(FileMark.OnlyHere, comparison.MarksFor(left: true)["left.txt"]);
        Assert.False(comparison.MarksFor(left: true).ContainsKey("right.txt"));
        Assert.Equal(FileMark.OnlyHere, comparison.MarksFor(left: false)["right.txt"]);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1.5, null)]
    [InlineData(2, null)]
    [InlineData(-2, null)]
    [InlineData(2.5, CompareState.LeftNewer)]
    [InlineData(-3, CompareState.RightNewer)]
    public void Times_WithinTwoSeconds_AreTheSame(double secondsApart, CompareState? expected) =>
        Assert.Equal(expected, FolderComparison.CompareFiles(File("a", 10, Noon.AddSeconds(secondsApart)), File("a", 10, Noon)));

    [Fact]
    public void TheSameTime_AndAnotherSize_CannotSayWhichIsNewer() =>
        Assert.Equal(CompareState.SizeDiffers, FolderComparison.CompareFiles(File("a", 10, Noon.AddSeconds(1)), File("a", 11, Noon)));

    [Fact]
    public void ATimeThatIsNotKnown_LeavesTheSizeToDecide()
    {
        Assert.Null(FolderComparison.CompareFiles(File("a", 10, DateTime.MinValue), File("a", 10, Noon)));
        Assert.Equal(CompareState.SizeDiffers, FolderComparison.CompareFiles(File("a", 10, DateTime.MinValue), File("a", 12, Noon)));
    }

    [Fact]
    public void Windows_AgainstAServer_MatchesANameInAnotherCase_ButNeverCopiesOverIt()
    {
        // This computer on Windows takes "Readme.md" and "README.md" for one file; the server takes them for two.
        FolderComparison comparison = Compare(Side(false, true, ("", [File("Readme.md")])), Side(true, true, ("", [File("README.md")])));

        CompareItem item = Assert.Single(comparison.Items);
        Assert.Equal(CompareState.CaseDiffers, item.State);
        Assert.Equal(FileMark.CaseConflict, comparison.MarksFor(left: true)["Readme.md"]);
        Assert.Equal(FileMark.CaseConflict, comparison.MarksFor(left: false)["README.md"]);

        foreach (SyncDirection direction in Enum.GetValues<SyncDirection>())
        {
            SyncPlan plan = SyncPlan.Build(comparison, direction, replaceNewer: true);
            Assert.Empty(plan.Copies);
            Assert.Equal(SkipReason.NamesDifferInCase, Assert.Single(plan.Skipped).Reason);
        }
    }

    [Fact]
    public void TwoServers_TellNamesInAnotherCaseApart()
    {
        FolderComparison comparison = Compare(Side(File("Readme.md")), Side(File("README.md")));

        Assert.Equal([CompareState.LeftOnly, CompareState.RightOnly], comparison.Items.OrderBy(i => i.State).Select(i => i.State));
    }

    [Fact]
    public void NamesThatClashByCase_AreNotBroughtToASideThatWouldMergeThem()
    {
        // The server has two names that this computer on Windows could hold only one of.
        FolderComparison comparison = Compare(Side(false, true, ("", [])), Side(true, true, ("", [File("a.txt"), File("A.txt", 5)])));

        Assert.All(comparison.Items, i => Assert.Equal(CompareState.CaseClash, i.State));
        Assert.Equal(2, comparison.Items.Count);

        SyncPlan toLeft = SyncPlan.Build(comparison, SyncDirection.RightToLeft, false);
        Assert.Empty(toLeft.Copies);
        Assert.All(toLeft.Skipped, s => Assert.Equal(SkipReason.NamesClashByCase, s.Reason));

        SyncPlan toRight = SyncPlan.Build(comparison, SyncDirection.LeftToRight, false);
        Assert.Empty(toRight.Copies);
        Assert.Empty(toRight.Skipped);
        Assert.Equal(2, toRight.Untouched);
    }

    [Fact]
    public void Links_AreLeftOut_WithWhatHasTheirNameOnTheOtherSide()
    {
        FolderComparison comparison = Compare(Side(Link("x"), Link("only", toFolder: true), File("same")), Side(File("x", 3), Folder("y"), File("same")));

        CompareItem item = Assert.Single(comparison.Items);
        Assert.Equal("y", item.Name);
        Assert.Equal(2, comparison.LinksLeftOut);
        Assert.Equal(1, comparison.Same);
    }

    [Fact]
    public void AFileOnOneSide_AndAFolderOnTheOther_IsNeverCopied()
    {
        FolderComparison comparison = Compare(Side(File("data")), Side(Folder("data")));

        Assert.Equal(CompareState.KindDiffers, Assert.Single(comparison.Items).State);
        Assert.Equal(SkipReason.FileAndFolder, Assert.Single(SyncPlan.Build(comparison, SyncDirection.BothWays, false).Skipped).Reason);
    }

    [Fact]
    public void WithSubfolders_ADifferenceBelow_MarksTheFolderItIsIn()
    {
        FolderSnapshot left = Side(true, true, ("", [Folder("src")]), ("src", [Folder("lib")]), ("src/lib", [File("a.cs", 10, Noon.AddHours(1))]));
        FolderSnapshot right = Side(true, true, ("", [Folder("src")]), ("src", [Folder("lib")]), ("src/lib", [File("a.cs", 10, Noon)]));

        FolderComparison comparison = Compare(left, right);

        CompareItem item = Assert.Single(comparison.Items);
        Assert.Equal("src/lib", item.Folder);
        Assert.Equal("src/lib/a.cs", item.RelativePath);
        Assert.Equal(FileMark.DiffersInside, comparison.MarksFor(left: true)["src"]);
        Assert.Equal(FileMark.DiffersInside, comparison.MarksFor(left: false)["src"]);
    }

    [Fact]
    public void WithoutSubfolders_AFolderBothSidesHave_IsNotGoneInto()
    {
        FolderSnapshot left = Side(true, false, ("", [Folder("src")]));
        FolderSnapshot right = Side(true, false, ("", [Folder("src")]));

        Assert.Empty(Compare(left, right).Items);
    }

    [Fact]
    public void AFolderOnlyOneSideHas_CountsEverythingInIt_AndIsCopiedWhole()
    {
        FolderSnapshot left = Side(true, true, ("", [Folder("new")]), ("new", [File("a", 10), Folder("deep"), Link("loop", toFolder: true)]), ("new/deep", [File("b", 20)]));
        FolderSnapshot right = Side(true, true, ("", []));

        FolderComparison comparison = Compare(left, right);

        CompareItem item = Assert.Single(comparison.Items);
        Assert.Equal((CompareState.LeftOnly, 30L, 2), (item.State, item.Size, item.Files));

        SyncPlan plan = SyncPlan.Build(comparison, SyncDirection.LeftToRight, false);
        SyncCopy copy = Assert.Single(plan.Copies);
        Assert.True(copy.ToRight);
        Assert.Equal("new", copy.Source.Name);
        Assert.Equal((2, 30L), (plan.FilesToRight, plan.BytesToRight));
    }

    [Fact]
    public void WithoutSubfolders_AFolderOnlyOneSideHas_IsNotCopied()
    {
        FolderComparison comparison = Compare(Side(true, false, ("", [Folder("new")])), Side(true, false, ("", [])));

        SyncPlan plan = SyncPlan.Build(comparison, SyncDirection.LeftToRight, false);

        Assert.Empty(plan.Copies);
        Assert.Equal(SkipReason.FolderNeedsSubfolders, Assert.Single(plan.Skipped).Reason);
    }

    [Fact]
    public void AFolderOneSideCouldNotRead_IsLeftAlone()
    {
        FolderSnapshot left = Side(true, true, ("", [Folder("private")]), ("private", [File("a")]));
        var right = new FolderSnapshot
        {
            Folders = new Dictionary<string, IReadOnlyList<FileRow>> { [""] = [Folder("private")] },
            Subfolders = true,
            Unreadable = new HashSet<string> { "private" },
        };

        FolderComparison comparison = Compare(left, right);

        Assert.Equal(CompareState.Unreadable, Assert.Single(comparison.Items).State);
        Assert.Equal(SkipReason.Unreadable, Assert.Single(SyncPlan.Build(comparison, SyncDirection.LeftToRight, false).Skipped).Reason);
    }

    // left: only-left, newer-left, older-left, other-size; right: only-right and the other halves.
    private static FolderComparison Mixed() => Compare(
        Side(File("only-left", 1), File("newer-left", 2, Noon.AddHours(1)), File("newer-right", 3), File("other-size", 4)),
        Side(File("only-right", 5), File("newer-left", 6), File("newer-right", 7, Noon.AddHours(1)), File("other-size", 8)));

    [Fact]
    public void LeftToRight_CopiesWhatIsMissingOrDifferent_ButNotOverANewerFile()
    {
        SyncPlan plan = SyncPlan.Build(Mixed(), SyncDirection.LeftToRight, replaceNewer: false);

        Assert.Equal(["newer-left", "only-left", "other-size"], plan.Copies.Select(c => c.Item.Name).Order());
        Assert.All(plan.Copies, c => Assert.True(c.ToRight));
        Assert.Equal((3, 1L + 2 + 4), (plan.FilesToRight, plan.BytesToRight));
        SyncSkip skip = Assert.Single(plan.Skipped);
        Assert.Equal(("newer-right", SkipReason.NewerOnTheOtherSide), (skip.Item.Name, skip.Reason));
        Assert.Equal(1, plan.Untouched);
    }

    [Fact]
    public void OneWay_ReplacesNewerFiles_OnlyWhenAskedTo()
    {
        SyncPlan plan = SyncPlan.Build(Mixed(), SyncDirection.RightToLeft, replaceNewer: true);

        Assert.Equal(["newer-left", "newer-right", "only-right", "other-size"], plan.Copies.Select(c => c.Item.Name).Order());
        Assert.All(plan.Copies, c => Assert.False(c.ToRight));
        Assert.Empty(plan.Skipped);
        Assert.Equal(6, plan.Copies.Single(c => c.Item.Name == "newer-left").Bytes);
    }

    [Fact]
    public void BothWays_TakesTheNewerFile_AndLeavesWhatCannotBeTold()
    {
        SyncPlan plan = SyncPlan.Build(Mixed(), SyncDirection.BothWays, replaceNewer: true);

        Assert.Equal(["newer-left", "only-left"], plan.Copies.Where(c => c.ToRight).Select(c => c.Item.Name).Order());
        Assert.Equal(["newer-right", "only-right"], plan.Copies.Where(c => !c.ToRight).Select(c => c.Item.Name).Order());
        Assert.Equal((2, 3L, 2, 12L), (plan.FilesToRight, plan.BytesToRight, plan.FilesToLeft, plan.BytesToLeft));
        SyncSkip skip = Assert.Single(plan.Skipped);
        Assert.Equal(("other-size", SkipReason.CannotTellWhichIsNewer), (skip.Item.Name, skip.Reason));
        Assert.Equal(0, plan.Untouched);
    }

    [Fact]
    public void AnIncompleteComparison_CannotBeSynchronised()
    {
        var left = new FolderSnapshot { Folders = new Dictionary<string, IReadOnlyList<FileRow>> { [""] = [File("a")] }, Truncated = true };

        FolderComparison comparison = Compare(left, Side());

        Assert.True(comparison.Incomplete);
        Assert.Throws<InvalidOperationException>(() => SyncPlan.Build(comparison, SyncDirection.LeftToRight, false));
    }

    [Fact]
    public async Task Snapshot_GoesThroughTheFoldersBelow_WithoutFollowingLinks()
    {
        MemoryFileSource source = new MemoryFileSource()
            .Folder("/top").File("/top/a").Folder("/top/sub").File("/top/sub/b").Folder("/top/sub/deeper").File("/top/sub/deeper/c")
            .Link("/top/loop", toFolder: true).Folder("/top/shut");
        source.Unreadable.Add("/top/shut");

        FolderSnapshot snapshot = await FolderSnapshot.TakeAsync(source, "/top", subfolders: true, caseSensitive: true, null, CancellationToken.None);

        Assert.Equal(["", "sub", "sub/deeper"], snapshot.Folders.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["shut"], snapshot.Unreadable);
        Assert.DoesNotContain("/top/loop", source.Listed);
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public async Task Snapshot_WithoutSubfolders_ListsOnlyTheFolderItself()
    {
        MemoryFileSource source = new MemoryFileSource().Folder("/top").File("/top/a").Folder("/top/sub").File("/top/sub/b");

        FolderSnapshot snapshot = await FolderSnapshot.TakeAsync(source, "/top", subfolders: false, caseSensitive: true, null, CancellationToken.None);

        Assert.Equal([""], snapshot.Folders.Keys);
        Assert.Equal(["/top"], source.Listed);
    }

    [Fact]
    public async Task Snapshot_StopsAtItsLimit_AndSaysSo()
    {
        MemoryFileSource source = new MemoryFileSource().Folder("/top");
        for (int i = 0; i < 5; i++)
        {
            source.Folder($"/top/{i}");
            for (int j = 0; j < 5; j++) source.File($"/top/{i}/{j}");
        }

        FolderSnapshot snapshot = await FolderSnapshot.TakeAsync(source, "/top", subfolders: true, caseSensitive: true, null, CancellationToken.None, limit: 12);

        Assert.True(snapshot.Truncated);
        Assert.True(FolderComparison.Compare(snapshot, snapshot).Incomplete);
    }

    [Fact]
    public async Task Snapshot_ThatCannotListTheTopFolder_Fails()
    {
        MemoryFileSource source = new MemoryFileSource().Folder("/top");
        source.Unreadable.Add("/top");

        await Assert.ThrowsAsync<IOException>(() => FolderSnapshot.TakeAsync(source, "/top", true, true, null, CancellationToken.None));
    }
}

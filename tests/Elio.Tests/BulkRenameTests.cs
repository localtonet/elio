using Elio.UI.Files;

namespace Elio.Tests;

public sealed class BulkRenameTests
{
    private static BulkRenamePlan Plan(string[] names, BulkRenameOptions options, params string[] others) =>
        BulkRename.Plan(names, [.. names, .. others], options);

    private static string[] New(BulkRenamePlan plan) => [.. plan.Entries.Select(e => e.New)];

    // Carries the steps out on a folder that does not care about case, the stricter of the two kinds, and refuses any
    // step that would put a file on top of another or move one that is not there.
    private static string[] Apply(IEnumerable<string> folder, IEnumerable<(string From, string To)> steps)
    {
        var names = new HashSet<string>(folder, StringComparer.Ordinal);
        var ignoringCase = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names) ignoringCase[name] = ignoringCase.GetValueOrDefault(name) + 1;

        foreach ((string from, string to) in steps)
        {
            Assert.Contains(from, names);
            bool onlyCase = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
            Assert.True(onlyCase || !ignoringCase.ContainsKey(to), $"{from} -> {to} would land on a name that is in use");

            names.Remove(from);
            if (--ignoringCase[from] == 0) ignoringCase.Remove(from);
            names.Add(to);
            ignoringCase[to] = ignoringCase.GetValueOrDefault(to) + 1;
        }

        return [.. names.Order(StringComparer.Ordinal)];
    }

    [Fact]
    public void FindReplace_ReplacesTextInTheNameAndKeepsTheExtension()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Find = "img", Replace = "photo" };
        BulkRenamePlan plan = Plan(["IMG_001.jpg", "my-img.png", "notes.txt"], options);

        Assert.Equal(["photo_001.jpg", "my-photo.png", "notes.txt"], New(plan));
        Assert.Equal(2, plan.Changes);
        Assert.False(plan.HasProblems);
    }

    [Fact]
    public void FindReplace_LeavesTheExtensionAloneUnlessAsked()
    {
        var keep = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Find = "jpg", Replace = "png" };
        Assert.Equal(["photo.jpg"], New(Plan(["photo.jpg"], keep)));

        // Only the name in front of the extension is looked through.
        Assert.Equal(["png.jpg"], New(Plan(["jpg.jpg"], keep)));

        BulkRenamePlan whole = Plan(["photo.jpg"], keep with { KeepExtension = false });
        Assert.Equal(["photo.png"], New(whole));
    }

    [Fact]
    public void FindReplace_CanBeCaseSensitive()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Find = "a", Replace = "X", IgnoreCase = false };
        Assert.Equal(["XA", "bX"], New(Plan(["aA", "ba"], options)));
    }

    [Fact]
    public void FindReplace_WithNothingToFind_ChangesNothing()
    {
        BulkRenamePlan plan = Plan(["a", "b"], new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Replace = "x" });

        Assert.Equal(0, plan.Changes);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void FindReplace_Regex_FillsInTheGroups()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Regex = true, Find = @"(\d{4})-(\d{2})-(\d{2})", Replace = "$3.$2.$1" };
        BulkRenamePlan plan = Plan(["report 2024-05-17.pdf", "2023-01-02.txt"], options);

        Assert.Equal(["report 17.05.2024.pdf", "02.01.2023.txt"], New(plan));
    }

    [Fact]
    public void FindReplace_Regex_IsNotCaseSensitiveByDefault()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Regex = true, Find = "^img", Replace = "pic" };
        Assert.Equal(["pic1.jpg"], New(Plan(["IMG1.jpg"], options)));
        Assert.Equal(["IMG1.jpg"], New(Plan(["IMG1.jpg"], options with { IgnoreCase = false })));
    }

    [Fact]
    public void FindReplace_Regex_ThatIsNotOne_IsAProblemForEveryName()
    {
        BulkRenamePlan plan = Plan(["a", "b"], new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Regex = true, Find = "(" });

        Assert.All(plan.Entries, e => Assert.NotNull(e.Problem));
        Assert.Equal(["a", "b"], New(plan));
        Assert.Empty(plan.Steps);
        Assert.True(plan.HasProblems);
    }

    [Fact]
    public void FindReplace_Regex_ThatTakesForever_IsAProblemAndNotAnException()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Regex = true, Find = "^(a+)+$", Replace = "x", KeepExtension = false };
        BulkRenamePlan plan = Plan([new string('a', 64) + "!", "b"], options);

        Assert.NotNull(plan.Entries[0].Problem);
        Assert.Null(plan.Entries[1].Problem);
    }

    [Fact]
    public void Number_UsesTheTemplateAndKeepsTheExtension()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "{name}-{n}", Pad = 3 };
        BulkRenamePlan plan = Plan(["a.txt", "b.txt", "c"], options);

        Assert.Equal(["a-001.txt", "b-002.txt", "c-003"], New(plan));
    }

    [Fact]
    public void Number_FollowsTheOrderGivenAndTheStartAndStep()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "pic{n}", Start = 10, Step = 5 };
        BulkRenamePlan plan = Plan(["zeta.png", "alpha.png", "mid.png"], options);

        Assert.Equal(["pic10.png", "pic15.png", "pic20.png"], New(plan));
    }

    [Fact]
    public void Number_WithTheExtensionInTheTemplate_PlacesItThere()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "{n}_{name}{ext}", Pad = 2 };
        Assert.Equal(["01_a.tar.gz"], New(Plan(["a.tar.gz"], options)));

        // The extension is the template's to place, so it does not come twice and not where it was not asked for.
        Assert.Equal(["x.txt-1"], New(Plan(["x.txt"], new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "{name}{ext}-{n}" })));
    }

    [Fact]
    public void Number_WithoutKeepingTheExtension_TreatsTheWholeNameAsTheName()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "{name}-{n}", KeepExtension = false };
        Assert.Equal(["a.txt-1"], New(Plan(["a.txt"], options)));
    }

    [Fact]
    public void Number_DoesNotReadWhatIsPutInAsAPlaceholder()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "{name}!{n}" };
        Assert.Equal(["{n}{ext}!1"], New(Plan(["{n}{ext}"], options)));
    }

    [Fact]
    public void Number_CountsDownAndPadsNegativeNumbers()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "{n}", Start = 2, Step = -2, Pad = 3 };
        Assert.Equal(["002", "000", "-002"], New(Plan(["a", "b", "c"], options)));
    }

    [Theory]
    [InlineData(BulkRenameCase.Lower, "Résumé Final.PDF", "résumé final.PDF")]
    [InlineData(BulkRenameCase.Upper, "Résumé Final.pdf", "RÉSUMÉ FINAL.pdf")]
    [InlineData(BulkRenameCase.Title, "the quick_BROWN-fox (don't).txt", "The Quick_Brown-Fox (Don't).txt")]
    [InlineData(BulkRenameCase.Title, "2nd place.txt", "2nd Place.txt")]
    public void Case_ChangesTheNameAndNotTheExtension(BulkRenameCase how, string name, string expected)
    {
        Assert.Equal([expected], New(Plan([name], new BulkRenameOptions { Mode = BulkRenameMode.Case, Case = how })));
    }

    [Fact]
    public void Case_WithoutKeepingTheExtension_ChangesItToo() =>
        Assert.Equal(["PHOTO.JPG"], New(Plan(["photo.jpg"], new BulkRenameOptions { Mode = BulkRenameMode.Case, Case = BulkRenameCase.Upper, KeepExtension = false })));

    [Fact]
    public void Case_OnlyInCase_IsAnOrdinaryRename()
    {
        BulkRenamePlan plan = Plan(["Readme", "NOTES"], new BulkRenameOptions { Mode = BulkRenameMode.Case, Case = BulkRenameCase.Lower });

        Assert.False(plan.HasProblems);
        Assert.Equal([("Readme", "readme"), ("NOTES", "notes")], plan.Steps);
    }

    [Fact]
    public void Affix_PutsTheSuffixBeforeTheExtension()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Affix, Prefix = "old_", Suffix = "_v2" };
        Assert.Equal(["old_report_v2.pdf", "old_.bashrc_v2", "old_Makefile_v2"], New(Plan(["report.pdf", ".bashrc", "Makefile"], options)));
    }

    [Fact]
    public void Affix_WithoutKeepingTheExtension_PutsTheSuffixAtTheEnd() =>
        Assert.Equal(["report.pdf_v2"], New(Plan(["report.pdf"], new BulkRenameOptions { Mode = BulkRenameMode.Affix, Suffix = "_v2", KeepExtension = false })));

    [Fact]
    public void Unicode_IsKeptAsItIs()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Find = "写真", Replace = "photo" };
        Assert.Equal(["photo-東京.jpg", "😀 photo.png"], New(Plan(["写真-東京.jpg", "😀 写真.png"], options)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a\0b")]
    [InlineData(".")]
    [InlineData("..")]
    public void ANameThatCannotBeUsed_IsAProblemAndNotAStep(string bad)
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Regex = true, Find = "^.*$", Replace = bad, KeepExtension = false };
        BulkRenamePlan plan = Plan(["a"], options);

        Assert.NotNull(plan.Entries[0].Problem);
        Assert.Empty(plan.Steps);
        Assert.Equal(0, plan.Changes);
    }

    [Fact]
    public void ANameThatIsTooLong_IsAProblemWhenCountedInBytes()
    {
        var replace = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Regex = true, Find = "^.*$", KeepExtension = false };

        Assert.Null(Plan(["a"], replace with { Replace = new string('a', 255) }).Entries[0].Problem);
        Assert.NotNull(Plan(["a"], replace with { Replace = new string('a', 256) }).Entries[0].Problem);

        // 85 characters of three bytes each are 255 bytes. 86 are over.
        Assert.Null(Plan(["a"], replace with { Replace = new string('日', 85) }).Entries[0].Problem);
        Assert.NotNull(Plan(["a"], replace with { Replace = new string('日', 86) }).Entries[0].Problem);
    }

    [Fact]
    public void ANameLeftEmptyByTheReplacement_IsAProblem()
    {
        BulkRenamePlan plan = Plan(["abc"], new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Find = "abc" });

        Assert.NotNull(plan.Entries[0].Problem);
    }

    [Fact]
    public void ANameThatIsAlreadyInTheFolder_IsAProblem()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Find = "a", Replace = "b" };
        BulkRenamePlan plan = Plan(["a.txt", "c.txt"], options, "b.txt");

        Assert.NotNull(plan.Entries[0].Problem);
        Assert.Null(plan.Entries[1].Problem);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void ANameThatIsAlreadyInTheFolder_IsFoundWhateverTheCase()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Find = ".txt", Replace = string.Empty, KeepExtension = false };
        BulkRenamePlan plan = Plan(["readme.txt"], options, "README");

        Assert.NotNull(plan.Entries[0].Problem);
    }

    [Fact]
    public void TwoFilesGettingTheSameName_AreBothAProblem()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Find = "-copy", Replace = string.Empty };
        BulkRenamePlan plan = Plan(["a-copy.txt", "a-copy-copy.txt", "b.txt"], options);

        Assert.NotNull(plan.Entries[0].Problem);
        Assert.NotNull(plan.Entries[1].Problem);
        Assert.Null(plan.Entries[2].Problem);
    }

    [Fact]
    public void TwoNamesThatOnlyDifferInCase_AreAProblemEvenIfTheyAreNotTheSame()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Find = "x", Replace = string.Empty };
        BulkRenamePlan plan = Plan(["xa", "xA"], options);

        Assert.All(plan.Entries, e => Assert.NotNull(e.Problem));
    }

    [Fact]
    public void ANameWantedByAFileThatStaysPut_IsAProblemForTheOtherOnly()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Find = "b", Replace = "a" };
        BulkRenamePlan plan = Plan(["a", "b"], options);

        Assert.Null(plan.Entries[0].Problem);
        Assert.NotNull(plan.Entries[1].Problem);
    }

    [Fact]
    public void ANameWantedByAFileThatCannotBeRenamed_IsAProblemToo()
    {
        // Both would be called "x", and the first of them is not renamed at all.
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "x", KeepExtension = false };
        BulkRenamePlan plan = BulkRename.Plan(["x", "y"], ["x", "y"], options);

        Assert.Null(plan.Entries[0].Problem);
        Assert.NotNull(plan.Entries[1].Problem);
    }

    [Fact]
    public void ANameFreedByAnotherRename_CanBeTaken()
    {
        // 2 -> 1 -> and 3 -> 2: the names are in use only until the files that have them move.
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "{n}", Start = 1, KeepExtension = false };
        BulkRenamePlan plan = Plan(["2", "3", "4"], options);

        Assert.False(plan.HasProblems);
        Assert.Equal(["1", "2", "3"], New(plan));
    }

    [Fact]
    public void Steps_GoInAnOrderThatNeverLandsOnAFileStillToMove_Chain()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "{n}", Start = 2, KeepExtension = false };
        BulkRenamePlan plan = Plan(["1", "2", "3"], options);

        // Each name goes to the next one, so the last has to move first.
        Assert.Equal([("3", "4"), ("2", "3"), ("1", "2")], plan.Steps);
        Assert.Equal(["2", "3", "4"], Apply(["1", "2", "3"], plan.Steps));
    }

    [Fact]
    public void Steps_ForAChainDownward_KeepTheOrderGiven()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "{n}", Start = 1, KeepExtension = false };
        BulkRenamePlan plan = Plan(["2", "3", "4"], options);

        Assert.Equal([("2", "1"), ("3", "2"), ("4", "3")], plan.Steps);
    }

    [Fact]
    public void Steps_ForASwap_GoThroughATemporaryName()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "{n}", Start = 1, KeepExtension = false };
        BulkRenamePlan plan = Plan(["2", "1"], options);

        Assert.False(plan.HasProblems);
        Assert.Equal(3, plan.Steps.Count);
        Assert.StartsWith(".elio-rename-", plan.Steps[0].To);
        Assert.Equal(["1", "2"], Apply(["1", "2"], plan.Steps));
    }

    [Fact]
    public void Steps_ForACycleOfThree_ComeOutRight()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "{n}", Start = 1, KeepExtension = false };
        BulkRenamePlan plan = Plan(["2", "3", "1"], options);

        Assert.Equal(4, plan.Steps.Count);
        Assert.Equal(["1", "2", "3"], Apply(["1", "2", "3"], plan.Steps));
    }

    [Fact]
    public void Steps_ForACycleOfThreeNextToASwap_ComeOutRight()
    {
        // A swap of 2 and 1, and 4 -> 3 -> 5 -> 4 round a second circle.
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "{n}", Start = 1, KeepExtension = false };
        string[] names = ["2", "1", "4", "5", "3"];
        BulkRenamePlan plan = Plan(names, options);

        Assert.False(plan.HasProblems);
        Assert.Equal(["1", "2", "3", "4", "5"], Apply(names, plan.Steps));
    }

    [Fact]
    public void Steps_ForSeveralCycles_UseADifferentTemporaryNameForEach()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "{n}", Start = 1, KeepExtension = false };
        string[] names = ["2", "1", "4", "3"];
        BulkRenamePlan plan = Plan(names, options);

        string[] temporaries = [.. plan.Steps.Where(s => s.To.StartsWith(".elio-rename-")).Select(s => s.To)];
        Assert.Equal(2, temporaries.Length);
        Assert.Equal(2, temporaries.Distinct().Count());
        Assert.Equal(["1", "2", "3", "4"], Apply(names, plan.Steps));
    }

    [Fact]
    public void TemporaryNames_NeverCollideWithTheFolder()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "{n}", Start = 1, KeepExtension = false };
        BulkRenamePlan plan = Plan(["2", "1"], options, ".elio-rename-1", ".ELIO-RENAME-2");

        Assert.Equal(".elio-rename-3", plan.Steps[0].To);
        Assert.Equal(["1", "2"], Apply(["1", "2", ".elio-rename-1", ".ELIO-RENAME-2"], plan.Steps).Where(n => !n.StartsWith(".", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Steps_LeaveOutWhatDoesNotChangeAndWhatCannotBeUsed()
    {
        var options = new BulkRenameOptions { Mode = BulkRenameMode.FindReplace, Find = "a", Replace = "b" };
        BulkRenamePlan plan = Plan(["a", "xyz", "ca"], options, "cb");

        Assert.Equal([("a", "b")], plan.Steps);
        Assert.Equal(1, plan.Changes);
        Assert.True(plan.HasProblems);
    }

    [Fact]
    public void ACaseOnlyRename_NextToTheSameNameInAnotherCase_StillWorksWhenItIsTheOneBeingRenamed()
    {
        // Folders on this kind of file system are checked without regard to case, and a file may be renamed to its own name in another case.
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Case, Case = BulkRenameCase.Upper };
        BulkRenamePlan plan = Plan(["a.txt"], options);

        Assert.Equal([("a.txt", "A.txt")], plan.Steps);
        Assert.Equal(["A.txt"], Apply(["a.txt"], plan.Steps));
    }

    [Fact]
    public void NoNames_GiveAnEmptyPlan()
    {
        BulkRenamePlan plan = BulkRename.Plan([], [], new BulkRenameOptions());

        Assert.Empty(plan.Entries);
        Assert.Empty(plan.Steps);
        Assert.False(plan.HasProblems);
        Assert.Equal(0, plan.Changes);
    }

    [Fact]
    public void ALargeFolder_IsPlannedAtOnce()
    {
        string[] names = [.. Enumerable.Range(1, 5000).Select(i => $"file{i}.txt")];
        var options = new BulkRenameOptions { Mode = BulkRenameMode.Number, Template = "file{n}", Start = 2 };
        BulkRenamePlan plan = Plan(names, options);

        Assert.False(plan.HasProblems);
        Assert.Equal(5000, plan.Steps.Count);
        Assert.Equal(names.Select((_, i) => $"file{i + 2}.txt").Order(StringComparer.Ordinal), Apply(names, plan.Steps));
    }
}

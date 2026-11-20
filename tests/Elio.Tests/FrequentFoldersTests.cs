using System.Text.Json;
using Elio.Core;

namespace Elio.Tests;

public sealed class FrequentFoldersTests : TempDataDirectoryTest
{
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private string File => Path.Combine(DataDirectory, "frequent-folders.json");

    [Fact]
    public void Top_ListsTheMostVisitedFirst()
    {
        var folders = new FrequentFolders(File);
        for (int i = 0; i < 3; i++) folders.Visit("srv", "/var/log", Now);
        folders.Visit("srv", "/etc", Now);
        for (int i = 0; i < 2; i++) folders.Visit("srv", "/home/me", Now);

        Assert.Equal(["/var/log", "/home/me", "/etc"], folders.Top("srv", 10, Now));
    }

    [Fact]
    public void Top_StopsAtTheCount()
    {
        var folders = new FrequentFolders(File);
        foreach (string path in new[] { "/a", "/b", "/c", "/d" }) folders.Visit("srv", path, Now);

        Assert.Equal(2, folders.Top("srv", 2, Now).Count);
        Assert.Empty(folders.Top("srv", 0, Now));
        Assert.Empty(folders.Top("srv", -1, Now));
    }

    // A is opened lately, B a hundred days ago, so that B is worth a quarter of a visit each. Whether A comes first tells
    // which weight A's age gets: the numbers are chosen so that the next weight up or down would turn the answer round.
    [Theory]
    [InlineData(0, 1, 12, true)]
    [InlineData(60, 1, 12, true)]
    [InlineData(61, 1, 12, false)]
    [InlineData(61, 1, 7, true)]
    [InlineData(60 * 24, 1, 7, true)]
    [InlineData(60 * 24 + 1, 1, 7, false)]
    [InlineData(60 * 24 + 1, 2, 3, true)]
    [InlineData(60 * 24 * 7, 2, 3, true)]
    [InlineData(60 * 24 * 7 + 1, 2, 3, false)]
    public void Top_CountsRecentVisitsForMore(int minutesAgo, int visitsOfA, int visitsOfB, bool aFirst)
    {
        var folders = new FrequentFolders(File);
        for (int i = 0; i < visitsOfB; i++) folders.Visit("srv", "/b", Now.AddDays(-100));
        for (int i = 0; i < visitsOfA; i++) folders.Visit("srv", "/a", Now.AddMinutes(-minutesAgo));

        Assert.Equal(aFirst ? ["/a", "/b"] : ["/b", "/a"], folders.Top("srv", 2, Now));
    }

    [Fact]
    public void Top_LetsAFolderOpenedOftenOutweighAFreshOne()
    {
        var folders = new FrequentFolders(File);
        for (int i = 0; i < 20; i++) folders.Visit("srv", "/favorite", Now.AddDays(-3));
        folders.Visit("srv", "/just-now", Now);

        // 20 x 0.5 against 1 x 4.
        Assert.Equal(["/favorite", "/just-now"], folders.Top("srv", 2, Now));
    }

    [Fact]
    public void Top_LetsAnOldFolderFadeBehindARecentOne()
    {
        var folders = new FrequentFolders(File);
        for (int i = 0; i < 4; i++) folders.Visit("srv", "/last-year", Now.AddDays(-400));
        for (int i = 0; i < 3; i++) folders.Visit("srv", "/this-week", Now.AddDays(-2));

        // 4 x 0.25 against 3 x 0.5.
        Assert.Equal(["/this-week", "/last-year"], folders.Top("srv", 2, Now));
    }

    [Fact]
    public void Top_BreaksATieWithTheMostRecent()
    {
        var folders = new FrequentFolders(File);
        folders.Visit("srv", "/a", Now.AddMinutes(-30));
        folders.Visit("srv", "/b", Now.AddMinutes(-10));
        folders.Visit("srv", "/c", Now.AddMinutes(-20));

        Assert.Equal(["/b", "/c", "/a"], folders.Top("srv", 3, Now));
    }

    [Fact]
    public void Top_ForAnUnknownServer_IsEmpty()
    {
        var folders = new FrequentFolders(File);
        folders.Visit("srv", "/a", Now);

        Assert.Empty(folders.Top("other", 5, Now));
        Assert.Empty(folders.Top("", 5, Now));
    }

    [Fact]
    public void Servers_KeepTheirOwnFolders()
    {
        var folders = new FrequentFolders(File);
        folders.Visit("one", "/a", Now);
        folders.Visit("two", "/b", Now);
        folders.Visit("local", @"C:\Users\me", Now);

        Assert.Equal(["/a"], folders.Top("one", 5, Now));
        Assert.Equal(["/b"], folders.Top("two", 5, Now));
        Assert.Equal([@"C:\Users\me"], folders.Top("local", 5, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Visit_IgnoresAnEmptyPath(string? path)
    {
        var folders = new FrequentFolders(File);
        folders.Visit("srv", path!, Now);
        folders.Visit("", "/a", Now);

        Assert.Empty(folders.Top("srv", 5, Now));
        Assert.Empty(folders.Top("", 5, Now));
    }

    [Theory]
    [InlineData("/var/log/", "/var/log")]
    [InlineData("/var/log//", "/var/log")]
    [InlineData("/", "/")]
    [InlineData("//", "/")]
    [InlineData(@"C:\Users\me\", @"C:\Users\me")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("C:/", "C:/")]
    [InlineData("C:", "C:")]
    public void Visit_TreatsAFolderTheSameWithOrWithoutTheSlashAtTheEnd(string path, string kept)
    {
        var folders = new FrequentFolders(File);
        folders.Visit("srv", path, Now);

        Assert.Equal([kept], folders.Top("srv", 5, Now));
    }

    [Fact]
    public void Visit_CountsTheSameFolderOnce()
    {
        var folders = new FrequentFolders(File);
        folders.Visit("srv", "/a/", Now);
        folders.Visit("srv", "/a", Now);
        folders.Visit("srv", "/b", Now);

        Assert.Equal(["/a", "/b"], folders.Top("srv", 5, Now));
    }

    [Fact]
    public void Forget_TakesAFolderOff()
    {
        var folders = new FrequentFolders(File);
        folders.Visit("srv", "/a", Now);
        folders.Visit("srv", "/b", Now);

        folders.Forget("srv", "/a/");
        folders.Forget("srv", "/never-there");
        folders.Forget("nobody", "/a");
        folders.Forget("srv", "");

        Assert.Equal(["/b"], folders.Top("srv", 5, Now));
    }

    [Fact]
    public void ALongList_KeepsTheFoldersThatCountMost()
    {
        var folders = new FrequentFolders(File);
        for (int i = 0; i < 350; i++) folders.Visit("srv", $"/dir{i}", Now.AddSeconds(i));
        for (int i = 0; i < 5; i++) folders.Visit("srv", "/loved", Now);

        IReadOnlyList<string> top = folders.Top("srv", 1000, Now.AddSeconds(400));
        Assert.Equal(300, top.Count);
        Assert.Equal("/loved", top[0]);

        // The ones that went are among the oldest of the once-opened ones, and the newest stayed.
        Assert.Contains("/dir349", top);
        Assert.DoesNotContain("/dir0", top);
    }

    [Fact]
    public void ALongList_NeverDropsTheFolderJustOpened()
    {
        var folders = new FrequentFolders(File);
        for (int i = 0; i < 300; i++)
        {
            folders.Visit("srv", $"/dir{i}", Now);
            folders.Visit("srv", $"/dir{i}", Now);
        }

        folders.Visit("srv", "/new", Now.AddDays(-30));

        Assert.Contains("/new", folders.Top("srv", 1000, Now.AddDays(-30)));
    }

    [Fact]
    public void Counts_AgeSoTheyDoNotGrowWithoutEnd()
    {
        var folders = new FrequentFolders(File);
        for (int i = 0; i < 5000; i++) folders.Visit("srv", "/busy", Now);
        folders.Visit("srv", "/other", Now);
        folders.Flush();

        using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText(File));
        double busy = document.RootElement.GetProperty("Servers").GetProperty("srv").EnumerateArray()
            .First(e => e.GetProperty("Path").GetString() == "/busy").GetProperty("Visits").GetDouble();

        Assert.InRange(busy, 800, 1001);
        Assert.Equal(["/busy", "/other"], folders.Top("srv", 5, Now));
    }

    [Fact]
    public void Aging_KeepsWhatWasJustOpenedHoweverLittleItCounts()
    {
        var folders = new FrequentFolders(File);
        for (int i = 0; i < 2000; i++) folders.Visit("srv", "/busy", Now);
        folders.Visit("srv", "/once", Now);

        // Its one visit is worth less than one once the counts were made smaller, and it is still there.
        Assert.Contains("/once", folders.Top("srv", 5, Now));
    }

    [Fact]
    public void Flush_WritesTheListAndANewInstanceReadsItBack()
    {
        var folders = new FrequentFolders(File);
        folders.Visit("srv", "/var/log", Now);
        folders.Visit("srv", "/var/log", Now);
        folders.Visit("srv", "/étc/日本語 ünï", Now.AddMinutes(-5));
        folders.Visit("local", @"C:\Users\me\Documents", Now);
        folders.Flush();

        Assert.True(System.IO.File.Exists(File));

        var again = new FrequentFolders(File);
        Assert.Equal(["/var/log", "/étc/日本語 ünï"], again.Top("srv", 5, Now));
        Assert.Equal([@"C:\Users\me\Documents"], again.Top("local", 5, Now));
    }

    [Fact]
    public void Flush_KeepsTheTimeOfTheLastVisit()
    {
        var folders = new FrequentFolders(File);
        folders.Visit("srv", "/old", Now.AddDays(-30));
        folders.Visit("srv", "/old", Now.AddDays(-30));
        folders.Visit("srv", "/new", Now);
        folders.Flush();

        // /old is worth 2 x 0.25 and /new 1 x 4, which only comes out right if the times came back as they were.
        Assert.Equal(["/new", "/old"], new FrequentFolders(File).Top("srv", 5, Now));
    }

    [Fact]
    public void Flush_WithNothingNewDoesNotWrite()
    {
        var folders = new FrequentFolders(File);
        folders.Flush();
        Assert.False(System.IO.File.Exists(File));

        folders.Visit("srv", "/a", Now);
        folders.Flush();
        Assert.True(System.IO.File.Exists(File));

        System.IO.File.Delete(File);
        folders.Flush();

        Assert.False(System.IO.File.Exists(File));
    }

    [Fact]
    public void Forget_IsKeptToo()
    {
        var folders = new FrequentFolders(File);
        folders.Visit("srv", "/a", Now);
        folders.Visit("srv", "/b", Now);
        folders.Flush();
        folders.Forget("srv", "/a");
        folders.Flush();

        Assert.Equal(["/b"], new FrequentFolders(File).Top("srv", 5, Now));
    }

    [Fact]
    public void AMissingFile_IsAnEmptyList()
    {
        var folders = new FrequentFolders(Path.Combine(DataDirectory, "never", "was.json"));

        Assert.Empty(folders.Top("srv", 5, Now));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{\"SchemaVersion\": 1, \"Servers\": 5}")]
    [InlineData("{\"SchemaVersion\": \"one\"}")]
    public void ADamagedFile_IsAnEmptyListAndNeverAnException(string content)
    {
        System.IO.File.WriteAllText(File, content);

        var folders = new FrequentFolders(File);
        Assert.Empty(folders.Top("srv", 5, Now));

        // And it can be used and saved again.
        folders.Visit("srv", "/a", Now);
        folders.Flush();
        Assert.Equal(["/a"], new FrequentFolders(File).Top("srv", 5, Now));
    }

    [Fact]
    public void AFileWithHoles_IsReadAsFarAsItGoes()
    {
        System.IO.File.WriteAllText(File, """
            {
              "SchemaVersion": 1,
              "Servers": {
                "srv": [
                  { "Path": "/good", "Visits": 3, "LastUtc": "2026-06-15T11:00:00Z" },
                  { "Path": "", "Visits": 9, "LastUtc": "2026-06-15T11:00:00Z" },
                  { "Path": "/nobody-came", "Visits": 0, "LastUtc": "2026-06-15T11:00:00Z" },
                  null
                ],
                "empty": null
              }
            }
            """);

        var folders = new FrequentFolders(File);

        Assert.Equal(["/good"], folders.Top("srv", 5, Now));
        Assert.Empty(folders.Top("empty", 5, Now));
    }

    [Fact]
    public void AFileFromANewerVersion_IsNotWrittenOver()
    {
        const string Newer = "{\"SchemaVersion\": 99, \"Servers\": {\"srv\": [{\"Path\": \"/future\", \"Visits\": 1, \"LastUtc\": \"2026-06-15T11:00:00Z\"}]}}";
        System.IO.File.WriteAllText(File, Newer);

        var folders = new FrequentFolders(File);
        folders.Visit("srv", "/a", Now);
        folders.Flush();

        Assert.Equal(Newer, System.IO.File.ReadAllText(File));
        Assert.Equal(["/a"], folders.Top("srv", 5, Now));
    }

    [Fact]
    public void AFileThatCannotBeWritten_DoesNotStopAnything()
    {
        // A folder where the file should be.
        Directory.CreateDirectory(File);

        var folders = new FrequentFolders(File);
        folders.Visit("srv", "/a", Now);
        folders.Flush();

        Assert.Equal(["/a"], folders.Top("srv", 5, Now));
    }

    [Fact]
    public void ManyThreads_CanVisitAtOnce()
    {
        var folders = new FrequentFolders(File);
        Parallel.For(0, 400, i =>
        {
            folders.Visit("srv", $"/dir{i % 20}", Now);
            folders.Top("srv", 5, Now);
        });
        folders.Flush();

        Assert.Equal(20, folders.Top("srv", 100, Now).Count);
        Assert.Equal(20, new FrequentFolders(File).Top("srv", 100, Now).Count);
    }

    [Fact]
    public async Task Visit_SavesBySelfAfterAShortWhile()
    {
        var folders = new FrequentFolders(File);
        folders.Visit("srv", "/a");

        // Written two seconds after the last visit, and not before.
        Assert.False(System.IO.File.Exists(File));
        for (int i = 0; i < 40 && !System.IO.File.Exists(File); i++) await Task.Delay(250);

        Assert.True(System.IO.File.Exists(File));
        Assert.Equal(["/a"], new FrequentFolders(File).Top("srv", 5));
    }

    [Fact]
    public void Shared_LivesInTheDataFolder()
    {
        // The data folder of the tests, not the real one: the module initializer sees to that.
        Assert.StartsWith(TestData.Root, Path.GetFullPath(ElioPaths.DataDirectory));
        Assert.Empty(FrequentFolders.Shared.Top("nobody-in-the-tests", 5));
    }
}

using System.Collections.Concurrent;
using Elio.UI.Files;

namespace Elio.Tests;

/// <summary>The walk through the folders below one, on a tree held in memory, so that nothing here touches a disk or a server.</summary>
public sealed class FileTreeScannerTests
{
    /// <summary>Folders and what is in them, written as "/a/b" => names, where a name ending in "/" is a folder and one ending in "@" a link to a folder.</summary>
    private sealed class Tree(Dictionary<string, string[]> folders) : IFileSource
    {
        public ConcurrentBag<string> Listed { get; } = [];

        public Func<string, Exception?>? Fails { get; init; }

        public Func<string, CancellationToken, Task>? Before { get; init; }

        public bool IsRemote => false;

        public Task<string> GetStartPathAsync(CancellationToken cancellation) => Task.FromResult("/");

        public async Task<List<FileRow>> ListAsync(string path, CancellationToken cancellation)
        {
            Listed.Add(path);
            if (Before is not null) await Before(path, cancellation);
            if (Fails?.Invoke(path) is Exception failure) throw failure;

            string[] names = folders.TryGetValue(path, out string[]? found) ? found : [];
            return names.Select(name =>
            {
                bool link = name.EndsWith('@');
                bool folder = link || name.EndsWith('/');
                string clean = name.TrimEnd('/', '@');
                return Row(clean, Combine(path, clean), folder, link);
            }).ToList();
        }

        public string? Parent(string path) => path == "/" ? null : path[..path.LastIndexOf('/')].Length == 0 ? "/" : path[..path.LastIndexOf('/')];

        public string Combine(string folder, string name) => folder == "/" ? "/" + name : folder + "/" + name;

        public Task CreateFolderAsync(string path, CancellationToken cancellation) => throw new NotSupportedException();

        public Task RenameAsync(FileRow row, string newName, CancellationToken cancellation) => throw new NotSupportedException();

        public Task DeleteAsync(FileRow row, CancellationToken cancellation) => throw new NotSupportedException();
    }

    private static FileRow Row(string name, string path, bool folder = false, bool link = false) =>
        new(name, path, 0, DateTime.MinValue, folder, link, string.Empty, null);

    private static List<FileRow> Top(Tree tree, string folder = "/") => tree.ListAsync(folder, CancellationToken.None).Result;

    private static async Task<(List<FileRow> Rows, FileScanSummary Summary)> Scan(Tree tree, int parallelism = 1, int limit = FileTreeScanner.DefaultLimit, CancellationToken cancellation = default)
    {
        var rows = new ConcurrentBag<FileRow>();
        FileScanSummary summary = await FileTreeScanner.ScanAsync(tree, Top(tree), '/', batch => { foreach (FileRow row in batch) rows.Add(row); }, parallelism, limit, cancellation);
        return ([.. rows], summary);
    }

    private static Tree Sample() => new(new Dictionary<string, string[]>
    {
        ["/"] = ["docs/", "src/", "readme.md"],
        ["/docs"] = ["guide.md", "img/"],
        ["/docs/img"] = ["logo.png"],
        ["/src"] = ["main.cs", "util/"],
        ["/src/util"] = ["strings.cs", "deep/"],
        ["/src/util/deep"] = ["deepest.txt"],
    });

    [Fact]
    public async Task Scan_FindsEverythingBelow_WithTheFolderEachWasFoundIn()
    {
        (List<FileRow> rows, FileScanSummary summary) = await Scan(Sample());

        Assert.Equal(
            [
                "deep @ src/util",
                "deepest.txt @ src/util/deep",
                "guide.md @ docs",
                "img @ docs",
                "logo.png @ docs/img",
                "main.cs @ src",
                "strings.cs @ src/util",
                "util @ src",
            ],
            rows.Select(r => $"{r.Name} @ {r.Where}").Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new FileScanSummary(Folders: 5, Items: 8, Unreadable: 0, Truncated: false), summary);
    }

    [Fact]
    public async Task Scan_DoesNotIncludeTheFoldersItStartsFrom_OnlyWhatIsInThem()
    {
        (List<FileRow> rows, _) = await Scan(Sample());

        Assert.DoesNotContain(rows, r => r.Name == "readme.md");
        Assert.DoesNotContain(rows, r => r.Name == "docs" && r.Where == string.Empty);
    }

    [Fact]
    public async Task Scan_GivesEachResultTheFullPathItCanBeFoundAt()
    {
        (List<FileRow> rows, _) = await Scan(Sample());

        Assert.Equal("/src/util/deep/deepest.txt", rows.Single(r => r.Name == "deepest.txt").Path);
    }

    [Fact]
    public async Task Scan_WithSeveralFoldersAtOnce_FindsTheSameThings()
    {
        (List<FileRow> one, _) = await Scan(Sample(), parallelism: 1);
        (List<FileRow> four, _) = await Scan(Sample(), parallelism: 4);

        Assert.Equal(one.Select(r => r.Path).Order(StringComparer.Ordinal), four.Select(r => r.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Scan_NeverGoesIntoALink_SoALoopBackToTheStartCannotKeepItGoing()
    {
        var tree = new Tree(new Dictionary<string, string[]>
        {
            ["/"] = ["real/", "loop@"],
            ["/real"] = ["file.txt", "back@"],
            ["/loop"] = ["should-not-be-listed.txt"],
            ["/real/back"] = ["nor-this.txt"],
        });

        (List<FileRow> rows, FileScanSummary summary) = await Scan(tree);

        Assert.Equal(["back", "file.txt"], rows.Select(r => r.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("/loop", tree.Listed);
        Assert.DoesNotContain("/real/back", tree.Listed);
        Assert.False(summary.Truncated);
    }

    [Fact]
    public async Task Scan_AFolderThatCannotBeRead_IsCountedAndTheRestIsStillSearched()
    {
        Tree tree = new(new Dictionary<string, string[]>
        {
            ["/"] = ["locked/", "open/"],
            ["/locked"] = ["secret.txt"],
            ["/open"] = ["found.txt"],
        })
        {
            Fails = path => path == "/locked" ? new UnauthorizedAccessException("no") : null,
        };

        (List<FileRow> rows, FileScanSummary summary) = await Scan(tree);

        Assert.Equal(["found.txt"], rows.Select(r => r.Name));
        Assert.Equal(1, summary.Unreadable);
        Assert.Equal(1, summary.Folders);
    }

    [Fact]
    public async Task Scan_StopsAtTheLimit_AndSaysThereWasMore()
    {
        Tree tree = new(new Dictionary<string, string[]>
        {
            ["/"] = ["a/", "b/"],
            ["/a"] = Enumerable.Range(0, 60).Select(i => $"a{i}.txt").ToArray(),
            ["/b"] = Enumerable.Range(0, 60).Select(i => $"b{i}.txt").ToArray(),
        });

        (List<FileRow> rows, FileScanSummary summary) = await Scan(tree, limit: 100);

        Assert.True(summary.Truncated);
        Assert.Equal(100, summary.Items);
        Assert.True(rows.Count <= 100, $"{rows.Count} rows were passed on");
    }

    [Fact]
    public async Task Scan_ExactlyAtTheLimit_IsNotTruncated()
    {
        Tree tree = new(new Dictionary<string, string[]> { ["/"] = ["a/"], ["/a"] = ["1", "2", "3"] });

        (_, FileScanSummary summary) = await Scan(tree, limit: 3);

        Assert.False(summary.Truncated);
        Assert.Equal(3, summary.Items);
    }

    [Fact]
    public async Task Scan_AFolderThatContainsItselfForever_EndsAtTheDepthLimit()
    {
        // A bind mount does this, and is no link that could be told from a folder.
        var endless = new Dictionary<string, string[]>();
        string path = string.Empty;
        for (int i = 0; i < FileTreeScanner.MaxDepth + 10; i++)
        {
            string parent = path.Length == 0 ? "/" : path;
            path = path.Length == 0 ? "/d" : path + "/d";
            endless[parent] = ["d/"];
        }

        (List<FileRow> rows, FileScanSummary summary) = await Scan(new Tree(endless));

        Assert.True(summary.Truncated);
        Assert.InRange(rows.Count, 1, FileTreeScanner.MaxDepth + 1);
    }

    [Fact]
    public async Task Scan_Cancelled_StopsAndThrows()
    {
        using var cancel = new CancellationTokenSource();
        Tree tree = new(new Dictionary<string, string[]>
        {
            ["/"] = ["a/"],
            ["/a"] = ["b/"],
            ["/a/b"] = ["c/"],
            ["/a/b/c"] = ["file.txt"],
        })
        {
            // The user changes their mind while the second folder is being asked for.
            Before = (path, token) =>
            {
                if (path == "/a/b") cancel.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scan(tree, cancellation: cancel.Token));
        Assert.DoesNotContain("/a/b/c", tree.Listed);
    }

    [Fact]
    public async Task Scan_AlreadyCancelled_AsksForNothing()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Tree tree = Sample();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scan(tree, cancellation: cancelled.Token));
        Assert.All(tree.Listed, path => Assert.Equal("/", path));
    }

    [Fact]
    public async Task Scan_HandsOverWhatEachFolderHeld_AsItGoes()
    {
        var batches = new ConcurrentBag<int>();
        Tree tree = Sample();

        await FileTreeScanner.ScanAsync(tree, Top(tree), '/', batch => batches.Add(batch.Count), 1, FileTreeScanner.DefaultLimit, CancellationToken.None);

        // One call for each folder that held anything: docs (2), docs/img (1), src (2), src/util (2) and src/util/deep (1).
        Assert.Equal([1, 1, 2, 2, 2], batches.Order());
    }

    [Fact]
    public async Task Scan_WritesFoldersWithTheSeparatorItIsGiven()
    {
        Tree tree = Sample();
        var rows = new ConcurrentBag<FileRow>();

        await FileTreeScanner.ScanAsync(tree, Top(tree), '\\', batch => { foreach (FileRow row in batch) rows.Add(row); }, 1, FileTreeScanner.DefaultLimit, CancellationToken.None);

        Assert.Equal("src\\util\\deep", rows.Single(r => r.Name == "deepest.txt").Where);
    }
}

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Elio.Connections;
using Elio.UI.Files;

namespace Elio.Tests;

/// <summary>What the file panel can do on this computer, in folders made for each test.</summary>
public sealed class FileOperationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "elio-tests", Guid.NewGuid().ToString("N"));
    private readonly LocalFileSource _source = new();

    public FileOperationsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // The system cleans its temp folder up in time.
        }
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private static FileRow RowOf(string path, bool directory = false) =>
        new(Path.GetFileName(path), path, 0, DateTime.UtcNow, directory, false, string.Empty, null);

    private string Write(string relative, string text)
    {
        string path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task CreateFile_MakesAnEmptyFile_AndRefusesToReplaceOne()
    {
        string path = P("new.txt");

        await _source.CreateFileAsync(path, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(0, new FileInfo(path).Length);
        await Assert.ThrowsAsync<IOException>(() => _source.CreateFileAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Copy_OfAFile_LeavesTheOriginalAndNeverReplaces()
    {
        string from = Write("a.txt", "hello");

        await _source.CopyAsync(RowOf(from), P("b.txt"), CancellationToken.None);

        Assert.Equal("hello", File.ReadAllText(P("b.txt")));
        Assert.True(File.Exists(from));
        await Assert.ThrowsAsync<IOException>(() => _source.CopyAsync(RowOf(from), P("b.txt"), CancellationToken.None));
    }

    [Fact]
    public async Task Copy_OfAFolder_TakesEverythingInside()
    {
        Write(Path.Combine("src", "one.txt"), "1");
        Write(Path.Combine("src", "deep", "two.txt"), "2");

        await _source.CopyAsync(RowOf(P("src"), directory: true), P("dst"), CancellationToken.None);

        Assert.Equal("1", File.ReadAllText(P("dst", "one.txt")));
        Assert.Equal("2", File.ReadAllText(P("dst", "deep", "two.txt")));
    }

    [Fact]
    public async Task Copy_OfAFolderIntoItself_IsRefused()
    {
        Write(Path.Combine("src", "one.txt"), "1");

        await Assert.ThrowsAsync<IOException>(() => _source.CopyAsync(RowOf(P("src"), directory: true), P("src", "inside"), CancellationToken.None));
    }

    [Fact]
    public async Task Move_OfAFileAndAFolder_GoesToTheNewPath()
    {
        string file = Write("a.txt", "x");
        Write(Path.Combine("folder", "f.txt"), "y");
        Directory.CreateDirectory(P("target"));

        await _source.MoveAsync(RowOf(file), P("target", "a.txt"), CancellationToken.None);
        await _source.MoveAsync(RowOf(P("folder"), directory: true), P("target", "folder"), CancellationToken.None);

        Assert.False(File.Exists(file));
        Assert.Equal("x", File.ReadAllText(P("target", "a.txt")));
        Assert.Equal("y", File.ReadAllText(P("target", "folder", "f.txt")));
    }

    [Fact]
    public async Task Move_OntoAnExistingFile_IsRefused()
    {
        string a = Write("a.txt", "a");
        Write("b.txt", "b");

        await Assert.ThrowsAnyAsync<IOException>(() => _source.MoveAsync(RowOf(a), P("b.txt"), CancellationToken.None));

        Assert.Equal("a", File.ReadAllText(a));
        Assert.Equal("b", File.ReadAllText(P("b.txt")));
    }

    [Fact]
    public async Task Read_StopsAtTheLimit_AndWriteReplacesTheWholeFile()
    {
        string path = Write("t.txt", "0123456789");

        byte[] first = await _source.ReadAsync(path, 4, CancellationToken.None);
        await _source.WriteAsync(path, Encoding.UTF8.GetBytes("new"), CancellationToken.None);

        Assert.Equal("0123", Encoding.UTF8.GetString(first));
        Assert.Equal("new", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(_root, "*.eliopart"));
    }

    [Fact]
    public async Task Stat_OfAMissingFile_IsNull()
    {
        Assert.Null(await _source.StatAsync(P("nothing"), CancellationToken.None));

        string path = Write("x.txt", "abc");
        Assert.Equal(3, (await _source.StatAsync(path, CancellationToken.None))!.Value.Size);
    }

    [Theory]
    [InlineData(ArchiveKind.Zip, ".zip")]
    [InlineData(ArchiveKind.TarGz, ".tar.gz")]
    [InlineData(ArchiveKind.Tar, ".tar")]
    public async Task Compress_ThenExtract_GivesBackTheSameFiles(ArchiveKind kind, string extension)
    {
        Write(Path.Combine("pack", "a.txt"), "alpha");
        Write(Path.Combine("pack", "sub", "b.txt"), "beta");
        Write("c.txt", "gamma");
        string name = "bundle" + extension;

        await _source.CompressAsync(_root, ["pack", "c.txt"], name, kind, CancellationToken.None);
        await _source.ExtractAsync(RowOf(P(name)), P("out"), CancellationToken.None);

        Assert.Equal("alpha", File.ReadAllText(P("out", "pack", "a.txt")));
        Assert.Equal("beta", File.ReadAllText(P("out", "pack", "sub", "b.txt")));
        Assert.Equal("gamma", File.ReadAllText(P("out", "c.txt")));
    }

    [Fact]
    public async Task Compress_ToAnExistingName_KeepsWhatWasThere()
    {
        Write("a.txt", "a");
        Write("bundle.zip", "not really a zip");

        await Assert.ThrowsAnyAsync<IOException>(() => _source.CompressAsync(_root, ["a.txt"], "bundle.zip", ArchiveKind.Zip, CancellationToken.None));

        Assert.Equal("not really a zip", File.ReadAllText(P("bundle.zip")));
    }

    [Fact]
    public async Task Extract_OfATarWithAPathThatLeavesTheFolder_WritesNothingOutside()
    {
        string archive = P("evil.tar");
        await using (FileStream stream = File.Create(archive))
        await using (var writer = new TarWriter(stream))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "../escaped.txt") { DataStream = new MemoryStream("gotcha"u8.ToArray()) };
            await writer.WriteEntryAsync(entry);
        }

        await Assert.ThrowsAsync<IOException>(() => _source.ExtractAsync(RowOf(archive), P("out"), CancellationToken.None));

        Assert.False(File.Exists(P("escaped.txt")));
    }

    [Fact]
    public async Task Extract_OfATarKeepsLinksOut()
    {
        string archive = P("links.tar");
        await using (FileStream stream = File.Create(archive))
        await using (var writer = new TarWriter(stream))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, "shortcut") { LinkName = _root });
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "plain.txt") { DataStream = new MemoryStream("ok"u8.ToArray()) });
        }

        await _source.ExtractAsync(RowOf(archive), P("out"), CancellationToken.None);

        Assert.Equal("ok", File.ReadAllText(P("out", "plain.txt")));
        Assert.False(File.Exists(P("out", "shortcut")) || Directory.Exists(P("out", "shortcut")));
    }

    [Fact]
    public async Task Extract_OfAZipWithAPathThatLeavesTheFolder_WritesNothingOutside()
    {
        string archive = P("evil.zip");
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("../escaped.txt");
            await using Stream data = entry.Open();
            await data.WriteAsync("gotcha"u8.ToArray());
        }

        await Assert.ThrowsAnyAsync<IOException>(() => _source.ExtractAsync(RowOf(archive), P("out"), CancellationToken.None));

        Assert.False(File.Exists(P("escaped.txt")));
    }

    [Fact]
    public async Task SearchContent_FindsTheWordsInTextFiles_AndOnlyThere()
    {
        Write(Path.Combine("a", "notes.txt"), "first line\nthe Needle is here\nlast");
        Write("other.txt", "nothing to see");
        File.WriteAllBytes(P("binary.dat"), [0x4E, 0x65, 0x65, 0x64, 0x6C, 0x65, 0x00, 0x01]);

        ContentSearchResult result = await _source.SearchContentAsync(_root, "needle", ignoreCase: true, regex: false, limit: 100, CancellationToken.None);

        FileRow hit = Assert.Single(result.Rows);
        Assert.Equal("notes.txt", hit.Name);
        Assert.Equal(2, hit.Line);
        Assert.Equal("the Needle is here", hit.Snippet);
        Assert.Equal("a", hit.Where);
        Assert.True(hit.IsContentHit);
    }

    [Fact]
    public async Task SearchContent_MatchCase_SkipsOtherCases()
    {
        Write("a.txt", "Needle");
        Write("b.txt", "needle");

        ContentSearchResult result = await _source.SearchContentAsync(_root, "needle", ignoreCase: false, regex: false, limit: 100, CancellationToken.None);

        Assert.Equal(["b.txt"], result.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task SearchContent_AsARegularExpression_AndStopsAtTheLimit()
    {
        for (int i = 0; i < 5; i++) Write($"f{i}.txt", $"order {i}0{i}");

        ContentSearchResult result = await _source.SearchContentAsync(_root, @"order \d0\d", ignoreCase: true, regex: true, limit: 3, CancellationToken.None);

        Assert.Equal(3, result.Rows.Count);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task SearchContent_WithABrokenExpression_Fails()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _source.SearchContentAsync(_root, "(", ignoreCase: true, regex: true, limit: 10, CancellationToken.None));
    }

    [Fact]
    public void FileRow_OfASearch_KnowsItIsOnlyAWayToTheFile()
    {
        Assert.False(RowOf(P("x.txt")).IsContentHit);
        Assert.True((RowOf(P("x.txt")) with { Snippet = "line" }).IsContentHit);
    }
}

public sealed class FileNamingTests
{
    [Theory]
    [InlineData("report.txt", false, "report copy.txt")]
    [InlineData("photos", true, "photos copy")]
    [InlineData(".bashrc", false, ".bashrc copy")]
    [InlineData("noextension", false, "noextension copy")]
    [InlineData("backup.tar.gz", false, "backup copy.tar.gz")]
    [InlineData("v1.2.notes.md", false, "v1.2.notes copy.md")]
    public void CopyName_PutsCopyBeforeTheExtension(string name, bool directory, string expected) =>
        Assert.Equal(expected, FileNaming.CopyName(name, directory, [name]));

    [Fact]
    public void CopyName_CountsUpWhenTheNameIsTaken()
    {
        string[] taken = ["a.txt", "a copy.txt", "A COPY 2.txt"];

        // Case is not told apart, because a copy named like that would replace the other on many systems.
        Assert.Equal("a copy 3.txt", FileNaming.CopyName("a.txt", false, taken));
    }

    [Fact]
    public void Unused_AddsANumberOnlyWhenNeeded()
    {
        Assert.Equal("data", FileNaming.Unused("data", ["other"]));
        Assert.Equal("data 2", FileNaming.Unused("data", ["data"]));
        Assert.Equal("data 3", FileNaming.Unused("data", ["data", "Data 2"]));
    }

    [Theory]
    [InlineData("notes.txt", true)]
    [InlineData("with space", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("a\0b", false)]
    public void IsPlainName_AcceptsOnlyOneFileName(string name, bool expected) =>
        Assert.Equal(expected, FileNaming.IsPlainName(name));
}

public sealed class FilePreviewHelperTests
{
    [Fact]
    public void WithoutPartialLetter_DropsACutOffLetter()
    {
        byte[] euro = Encoding.UTF8.GetBytes("a€");

        Assert.Equal("a", Encoding.UTF8.GetString(FilePreviewView.WithoutPartialLetter(euro[..^1])));
        Assert.Equal("a", Encoding.UTF8.GetString(FilePreviewView.WithoutPartialLetter(euro[..^2])));
        Assert.Equal("a€", Encoding.UTF8.GetString(FilePreviewView.WithoutPartialLetter(euro)));
    }

    [Fact]
    public void WithoutPartialLetter_LeavesPlainTextAlone()
    {
        byte[] text = "plain text"u8.ToArray();

        Assert.Same(text, FilePreviewView.WithoutPartialLetter(text));
        Assert.Empty(FilePreviewView.WithoutPartialLetter([]));
    }

    [Fact]
    public void PngPixels_ReadsTheSizeFromTheHeader()
    {
        byte[] png = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        // Width 40000 and height 30000 (0x9C40, 0x7530) after the length and name of the first chunk.
        new byte[] { 0, 0, 0x9C, 0x40, 0, 0, 0x75, 0x30 }.CopyTo(png, 16);

        Assert.Equal(1_200_000_000L, FilePreviewView.PngPixels(png));
        Assert.Equal(0, FilePreviewView.PngPixels("not a picture at all, just words"u8.ToArray()));
    }

    [Fact]
    public void HexDump_ShowsOffsetsBytesAndLetters()
    {
        string dump = FilePreviewView.HexDump([0x48, 0x69, 0x00, 0xFF], 4);

        Assert.Contains("00000000", dump);
        Assert.Contains("48 69 00 ff", dump);
        Assert.Contains("Hi..", dump);
    }
}

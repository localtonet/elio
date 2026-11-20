using System.Diagnostics;
using System.Text;
using Elio.Connections;

namespace Elio.Tests;

public sealed class RemoteCommandsTests : IDisposable
{
    private readonly string _folder = Path.Combine(TestData.Root, "commands-" + Guid.NewGuid().ToString("N"));

    public RemoteCommandsTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        TestData.Delete(_folder);
        GC.SuppressFinalize(this);
    }

    // The commands are for a POSIX shell, so the ones that need one run only where there is one. On Windows they are
    // looked at as text, and were run by hand in a Unix shell.
    private static bool HasShell => !OperatingSystem.IsWindows() && File.Exists("/bin/sh");

    private static (int Exit, string Output, string Error) Run(string command, string folder, string? path = null)
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        if (path is not null) start.Environment["PATH"] = path;

        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error.Result);
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("", "''")]
    [InlineData("with space", "'with space'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("'", "''\\'''")]
    [InlineData("\"double\"", "'\"double\"'")]
    [InlineData("$(rm -rf /)", "'$(rm -rf /)'")]
    [InlineData("`id`", "'`id`'")]
    [InlineData("a;b&&c|d>e", "'a;b&&c|d>e'")]
    [InlineData("-rf", "'-rf'")]
    [InlineData("back\\slash", "'back\\slash'")]
    [InlineData("two\nlines", "'two\nlines'")]
    [InlineData("日本語 😀", "'日本語 😀'")]
    public void Single_QuotesWhatItIsGiven(string text, string expected) =>
        Assert.Equal(expected, PosixQuote.Single(text));

    [Fact]
    public void Single_RefusesANul() =>
        Assert.Throws<ArgumentException>(() => PosixQuote.Single("a\0b"));

    [Fact]
    public void Single_GivesTheShellBackExactlyWhatWentIn()
    {
        if (!HasShell) return;

        string[] texts = ["plain", "", "it's", "'", "''", "a'b'c", "\"x\"", "$(echo pwned)", "`echo pwned`", "$HOME", "a;echo pwned", "-n", "back\\slash\\", "two\nlines", "tab\there", "日本語 😀", "*", "~", "!"];
        foreach (string text in texts)
        {
            (int exit, string output, _) = Run("printf %s " + PosixQuote.Single(text), _folder);
            Assert.Equal(0, exit);
            Assert.Equal(text, output);
        }
    }

    [Theory]
    [InlineData("a.zip", ArchiveKind.Zip)]
    [InlineData("A.ZIP", ArchiveKind.Zip)]
    [InlineData("a.tar", ArchiveKind.Tar)]
    [InlineData("a.tar.gz", ArchiveKind.TarGz)]
    [InlineData("a.TAR.GZ", ArchiveKind.TarGz)]
    [InlineData("a.tgz", ArchiveKind.TarGz)]
    [InlineData("a.tar.bz2", ArchiveKind.TarBz2)]
    [InlineData("a.tbz2", ArchiveKind.TarBz2)]
    [InlineData("a.tbz", ArchiveKind.TarBz2)]
    [InlineData("a.tar.xz", ArchiveKind.TarXz)]
    [InlineData("a.txz", ArchiveKind.TarXz)]
    [InlineData("notes.txt.gz", ArchiveKind.Gz)]
    [InlineData("a.GZ", ArchiveKind.Gz)]
    [InlineData("photos.2024.tar.gz", ArchiveKind.TarGz)]
    [InlineData("a.txt", ArchiveKind.None)]
    [InlineData("a.zip.txt", ArchiveKind.None)]
    [InlineData("zip", ArchiveKind.None)]
    [InlineData(".zip", ArchiveKind.None)]
    [InlineData(".tgz", ArchiveKind.None)]
    [InlineData("a.rar", ArchiveKind.None)]
    [InlineData("a.7z", ArchiveKind.None)]
    [InlineData("", ArchiveKind.None)]
    public void Detect_GoesByTheEndOfTheName(string name, ArchiveKind expected) =>
        Assert.Equal(expected, Archives.Detect(name));

    [Theory]
    [InlineData(ArchiveKind.Zip, ".zip")]
    [InlineData(ArchiveKind.Tar, ".tar")]
    [InlineData(ArchiveKind.TarGz, ".tar.gz")]
    [InlineData(ArchiveKind.TarBz2, ".tar.bz2")]
    [InlineData(ArchiveKind.TarXz, ".tar.xz")]
    [InlineData(ArchiveKind.Gz, ".gz")]
    [InlineData(ArchiveKind.None, "")]
    public void Extension_IsWhatDetectRecognizes(ArchiveKind kind, string expected)
    {
        Assert.Equal(expected, Archives.Extension(kind));
        if (kind != ArchiveKind.None) Assert.Equal(kind, Archives.Detect("name" + expected));
    }

    [Theory]
    [InlineData("photos.tar.gz", "photos")]
    [InlineData("photos.2024.tgz", "photos.2024")]
    [InlineData("Backup.ZIP", "Backup")]
    [InlineData("data.tar", "data")]
    [InlineData("log.txt.gz", "log.txt")]
    [InlineData("thing.rar", "thing")]
    [InlineData("plain", "plain")]
    [InlineData(".hidden", ".hidden")]
    public void FolderNameFor_LeavesOutTheSuffix(string archive, string expected) =>
        Assert.Equal(expected, Archives.FolderNameFor(archive));

    [Theory]
    [InlineData(ArchiveKind.Zip, "mkdir -p '/srv/out dir' && unzip -n -q '/srv/my a.zip' -d '/srv/out dir'")]
    [InlineData(ArchiveKind.Tar, "mkdir -p '/srv/out dir' && tar -xf '/srv/my a.zip' -C '/srv/out dir'")]
    [InlineData(ArchiveKind.TarGz, "mkdir -p '/srv/out dir' && tar -xzf '/srv/my a.zip' -C '/srv/out dir'")]
    [InlineData(ArchiveKind.TarBz2, "mkdir -p '/srv/out dir' && tar -xjf '/srv/my a.zip' -C '/srv/out dir'")]
    [InlineData(ArchiveKind.TarXz, "mkdir -p '/srv/out dir' && tar -xJf '/srv/my a.zip' -C '/srv/out dir'")]
    public void ExtractCommand_UnpacksIntoTheFolder(ArchiveKind kind, string expected) =>
        Assert.Equal(expected, Archives.ExtractCommand("/srv/my a.zip", "/srv/out dir", kind));

    [Fact]
    public void ExtractCommand_ForAFilePackedOnItsOwn_NamesTheResultAfterTheFile()
    {
        Assert.Equal("mkdir -p '/out' && [ ! -e '/out/notes.txt' ] && gunzip -c '/srv/notes.txt.gz' > '/out/notes.txt'", Archives.ExtractCommand("/srv/notes.txt.gz", "/out", ArchiveKind.Gz));
        Assert.Equal("mkdir -p '/' && [ ! -e '/a' ] && gunzip -c '/a.GZ' > '/a'", Archives.ExtractCommand("/a.GZ", "/", ArchiveKind.Gz));
    }

    [Fact]
    public void ExtractCommand_QuotesWhatWouldOtherwiseBeRead()
    {
        string command = Archives.ExtractCommand("/srv/it's $(x).zip", "/srv/a;b", ArchiveKind.Zip);

        Assert.Equal("mkdir -p '/srv/a;b' && unzip -n -q '/srv/it'\\''s $(x).zip' -d '/srv/a;b'", command);
    }

    [Fact]
    public void ExtractCommand_WithAPathThatLooksLikeAnOption_MakesItAPath()
    {
        string command = Archives.ExtractCommand("-evil.zip", "-out", ArchiveKind.Zip);

        Assert.Equal("mkdir -p './-out' && unzip -n -q './-evil.zip' -d './-out'", command);
    }

    [Fact]
    public void ExtractCommand_ForSomethingThatIsNotAnArchive_FailsWithAMessage()
    {
        string command = Archives.ExtractCommand("/a.txt", "/out", ArchiveKind.None);

        Assert.StartsWith("echo '", command);
        Assert.EndsWith(">&2; exit 2", command);
        if (!HasShell) return;

        (int exit, string output, string error) = Run(command, _folder);
        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, output);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData(ArchiveKind.Zip, "cd '/srv/p q' && zip -r -q '/srv/out.zip' 'a b' 'c'\\''d'")]
    [InlineData(ArchiveKind.Tar, "cd '/srv/p q' && tar -cf '/srv/out.zip' 'a b' 'c'\\''d'")]
    [InlineData(ArchiveKind.TarGz, "cd '/srv/p q' && tar -czf '/srv/out.zip' 'a b' 'c'\\''d'")]
    [InlineData(ArchiveKind.TarBz2, "cd '/srv/p q' && tar -cjf '/srv/out.zip' 'a b' 'c'\\''d'")]
    [InlineData(ArchiveKind.TarXz, "cd '/srv/p q' && tar -cJf '/srv/out.zip' 'a b' 'c'\\''d'")]
    public void CreateCommand_PacksTheNamesOfTheFolder(ArchiveKind kind, string expected) =>
        Assert.Equal(expected, Archives.CreateCommand("/srv/p q", ["a b", "c'd"], "/srv/out.zip", kind));

    [Fact]
    public void CreateCommand_MakesANameThatLooksLikeAnOptionAPath() =>
        Assert.Equal("cd '/d' && tar -cf '/o.tar' './-rf' 'x'", Archives.CreateCommand("/d", ["-rf", "x"], "/o.tar", ArchiveKind.Tar));

    [Theory]
    [InlineData(ArchiveKind.None)]
    [InlineData(ArchiveKind.Gz)]
    public void CreateCommand_RefusesKindsThatAreNotMade(ArchiveKind kind) =>
        Assert.Throws<ArgumentException>(() => Archives.CreateCommand("/d", ["x"], "/o", kind));

    [Fact]
    public void CreateCommand_RefusesNothingToPack() =>
        Assert.Throws<ArgumentException>(() => Archives.CreateCommand("/d", [], "/o.tar", ArchiveKind.Tar));

    [Theory]
    [InlineData(ArchiveKind.Tar, "o.tar")]
    [InlineData(ArchiveKind.TarGz, "o.tar.gz")]
    [InlineData(ArchiveKind.TarBz2, "o.tar.bz2")]
    [InlineData(ArchiveKind.TarXz, "o.tar.xz")]
    public void TarArchives_PackAndUnpackForReal(ArchiveKind kind, string archive)
    {
        if (!HasShell) return;

        string source = Path.Combine(_folder, "src dir");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "it's here.txt"), "one");
        File.WriteAllText(Path.Combine(source, "-dash"), "two");
        File.WriteAllText(Path.Combine(source, "sub", "deep.txt"), "three");

        string packed = Path.Combine(_folder, archive);
        (int made, _, string makeError) = Run(Archives.CreateCommand(source, ["it's here.txt", "-dash", "sub"], packed, kind), _folder);
        Assert.True(made == 0, makeError);

        string target = Path.Combine(_folder, "out dir");
        (int opened, _, string openError) = Run(Archives.ExtractCommand(packed, target, kind), _folder);
        Assert.True(opened == 0, openError);

        Assert.Equal("one", File.ReadAllText(Path.Combine(target, "it's here.txt")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(target, "-dash")));
        Assert.Equal("three", File.ReadAllText(Path.Combine(target, "sub", "deep.txt")));
    }

    [Fact]
    public void GzArchive_UnpacksUnderTheOriginalName()
    {
        if (!HasShell) return;

        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "packed text");
        Assert.Equal(0, Run("gzip -k notes.txt", _folder).Exit);

        string target = Path.Combine(_folder, "unpacked");
        (int exit, _, string error) = Run(Archives.ExtractCommand(Path.Combine(_folder, "notes.txt.gz"), target, ArchiveKind.Gz), _folder);

        Assert.True(exit == 0, error);
        Assert.Equal("packed text", File.ReadAllText(Path.Combine(target, "notes.txt")));
    }

    [Fact]
    public void CopyCommand_QuotesBothPaths() =>
        Assert.Equal("cp -a -- '/a/it'\\''s' '/b/c d'", CopyCommand.Build("/a/it's", "/b/c d"));

    [Fact]
    public void CopyCommand_CopiesAFolderWithWhatIsInIt()
    {
        if (!HasShell) return;

        string source = Path.Combine(_folder, "from");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "sub", "x.txt"), "x");

        (int exit, _, string error) = Run(CopyCommand.Build(source, Path.Combine(_folder, "to $1")), _folder);

        Assert.True(exit == 0, error);
        Assert.Equal("x", File.ReadAllText(Path.Combine(_folder, "to $1", "sub", "x.txt")));
    }

    [Fact]
    public void Build_AsksForOneMatchPerFileAndCapsWhatComesBack()
    {
        string command = ContentSearchCommand.Build("/srv/www/", "it's", ignoreCase: true, regex: false, limit: 25);

        Assert.StartsWith("sh -c '", command);
        Assert.Contains("command -v rg", command);
        Assert.Contains("--max-count 1", command);
        Assert.Contains("-m 1", command);
        Assert.Contains("head -n 25", command);

        // The query only ever appears quoted, as one word, in each of the three programs. The script is quoted
        // as a whole, so every quote in it is quoted again.
        string quotedTwice = PosixQuote.Single("it's").Replace("'", "'\\''");
        Assert.Equal(3, CountOf(command, "-e " + quotedTwice));
        Assert.Contains("'\\''/srv/www'\\''", command);
        Assert.DoesNotContain("/srv/www/", command);
    }

    private static int CountOf(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    [Theory]
    [InlineData(true, true, " -i", " -E")]
    [InlineData(false, false, "", " -F")]
    public void Build_FollowsTheOptions(bool ignoreCase, bool regex, string caseFlag, string kindFlag)
    {
        string command = ContentSearchCommand.Build("/d", "q", ignoreCase, regex, 10);

        Assert.Contains($"-m 1 --exclude-dir=.git{caseFlag}{kindFlag} -e", command);
        Assert.Contains($"-m 1{caseFlag}{kindFlag} -e", command);
        Assert.Equal(ignoreCase, command.Contains(" -i "));
    }

    [Fact]
    public void Build_NeverAsksForLessThanOneLine() =>
        Assert.Contains("head -n 1'", ContentSearchCommand.Build("/d", "q", false, false, 0));

    [Fact]
    public void Build_FindsTheTextInTheFilesOfAFolder()
    {
        if (!HasShell) return;

        string root = Path.Combine(_folder, "tree with 'quote'");
        Directory.CreateDirectory(Path.Combine(root, "sub dir"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "nothing\nHello World again\n");
        File.WriteAllText(Path.Combine(root, "sub dir", "b:12:c.txt"), "x\ny\nsay hello\n");
        File.WriteAllText(Path.Combine(root, ".git", "config"), "hello from git\n");
        File.WriteAllText(Path.Combine(root, "none.txt"), "goodbye\n");
        File.WriteAllBytes(Path.Combine(root, "binary.bin"), [0x68, 0x65, 0x6c, 0x6c, 0x6f, 0x00, 0x01]);

        (int exit, string output, _) = Run(ContentSearchCommand.Build(root, "hello", ignoreCase: true, regex: false, limit: 100), _folder);
        Assert.Equal(0, exit);

        ContentMatch[] matches = [.. ContentSearchCommand.Parse(output).OrderBy(m => m.Path, StringComparer.Ordinal)];
        Assert.Equal(2, matches.Length);
        Assert.Equal(Path.Combine(root, "a.txt"), matches[0].Path);
        Assert.Equal(2, matches[0].Line);
        Assert.Equal("Hello World again", matches[0].Snippet);
        Assert.Equal(Path.Combine(root, "sub dir", "b:12:c.txt"), matches[1].Path);
        Assert.Equal(3, matches[1].Line);

        // Case matters when asked for, and a pattern is only one when asked for.
        Assert.Single(ContentSearchCommand.Parse(Run(ContentSearchCommand.Build(root, "hello", ignoreCase: false, regex: false, limit: 100), _folder).Output));
        Assert.Single(ContentSearchCommand.Parse(Run(ContentSearchCommand.Build(root, "^say h.llo$", ignoreCase: false, regex: true, limit: 100), _folder).Output));
        Assert.Empty(ContentSearchCommand.Parse(Run(ContentSearchCommand.Build(root, "h.llo", ignoreCase: true, regex: false, limit: 100), _folder).Output));
    }

    [Fact]
    public void Build_CapsTheLines()
    {
        if (!HasShell) return;

        for (int i = 0; i < 20; i++) File.WriteAllText(Path.Combine(_folder, $"f{i}.txt"), "needle\n");

        string output = Run(ContentSearchCommand.Build(_folder, "needle", false, false, 5), _folder).Output;

        Assert.Equal(5, ContentSearchCommand.Parse(output).Count);
    }

    [Fact]
    public void Build_WithAGrepThatIsTooSmallForTheOptions_FallsBackToAPlainerRun()
    {
        if (!HasShell || RealGrep() is not string grep) return;

        // A grep like BusyBox's: it stops at the options it does not have, as BusyBox does, and runs the plainer ones.
        string bin = Path.Combine(_folder, "bin");
        Directory.CreateDirectory(bin);
        string script = Path.Combine(bin, "grep");
        File.WriteAllText(script, $"#!/bin/sh\nfor a in \"$@\"; do case \"$a\" in --null|-I|--exclude-dir=*|-rIn) echo \"grep: unrecognized option $a\" >&2; exit 1;; esac; done\nexec {grep} \"$@\"\n");
        MakeExecutable(script);

        string root = Path.Combine(_folder, "data");
        Directory.CreateDirectory(Path.Combine(root, "a b"));
        File.WriteAllText(Path.Combine(root, "a b", "x.txt"), "first\nthe needle here\n");

        (int exit, string output, _) = Run(ContentSearchCommand.Build(root, "needle", false, false, 100), _folder, bin + ":" + Path.GetDirectoryName(grep) + ":/bin:/usr/bin");

        Assert.Equal(0, exit);
        ContentMatch match = Assert.Single(ContentSearchCommand.Parse(output));
        Assert.Equal(Path.Combine(root, "a b", "x.txt"), match.Path);
        Assert.Equal(2, match.Line);
        Assert.Equal("the needle here", match.Snippet);
    }

    [Fact]
    public void Build_PrefersRgWhereThereIsOne()
    {
        if (!HasShell) return;

        string bin = Path.Combine(_folder, "bin");
        Directory.CreateDirectory(bin);
        string script = Path.Combine(bin, "rg");
        File.WriteAllText(script, "#!/bin/sh\nprintf '%s\\0%s\\n' /from/rg '7:found by rg'\n");
        MakeExecutable(script);

        (int exit, string output, _) = Run(ContentSearchCommand.Build(_folder, "x", false, false, 10), _folder, bin + ":/bin:/usr/bin");

        Assert.Equal(0, exit);
        Assert.Equal([new ContentMatch("/from/rg", 7, "found by rg")], ContentSearchCommand.Parse(output));
    }

    private static void MakeExecutable(string script)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string? RealGrep() => new[] { "/usr/bin/grep", "/bin/grep" }.FirstOrDefault(File.Exists);

    [Fact]
    public void Parse_ReadsGrepAndRgWithANulAfterThePath()
    {
        const string Output =
            "/home/u/my docs/a.txt\0" + "12:hello world\n" +
            "/srv/a:12:b/file.txt\0" + "3:text :4: more\n" +
            "/srv/日本語/ファイル.txt\0" + "1:こんにちは 😀\n";

        Assert.Equal(
            [
                new ContentMatch("/home/u/my docs/a.txt", 12, "hello world"),
                new ContentMatch("/srv/a:12:b/file.txt", 3, "text :4: more"),
                new ContentMatch("/srv/日本語/ファイル.txt", 1, "こんにちは 😀"),
            ],
            ContentSearchCommand.Parse(Output));
    }

    [Fact]
    public void Parse_ReadsThePlainFormatAtTheFirstLineNumber()
    {
        const string Output =
            "/srv/data/file.txt:12:some text\n" +
            "/srv/my docs/a b.txt:7:ratio 3:4:5 is odd\n" +
            "./rel/path.cs:100:\tindented()\n" +
            "/x:y/z.txt:1:colon in path before the number\n";

        Assert.Equal(
            [
                new ContentMatch("/srv/data/file.txt", 12, "some text"),
                new ContentMatch("/srv/my docs/a b.txt", 7, "ratio 3:4:5 is odd"),
                new ContentMatch("./rel/path.cs", 100, "indented()"),
                new ContentMatch("/x:y/z.txt", 1, "colon in path before the number"),
            ],
            ContentSearchCommand.Parse(Output));
    }

    [Fact]
    public void Parse_TakesTheFirstColonNumberColonAsTheEndOfThePath()
    {
        // Without a NUL there is no telling, and the first one wins. The NUL form is the one to ask for.
        ContentMatch match = Assert.Single(ContentSearchCommand.Parse("/a:12:b/file.txt:3:text\n"));

        Assert.Equal("/a", match.Path);
        Assert.Equal(12, match.Line);
        Assert.Equal("b/file.txt:3:text", match.Snippet);
    }

    [Fact]
    public void Parse_HandlesWindowsLineEndingsAndBlankLines()
    {
        const string Output = "/a.txt\0" + "1:one\r\n\r\n/b.txt\0" + "2:two\r\n";

        Assert.Equal([new ContentMatch("/a.txt", 1, "one"), new ContentMatch("/b.txt", 2, "two")], ContentSearchCommand.Parse(Output));
    }

    [Fact]
    public void Parse_CutsLongLinesShort()
    {
        string output = "/min.js\0" + "1:" + new string('x', 5000) + "\n";

        ContentMatch match = Assert.Single(ContentSearchCommand.Parse(output));
        Assert.Equal(201, match.Snippet.Length);
        Assert.EndsWith("…", match.Snippet);
        Assert.Equal(new string('x', 200), match.Snippet[..200]);

        // One that is just short enough is left whole.
        Assert.Equal(200, Assert.Single(ContentSearchCommand.Parse("/a\0" + "1:" + new string('y', 200) + "\n")).Snippet.Length);
    }

    [Fact]
    public void Parse_TrimsTheSpaceAroundASnippet() =>
        Assert.Equal("code();", Assert.Single(ContentSearchCommand.Parse("/a.cs\0" + "5:      code();   \n")).Snippet);

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("Binary file /srv/a.bin matches")]
    [InlineData("grep: /root/secret: Permission denied")]
    [InlineData("rg: /x: IO error for operation on /x: Permission denied (os error 13)")]
    [InlineData("no numbers here")]
    [InlineData(":12:starts with a colon")]
    [InlineData("/a.txt:x:not a number")]
    [InlineData("/a.txt:12")]
    [InlineData("/a.txt:12 :spaced")]
    [InlineData("/a.txt\0no number")]
    [InlineData("/a.txt\0x:y")]
    [InlineData("\01:no path")]
    [InlineData("/a.txt:99999999999999999999:too big")]
    public void Parse_LeavesOutWhatIsNotAMatch(string output) =>
        Assert.Empty(ContentSearchCommand.Parse(output));

    [Fact]
    public void Parse_KeepsTheMatchesAmongThingsItLeavesOut()
    {
        const string Output =
            "grep: /root/secret: Permission denied\n" +
            "/a.txt\0" + "3:found\n" +
            "Binary file /b.bin matches\n";

        Assert.Equal([new ContentMatch("/a.txt", 3, "found")], ContentSearchCommand.Parse(Output));
    }
}

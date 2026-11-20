using System.Diagnostics;
using Elio.Terminal;

namespace Elio.Tests;

/// <summary>The commands that exist: read from a list or from the folders of $PATH, and asked about one word at a time.</summary>
public sealed class CommandIndexTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "elio-tests", "commands-" + Guid.NewGuid().ToString("N"));

    public CommandIndexTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // The system cleans its temp folder up in time.
        }
    }

    private static void WaitUntil(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(20);
        Assert.True(done(), "gave up waiting");
    }

    [Fact]
    public void FromListing_TakesOneNameToALine()
    {
        CommandIndex index = CommandIndex.FromListing("ls\r\ngit\n\n  make  \nfoo bar\n/usr/bin/x\n\twith tab\nsh", authoritative: true);

        Assert.True(index.IsLoaded);
        Assert.Equal(CommandState.Exists, index.Check("ls"));
        Assert.Equal(CommandState.Exists, index.Check("git"));
        Assert.Equal(CommandState.Exists, index.Check("make"));
        Assert.Equal(CommandState.Exists, index.Check("sh"));
        Assert.Equal(CommandState.Missing, index.Check("foo"));
        Assert.Equal(CommandState.Missing, index.Check("bar"));
        Assert.Equal(CommandState.Missing, index.Check("with"));
    }

    [Fact]
    public void FromListing_OfNothing_IsLoadedAndCallsEverythingMissing()
    {
        CommandIndex index = CommandIndex.FromListing(string.Empty, authoritative: true);

        Assert.True(index.IsLoaded);
        Assert.Equal(CommandState.Missing, index.Check("ls"));
    }

    [Fact]
    public void Check_WhereTheListIsNotTheWholeStory_NeverSaysMissing()
    {
        CommandIndex index = CommandIndex.FromListing("git", authoritative: false);

        Assert.Equal(CommandState.Exists, index.Check("git"));
        Assert.Equal(CommandState.Unknown, index.Check("Get-ChildItem"));
    }

    [Fact]
    public void Check_BeforeAnythingWasRead_IsUnknown()
    {
        var index = new CommandIndex(authoritative: true);

        Assert.False(index.IsLoaded);
        Assert.Equal(CommandState.Unknown, index.Check("anything"));

        index.Add("mine");
        Assert.Equal(CommandState.Exists, index.Check("mine"));
        Assert.Equal(CommandState.Unknown, index.Check("other"));
    }

    [Theory]
    [InlineData("./run.sh")]
    [InlineData("../run.sh")]
    [InlineData("/usr/bin/ls")]
    [InlineData("bin/tool")]
    [InlineData("dir\\tool")]
    [InlineData("~/bin/tool")]
    [InlineData("~")]
    [InlineData("$EDITOR")]
    [InlineData(".")]
    [InlineData(".hidden")]
    [InlineData("")]
    public void Check_OfAPathAndNotAName_IsUnknown(string word)
    {
        // Even when nothing is listed and the list is the whole story, and even when the word is in it.
        CommandIndex index = CommandIndex.FromListing("hidden", authoritative: true);

        Assert.Equal(CommandState.Unknown, index.Check(word));
    }

    [Fact]
    public void Check_TellsCaseApart_OnlyWhereTheShellDoes()
    {
        CommandIndex posix = CommandIndex.FromListing("git", authoritative: true);
        CommandIndex lenient = CommandIndex.FromListing("git", authoritative: false);

        Assert.Equal(CommandState.Missing, posix.Check("Git"));
        Assert.Equal(CommandState.Exists, lenient.Check("GIT"));
    }

    [Fact]
    public void Add_CountsANameAsACommand_AndLeavesOutWhatCannotBeOne()
    {
        CommandIndex index = CommandIndex.FromListing("ls", authoritative: true);

        index.Add("  mytool  ");
        index.Add("two words");
        index.Add("a/b");
        index.Add(string.Empty);
        index.Add("   ");
        index.Add(new string('x', 300));

        Assert.Equal(CommandState.Exists, index.Check("mytool"));
        Assert.Equal(CommandState.Missing, index.Check("two"));
        Assert.Equal(CommandState.Missing, index.Check("b"));
        Assert.Equal(CommandState.Missing, index.Check(new string('x', 300)));
    }

    [Fact]
    public void Rescan_OfAListThatWasNotReadFromHere_DoesNothing()
    {
        CommandIndex index = CommandIndex.FromListing("ls", authoritative: true);

        index.Rescan();
        Thread.Sleep(100);

        Assert.Equal(CommandState.Exists, index.Check("ls"));
        Assert.Equal(CommandState.Missing, index.Check("zzz"));
    }

    [Theory]
    [InlineData("git.exe", true, new[] { "git.exe", "git" })]
    [InlineData("GIT.EXE", true, new[] { "GIT.EXE", "GIT" })]
    [InlineData("run.cmd", true, new[] { "run.cmd", "run" })]
    [InlineData("run.PS1", true, new[] { "run.PS1", "run" })]
    [InlineData("a.b.exe", true, new[] { "a.b.exe", "a.b" })]
    [InlineData("readme.txt", true, new string[0])]
    [InlineData("tool", true, new string[0])]
    [InlineData("tool.dll", true, new string[0])]
    [InlineData(".exe", true, new[] { ".exe" })]
    [InlineData("git.exe", false, new[] { "git.exe" })]
    [InlineData("readme.txt", false, new[] { "readme.txt" })]
    [InlineData("tool", false, new[] { "tool" })]
    public void NamesFor_OnWindowsAFileRunsByItsKind(string file, bool windows, string[] expected) =>
        Assert.Equal(expected, CommandIndex.NamesFor(file, windows, CommandIndex.ExtensionsFrom(null)).ToArray());

    [Fact]
    public void NamesFor_UsesTheKindsTheSystemSays()
    {
        string[] extensions = CommandIndex.ExtensionsFrom(".COM;.EXE;.VBS");

        Assert.Equal(new[] { "a.vbs", "a" }, CommandIndex.NamesFor("a.vbs", windowsStyle: true, extensions));
        Assert.Empty(CommandIndex.NamesFor("a.js", windowsStyle: true, extensions));
    }

    [Fact]
    public void ExtensionsFrom_AlwaysHasTheUsualOnes_AndWhatTheSystemAdds()
    {
        Assert.Equal([".exe", ".cmd", ".bat", ".com", ".ps1"], CommandIndex.ExtensionsFrom(null));
        Assert.Equal([".exe", ".cmd", ".bat", ".com", ".ps1"], CommandIndex.ExtensionsFrom(string.Empty));
        // What the system says is kept as it says it, and the ones that were already there are not said twice.
        Assert.Equal([".exe", ".cmd", ".bat", ".com", ".ps1", ".VBS", ".X"], CommandIndex.ExtensionsFrom(".COM; .EXE ;;.VBS;X"));
    }

    [Fact]
    public void ListCommand_IsOneShellCommandAnyLoginCanRead()
    {
        string command = CommandIndex.ListCommand;

        Assert.StartsWith("sh -lc '", command);
        Assert.EndsWith("'", command);

        // One pair of single quotes holds the whole script. A single quote inside would end it for fish and csh alike,
        // and so would a ! for csh and a backslash for any of them that reads one differently inside single quotes.
        Assert.Equal(2, command.Count(c => c == '\''));
        Assert.DoesNotContain('!', command);
        Assert.DoesNotContain('\r', command);
        Assert.DoesNotContain('\n', command);
        Assert.Equal(1, command.Split("\\n").Length - 1);
    }

    [Fact]
    public async Task ListCommand_OnAComputerThatHasASh_PrintsSortedNamesAndNothingElse()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh")) return;

        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(CommandIndex.ListCommand);
        using Process process = Process.Start(start)!;
        string listing = await process.StandardOutput.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "did not finish");

        string[] names = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(names);
        Assert.Contains("sh", names);
        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.DoesNotContain(names, name => name.Contains('/') || name.Contains(' '));

        // What it printed is what a CommandIndex reads.
        CommandIndex index = CommandIndex.FromListing(listing, authoritative: true);
        Assert.Equal(CommandState.Exists, index.Check("sh"));
        Assert.Equal(CommandState.Missing, index.Check("no-such-command-zzz"));
    }

    // PATH is read when the index is made, so it is put back as soon as that is done.
    private CommandIndex MakeIndexOver(string path)
    {
        string? before = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", path);
            return CommandIndex.ForThisComputer();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", before);
        }
    }

    private void MakeCommand(string name, bool executable = true)
    {
        string file = Path.Combine(_folder, name);
        File.WriteAllText(file, "echo");
        if (executable && !OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (!executable && !OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void ForThisComputer_ReadsTheFoldersOfPath()
    {
        bool windows = OperatingSystem.IsWindows();
        MakeCommand(windows ? "mytool.exe" : "mytool");
        MakeCommand(windows ? "runme.cmd" : "runme");
        MakeCommand(windows ? "readme.txt" : "plain", executable: false);
        Directory.CreateDirectory(Path.Combine(_folder, windows ? "folder.exe" : "folder"));

        CommandIndex index = MakeIndexOver(string.Join(Path.PathSeparator, Path.Combine(_folder, "not-there"), _folder));
        WaitUntil(() => index.IsLoaded);

        Assert.Equal(CommandState.Exists, index.Check("mytool"));
        Assert.Equal(CommandState.Exists, index.Check("runme"));
        if (windows)
        {
            // Not told apart by case, found with and without the kind, and never called missing: cmd and PowerShell have more.
            Assert.Equal(CommandState.Exists, index.Check("MYTOOL"));
            Assert.Equal(CommandState.Exists, index.Check("mytool.exe"));
            Assert.Equal(CommandState.Unknown, index.Check("readme"));
            Assert.Equal(CommandState.Unknown, index.Check("readme.txt"));
            Assert.Equal(CommandState.Unknown, index.Check("zzz-not-a-command"));
        }
        else
        {
            // A file that cannot be run is not a command, nor is a folder, and a name that is not there is missing.
            Assert.Equal(CommandState.Missing, index.Check("plain"));
            Assert.Equal(CommandState.Missing, index.Check("folder"));
            Assert.Equal(CommandState.Missing, index.Check("zzz-not-a-command"));
        }
    }

    [Fact]
    public void ForThisComputer_WithAnEmptyPath_IsLoadedAndSaysNothingExists()
    {
        CommandIndex index = MakeIndexOver(string.Empty);
        WaitUntil(() => index.IsLoaded);

        Assert.NotEqual(CommandState.Exists, index.Check("mytool"));
    }

    [Fact]
    public void Rescan_FindsWhatWasInstalledSince_AndNeverLosesWhatWasThere()
    {
        bool windows = OperatingSystem.IsWindows();
        MakeCommand(windows ? "first.exe" : "first");
        CommandIndex index = MakeIndexOver(_folder);
        WaitUntil(() => index.IsLoaded);
        Assert.Equal(CommandState.Exists, index.Check("first"));
        Assert.NotEqual(CommandState.Exists, index.Check("second"));

        // The folder is read from the PATH of the moment, so it is the one the index was made over that matters.
        MakeCommand(windows ? "second.exe" : "second");
        string? before = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", _folder);
            WaitUntil(() =>
            {
                index.Rescan();

                // While it is read again, what was read before is still there.
                Assert.Equal(CommandState.Exists, index.Check("first"));
                return index.Check("second") == CommandState.Exists;
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", before);
        }
    }

    [Fact]
    public void Index_StandsUpToManyThreadsAtOnce()
    {
        bool windows = OperatingSystem.IsWindows();
        MakeCommand(windows ? "shared.exe" : "shared");
        CommandIndex index = MakeIndexOver(_folder);
        WaitUntil(() => index.IsLoaded);
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();

        string? before = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", _folder);
            Parallel.For(0, 4000, i =>
            {
                try
                {
                    switch (i % 4)
                    {
                        case 0:
                            index.Add("added" + i);
                            break;
                        case 1:
                            if (index.Check("shared") != CommandState.Exists) failures.Enqueue("lost shared");
                            break;
                        case 2:
                            index.Rescan();
                            break;
                        default:
                            index.Check("nothing" + i);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex.ToString());
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", before);
        }

        Assert.Empty(failures);
        for (int i = 0; i < 4000; i += 4) Assert.Equal(CommandState.Exists, index.Check("added" + i));
    }
}

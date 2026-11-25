using System.Diagnostics;
using System.Text;
using Elio.Connections;
using Elio.UI.Files;

namespace Elio.Tests;

/// <summary>Changing permissions on what was chosen, and on everything in a folder.</summary>
public sealed class PermissionChangeTests
{
    private const int Mode755 = 0b111_101_101;

    // /site holds a file, a folder with a file in it, a link to a folder outside and a link to a file.
    private static MemoryFileSource Site() => new MemoryFileSource()
        .Folder("/site")
        .File("/site/index.html")
        .Folder("/site/img")
        .File("/site/img/logo.png")
        .Folder("/outside")
        .File("/outside/secret")
        .Link("/site/out", toFolder: true)
        .Link("/site/latest", toFolder: false);

    private static Task<PermissionReport> ApplyAsync(MemoryFileSource source, PermissionScope scope, params string[] paths) =>
        PermissionChanger.ApplyAsync(source, paths.Select(source.Row).ToList(), new PermissionChoice(Mode755, scope), null, CancellationToken.None);

    [Fact]
    public async Task OnlyThis_ChangesWhatWasChosen_AndNothingInside()
    {
        MemoryFileSource source = Site();

        PermissionReport report = await ApplyAsync(source, PermissionScope.OnlyThis, "/site", "/site/latest");

        Assert.Equal(["/site", "/site/latest"], source.Changes.Select(c => c.Path));
        Assert.Equal(2, report.Changed);
        Assert.Empty(source.Listed);
    }

    [Fact]
    public async Task Everything_ChangesEveryFileAndFolder_EachFolderAfterWhatIsInIt()
    {
        MemoryFileSource source = Site();

        PermissionReport report = await ApplyAsync(source, PermissionScope.Everything, "/site");

        List<string> changed = source.Changes.Select(c => c.Path).ToList();
        Assert.Equal(["/site/index.html", "/site/img/logo.png", "/site/img", "/site"], changed);
        Assert.All(source.Changes, c => Assert.Equal(Mode755, c.Mode));
        Assert.Equal(4, report.Changed);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task Links_AreNeitherFollowedNorChanged()
    {
        MemoryFileSource source = Site();

        PermissionReport report = await ApplyAsync(source, PermissionScope.Everything, "/site");

        Assert.DoesNotContain("/site/out", source.Listed);
        Assert.DoesNotContain(source.Changes, c => c.Path.StartsWith("/outside", StringComparison.Ordinal) || c.Path is "/site/out" or "/site/latest");
        Assert.Equal(2, report.LinksLeftOut);
    }

    [Fact]
    public async Task OnlyFiles_LeavesEveryFolderAsItWas()
    {
        MemoryFileSource source = Site();

        await ApplyAsync(source, PermissionScope.OnlyFiles, "/site");

        Assert.Equal(["/site/index.html", "/site/img/logo.png"], source.Changes.Select(c => c.Path));
    }

    [Fact]
    public async Task OnlyFolders_LeavesEveryFileAsItWas()
    {
        MemoryFileSource source = Site();

        await ApplyAsync(source, PermissionScope.OnlyFolders, "/site");

        Assert.Equal(["/site/img", "/site"], source.Changes.Select(c => c.Path));
    }

    [Fact]
    public async Task WhatCannotBeChanged_IsReported_AndTheRestIsDone()
    {
        MemoryFileSource source = Site();
        source.Refuses.Add("/site/index.html");

        PermissionReport report = await ApplyAsync(source, PermissionScope.Everything, "/site");

        Assert.Equal(["/site/img/logo.png", "/site/img", "/site"], source.Changes.Select(c => c.Path));
        string failure = Assert.Single(report.Failures);
        Assert.StartsWith("/site/index.html: ", failure, StringComparison.Ordinal);
        Assert.Equal(1, report.FailureCount);
        Assert.False(report.GaveUp);
    }

    [Fact]
    public async Task AFolderThatCannotBeRead_IsChangedFirst_WhenThatOpensIt()
    {
        MemoryFileSource source = Site();
        source.ShutUntilChanged.Add("/site/img");

        PermissionReport report = await ApplyAsync(source, PermissionScope.Everything, "/site");

        List<string> changed = source.Changes.Select(c => c.Path).ToList();
        Assert.True(changed.IndexOf("/site/img") < changed.IndexOf("/site/img/logo.png"));
        Assert.Contains("/site/img/logo.png", changed);
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task AFolderThatCannotBeRead_WhenOnlyFilesChange_IsReported()
    {
        MemoryFileSource source = Site();
        source.ShutUntilChanged.Add("/site/img");

        PermissionReport report = await ApplyAsync(source, PermissionScope.OnlyFiles, "/site");

        Assert.Equal(["/site/index.html"], source.Changes.Select(c => c.Path));
        Assert.StartsWith("/site/img: ", Assert.Single(report.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_StopsTheWalk()
    {
        MemoryFileSource source = Site();
        using var stop = new CancellationTokenSource();
        source.BeforeChange = path =>
        {
            if (path == "/site/img/logo.png") stop.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PermissionChanger.ApplyAsync(source, [source.Row("/site")], new PermissionChoice(Mode755, PermissionScope.Everything), null, stop.Token));

        Assert.Equal(["/site/index.html"], source.Changes.Select(c => c.Path));
    }

    [Fact]
    public async Task Everything_GoesToTheServerInOneGo_WhenItCan()
    {
        MemoryFileSource source = Site();
        source.Tree = (_, _) => ["chmod: changing permissions of '/site/x': Operation not permitted"];

        PermissionReport report = await ApplyAsync(source, PermissionScope.Everything, "/site");

        Assert.Empty(source.Changes);
        Assert.Empty(source.Listed);
        Assert.Contains("Operation not permitted", Assert.Single(report.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneGo_IsOnlyForEverything_AndASourceThatCannotIsWalked()
    {
        MemoryFileSource source = Site();
        int asked = 0;
        source.Tree = (_, _) =>
        {
            asked++;
            return null;
        };

        await ApplyAsync(source, PermissionScope.Everything, "/site");
        await ApplyAsync(source, PermissionScope.OnlyFiles, "/site");

        Assert.Equal(1, asked);
        Assert.Contains(source.Changes, c => c.Path == "/site/img/logo.png");
    }

    [Fact]
    public async Task ManyFailuresInARow_StopTheWalk()
    {
        var source = new MemoryFileSource().Folder("/many");
        for (int i = 0; i < 100; i++)
        {
            source.File($"/many/{i:D3}");
            source.Refuses.Add($"/many/{i:D3}");
        }

        PermissionReport report = await ApplyAsync(source, PermissionScope.OnlyFiles, "/many");

        Assert.True(report.GaveUp);
        Assert.True(report.FailureCount < 100);
    }

    [Theory]
    [InlineData("644", 0b110_100_100)]
    [InlineData("755", 0b111_101_101)]
    [InlineData(" 700 ", 0b111_000_000)]
    [InlineData("000", 0)]
    public void Parse_ReadsThreeOctalDigits(string text, int expected) =>
        Assert.Equal(expected, PermissionBits.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("64")]
    [InlineData("6444")]
    [InlineData("648")]
    [InlineData("rwx")]
    [InlineData(null)]
    public void Parse_RefusesAnythingElse(string? text) =>
        Assert.Null(PermissionBits.Parse(text));

    [Fact]
    public void Letters_AndDigits_GoBothWays()
    {
        Assert.Equal("rwxr-x---", PermissionBits.Letters(0b111_101_000));
        Assert.Equal("750", PermissionBits.Digits(0b111_101_000));
        Assert.Equal("750", PermissionBits.DigitsOfLetters("rwxr-x---"));
        Assert.Equal(string.Empty, PermissionBits.DigitsOfLetters(string.Empty));
    }

    [Fact]
    public void Command_IsChmodWithTheDigits_AndAQuotedPath()
    {
        Assert.Equal("chmod -R 755 '/srv/my site'", PermissionCommand.Build("/srv/my site", Mode755));
        Assert.Equal("chmod -R 640 './-odd'", PermissionCommand.Build("-odd", 0b110_100_000));
        Assert.Equal("chmod -R 700 '/a'\\''b'", PermissionCommand.Build("/a'b", 0b1_111_000_000));
    }

    [Fact]
    public async Task ThisComputer_OnWindows_OffersNoPermissions_AndElsewhereChangesThemWithoutFollowingLinks()
    {
        var local = new LocalFileSource();
        if (OperatingSystem.IsWindows())
        {
            Assert.False(local.CanChangeMode);
            return;
        }

        string root = Path.Combine(TestData.Root, "permissions-" + Guid.NewGuid().ToString("N"));
        try
        {
            string site = Path.Combine(root, "site");
            string outside = Path.Combine(root, "outside.txt");
            Directory.CreateDirectory(Path.Combine(site, "img"));
            File.WriteAllText(Path.Combine(site, "img", "logo.png"), "x");
            File.WriteAllText(outside, "x");
            File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.CreateSymbolicLink(Path.Combine(site, "link"), outside);

            var row = new FileRow("site", site, 0, DateTime.UtcNow, true, false, string.Empty, null);
            PermissionReport report = await PermissionChanger.ApplyAsync(local, [row], new PermissionChoice(0b111_101_000, PermissionScope.Everything), null, CancellationToken.None);

            const UnixFileMode Expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
            Assert.Equal(Expected, File.GetUnixFileMode(Path.Combine(site, "img", "logo.png")));
            Assert.Equal(Expected, File.GetUnixFileMode(Path.Combine(site, "img")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(outside));
            Assert.Equal(1, report.LinksLeftOut);
        }
        finally
        {
            TestData.Delete(root);
        }
    }

    [Fact]
    public void Command_RunInAShell_LeavesWhatALinkPointsAtAlone()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh")) return;

        string root = Path.Combine(TestData.Root, "chmod-" + Guid.NewGuid().ToString("N"));
        try
        {
            string site = Path.Combine(root, "site");
            string outside = Path.Combine(root, "outside.txt");
            Directory.CreateDirectory(site);
            File.WriteAllText(Path.Combine(site, "page"), "x");
            File.WriteAllText(outside, "x");
            File.SetUnixFileMode(outside, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.CreateSymbolicLink(Path.Combine(site, "link"), outside);

            var start = new ProcessStartInfo("/bin/sh") { RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(PermissionCommand.Build(site, 0b111_000_000));
            using Process process = Process.Start(start)!;
            process.WaitForExit();

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(site, "page")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(outside));
        }
        finally
        {
            TestData.Delete(root);
        }
    }
}

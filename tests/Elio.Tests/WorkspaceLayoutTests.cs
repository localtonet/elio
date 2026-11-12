using Elio.Core;

namespace Elio.Tests;

public sealed class WorkspaceLayoutTests : TempDataDirectoryTest
{
    private static readonly Guid Web = Guid.NewGuid();
    private static readonly Guid Database = Guid.NewGuid();
    private static readonly Guid Gone = Guid.NewGuid();

    private static PaneLayout Pane(Guid host) => new() { HostId = host };

    private static PaneLayout Split(PaneLayout first, PaneLayout second, bool horizontal = true, double ratio = 0.5) =>
        new() { Horizontal = horizontal, Ratio = ratio, First = first, Second = second };

    private static bool Exists(Guid id) => id != Gone;

    [Fact]
    public void SavedWorkspace_WrittenToItsFile_ComesBackTheSame()
    {
        var file = new WorkspaceFile();
        file.Workspaces.Add(new SavedWorkspace
        {
            Name = "deploy",
            SavedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Tabs = WorkspaceLayouts.Capture(
            [
                new TabLayout { Title = "front", Color = 0x3366FF, Pinned = true, Root = Split(Pane(Web), Split(Pane(Database), new PaneLayout { HostId = Web, Files = true, OtherHostId = Database }, horizontal: false, ratio: 0.3), ratio: 0.7) },
                new TabLayout { Root = new PaneLayout { HostId = Database, Directory = "/var/log" } },
            ]),
        });

        Stores.Workspaces().Save(file);
        SavedWorkspace read = Assert.Single(Stores.Workspaces().Load().Workspaces);

        Assert.True(File.Exists(Path.Combine(DataDirectory, "workspaces.json")));
        Assert.Equal(file.Workspaces[0].Id, read.Id);
        Assert.Equal("deploy", read.Name);
        Assert.Equal(file.Workspaces[0].SavedUtc, read.SavedUtc);
        Assert.Equal(2, read.Tabs.Count);

        TabLayout front = read.Tabs[0];
        Assert.Equal("front", front.Title);
        Assert.Equal(0x3366FFu, front.Color);
        Assert.True(front.Pinned);
        Assert.True(front.Root.Horizontal);
        Assert.Equal(0.7, front.Root.Ratio);
        Assert.Equal(Web, front.Root.First!.HostId);
        Assert.False(front.Root.Second!.Horizontal);
        Assert.Equal(0.3, front.Root.Second.Ratio);
        Assert.Equal(Database, front.Root.Second.First!.HostId);
        Assert.True(front.Root.Second.Second!.Files);
        Assert.Equal(Database, front.Root.Second.Second.OtherHostId);

        Assert.Null(read.Tabs[1].Title);
        Assert.False(read.Tabs[1].Pinned);
        Assert.Equal("/var/log", read.Tabs[1].Root.Directory);
    }

    [Fact]
    public void Capture_LeavesOutPanesWithNothingToComeBackTo_AndTheNameOfARunningShell()
    {
        List<TabLayout> kept = WorkspaceLayouts.Capture(
        [
            new TabLayout { Root = new PaneLayout() },
            new TabLayout { Root = Split(new PaneLayout(), new PaneLayout { HostId = Web, ShellId = "shell-7", Directory = "/srv" }) },
        ]);

        PaneLayout pane = Assert.Single(kept).Root;
        Assert.Equal(Web, pane.HostId);
        Assert.Equal("/srv", pane.Directory);
        Assert.Null(pane.ShellId);
        Assert.Null(pane.First);
    }

    [Fact]
    public void Capture_CopiesTheLayout_SoLaterChangesToTheTabsDoNotReachTheWorkspace()
    {
        var tab = new TabLayout { Title = "one", Root = Pane(Web) };

        List<TabLayout> kept = WorkspaceLayouts.Capture([tab]);
        tab.Title = "two";
        tab.Root.HostId = Database;

        Assert.Equal("one", kept[0].Title);
        Assert.Equal(Web, kept[0].Root.HostId);
    }

    [Fact]
    public void Resolve_WithAMissingServer_SkipsJustThatPaneAndThatTab()
    {
        var workspace = new SavedWorkspace
        {
            Tabs =
            [
                new TabLayout { Title = "both", Root = Split(Pane(Web), Split(Pane(Gone), Pane(Database), horizontal: false), ratio: 0.4) },
                new TabLayout { Title = "gone", Root = Pane(Gone) },
                new TabLayout { Title = "whole", Pinned = true, Root = Pane(Database) },
            ],
        };

        List<TabLayout> tabs = WorkspaceLayouts.Resolve(workspace, Exists, out int skipped);

        Assert.Equal(2, skipped);
        Assert.Equal(["both", "whole"], tabs.Select(t => t.Title));

        // The split that lost one side is now its other side, inside the split that is still whole.
        PaneLayout both = tabs[0].Root;
        Assert.Equal(0.4, both.Ratio);
        Assert.Equal(Web, both.First!.HostId);
        Assert.Equal(Database, both.Second!.HostId);
        Assert.Null(both.Second.First);

        Assert.True(tabs[1].Pinned);

        // The workspace itself keeps the pane, in case the server comes back with sync.
        Assert.Equal(3, workspace.Tabs.Count);
    }

    [Fact]
    public void Resolve_WithEveryServerThere_ChangesNothing()
    {
        var workspace = new SavedWorkspace { Tabs = [new TabLayout { Root = Split(Pane(Web), Pane(Database)) }] };

        List<TabLayout> tabs = WorkspaceLayouts.Resolve(workspace, Exists, out int skipped);

        Assert.Equal(0, skipped);
        Assert.Equal(Web, Assert.Single(tabs).Root.First!.HostId);
    }

    [Fact]
    public void Resolve_FilesOfTwoServersWithOneGone_OpensTheOtherOnItsOwn()
    {
        var workspace = new SavedWorkspace
        {
            Tabs =
            [
                new TabLayout { Root = new PaneLayout { HostId = Gone, Files = true, OtherHostId = Web } },
                new TabLayout { Root = new PaneLayout { HostId = Database, Files = true, OtherHostId = Gone } },
            ],
        };

        List<TabLayout> tabs = WorkspaceLayouts.Resolve(workspace, Exists, out int skipped);

        Assert.Equal(2, skipped);
        Assert.Equal((Web, true, null), (tabs[0].Root.HostId, tabs[0].Root.Files, tabs[0].Root.OtherHostId));
        Assert.Equal((Database, true, null), (tabs[1].Root.HostId, tabs[1].Root.Files, tabs[1].Root.OtherHostId));
    }
}

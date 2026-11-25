using Elio.Core;
using Elio.Sync;

namespace Elio.Tests;

public sealed class SyncWorkspaceTests
{
    private static readonly DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    private static SavedWorkspace Workspace(Guid id, string name, DateTime saved, DateTime modified = default) =>
        new() { Id = id, Name = name, SavedUtc = saved, ModifiedUtc = modified, Tabs = [new TabLayout { Title = name }] };

    [Fact]
    public void WorkspacesFromBothDevices_AreKept()
    {
        var mine = new SyncDocument { Workspaces = [Workspace(Guid.NewGuid(), "Mine", Now.AddHours(-1))] };
        var theirs = new SyncDocument { Workspaces = [Workspace(Guid.NewGuid(), "Theirs", Now.AddHours(-2))] };

        SyncDocument merged = SyncMerge.Merge(mine, theirs, Now);

        Assert.Equal(["Mine", "Theirs"], merged.Workspaces!.Select(w => w.Name).Order());
    }

    [Fact]
    public void TheLaterChange_Wins_RenamingIncluded()
    {
        Guid id = Guid.NewGuid();
        var mine = new SyncDocument { Workspaces = [Workspace(id, "Old name", Now.AddHours(-3))] };
        var theirs = new SyncDocument { Workspaces = [Workspace(id, "New name", Now.AddHours(-3), modified: Now.AddHours(-1))] };

        Assert.Equal("New name", Assert.Single(SyncMerge.Merge(mine, theirs, Now).Workspaces!).Name);
        Assert.Equal("New name", Assert.Single(SyncMerge.Merge(theirs, mine, Now).Workspaces!).Name);
    }

    [Fact]
    public void AWorkspaceDeletedOnOneDevice_IsDeletedEverywhere()
    {
        Guid id = Guid.NewGuid();
        var mine = new SyncDocument { Workspaces = [], WorkspacesDeleted = new() { [id] = Now.AddMinutes(-5) } };
        var theirs = new SyncDocument { Workspaces = [Workspace(id, "Gone", Now.AddHours(-1))] };

        SyncDocument merged = SyncMerge.Merge(mine, theirs, Now);

        Assert.Empty(merged.Workspaces!);
        Assert.True(merged.WorkspacesDeleted!.ContainsKey(id));
    }

    [Fact]
    public void AWorkspaceSavedAgainAfterItWasDeleted_ComesBack()
    {
        Guid id = Guid.NewGuid();
        var mine = new SyncDocument { Workspaces = [], WorkspacesDeleted = new() { [id] = Now.AddHours(-2) } };
        var theirs = new SyncDocument { Workspaces = [Workspace(id, "Back", Now.AddHours(-1))] };

        SyncDocument merged = SyncMerge.Merge(mine, theirs, Now);

        Assert.Equal("Back", Assert.Single(merged.Workspaces!).Name);
        Assert.False(merged.WorkspacesDeleted!.ContainsKey(id));
    }

    [Fact]
    public void ACopyFromBeforeWorkspacesWereSynced_TakesNoneAway()
    {
        // An older Elio sends back what it merged without the workspaces it never knew about.
        var mine = new SyncDocument { Workspaces = [Workspace(Guid.NewGuid(), "Kept", Now.AddHours(-1))] };
        var older = new SyncDocument { Workspaces = null, WorkspacesDeleted = null };

        Assert.Equal("Kept", Assert.Single(SyncMerge.Merge(mine, older, Now).Workspaces!).Name);
        Assert.Equal("Kept", Assert.Single(SyncMerge.Merge(older, mine, Now).Workspaces!).Name);
    }

    [Fact]
    public void TabsTravelWithTheWorkspace()
    {
        Guid host = Guid.NewGuid();
        SavedWorkspace workspace = Workspace(Guid.NewGuid(), "Grid", Now.AddHours(-1));
        workspace.Tabs = [new TabLayout { Title = "web", Color = 0xFF5555, Pinned = true, Root = new PaneLayout { HostId = host } }];
        var mine = new SyncDocument { Workspaces = [workspace] };

        SyncDocument copy = SyncMerge.Merge(mine.Clone(), new SyncDocument(), Now);

        TabLayout tab = Assert.Single(Assert.Single(copy.Workspaces!).Tabs);
        Assert.Equal("web", tab.Title);
        Assert.Equal(0xFF5555u, tab.Color);
        Assert.True(tab.Pinned);
        Assert.Equal(host, tab.Root.HostId);
    }

    [Fact]
    public void ChangedUtc_IsTheLaterOfSavedAndModified()
    {
        Assert.Equal(Now, Workspace(Guid.NewGuid(), "a", Now, modified: default).ChangedUtc);
        Assert.Equal(Now.AddHours(1), Workspace(Guid.NewGuid(), "a", Now, modified: Now.AddHours(1)).ChangedUtc);
    }
}

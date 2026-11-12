using Elio.Core;

namespace Elio.UI;

/// <content>Layouts saved under a name: keeping this window's tabs as one, and opening, renaming and deleting them.</content>
public partial class MainView
{
    private static List<SavedWorkspace> SavedWorkspaces => AppServices.Workspaces.Workspaces;

    private static IEnumerable<SavedWorkspace> WorkspacesByName() =>
        SavedWorkspaces.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase);

    private static string TabCount(SavedWorkspace workspace) => L.T("{0} tabs", workspace.Tabs.Count);

    /// <summary>Everything about workspaces in one list: the saved ones, to open, and below them what can be done with them.</summary>
    public async Task ShowWorkspacesAsync()
    {
        List<PaletteItem> items = WorkspacesByName().Select(w => new PaletteItem(w.Name, TabCount(w), () => _ = OpenWorkspaceAsync(w))).ToList();
        items.Add(new(L.T("Save this layout as a workspace…"), L.T("Workspaces"), () => _ = SaveWorkspaceAsync()));
        if (SavedWorkspaces.Count > 0)
        {
            items.Add(new(L.T("Rename workspace…"), L.T("Workspaces"), () => _ = RenameWorkspaceAsync()));
            items.Add(new(L.T("Delete workspace…"), L.T("Workspaces"), () => _ = DeleteWorkspaceAsync()));
        }

        if (await Dialogs.ChooseAsync(items, L.T("Workspaces")) is PaletteItem chosen) chosen.Run();
    }

    /// <summary>Keeps this window's tabs, with their panes, names, colors and pins, under a name the user gives.</summary>
    public async Task SaveWorkspaceAsync()
    {
        List<TabLayout> tabs = WorkspaceLayouts.Capture(Workspace.ExportTabs());
        if (tabs.Count == 0)
        {
            await Dialogs.ShowMessageAsync(L.T("Workspaces"), L.T("There is nothing open to save."));
            return;
        }

        if (await Dialogs.AskTextAsync(L.T("Save this layout as a workspace…"), L.T("Workspace name"), null, L.T("Save")) is not string name) return;

        // Saving under a name that is taken is how a workspace is brought up to date, once the user has agreed to it.
        SavedWorkspace? same = SavedWorkspaces.FirstOrDefault(w => w.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (same is not null && !await Dialogs.ConfirmAsync(L.T("Save this layout as a workspace…"), L.T("A workspace called \"{0}\" already exists. Replace it?", same.Name), L.T("Replace"))) return;

        if (same is null) SavedWorkspaces.Add(same = new SavedWorkspace { Name = name });
        same.Tabs = tabs;
        same.SavedUtc = DateTime.UtcNow;
        AppServices.SaveWorkspaces();
    }

    /// <summary>Asks which workspace, and opens it.</summary>
    public async Task ChooseWorkspaceToOpenAsync()
    {
        if (await PickWorkspaceAsync(L.T("Open workspace…")) is SavedWorkspace workspace) await OpenWorkspaceAsync(workspace);
    }

    /// <summary>
    /// Adds a workspace's tabs to this window. A pane whose server has been deleted since is left out, and the user
    /// is told how many were, rather than the whole workspace failing over it.
    /// </summary>
    public async Task OpenWorkspaceAsync(SavedWorkspace workspace)
    {
        List<TabLayout> tabs = WorkspaceLayouts.Resolve(workspace, id => HostList.Find(id) is not null, out int skipped);
        int opened = Workspace.OpenTabs(tabs, HostList.Find);

        if (opened == 0) await Dialogs.ShowMessageAsync(workspace.Name, L.T("None of the servers in \"{0}\" exist any more.", workspace.Name));
        else if (skipped > 0) await Dialogs.ShowMessageAsync(workspace.Name, L.T("{0} panes were left out because their servers no longer exist.", skipped));
    }

    public async Task RenameWorkspaceAsync()
    {
        if (await PickWorkspaceAsync(L.T("Rename workspace…")) is not SavedWorkspace workspace) return;
        if (await Dialogs.AskTextAsync(L.T("Rename workspace…"), L.T("Workspace name"), workspace.Name, L.T("Save")) is not string name) return;

        workspace.Name = name;
        AppServices.SaveWorkspaces();
    }

    public async Task DeleteWorkspaceAsync()
    {
        if (await PickWorkspaceAsync(L.T("Delete workspace…")) is not SavedWorkspace workspace) return;
        if (!await Dialogs.ConfirmAsync(L.T("Delete workspace…"), L.T("\"{0}\" will be deleted.", workspace.Name), L.T("Delete"), danger: true)) return;

        SavedWorkspaces.Remove(workspace);
        AppServices.SaveWorkspaces();
    }

    private async Task<SavedWorkspace?> PickWorkspaceAsync(string placeholder)
    {
        if (SavedWorkspaces.Count == 0)
        {
            await Dialogs.ShowMessageAsync(L.T("Workspaces"), L.T("No workspaces saved yet. Open the tabs you want, then choose \"Save this layout as a workspace…\"."));
            return null;
        }

        SavedWorkspace? picked = null;
        List<PaletteItem> items = WorkspacesByName().Select(w => new PaletteItem(w.Name, TabCount(w), () => picked = w)).ToList();
        if (await Dialogs.ChooseAsync(items, placeholder) is not PaletteItem chosen) return null;

        chosen.Run();
        return picked;
    }
}

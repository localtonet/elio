namespace Elio.Core;

/// <summary>
/// Turns the tabs of a window into a workspace that can be kept, and a kept workspace back into tabs that can
/// be opened. Both are the layout the last session is saved in, so the code that restores a session opens these too.
/// </summary>
public static class WorkspaceLayouts
{
    /// <summary>
    /// A copy of the tabs that is safe to keep: panes that show no saved server or shell are left out, and so
    /// is anything that only means something while the app that wrote it is still running.
    /// </summary>
    public static List<TabLayout> Capture(IEnumerable<TabLayout> tabs) =>
        Keep(tabs, _ => true, out _);

    /// <summary>
    /// The tabs of a workspace with every pane whose server is no longer there taken out. A split that loses one
    /// side becomes its other side, and a tab that loses every pane is dropped.
    /// </summary>
    /// <param name="exists">Whether a server or local shell with this id can still be opened.</param>
    /// <param name="skipped">How many panes were left out because their server is gone.</param>
    public static List<TabLayout> Resolve(SavedWorkspace workspace, Func<Guid, bool> exists, out int skipped) =>
        Keep(workspace.Tabs, exists, out skipped);

    private static List<TabLayout> Keep(IEnumerable<TabLayout> tabs, Func<Guid, bool> exists, out int skipped)
    {
        int missing = 0;
        var kept = new List<TabLayout>();
        foreach (TabLayout tab in tabs)
        {
            // A file written by hand may leave the root out, which is a tab with nothing in it.
            if (tab.Root is null || Pane(tab.Root, exists, ref missing) is not PaneLayout root) continue;
            kept.Add(new TabLayout { Root = root, Title = tab.Title, Color = tab.Color, Pinned = tab.Pinned });
        }

        skipped = missing;
        return kept;
    }

    private static PaneLayout? Pane(PaneLayout pane, Func<Guid, bool> exists, ref int missing)
    {
        if (pane.First is not null && pane.Second is not null)
        {
            PaneLayout? first = Pane(pane.First, exists, ref missing);
            PaneLayout? second = Pane(pane.Second, exists, ref missing);
            if (first is null || second is null) return first ?? second;

            return new PaneLayout { Horizontal = pane.Horizontal, Ratio = pane.Ratio, First = first, Second = second };
        }

        // A pane with no server is the welcome screen or someone else's shared terminal: nothing to come back to.
        if (pane.HostId is not Guid id) return null;

        bool here = exists(id);
        bool otherHere = pane.Files && pane.OtherHostId is Guid other && exists(other);
        if (pane.Files && pane.OtherHostId is not null && !otherHere) missing++;
        if (!here) missing++;

        // Of two servers' files, the one that is still there opens on its own.
        if (!here) return otherHere ? new PaneLayout { HostId = pane.OtherHostId, Files = true } : null;

        // The name of a shell that was left running is not kept: it belongs to the session that started it, and
        // a workspace opened twice must get two shells, not the same one twice.
        return new PaneLayout { HostId = id, Files = pane.Files, OtherHostId = otherHere ? pane.OtherHostId : null, Directory = pane.Directory };
    }
}

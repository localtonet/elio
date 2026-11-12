using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Elio.Core;

namespace Elio.UI;

/// <summary>Which side of a pane something dragged over it would land on.</summary>
internal enum DropSide
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <content>Dragging a server from the list onto a pane, to open it beside what is already there.</content>
public sealed partial class WorkspaceView
{
    /// <summary>
    /// Lets servers be dropped on a pane. Where the pointer is decides the side: the half of the pane nearest to it
    /// lights up, and the server opens there. With broadcast on, typing then reaches all of them at once.
    /// </summary>
    private static void AcceptServers(TerminalPane pane)
    {
        DragDrop.SetAllowDrop(pane.Frame, true);

        // The terminal inside says no to anything that is not a file. This runs after it, as the event comes up.
        DragDrop.AddDragOverHandler(pane.Frame, (_, e) =>
        {
            if (!e.DataTransfer.Contains(HostListView.HostsFormat)) return;

            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            pane.ShowDropSide(SideUnder(pane, e));
        });
        DragDrop.AddDragLeaveHandler(pane.Frame, (_, _) => pane.ShowDropSide(null));
        DragDrop.AddDropHandler(pane.Frame, (_, e) =>
        {
            pane.ShowDropSide(null);
            if (e.DataTransfer.TryGetValue(HostListView.HostsFormat) is not { Length: > 0 } hosts) return;

            e.Handled = true;
            _ = pane.Owner.OpenBesideAsync(pane, hosts, SideUnder(pane, e));
        });
    }

    /// <summary>
    /// Puts a close button in the pane's top corner. It shows while the pointer is over the pane, so that it is at
    /// hand with several panes open and out of the way of what the terminal prints the rest of the time.
    /// </summary>
    private static void AddCloseButton(TerminalPane pane)
    {
        Button close = ElioStyle.IconButton(ElioStyle.Icon(ElioStyle.IconClose, ElioColors.Text, 14, 1.8), L.T("Close pane"), 26);
        close.Background = ElioColors.Elevated;
        close.BorderBrush = ElioColors.Border;
        close.BorderThickness = new Thickness(1);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;

        // Clear of the scrollbar that runs down the same edge.
        close.Margin = new Thickness(0, 6, 16, 0);
        close.IsVisible = false;
        close.Focusable = false;
        close.Tag = "pane-close";
        close.Click += (_, _) => _ = pane.Owner.ClosePaneAsync(pane.Tab, pane);

        pane.Container.Children.Add(close);
        pane.Frame.PointerEntered += (_, _) => close.IsVisible = true;
        pane.Frame.PointerExited += (_, _) => close.IsVisible = false;
    }

    /// <summary>With nothing open yet there is no pane to drop on, so the empty window takes the server as a new tab.</summary>
    private void AcceptServersWhenEmpty()
    {
        DragDrop.SetAllowDrop(_content, true);
        DragDrop.AddDragOverHandler(_content, (_, e) =>
        {
            if (e.Handled || !e.DataTransfer.Contains(HostListView.HostsFormat)) return;
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        });
        DragDrop.AddDropHandler(_content, async (_, e) =>
        {
            if (e.Handled || e.DataTransfer.TryGetValue(HostListView.HostsFormat) is not { Length: > 0 } hosts) return;

            e.Handled = true;
            foreach (HostEntry host in hosts) await OpenAsync(host);
        });
    }

    private static DropSide SideUnder(TerminalPane pane, DragEventArgs e)
    {
        Point at = e.GetPosition(pane.Frame);
        double across = at.X / Math.Max(1, pane.Frame.Bounds.Width);
        double down = at.Y / Math.Max(1, pane.Frame.Bounds.Height);

        // Whichever edge the pointer is nearest to, measured as a share of the pane so that a wide pane is not all "top".
        double nearest = Math.Min(Math.Min(across, 1 - across), Math.Min(down, 1 - down));
        return nearest == across ? DropSide.Left
            : nearest == 1 - across ? DropSide.Right
            : nearest == down ? DropSide.Top
            : DropSide.Bottom;
    }

    /// <summary>Opens servers beside a pane, each next to the one before it.</summary>
    private async Task OpenBesideAsync(TerminalPane target, IReadOnlyList<HostEntry> hosts, DropSide side)
    {
        // tmux decides what its windows hold, so there a server gets a tab of its own.
        if (target.Tmux is not null)
        {
            foreach (HostEntry host in hosts) await OpenAsync(host);
            return;
        }

        Orientation orientation = side is DropSide.Left or DropSide.Right ? Orientation.Horizontal : Orientation.Vertical;
        bool before = side is DropSide.Left or DropSide.Top;
        ContentPane beside = target;

        foreach (HostEntry host in hosts)
        {
            if (!await EnsureSecretsAsync(host)) continue;

            // The pane or its tab may have been closed while a password was being asked for.
            if (!_tabs.Contains(beside.Tab) || !beside.Tab.AllPanes.Contains(beside)) return;

            TerminalPane added = CreatePane(host, interactive: true);
            InsertAt(beside.Tab, beside, added, orientation, before);
            beside = added;

            // After the first, the rest follow it in the same direction.
            before = false;
        }
    }

    private void InsertAt(WorkspaceTab tab, ContentPane existing, ContentPane added, Orientation orientation, bool before)
    {
        added.Tab = tab;
        added.Owner = this;
        tab.Zoomed = null;

        var split = new SplitNode { Orientation = orientation, First = before ? added : existing, Second = before ? existing : added, Parent = existing.Parent };
        ReplaceChild(tab, existing, split);
        existing.Parent = split;
        added.Parent = split;
        tab.Active = added;
        RebuildVisual(tab);
        ShareEvenly(split);
        ShowCurrent();
        LayoutChanged();
    }

    /// <summary>
    /// Gives the panes in a row, or in a column, the same share each after one more joins them. Splitting only ever
    /// halves one pane, so without this the fifth pane in a row would be a sliver beside four wide ones.
    /// </summary>
    private static void ShareEvenly(SplitNode added)
    {
        // The whole run of splits that go the same way, from its top.
        SplitNode top = added;
        while (top.Parent is SplitNode parent && parent.Orientation == added.Orientation) top = parent;

        int Share(PaneNode node)
        {
            if (node is not SplitNode split || split.Orientation != added.Orientation) return 1;

            int first = Share(split.First);
            int second = Share(split.Second);
            split.Ratio = (double)first / (first + second);
            return first + second;
        }

        Share(top);
    }
}

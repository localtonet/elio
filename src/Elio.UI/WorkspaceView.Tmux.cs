using Avalonia.Layout;
using Avalonia.Threading;
using Elio.Connections;
using Elio.Core;
using Elio.Terminal;

namespace Elio.UI;

/// <content>tmux in control mode: each tmux window is a tab here, and each of its panes a pane.</content>
public sealed partial class WorkspaceView
{
    private sealed class TmuxTab(WorkspaceTab tab, HostEntry host)
    {
        public WorkspaceTab Tab { get; } = tab;

        /// <summary>The made-up entry all of the window's panes share, so renaming the window renames the tab.</summary>
        public HostEntry Host { get; } = host;

        /// <summary>Which panes the window has and how they are split, without their sizes.</summary>
        public string Shape { get; set; } = string.Empty;
    }

    private static readonly Dictionary<TmuxSession, Dictionary<TmuxWindow, TmuxTab>> TmuxSessions = [];

    /// <summary>
    /// Lets a shell be taken over by "tmux -CC". Until tmux says so, everything passes through untouched.
    /// </summary>
    private ITerminalTransport WatchForTmux(ITerminalTransport transport)
    {
        var gateway = new TmuxGatewayTransport(transport);

        // Both arrive on the thread the shell's output is read on.
        gateway.Attached += session => Dispatcher.UIThread.Post(() => OnTmuxAttached(session));
        gateway.Detached += session => Dispatcher.UIThread.Post(() => OnTmuxDetached(session));
        return gateway;
    }

    private void OnTmuxAttached(TmuxSession session)
    {
        if (session.IsClosed || TmuxSessions.ContainsKey(session)) return;

        TmuxSessions[session] = [];
        foreach (TmuxWindow window in session.Windows) AddTmuxTab(session, window);

        session.WindowAdded += window => Dispatcher.UIThread.Post(() => AddTmuxTab(session, window));
        session.WindowClosed += window => Dispatcher.UIThread.Post(() => RemoveTmuxTab(session, window));
        session.WindowRenamed += window => Dispatcher.UIThread.Post(() => RenameTmuxTab(session, window));
        session.LayoutChanged += window => Dispatcher.UIThread.Post(() => RelayoutTmuxTab(session, window));

        if (session.Windows.FirstOrDefault(w => w.Active) is TmuxWindow active && TmuxSessions[session].TryGetValue(active, out TmuxTab? front)) Select(front.Tab);
    }

    private void OnTmuxDetached(TmuxSession session)
    {
        if (!TmuxSessions.Remove(session, out Dictionary<TmuxWindow, TmuxTab>? tabs)) return;
        foreach (TmuxTab entry in tabs.Values) DropTmuxTab(entry);
    }

    private static string TmuxTitle(TmuxWindow window) => "tmux · " + window.Name;

    private void AddTmuxTab(TmuxSession session, TmuxWindow window)
    {
        if (!TmuxSessions.TryGetValue(session, out Dictionary<TmuxWindow, TmuxTab>? tabs) || tabs.ContainsKey(window) || window.Layout is not TmuxLayoutNode layout) return;

        var host = new HostEntry { Kind = HostKind.Local, Name = TmuxTitle(window), AutoReconnect = false };
        PaneNode root = BuildTmux(session, host, layout, []);
        root.Parent = null;
        tabs[window] = new TmuxTab(AddTab(root), host) { Shape = ShapeOf(layout) };
    }

    private void RemoveTmuxTab(TmuxSession session, TmuxWindow window)
    {
        if (TmuxSessions.TryGetValue(session, out Dictionary<TmuxWindow, TmuxTab>? tabs) && tabs.Remove(window, out TmuxTab? entry)) DropTmuxTab(entry);
    }

    // The tab may have been dragged to another window since it was made.
    private static void DropTmuxTab(TmuxTab entry)
    {
        List<ContentPane> panes = entry.Tab.AllPanes.ToList();
        WorkspaceView owner = panes[0].Owner;
        if (owner._tabs.Contains(entry.Tab))
        {
            owner.RemoveTab(entry.Tab);
            owner.LayoutChanged();
        }

        foreach (ContentPane pane in panes) _ = pane.ShutAsync();
    }

    private static void RenameTmuxTab(TmuxSession session, TmuxWindow window)
    {
        if (!TmuxSessions.TryGetValue(session, out Dictionary<TmuxWindow, TmuxTab>? tabs) || !tabs.TryGetValue(window, out TmuxTab? entry)) return;

        entry.Host.Name = TmuxTitle(window);
        entry.Tab.AllPanes.First().Owner.RebuildTabStrip();
    }

    /// <summary>
    /// Follows tmux when panes come and go. A change of sizes alone is left be: the sizes are decided here,
    /// by the splitters, and tmux is told, so following it back would have the two chase each other.
    /// </summary>
    private static void RelayoutTmuxTab(TmuxSession session, TmuxWindow window)
    {
        if (!TmuxSessions.TryGetValue(session, out Dictionary<TmuxWindow, TmuxTab>? tabs) || !tabs.TryGetValue(window, out TmuxTab? entry) || window.Layout is not TmuxLayoutNode layout) return;

        string shape = ShapeOf(layout);
        if (shape == entry.Shape) return;
        entry.Shape = shape;

        WorkspaceTab tab = entry.Tab;
        WorkspaceView owner = tab.AllPanes.First().Owner;
        Dictionary<string, TerminalPane> kept = tab.AllPanes.OfType<TerminalPane>().Where(p => p.TmuxPaneId is not null).ToDictionary(p => p.TmuxPaneId!);

        owner.RebuildVisual(tab);
        PaneNode root = owner.BuildTmux(session, entry.Host, layout, kept);
        root.Parent = null;
        tab.Root = root;
        tab.Zoomed = null;

        List<ContentPane> panes = tab.AllPanes.ToList();
        foreach (ContentPane pane in panes)
        {
            pane.Tab = tab;
            pane.Owner = owner;
        }

        // A pane tmux just made is the one it put the cursor in.
        tab.Active = panes.OfType<TerminalPane>().FirstOrDefault(p => p.TmuxPaneId == window.ActivePaneId) ?? (panes.Contains(tab.Active) ? tab.Active : panes[0]);
        foreach (TerminalPane gone in kept.Values) _ = gone.ShutAsync();

        if (owner._current == tab) owner.ShowCurrent();
    }

    private static string ShapeOf(TmuxLayoutNode node) => node.Kind == TmuxLayoutKind.Pane
        ? node.PaneId ?? "?"
        : (node.Kind == TmuxLayoutKind.Columns ? "C(" : "R(") + string.Join(',', node.Children.Select(ShapeOf)) + ")";

    /// <summary>Turns tmux's layout, where a split can have any number of sides, into nested two-sided splits.</summary>
    private PaneNode BuildTmux(TmuxSession session, HostEntry host, TmuxLayoutNode node, Dictionary<string, TerminalPane> kept)
    {
        if (node.Kind == TmuxLayoutKind.Pane || node.Children.Count == 0)
        {
            string id = node.PaneId ?? string.Empty;
            if (kept.Remove(id, out TerminalPane? existing)) return existing;

            TerminalPane pane = CreatePane(host, interactive: false, null, (_, _, _) => Task.FromResult(session.OpenPane(id)));
            pane.Tmux = session;
            pane.TmuxPaneId = id;
            return pane;
        }

        bool sideBySide = node.Kind == TmuxLayoutKind.Columns;
        int SizeOf(TmuxLayoutNode child) => sideBySide ? child.Width : child.Height;

        PaneNode result = BuildTmux(session, host, node.Children[^1], kept);
        double rest = SizeOf(node.Children[^1]);
        for (int i = node.Children.Count - 2; i >= 0; i--)
        {
            PaneNode first = BuildTmux(session, host, node.Children[i], kept);
            double size = SizeOf(node.Children[i]);
            var split = new SplitNode { Orientation = sideBySide ? Orientation.Horizontal : Orientation.Vertical, First = first, Second = result, Ratio = size / Math.Max(1, size + rest) };
            first.Parent = split;
            result.Parent = split;
            result = split;

            // tmux draws its dividers one cell wide.
            rest += size + 1;
        }

        return result;
    }

    /// <summary>Whether tmux, not this window, decides what happens to the pane. Asks tmux to do it if so.</summary>
    private static bool TmuxCloses(ContentPane pane)
    {
        if (pane is not TerminalPane { Tmux: TmuxSession session, TmuxPaneId: string id } || session.IsClosed) return false;

        _ = session.KillPane(id);
        return true;
    }

    private static bool TmuxClosesTab(WorkspaceTab tab)
    {
        foreach ((TmuxSession session, Dictionary<TmuxWindow, TmuxTab> tabs) in TmuxSessions)
        {
            TmuxWindow? window = tabs.FirstOrDefault(pair => pair.Value.Tab == tab).Key;
            if (window is null || session.IsClosed) continue;

            _ = session.KillWindow(window.Id);
            return true;
        }

        return false;
    }

    private static bool TmuxSplits(ContentPane pane, Orientation orientation)
    {
        if (pane is not TerminalPane { Tmux: TmuxSession session, TmuxPaneId: string id } || session.IsClosed) return false;

        _ = session.SplitPane(id, orientation == Orientation.Horizontal);
        return true;
    }
}

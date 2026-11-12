using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Elio.Core;

namespace Elio.UI;

/// <summary>The saved servers, in nested groups. Choosing one opens it in a new tab.</summary>
public sealed class HostListView : UserControl
{
    private const double IndentPerLevel = 14;
    private const double GroupRowHeight = 28;
    private const double SnippetRowHeight = 28;

    // On a Mac the window's own buttons sit in the sidebar's top corner, and on a phone the button that
    // opens the list does. The list starts below them.
    // On Windows and macOS the window's own title and buttons are drawn over the top of the content, so the
    // list starts below them. A phone has its status bar there.
    /// <summary>What a server row carries while it is dragged onto a pane.</summary>
    public static readonly DataFormat<HostEntry[]> HostsFormat = DataFormat.CreateInProcessFormat<HostEntry[]>("elio-hosts");

    private const double DragStartDistance = 6;

    /// <summary>
    /// Lets a row be dragged onto a terminal pane, where the server then opens beside what is there. A press that does
    /// not move stays a click.
    /// </summary>
    internal static void EnableDrag(Control row, HostEntry host)
    {
        PointerPressedEventArgs? pressed = null;
        Point pressedAt = default;

        row.PointerPressed += (_, e) =>
        {
            pressed = e.GetCurrentPoint(row).Properties.IsLeftButtonPressed ? e : null;
            pressedAt = e.GetPosition(row);
        };
        row.PointerReleased += (_, _) => pressed = null;
        row.PointerMoved += (_, e) =>
        {
            if (pressed is not PointerPressedEventArgs start) return;

            Point now = e.GetPosition(row);
            if (Math.Abs(now.X - pressedAt.X) + Math.Abs(now.Y - pressedAt.Y) <= DragStartDistance) return;

            pressed = null;
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(HostsFormat, new[] { host }));
            _ = DragDrop.DoDragDropAsync(start, data, DragDropEffects.Copy);
        };
    }

    // A second tap this soon after one that connected is the same gesture, not a wish for a second connection.
    private static readonly TimeSpan RepeatedTapWindow = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// Makes a row open what it stands for: with a double-click on a computer, where one click only selects, and with
    /// one tap on a phone, where there is nothing to select and a double tap is awkward.
    /// </summary>
    internal static void ConnectOnActivate(Control row, Action connect)
    {
        if (!AppServices.MobileLayout)
        {
            row.DoubleTapped += (_, _) => connect();
            return;
        }

        // A finger that was held down for the menu, or that moved far enough to drag or scroll, was not a tap, even
        // if it comes up where it went down.
        bool spent = false;
        Point pressedAt = default;
        DateTime lastTap = DateTime.MinValue;

        row.PointerPressed += (_, e) => (spent, pressedAt) = (false, e.GetPosition(row));
        row.PointerMoved += (_, e) =>
        {
            Point now = e.GetPosition(row);
            if (Math.Abs(now.X - pressedAt.X) + Math.Abs(now.Y - pressedAt.Y) > DragStartDistance) spent = true;
        };
        row.Holding += (_, _) => spent = true;
        row.ContextRequested += (_, _) => spent = true;
        row.Tapped += (_, e) =>
        {
            if (spent || e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

            // The second half of a double tap arrives as a tap of its own.
            DateTime now = DateTime.UtcNow;
            if (now - lastTap < RepeatedTapWindow) return;

            lastTap = now;
            e.Handled = true;
            connect();
        };
    }

    private static readonly double TopInset = OperatingSystem.IsMacOS() || AppServices.MobileLayout ? 44 : OperatingSystem.IsWindows() ? 40 : 6;
    private const double SnippetListHeight = 160;

    private readonly HostLibrary _library = AppServices.Library;
    private readonly DialogHost _dialogs;
    private readonly StackPanel _rows = new() { Spacing = 1 };
    private readonly StackPanel _snippetRows = new() { Spacing = 1 };
    private readonly TextBox _filter;
    private readonly HashSet<Guid> _selected = [];

    /// <summary>The account's devices, under the saved servers.</summary>
    public LocaltonetPanel Localtonet { get; }

    public event Action<HostEntry>? ConnectRequested;

    public event Action<HostEntry>? FilesRequested;

    /// <summary>The user wants this server's files beside those of another server, yet to be picked.</summary>
    public event Action<HostEntry>? FilesWithAnotherRequested;

    public event Action<IReadOnlyList<HostEntry>>? OpenManyRequested;

    /// <summary>The user wants to run a command on these servers.</summary>
    public event Action<IReadOnlyList<HostEntry>, string?>? RunManyRequested;

    public event Action<Snippet>? SnippetRequested;

    public IReadOnlyList<Snippet> Snippets => _library.Snippets;

    /// <summary>The servers picked with Ctrl+click.</summary>
    public IReadOnlyList<HostEntry> SelectedHosts => _library.Hosts.Where(h => _selected.Contains(h.Id)).ToList();

    public IReadOnlyList<HostEntry> Hosts => _library.Hosts;

    public IReadOnlyList<HostGroup> Groups => _library.Groups;

    /// <summary>A saved server or a Localtonet device.</summary>
    public HostEntry? Find(Guid id) =>
        _library.Hosts.FirstOrDefault(h => h.Id == id)
        ?? AppServices.Localtonet.Devices.FirstOrDefault(h => h.Id == id)
        ?? AppServices.LocalShells.FirstOrDefault(h => h.Id == id);

    public HostListView(DialogHost dialogs)
    {
        _dialogs = dialogs;

        _filter = ElioStyle.SearchBox(L.T("Search servers"));
        _filter.Margin = new Thickness(0, 0, 0, 10);
        _filter.TextChanged += (_, _) => Rebuild();

        Button add = HeaderButton(ElioStyle.IconPlus, L.T("Add server"));
        add.Click += async (_, _) => await AddAsync();
        Button addGroup = HeaderButton(ElioStyle.IconFolderPlus, L.T("Add group"));
        addGroup.Click += async (_, _) => await AddGroupAsync(null);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { addGroup, add } };

        if (AppServices.LocalShells.Count > 0)
        {
            // A click opens the usual shell, a right click offers the others.
            Button shell = HeaderButton(ElioStyle.IconTerminal, L.T("Open local shell: {0}", AppServices.LocalShells[0].Name));
            shell.Tag = "local-shell";
            shell.Click += (_, _) => ConnectRequested?.Invoke(AppServices.LocalShells[0]);
            shell.ContextMenu = new ContextMenu { ItemsSource = AppServices.LocalShells.Select(s => Item(s.Name, () => ConnectRequested?.Invoke(s))).ToList() };
            buttons.Children.Insert(0, shell);
        }
        var header = new DockPanel { Margin = new Thickness(6, 0, 0, 6) };
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(ElioStyle.SectionTitle(L.T("Servers")));

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_filter, Dock.Top);
        Button addSnippet = HeaderButton(ElioStyle.IconPlus, L.T("Add snippet"));
        addSnippet.Click += async (_, _) => await AddSnippetAsync();
        var snippetHeader = new DockPanel { Margin = new Thickness(6, 10, 0, 2) };
        snippetHeader.Children.Add(Docked(addSnippet, Dock.Right));
        snippetHeader.Children.Add(ElioStyle.SectionTitle(L.T("Snippets")));
        var snippets = new DockPanel();
        snippets.Children.Add(Docked(snippetHeader, Dock.Top));
        snippets.Children.Add(new ScrollViewer { MaxHeight = SnippetListHeight, Content = _snippetRows });

        if (ShowsBrand) layout.Children.Add(Docked(Brand(), Dock.Top));
        layout.Children.Add(header);
        layout.Children.Add(_filter);
        Localtonet = new LocaltonetPanel(dialogs);
        layout.Children.Add(Docked(Footer(), Dock.Bottom));
        layout.Children.Add(Docked(snippets, Dock.Bottom));
        layout.Children.Add(Docked(Localtonet, Dock.Bottom));
        if (AppServices.LocalShells.Count > 0) layout.Children.Add(Docked(LocalShellList(), Dock.Bottom));
        layout.Children.Add(new ScrollViewer { Content = _rows });

        // The hairline is on the side the workspace is on, whichever way the language reads.
        Content = new Border { Background = ElioColors.Sidebar, BorderBrush = ElioColors.Border, BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(10, ShowsBrand ? BrandInset : TopInset, 10, 10), Child = layout };
        Rebuild();
        RebuildSnippets();
    }

    /// <summary>Asks the window for something that is not this list's own to do: "settings", "sync", "keychain", "workspaces", "shortcuts" or "palette".</summary>
    public event Action<string>? ActionRequested;

    /// <summary>
    /// The way in to the things that otherwise only a key or the palette opens. They sit at the bottom of the list,
    /// where the eye goes looking for settings.
    /// </summary>
    private StackPanel Footer()
    {
        Button Entry(string icon, string tip, string action)
        {
            Button button = HeaderButton(icon, tip);
            button.Tag = "footer-" + action;
            button.Click += (_, _) => ActionRequested?.Invoke(action);
            return button;
        }

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(4, 10, 0, 0),
            Children =
            {
                Entry(ElioStyle.IconGear, L.T("Settings"), "settings"),
                Entry(ElioStyle.IconRefresh, L.T("Sync"), "sync"),
                Entry(ElioStyle.IconPanes, L.T("Workspaces"), "workspaces"),
                Entry(ElioStyle.IconKey, L.T("Keychain"), "keychain"),
                Entry(ElioStyle.IconKeyboard, L.T("Keyboard shortcuts"), "shortcuts"),
                Entry(ElioStyle.IconSearch, L.T("Command palette"), "palette"),
            },
        };
    }

    /// <summary>Adds what a file of servers holds that is not here already: its groups, commands and identities too.</summary>
    public int Merge(HostLibrary file)
    {
        // While the vault is locked nobody can say which identities have a secret, so each is treated as having one.
        Vault vault = AppServices.Vault;
        int added = LibraryMerge.Merge(_library, file, identity => vault.IsLocked || vault.Entries.ContainsKey(identity.Id));
        Commit();
        return added;
    }

    /// <summary>
    /// The shells of this computer, as rows like the servers: double-click to open one in a tab, or drag it onto a pane
    /// to have it beside what is there.
    /// </summary>
    private DockPanel LocalShellList()
    {
        var rows = new StackPanel();
        foreach (HostEntry shell in AppServices.LocalShells)
        {
            HostEntry entry = shell;
            var row = new Border
            {
                Background = ElioColors.Transparent,
                CornerRadius = new CornerRadius(ElioStyle.RowRadius),
                Padding = new Thickness(8, 0),
                Height = SnippetRowHeight,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = "local-shell-row",
                Child = new DockPanel
                {
                    Children =
                    {
                        Docked(ElioStyle.Icon(ElioStyle.IconTerminal, ElioColors.TextFaint, 14), Dock.Left),
                        new TextBlock { Text = entry.Name, Foreground = ElioColors.Text, FontSize = ElioStyle.BaseFontSize, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            ToolTip.SetTip(row, entry.Address);
            Highlight(row);
            ConnectOnActivate(row, () => ConnectRequested?.Invoke(entry));
            row.ContextMenu = new ContextMenu { ItemsSource = new[] { Item(L.T("Open"), () => ConnectRequested?.Invoke(entry)) } };
            EnableDrag(row, entry);
            rows.Children.Add(row);
        }

        var header = new DockPanel { Margin = new Thickness(6, 10, 0, 2), MinHeight = 26 };
        header.Children.Add(ElioStyle.SectionTitle(L.T("This computer")));

        var list = new DockPanel();
        list.Children.Add(Docked(header, Dock.Top));
        list.Children.Add(new ScrollViewer { MaxHeight = SnippetRowHeight * 4, Content = rows });
        return list;
    }

    // Where the window's title would be, at the top of the list, Elio's mark and name stand instead. Not on macOS,
    // where the window's own buttons are in that corner, nor on a phone.
    private static readonly bool ShowsBrand = !AppServices.MobileLayout && !OperatingSystem.IsMacOS();
    private const double BrandInset = 8;

    private static Control Brand() => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 7,
        Height = 26,
        Margin = new Thickness(5, 0, 0, 8),

        // Left to the window, so that it can still be dragged by this corner.
        IsHitTestVisible = false,
        Tag = "brand",
        Children =
        {
            ElioStyle.Mark(24, ElioColors.Text, ElioColors.Accent),
            new TextBlock
            {
                Text = "elio",
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
                FontSize = 15,
                FontWeight = FontWeight.Bold,
                Foreground = ElioColors.Text,
                VerticalAlignment = VerticalAlignment.Center,
            },
        },
    };

    private static T Docked<T>(T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static Button HeaderButton(string icon, string tip) => ElioStyle.IconButton(ElioStyle.Icon(icon), tip, 26);

    /// <summary>Group names indented by depth, led by "no group", for choosing a place in a list.</summary>
    private List<(Guid? Id, string Name)> GroupChoices()
    {
        var choices = new List<(Guid? Id, string Name)> { (null, L.T("No group")) };

        void Add(Guid? parent, int depth)
        {
            foreach (HostGroup group in _library.Groups.Where(g => g.ParentId == parent).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                choices.Add((group.Id, new string(' ', depth * 3) + group.Name));
                Add(group.Id, depth + 1);
            }
        }

        Add(null, 0);
        return choices;
    }

    public async Task AddAsync()
    {
        if (await _dialogs.EditHostAsync(null, GroupChoices(), _library.Hosts) is not HostEntry created) return;
        created.ModifiedUtc = DateTime.UtcNow;
        _library.Hosts.Add(created);
        Commit();
    }

    public async Task AddGroupAsync(HostGroup? parent)
    {
        string title = parent is null ? L.T("Add group") : L.T("Add group in \"{0}\"", parent.Name);
        if (await _dialogs.EditGroupAsync(title, null, parent?.Id, _library) is not HostGroup created) return;
        created.ModifiedUtc = DateTime.UtcNow;
        _library.Groups.Add(created);
        Commit();
    }

    private async Task EditAsync(HostEntry host)
    {
        if (await _dialogs.EditHostAsync(host, GroupChoices(), _library.Hosts) is not HostEntry edited) return;
        edited.ModifiedUtc = DateTime.UtcNow;
        _library.Hosts[_library.Hosts.IndexOf(host)] = edited;
        Commit();
    }

    private async Task RemoveAsync(HostEntry host)
    {
        if (!await _dialogs.ConfirmAsync(L.T("Delete server"), L.T("\"{0}\" and its saved password will be deleted.", host.Name), L.T("Delete"), danger: true)) return;
        _library.Hosts.Remove(host);
        _library.Deleted[host.Id] = DateTime.UtcNow;
        _selected.Remove(host.Id);
        AppServices.Vault.Remove(host.Id);
        AppServices.Vault.Remove(ProxySettings.SecretId(host.Id));
        Commit();
    }

    /// <summary>Adds the servers from an OpenSSH client config. Returns a note for the user.</summary>
    public string ImportSshConfig()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        if (!File.Exists(path)) return L.T("{0} not found.", path);

        int added = Merge(HostImport.FromSshConfig(File.ReadAllText(path), ReadInclude));
        return L.T("{0} new servers added.", added);
    }

    private static IEnumerable<string> ReadInclude(string pattern)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string expanded = pattern.StartsWith('~') ? home + pattern[1..] : pattern;
        string full = Path.IsPathRooted(expanded) ? expanded : Path.Combine(home, ".ssh", expanded);
        string? folder = Path.GetDirectoryName(full);
        if (folder is null || !Directory.Exists(folder)) return [];

        return Directory.GetFiles(folder, Path.GetFileName(full)).Order(StringComparer.Ordinal).Select(File.ReadAllText).ToList();
    }

    /// <summary>Adds the sessions saved in PuTTY. Returns a note for the user.</summary>
    public string ImportPutty()
    {
        List<HostEntry> found = HostImport.FromPutty();
        return found.Count == 0 ? L.T("No saved PuTTY sessions found.") : L.T("{0} new servers added.", Merge(found));
    }

    /// <summary>Adds what isn't there yet, so importing twice changes nothing.</summary>
    public int Merge(IReadOnlyList<HostEntry> imported)
    {
        int added = LibraryMerge.Merge(_library, imported);
        Commit();
        return added;
    }

    private async Task AddSnippetAsync()
    {
        if (await _dialogs.EditSnippetAsync(null) is not Snippet created) return;
        created.ModifiedUtc = DateTime.UtcNow;
        _library.Snippets.Add(created);
        Commit();
    }

    private async Task EditSnippetAsync(Snippet snippet)
    {
        if (await _dialogs.EditSnippetAsync(snippet) is not Snippet edited) return;
        edited.ModifiedUtc = DateTime.UtcNow;
        _library.Snippets[_library.Snippets.IndexOf(snippet)] = edited;
        Commit();
    }

    private async Task RemoveSnippetAsync(Snippet snippet)
    {
        if (!await _dialogs.ConfirmAsync(L.T("Delete snippet"), L.T("\"{0}\" will be deleted.", snippet.Name), L.T("Delete"), danger: true)) return;
        _library.Snippets.Remove(snippet);
        _library.Deleted[snippet.Id] = DateTime.UtcNow;
        Commit();
    }

    /// <summary>The picked servers if this one is among them, otherwise just this one.</summary>
    private List<HostEntry> ActedOn(HostEntry host) => _selected.Contains(host.Id) ? SelectedHosts.ToList() : [host];

    private IEnumerable<HostEntry> HostsUnder(HostGroup group) =>
        _library.Hosts.Where(h => h.GroupId == group.Id).Concat(_library.Groups.Where(g => g.ParentId == group.Id).SelectMany(HostsUnder));

    private void ToggleSelected(HostEntry host)
    {
        if (!_selected.Remove(host.Id)) _selected.Add(host.Id);
        ShowSelection();
    }

    public void ClearSelection()
    {
        _selected.Clear();
        ShowSelection();
    }

    private IBrush RestingBrush(Border row) =>
        row.Tag is Guid id && _selected.Contains(id) ? ElioColors.Selection : ElioColors.Transparent;

    private void ShowSelection()
    {
        foreach (Border row in _rows.Children.OfType<Border>()) row.Background = RestingBrush(row);
    }

    // The group's name, and what its servers connect with unless they say otherwise.
    private async Task EditGroupAsync(HostGroup group)
    {
        if (await _dialogs.EditGroupAsync(L.T("Edit group"), group, group.ParentId, _library) is not HostGroup edited) return;

        // The list may have been replaced by a sync while the dialog was open, so the group is found again by its id.
        int at = _library.Groups.FindIndex(g => g.Id == group.Id);
        if (at < 0) return;

        // Where the group sits and whether it is open are not the dialog's to change, and may have changed meanwhile.
        edited.ParentId = _library.Groups[at].ParentId;
        edited.Collapsed = _library.Groups[at].Collapsed;
        edited.ModifiedUtc = DateTime.UtcNow;
        _library.Groups[at] = edited;
        Commit();
    }

    private async Task RemoveGroupAsync(HostGroup group)
    {
        if (!await _dialogs.ConfirmAsync(L.T("Delete group"), L.T("\"{0}\" will be deleted. Its servers move up one level.", group.Name), L.T("Delete"), danger: true)) return;
        DateTime now = DateTime.UtcNow;
        foreach (HostEntry host in _library.Hosts.Where(h => h.GroupId == group.Id))
        {
            host.GroupId = group.ParentId;
            host.ModifiedUtc = now;
        }

        foreach (HostGroup child in _library.Groups.Where(g => g.ParentId == group.Id))
        {
            child.ParentId = group.ParentId;
            child.ModifiedUtc = now;
        }

        _library.Groups.Remove(group);
        _library.Deleted[group.Id] = now;

        // A group has a secret only if its proxy has a password. Deleting a group never asked for the vault to
        // be opened before, so a locked one is left alone.
        if (!AppServices.Vault.IsLocked) AppServices.Vault.Remove(ProxySettings.SecretId(group.Id));
        Commit();
    }

    /// <summary>Opens or closes a group in the list.</summary>
    public void Toggle(HostGroup group)
    {
        group.Collapsed = !group.Collapsed;
        group.ModifiedUtc = DateTime.UtcNow;
        Commit();
    }

    private static void Commit() => AppServices.SaveLibrary();

    private void ShowLibrary()
    {
        Rebuild();
        RebuildSnippets();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AppServices.LibraryChanged += ShowLibrary;
        ShowLibrary();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        AppServices.LibraryChanged -= ShowLibrary;
    }

    private static bool Matches(HostEntry host, string filter) =>
        host.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || host.Address.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || host.Tags.Any(t => t.Contains(filter, StringComparison.CurrentCultureIgnoreCase));

    private IEnumerable<HostEntry> Sorted(IEnumerable<HostEntry> hosts) => hosts.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase);

    private void Rebuild()
    {
        _rows.Children.Clear();

        string filter = _filter.Text?.Trim() ?? string.Empty;
        if (filter.Length > 0)
        {
            // A search shows matches from every group, ignoring what is collapsed.
            foreach (HostEntry host in Sorted(_library.Hosts.Where(h => Matches(h, filter)))) _rows.Children.Add(Row(host, 0));
            if (_rows.Children.Count == 0) _rows.Children.Add(Hint(L.T("No matching servers.")));
            return;
        }

        AddLevel(null, 0);
        if (_rows.Children.Count == 0) _rows.Children.Add(Hint(L.T("No servers yet. Add one with +.")));
    }

    private void RebuildSnippets()
    {
        _snippetRows.Children.Clear();
        foreach (Snippet snippet in _library.Snippets.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)) _snippetRows.Children.Add(SnippetRow(snippet));
        if (_snippetRows.Children.Count == 0) _snippetRows.Children.Add(Hint(L.T("Add the commands you use often here.")));
    }

    private Control SnippetRow(Snippet snippet)
    {
        var row = new Border
        {
            Background = ElioColors.Transparent,
            CornerRadius = new CornerRadius(ElioStyle.RowRadius),
            Padding = new Thickness(8, 0),
            Height = SnippetRowHeight,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = "snippet",
            Child = new DockPanel
            {
                Children =
                {
                    Docked(ElioStyle.Icon(ElioStyle.IconTerminal, ElioColors.TextFaint, 14), Dock.Left),
                    new TextBlock { Text = snippet.Name, Foreground = ElioColors.Text, FontSize = ElioStyle.BaseFontSize, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
        };
        ToolTip.SetTip(row, snippet.Command);
        Highlight(row);
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) SnippetRequested?.Invoke(snippet);
        };
        row.ContextMenu = new ContextMenu
        {
            ItemsSource = new[]
            {
                Item(L.T("Edit"), () => _ = EditSnippetAsync(snippet)),
                Item(L.T("Run on multiple servers"), () => RunManyRequested?.Invoke(SelectedHosts, snippet.Command)),
                Item(L.T("Delete"), () => _ = RemoveSnippetAsync(snippet)),
            },
        };
        return row;
    }

    private void AddLevel(Guid? parent, int depth)
    {
        foreach (HostGroup group in _library.Groups.Where(g => g.ParentId == parent).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _rows.Children.Add(GroupRow(group, depth));
            if (!group.Collapsed) AddLevel(group.Id, depth + 1);
        }

        foreach (HostEntry host in Sorted(_library.Hosts.Where(h => h.GroupId == parent))) _rows.Children.Add(Row(host, depth));
    }

    private static TextBlock Hint(string text) => new() { Text = text, Foreground = ElioColors.TextMuted, FontSize = 12, LineHeight = 17, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 4, 6, 6) };

    private void Highlight(Border row) => ElioStyle.HoverFill(row, () => RestingBrush(row));

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private Control GroupRow(HostGroup group, int depth)
    {
        var row = new Border
        {
            Background = ElioColors.Transparent,
            CornerRadius = new CornerRadius(ElioStyle.RowRadius),
            Padding = new Thickness(6 + (depth * IndentPerLevel), 0, 8, 0),
            Height = GroupRowHeight,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = group.Id,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    ElioStyle.Icon(group.Collapsed ? ElioStyle.IconChevronRight : ElioStyle.IconChevronDown, ElioColors.TextFaint, 11, 2),
                    new TextBlock { Text = group.Name, Foreground = ElioColors.TextMuted, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };

        Highlight(row);
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) Toggle(group);
        };
        row.ContextMenu = new ContextMenu
        {
            ItemsSource = new[]
            {
                Item(L.T("Open all side by side"), () => OpenManyRequested?.Invoke(HostsUnder(group).ToList())),
                Item(L.T("Run a command on all"), () => RunManyRequested?.Invoke(HostsUnder(group).ToList(), null)),
                Item(L.T("Add group inside"), () => _ = AddGroupAsync(group)),
                Item(L.T("Edit"), () => _ = EditGroupAsync(group)),
                Item(L.T("Delete"), () => _ = RemoveGroupAsync(group)),
            },
        };
        return row;
    }

    /// <summary>One line saying where an entry leads.</summary>
    public static string Describe(HostEntry host) => host.Kind switch
    {
        HostKind.Telnet => $"Telnet · {host.Address}" + (host.Port == 23 ? string.Empty : $":{host.Port}"),
        HostKind.Serial => $"{L.T("Serial")} · {host.Address} · {host.Serial?.BaudRate ?? new SerialSettings().BaudRate}",
        HostKind.Local => $"{L.T("Local")} · {host.Address}",
        _ => DescribeSsh(host),
    };

    // The user and port shown are the ones the server really connects with, which its group or its identity may supply.
    private static string DescribeSsh(HostEntry host)
    {
        ResolvedHost resolved = ConnectionResolver.Resolve(host, AppServices.Library);
        return $"{resolved.User}@{host.Address}" + (resolved.Port == ConnectionResolver.DefaultSshPort ? string.Empty : $":{resolved.Port}");
    }

    private Control Row(HostEntry host, int depth)
    {
        string details = Describe(host);
        bool ssh = host.Kind == HostKind.Ssh;
        var menu = new List<MenuItem> { Item(L.T("Connect"), () => ConnectRequested?.Invoke(host)) };
        if (ssh) menu.Add(Item(L.T("Open files"), () => FilesRequested?.Invoke(host)));
        if (ssh) menu.Add(Item(L.T("Open files with another server…"), () => FilesWithAnotherRequested?.Invoke(host)));
        menu.Add(Item(L.T("Open selected side by side"), () => OpenManyRequested?.Invoke(ActedOn(host))));
        if (ssh) menu.Add(Item(L.T("Run a command on selected"), () => RunManyRequested?.Invoke(ActedOn(host), null)));
        menu.Add(Item(L.T("Edit"), () => _ = EditAsync(host)));
        menu.Add(Item(L.T("Delete"), () => _ = RemoveAsync(host)));
        var row = new Border
        {
            Background = ElioColors.Transparent,
            CornerRadius = new CornerRadius(ElioStyle.RowRadius),
            Padding = new Thickness(8 + (depth * IndentPerLevel), 5, 8, 5),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = host.Id,
            Child = new DockPanel
            {
                Children =
                {
                    Docked(ElioStyle.Avatar(host.Name), Dock.Left),
                    new StackPanel
                    {
                        Margin = new Thickness(9, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = host.Name, Foreground = ElioColors.Text, FontSize = ElioStyle.BaseFontSize, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock { Text = host.Tags.Count == 0 ? details : $"{details}  ·  {string.Join(", ", host.Tags)}", Foreground = ElioColors.TextMuted, FontSize = ElioStyle.SmallFontSize, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis },
                        },
                    },
                },
            },
        };

        Highlight(row);
        row.Background = RestingBrush(row);
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) ToggleSelected(host);
            else if (_selected.Count > 0) ClearSelection();
        };
        ConnectOnActivate(row, () => ConnectRequested?.Invoke(host));
        EnableDrag(row, host);
        row.ContextMenu = new ContextMenu { ItemsSource = menu };
        return row;
    }
}

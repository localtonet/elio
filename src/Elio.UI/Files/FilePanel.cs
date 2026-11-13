using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Elio.Core;

namespace Elio.UI.Files;

/// <summary>One side of the file screen: a folder's contents, with the usual things you can do to them.</summary>
public sealed class FilePanel : UserControl
{
    public static readonly DataFormat<FileRow[]> RowsFormat = DataFormat.CreateInProcessFormat<FileRow[]>("elio-file-rows");

    private const double DragStartDistance = 6;

    // Typing makes a burst of changes, and the list is only made again when it has paused.
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(120);
    private const string FullColumns = "20,*,86,130,84";
    private const string MediumColumns = "20,*,76,0,0";
    private const string NarrowColumns = "20,*,0,0,0";
    private const double FullWidth = 560;
    private const double MediumWidth = 300;

    private static readonly StreamGeometry FolderIcon = StreamGeometry.Parse("M1 3h5l1.5 2H15v9H1z");
    private static readonly StreamGeometry FileIcon = StreamGeometry.Parse("M3 1h7l3 3v11H3z");

    private readonly DialogHost _dialogs;
    private readonly ListBox _list;
    private readonly TextBox _path;
    private readonly TextBlock _status = new() { FontSize = 11, Foreground = ElioColors.TextMuted, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "panel-status" };
    private readonly Grid _header;
    private readonly Dictionary<string, (TextBlock Label, string Text)> _headerLabels = [];
    private readonly FileFindBar _find;
    private readonly SubfolderSearch _deep;
    private readonly Button _hiddenButton;
    private readonly TypeAhead _typeAhead = new();
    private string _columns = FullColumns;

    // Everything in the folder, and the part of it the list shows: what is left once hidden files and the search are through.
    private List<FileRow> _rows = [];
    private List<FileRow> _shown = [];
    private List<string> _shownNames = [];
    private FileFilter _applied = FileFilter.None;
    private int _candidates;
    private bool _hideHidden;
    private bool _quiet;
    private HashSet<string>? _restore;
    private CancellationTokenSource? _viewing;
    private CancellationTokenSource? _listing;
    private Comparison<FileRow> _sort = (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
    private string _sortKey = "name";
    private bool _sortDescending;
    private PointerPressedEventArgs? _pressed;
    private Point _pressedAt;
    private List<FileRow> _selectionAtPress = [];

    // The panel a drag of rows started in, so that the one it ends on can tell its neighbour from itself.
    private static FilePanel? _dragOrigin;

    private readonly string? _transferLabel;

    /// <param name="title">The server's name, shown above the list when both sides are servers and need telling apart.</param>
    /// <param name="transferLabel">What copying to the other side is called here, when it is not a plain upload or download.</param>
    public FilePanel(IFileSource source, DialogHost dialogs, string? title = null, string? transferLabel = null)
    {
        Source = source;
        _dialogs = dialogs;
        _transferLabel = transferLabel;

        _hideHidden = AppServices.Settings.HideHiddenFiles;
        _deep = new SubfolderSearch(source, OnSubfolderResults);

        // So that the keys of the panel have somewhere to stay when there is no row to hold the keyboard.
        Focusable = true;

        _path = new TextBox { FontSize = 12, MinHeight = 28, Padding = new Thickness(8, 4), Tag = "panel-path" };
        _path.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                // Back to where the list is, giving up what was typed.
                e.Handled = true;
                _path.Text = CurrentPath;
                FocusList();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                _ = NavigateAsync(_path.Text?.Trim() ?? string.Empty);
            }
        };

        Button up = ToolButton(ElioStyle.IconUp, L.T("Parent folder"), "panel-up");
        up.Click += (_, _) => GoUp();
        Button refresh = ToolButton(ElioStyle.IconRefresh, L.T("Refresh"), "panel-refresh");
        refresh.Click += (_, _) => _ = RefreshAsync();
        Button newFolder = ToolButton(ElioStyle.IconFolderPlus, L.T("New folder"), "panel-new-folder");
        newFolder.Click += (_, _) => _ = NewFolderAsync();
        Button find = ToolButton(ElioStyle.IconSearch, L.T("Search this folder ({0})", PrimaryKey + "+F"), "panel-find");
        find.Click += (_, _) => ShowFind();
        _hiddenButton = ToolButton(ElioStyle.IconEye, L.T("Hide hidden files: on / off ({0})", HiddenKeys), "panel-hidden");
        _hiddenButton.Click += (_, _) => ToggleHidden();
        MarkHiddenButton();

        _find = new FileFindBar(AppServices.Settings.FileFilterWithRegex);
        _find.Changed += OnFindChanged;
        _find.Accepted += () => _ = AcceptFindAsync();
        _find.Closed += () => _ = CloseFindAsync();
        _find.Moved += MoveSelection;

        var tools = new DockPanel { Margin = new Thickness(8, 8, 8, 4) };
        tools.Children.Add(Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 6, 0), Children = { up, refresh } }, Dock.Left));
        tools.Children.Add(Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 0, 0, 0), Children = { _hiddenButton, find, newFolder } }, Dock.Right));
        tools.Children.Add(_path);

        _list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            Background = ElioColors.Transparent,
            ItemTemplate = MakeTemplate(),
            Tag = "panel-list",
        };
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 4)),
                new Setter(ListBoxItem.CornerRadiusProperty, new CornerRadius(6)),
                new Setter(ListBoxItem.MarginProperty, new Thickness(4, 0)),
                new Setter(MinHeightProperty, 0d),
            },
        });
        _list.SelectionChanged += (_, _) => ShowSummary();
        _list.DoubleTapped += OnDoubleTapped;

        // On the way down, because the list keeps Enter for itself: it would pick the focused row and stop there.
        _list.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        // Typing comes up from the row that holds the keyboard, and is only for the list when nothing else took it.
        _list.AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Bubble, handledEventsToo: true);
        _list.AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerMovedEvent, OnPointerMovedTunnel, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerReleasedEvent, (_, _) => _pressed = null, RoutingStrategies.Tunnel);

        DragDrop.SetAllowDrop(_list, true);
        DragDrop.AddDragOverHandler(_list, OnDragOver);
        DragDrop.AddDropHandler(_list, OnDrop);
        DragDrop.AddDragLeaveHandler(_list, (_, _) => _dropHint.IsVisible = false);
        _list.ContextMenu = BuildMenu();

        _header = new Grid { ColumnDefinitions = new ColumnDefinitions(_columns), Margin = new Thickness(12, 0), Height = 22 };
        AddHeader(_header, 1, L.T("Name"), "name");
        AddHeader(_header, 2, L.T("Size"), "size");
        AddHeader(_header, 3, L.T("Modified"), "date");
        AddHeader(_header, 4, L.T("Permissions"), "permissions");
        ShowSortOrder();
        _list.SizeChanged += (_, e) => FitColumns(e.NewSize.Width);

        // On the way down, so that the keys work wherever in the panel the cursor is: in the list, or in the box for the path.
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);

        var footer = new Border { Padding = new Thickness(12, 4), BorderBrush = ElioColors.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = _status };

        var layout = new DockPanel();
        if (title is not null) layout.Children.Add(Docked(TitleRow(title), Dock.Top));
        layout.Children.Add(Docked(tools, Dock.Top));
        layout.Children.Add(Docked(_find, Dock.Top));
        layout.Children.Add(Docked(_header, Dock.Top));
        layout.Children.Add(Docked(footer, Dock.Bottom));
        layout.Children.Add(new Panel { Children = { _list, _dropHint } });
        Content = layout;
    }

    private static Control TitleRow(string title) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 7,
        Margin = new Thickness(12, 9, 12, 0),
        Tag = "panel-title",
        Children =
        {
            ElioStyle.Avatar(title, 18),
            new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = ElioColors.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
        },
    };

    // The same mark a terminal shows where a dragged server would open.
    private readonly Border _dropHint = new()
    {
        Background = ElioColors.Selection,
        BorderBrush = ElioColors.Accent,
        BorderThickness = new Thickness(1.5),
        CornerRadius = new CornerRadius(8),
        Margin = new Thickness(4),
        IsHitTestVisible = false,
        IsVisible = false,
        Tag = "panel-drop-hint",
    };

    public IFileSource Source { get; }

    /// <summary>The panel on the other side, when both show servers. Rows dragged from it are copied here.</summary>
    public FilePanel? Counterpart { get; set; }

    /// <summary>Whether a server dragged from the list may be dropped here, to show its files on this side.</summary>
    public bool AcceptsServers { get; set; }

    /// <summary>A server from the list was dropped here.</summary>
    public event Action<HostEntry>? ServerDropped;

    public string CurrentPath { get; private set; } = string.Empty;

    /// <summary>The rows the list shows now: what the search and the hidden-files switch let through.</summary>
    public IReadOnlyList<FileRow> Rows => _shown;

    public IReadOnlyList<FileRow> SelectedRows => _list.SelectedItems?.OfType<FileRow>().ToList() ?? [];

    /// <summary>Whether the list leaves hidden files out. Setting it does not tell <see cref="HideHiddenChanged"/>.</summary>
    public bool HideHidden
    {
        get => _hideHidden;
        set
        {
            if (_hideHidden == value) return;
            _hideHidden = value;
            MarkHiddenButton();
            _ = RebuildAsync();
        }
    }

    /// <summary>The user turned hidden files on or off, which the other side is to follow.</summary>
    public event Action<bool>? HideHiddenChanged;

    private static string PrimaryKey => OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";

    /// <summary>What the status line says.</summary>
    public string StatusText => _status.Text ?? string.Empty;

    /// <summary>The user wants these copied to the other side.</summary>
    public event Action<IReadOnlyList<FileRow>>? TransferRequested;

    public event Action<FileRow>? EditRequested;

    /// <summary>Rows from the other panel were dropped here.</summary>
    public event Action<IReadOnlyList<FileRow>>? RowsDropped;

    /// <summary>Files from outside the app were dropped here.</summary>
    public event Action<IReadOnlyList<string>>? PathsDropped;

    private static T Docked<T>(T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static Button ToolButton(string icon, string tip, string tag)
    {
        Button button = ElioStyle.IconButton(ElioStyle.Icon(icon), tip);
        button.Tag = tag;
        return button;
    }

    private void AddHeader(Grid header, int column, string text, string key)
    {
        var label = new TextBlock { Text = text, FontSize = ElioStyle.SmallFontSize, FontWeight = FontWeight.Medium, Foreground = ElioColors.TextFaint, VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand), Background = ElioColors.Transparent, Tag = "sort-" + key };
        label.PointerPressed += (_, _) => SortBy(key);
        Grid.SetColumn(label, column);
        header.Children.Add(label);
        _headerLabels[key] = (label, text);
    }

    // An arrow after the name of the column the list is in the order of, pointing the way it runs.
    private void ShowSortOrder()
    {
        foreach ((string key, (TextBlock label, string text)) in _headerLabels)
            label.Text = key == _sortKey ? text + (_sortDescending ? " ↓" : " ↑") : text;
    }

    private FuncDataTemplate<FileRow> MakeTemplate() => new((row, _) => BuildRow(row), supportsRecycling: false);

    // The name always stays. The details give way when the panel is narrow, such as beside a terminal.
    private void FitColumns(double width)
    {
        string columns = width >= FullWidth ? FullColumns : width >= MediumWidth ? MediumColumns : NarrowColumns;
        if (columns == _columns) return;

        _columns = columns;
        _header.ColumnDefinitions = new ColumnDefinitions(columns);
        _list.ItemTemplate = MakeTemplate();
    }

    private Control BuildRow(FileRow? row)
    {
        // A recycled list item passes through having no row at all.
        if (row is null) return new Panel();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(_columns), Background = ElioColors.Transparent };

        var icon = new Avalonia.Controls.Shapes.Path
        {
            Data = row.IsDirectory ? FolderIcon : FileIcon,
            Fill = row.IsDirectory ? ElioColors.Accent : ElioColors.TextMuted,
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(icon);

        void Add(int column, string text, IBrush brush)
        {
            var block = new TextBlock { Text = text, FontSize = 12, Foreground = brush, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }

        grid.Children.Add(NameCell(row));
        Add(2, row.IsDirectory ? string.Empty : FileText.Size(row.Size), ElioColors.TextMuted);
        Add(3, row.ModifiedUtc == DateTime.MinValue ? string.Empty : row.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), ElioColors.TextMuted);
        Add(4, row.Permissions, ElioColors.TextMuted);
        return grid;
    }

    // The name, with what the search found in it in bold, and for a result from a folder below the one shown, that folder after it.
    private TextBlock NameCell(FileRow row)
    {
        var cell = new TextBlock { FontSize = 12, Foreground = ElioColors.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(cell, 1);

        string suffix = row.IsLink ? " ↗" : string.Empty;
        bool found = _applied.TryFind(row.Name, out int start, out int length);
        if (!found && row.Where is not { Length: > 0 })
        {
            cell.Text = row.Name + suffix;
            return cell;
        }

        var runs = new InlineCollection();
        if (found)
        {
            runs.Add(new Run(row.Name[..start]));
            runs.Add(new Run(row.Name.Substring(start, length)) { FontWeight = FontWeight.Bold, Background = ElioColors.Selection });
            runs.Add(new Run(row.Name[(start + length)..] + suffix));
        }
        else
        {
            runs.Add(new Run(row.Name + suffix));
        }

        if (row.Where is { Length: > 0 } where)
            runs.Add(new Run("  " + where) { Foreground = ElioColors.TextMuted, FontSize = ElioStyle.SmallFontSize });

        cell.Inlines = runs;
        return cell;
    }

    private ContextMenu BuildMenu()
    {
        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        var items = new List<MenuItem>
        {
            Item(_transferLabel ?? (Source.IsRemote ? L.T("Download") : L.T("Upload")), () => RequestTransfer(SelectedRows)),
        };
        if (Source.IsRemote) items.Add(Item(L.T("Edit"), () => { if (SelectedRows.FirstOrDefault(r => !r.IsDirectory) is FileRow row) EditRequested?.Invoke(row); }));
        items.Add(Item(L.T("New folder"), () => _ = NewFolderAsync()));
        items.Add(Item(L.T("Rename"), () => _ = RenameSelectedAsync()));
        if (Source is RemoteFileSource) items.Add(Item(L.T("Permissions"), () => _ = ChangePermissionsAsync()));
        items.Add(Item(L.T("Delete"), () => _ = DeleteSelectedAsync()));
        items.Add(Item(L.T("Copy path"), () => _ = CopyPathAsync()));

        // Only for a result found in a folder below, where it is a way to get to the file.
        MenuItem inFolder = Item(L.T("Show in its folder"), () => { if (SelectedRows.FirstOrDefault() is FileRow row) _ = GoToAsync(row); });
        items.Add(inFolder);
        items.Add(Item(L.T("Refresh"), () => _ = RefreshAsync()));

        var menu = new ContextMenu { ItemsSource = items };
        menu.Opening += (_, _) => inFolder.IsVisible = SelectedRows is [{ Where: not null }];
        return menu;
    }

    private async Task CopyPathAsync()
    {
        string text = string.Join('\n', SelectedRows.Select(r => r.Path));
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is IClipboard clipboard) await clipboard.SetTextAsync(text);
    }

    private void RequestTransfer(IReadOnlyList<FileRow> rows)
    {
        if (rows.Count > 0) TransferRequested?.Invoke(rows);
    }

    private void ShowError(string message)
    {
        _status.Foreground = ElioColors.Danger;
        _status.Text = message;
    }

    private void ShowSummary()
    {
        _status.Foreground = ElioColors.TextMuted;
        IReadOnlyList<FileRow> selected = SelectedRows;
        if (selected.Count > 0)
        {
            _status.Text = L.T("{0} selected, {1}", selected.Count, FileText.Size(selected.Where(r => !r.IsDirectory).Sum(r => r.Size)));
            return;
        }

        // While a search narrows the list, what is left is told against what it was picked from.
        string text = _applied.IsEmpty ? L.T("{0} items", _shown.Count) : L.T("{0} of {1} items", _shown.Count, _candidates);
        if (_deep.Summary is { } summary && !_applied.IsEmpty)
        {
            if (summary.Truncated) text += " " + L.T("The search stopped at {0:N0} items, so there may be more.", FileTreeScanner.DefaultLimit);
            if (summary.Unreadable > 0) text += " " + L.T("{0} folders could not be read.", summary.Unreadable);
        }

        _status.Text = text;
    }

    /// <summary>Starts in the folder the source suggests.</summary>
    public async Task StartAsync()
    {
        _status.Text = L.T("Connecting...");
        try
        {
            await NavigateAsync(await Source.GetStartPathAsync(CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(ex.Message);
        }
    }

    /// <summary>Puts a message on the status line.</summary>
    public void ShowNotice(string text)
    {
        _status.Foreground = ElioColors.TextMuted;
        _status.Text = text;
    }

    /// <param name="searchSubfolders">Look through the folders below again when a search of them is open. Left off for a refresh nobody asked for, such as one after a transfer, which would cost a server a round trip for every folder.</param>
    public Task RefreshAsync(bool searchSubfolders = true) =>
        Source.IsRemote && CurrentPath.Length == 0 ? StartAsync() : NavigateAsync(CurrentPath, keepSelection: true, searchSubfolders);

    private void GoUp()
    {
        if (Source.Parent(CurrentPath) is string parent) _ = NavigateAsync(parent);
    }

    /// <summary>Shows a folder. A newer request replaces one still loading.</summary>
    public async Task NavigateAsync(string path, bool keepSelection = false, bool searchSubfolders = true)
    {
        _listing?.Cancel();
        var listing = new CancellationTokenSource();
        _listing = listing;
        HashSet<string> selected = keepSelection ? SelectedRows.Select(r => r.Path).ToHashSet() : [];

        try
        {
            List<FileRow> rows = await Source.ListAsync(path, listing.Token);
            if (listing.IsCancellationRequested) return;

            // A search belongs to the folder it was begun in. The same folder listed again keeps it.
            bool sameFolder = path == CurrentPath;
            CurrentPath = path;
            _path.Text = path;
            _rows = rows;
            _restore = selected;

            if (!sameFolder) EndFind();
            else if (searchSubfolders) RestartSubfolderSearch();

            await RebuildAsync();
        }
        catch (OperationCanceledException) when (listing.IsCancellationRequested)
        {
            // Replaced by a newer request.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _path.Text = CurrentPath;
            ShowError($"{path}: {ex.Message}");
        }
    }

    private void SortBy(string key)
    {
        // The biggest and the newest are what one looks for, so those columns begin with them. A second press turns it round.
        _sortDescending = _sortKey == key ? !_sortDescending : key is "size" or "date";
        _sortKey = key;
        _sort = key switch
        {
            "size" => (a, b) => a.Size.CompareTo(b.Size),
            "date" => (a, b) => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
            "permissions" => (a, b) => string.CompareOrdinal(a.Permissions, b.Permissions),
            _ => (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name),
        };
        ShowSortOrder();
        _ = RebuildAsync();
    }

    /// <summary>
    /// Makes the list from the folder: out go the hidden files and what the search does not want, and the rest is put in
    /// order. A big folder takes a while to go through, so that is done away from the window, and a newer call replaces one
    /// that is still going.
    /// </summary>
    /// <param name="delay">How long to wait for more typing first.</param>
    /// <param name="keepScroll">Stay where the list is scrolled to, for when more results arrive while it is being read.</param>
    private async Task RebuildAsync(TimeSpan delay = default, bool keepScroll = false)
    {
        _viewing?.Cancel();
        var viewing = new CancellationTokenSource();
        _viewing = viewing;

        FileFilter filter = FilterNow();
        bool inSubfolders = _find.IncludeSubfolders && !filter.IsEmpty;

        // What the search found below is added to on the window's thread, so what is read away from it is a copy.
        List<FileRow> pool = _rows;
        if (inSubfolders) pool = [.. _rows, .. _deep.Rows];
        bool showHidden = !_hideHidden;
        Comparison<FileRow> sort = _sort;
        int direction = _sortDescending ? -1 : 1;
        var order = Comparer<FileRow>.Create((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : direction * sort(a, b));

        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, viewing.Token);

            FileFilter.Result result = await Task.Run(() =>
            {
                FileFilter.Result picked = FileFilter.Apply(pool, filter, showHidden, viewing.Token);
                return picked.TimedOut ? picked : picked with { Rows = picked.Rows.Order(order).ToList() };
            }, viewing.Token);
            if (viewing.IsCancellationRequested) return;

            // What was chosen stays chosen, unless a refresh has said what is to be chosen again. Looked at only now, since
            // the user may have chosen something else while the list was being made.
            HashSet<string> keep = _restore ?? SelectedRows.Select(r => r.Path).ToHashSet();

            // The row that holds the keyboard goes with the old list, and the keyboard has to be given back.
            bool hadKeyboard = _list.IsKeyboardFocusWithin;

            Vector offset = keepScroll ? ListScroll()?.Offset ?? default : default;
            _applied = filter;
            _candidates = result.Candidates;
            _shown = result.Rows;
            _shownNames = _shown.ConvertAll(r => r.Name);
            _list.ItemsSource = _shown;
            if (keepScroll && ListScroll() is ScrollViewer scroll) scroll.Offset = offset;

            _restore = null;
            Reselect(keep, filter, result.TimedOut);
            if (hadKeyboard) FocusContents();
            ShowSummary();
            ShowMatchCount(result.TimedOut);
        }
        catch (OperationCanceledException) when (viewing.IsCancellationRequested)
        {
            // Replaced by a newer one.
        }
    }

    private ScrollViewer? ListScroll() => _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    // Chooses again what was chosen. With nothing chosen and a search being typed, the first name is the one Enter will go to.
    private void Reselect(HashSet<string> keep, FileFilter filter, bool timedOut)
    {
        _list.SelectedItems?.Clear();

        if (keep.Count > 0)
        {
            // By place and not by row, and a stretch at a time: asking the list for each row takes as long as the list does.
            int from = -1;
            for (int i = 0; i <= _shown.Count; i++)
            {
                bool kept = i < _shown.Count && keep.Contains(_shown[i].Path);
                if (kept && from < 0) from = i;
                if (kept || from < 0) continue;

                _list.Selection.SelectRange(from, i - 1);
                from = -1;
            }
        }

        if (_list.SelectedItems?.Count == 0 && _find.IsVisible && _find.IsTyping && !filter.IsEmpty && !timedOut && _shown.Count > 0)
            _list.SelectedIndex = 0;
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (RowUnder(e.Source) is FileRow row) Activate(row);
    }

    private static FileRow? RowUnder(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as FileRow;

    private void Activate(FileRow row)
    {
        if (row.IsDirectory) _ = NavigateAsync(row.Path);
        else RequestTransfer([row]);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when _find.IsVisible:
                _ = CloseFindAsync();
                break;
            case Key.A when e.KeyModifiers == PrimaryModifier:
                _list.SelectAll();
                break;
            case Key.Enter when SelectedRows.Count == 1 && SelectedRows[0].IsDirectory:
                _ = NavigateAsync(SelectedRows[0].Path);
                break;
            case Key.Enter:
                RequestTransfer(SelectedRows);
                break;
            case Key.Back:
                GoUp();
                break;
            case Key.F5:
                _ = RefreshAsync();
                break;
            case Key.F2:
                _ = RenameSelectedAsync();
                break;
            case Key.F4 when Source.IsRemote && SelectedRows.FirstOrDefault(r => !r.IsDirectory) is FileRow row:
                EditRequested?.Invoke(row);
                break;
            case Key.F7:
                _ = NewFolderAsync();
                break;
            case Key.Delete:
                _ = DeleteSelectedAsync();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static KeyModifiers PrimaryModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    // Where macOS has Cmd+H for hiding the program itself, Finder's own key for hidden files is the one to use.
    private static bool IsHiddenKey(KeyEventArgs e) => OperatingSystem.IsMacOS()
        ? e.Key == Key.OemPeriod && e.KeyModifiers == (KeyModifiers.Meta | KeyModifiers.Shift)
        : e.Key == Key.H && e.KeyModifiers == KeyModifiers.Control;

    private static string HiddenKeys => OperatingSystem.IsMacOS() ? "Cmd+Shift+." : "Ctrl+H";

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == PrimaryModifier && e.Key == Key.F) ShowFind();
        else if (e.KeyModifiers == PrimaryModifier && e.Key == Key.L) FocusPath();
        else if (IsHiddenKey(e)) ToggleHidden();
        else return;

        e.Handled = true;
    }

    private void FocusList() => FocusContents();

    /// <summary>
    /// Gives the keyboard to the list, on the row that is chosen, so that the keys of this panel work. A list cannot take
    /// the keyboard itself, only its rows can, and they exist once the list has been laid out. With no row to take it,
    /// the panel does.
    /// </summary>
    public void FocusContents() => Dispatcher.UIThread.Post(() =>
    {
        Control? row = _shown.Count > 0 ? _list.ContainerFromIndex(Math.Max(_list.SelectedIndex, 0)) : null;
        (row ?? this).Focus();
    }, DispatcherPriority.Loaded);

    private void FocusPath()
    {
        _path.Focus();
        _path.SelectAll();
    }

    // Typing in the list moves to the name that starts with the letters, as it does in a file manager.
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: > 0 } typed || _shown.Count == 0) return;

        int index = _typeAhead.Type(typed, _shownNames, _list.SelectedIndex, DateTime.UtcNow);
        if (index < 0) return;

        e.Handled = true;
        ChooseRow(index);
    }

    private void ChooseRow(int index)
    {
        _list.SelectedIndex = index;
        _list.ScrollIntoView(index);
    }

    private void MoveSelection(int by)
    {
        if (_shown.Count > 0) ChooseRow(Math.Clamp(_list.SelectedIndex + by, 0, _shown.Count - 1));
    }

    /// <summary>Opens the search bar over the list and puts the cursor in it.</summary>
    public void ShowFind() => _find.Open();

    private FileFilter FilterNow() => _find.IsVisible ? FileFilter.Parse(_find.Text, _find.UseRegex) : FileFilter.None;

    private void OnFindChanged(bool typing)
    {
        if (_quiet) return;

        if (AppServices.Settings.FileFilterWithRegex != _find.UseRegex)
        {
            AppServices.Settings.FileFilterWithRegex = _find.UseRegex;
            AppServices.SaveSettings();
        }

        // Turning the folders below off ends a walk that is still going. What it found is thrown away with it.
        if (!_find.IncludeSubfolders) _deep.Stop();
        else if (!_deep.HasStarted) RestartSubfolderSearch();

        _ = RebuildAsync(typing ? TypingDelay : TimeSpan.Zero);
    }

    // The folders below are only looked through for something to look for, and once, however much is typed.
    private void RestartSubfolderSearch()
    {
        if (_find.IsVisible && _find.IncludeSubfolders && !FilterNow().IsEmpty) _deep.Start(_rows);
        else _deep.Stop();
    }

    // More was found below, or the walk is over.
    private void OnSubfolderResults()
    {
        if (!_find.IsVisible) return;

        if (FilterNow().IsEmpty) ShowMatchCount(timedOut: false);
        else _ = RebuildAsync(keepScroll: true);
    }

    private void ShowMatchCount(bool timedOut)
    {
        if (!_find.IsVisible) return;

        if (_applied.IsInvalid) _find.ShowCount(L.T("Not a valid pattern"), problem: true);
        else if (timedOut) _find.ShowCount(L.T("Took too long"), problem: true);
        else if (_applied.IsEmpty) _find.ShowCount(string.Empty, problem: false);
        else if (_deep.IsRunning) _find.ShowCount(L.T("{0} found, searching...", _shown.Count), problem: false);
        else if (_shown.Count == 0) _find.ShowCount(L.T("No matches"), problem: false);
        else _find.ShowCount(L.T("{0} of {1}", _shown.Count, _candidates), problem: false);
    }

    // Enter: go where the chosen name is. A folder is opened, a file is chosen in its own folder, and nothing is copied
    // from here, since pressing Enter after typing is not asking for that.
    private async Task AcceptFindAsync()
    {
        await RebuildAsync();

        FileRow? target = SelectedRows.FirstOrDefault() ?? _shown.FirstOrDefault();
        if (target is null) return;

        if (target.IsDirectory)
        {
            await NavigateAsync(target.Path);
            _list.Focus();
        }
        else if (target.Where is not null)
        {
            await GoToAsync(target);
        }
        else
        {
            ChooseRow(_shown.IndexOf(target));
            _list.Focus();
        }
    }

    /// <summary>Shows the folder a file is in, with the file chosen.</summary>
    private async Task GoToAsync(FileRow row)
    {
        if (Source.Parent(row.Path) is not string folder) return;

        await NavigateAsync(folder);
        int index = _shown.FindIndex(r => r.Path == row.Path);
        if (index >= 0) ChooseRow(index);
        _list.Focus();
    }

    // Ends the search without touching the list: for when a new folder is about to replace it.
    private void EndFind()
    {
        _deep.Stop();
        _quiet = true;
        try
        {
            _find.IncludeSubfolders = false;
            _find.Text = string.Empty;
            _find.ShowCount(string.Empty, problem: false);
            _find.IsVisible = false;
        }
        finally
        {
            _quiet = false;
        }

        _applied = FileFilter.None;
    }

    private async Task CloseFindAsync()
    {
        EndFind();
        _list.Focus();
        await RebuildAsync();
    }

    private void ToggleHidden()
    {
        HideHidden = !_hideHidden;
        AppServices.Settings.HideHiddenFiles = _hideHidden;
        AppServices.SaveSettings();
        HideHiddenChanged?.Invoke(_hideHidden);
    }

    private void MarkHiddenButton() => _hiddenButton.Background = _hideHidden ? ElioColors.Selection : ElioColors.Transparent;

    private async Task NewFolderAsync()
    {
        if (await _dialogs.AskTextAsync(L.T("New folder"), L.T("Folder name"), null, L.T("Create")) is not string name) return;
        await RunAsync(() => Source.CreateFolderAsync(Source.Combine(CurrentPath, name), CancellationToken.None));
    }

    private async Task RenameSelectedAsync()
    {
        if (SelectedRows.Count != 1) return;
        FileRow row = SelectedRows[0];
        if (await _dialogs.AskTextAsync(L.T("Rename"), L.T("New name"), row.Name, L.T("Change")) is not string name || name == row.Name) return;
        await RunAsync(() => Source.RenameAsync(row, name, CancellationToken.None));
    }

    private async Task ChangePermissionsAsync()
    {
        if (Source is not RemoteFileSource remote || SelectedRows.Count != 1) return;
        FileRow row = SelectedRows[0];
        if (await _dialogs.AskTextAsync(L.T("Permissions"), L.T("Numeric permission (example: 644)"), OctalOf(row.Permissions), L.T("Apply")) is not string text) return;

        if (text.Length != 3 || text.Any(c => c is < '0' or > '7'))
        {
            ShowError(L.T("Permission must be three digits, for example 644."));
            return;
        }

        await RunAsync(() => remote.ChangeModeAsync(row, short.Parse(text), CancellationToken.None));
    }

    private static string OctalOf(string permissions)
    {
        if (permissions.Length != 9) return string.Empty;
        int Digit(int start) => (permissions[start] == 'r' ? 4 : 0) + (permissions[start + 1] == 'w' ? 2 : 0) + (permissions[start + 2] == 'x' ? 1 : 0);
        return $"{Digit(0)}{Digit(3)}{Digit(6)}";
    }

    private async Task DeleteSelectedAsync()
    {
        IReadOnlyList<FileRow> rows = SelectedRows;
        if (rows.Count == 0) return;

        string what = rows.Count == 1 ? $"\"{rows[0].Name}\"" : L.T("{0} items", rows.Count);
        string extra = rows.Any(r => r.IsDirectory && !r.IsLink) ? " " + L.T("Folders are deleted with their contents.") : string.Empty;
        if (!await _dialogs.ConfirmAsync(L.T("Delete"), L.T("{0} will be permanently deleted.{1} This cannot be undone.", what, extra), L.T("Delete"), danger: true)) return;

        await RunAsync(async () =>
        {
            foreach (FileRow row in rows) await Source.DeleteAsync(row, CancellationToken.None);
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(ex.Message);
        }
    }

    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null;
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed || RowUnder(e.Source) is not FileRow row) return;

        _pressed = e;
        _pressedAt = e.GetPosition(_list);

        // Pressing inside a selection collapses it before a drag can start, so what to drag is decided now.
        IReadOnlyList<FileRow> selected = SelectedRows;
        _selectionAtPress = selected.Contains(row) ? selected.ToList() : [row];
    }

    private void OnPointerMovedTunnel(object? sender, PointerEventArgs e)
    {
        if (_pressed is not PointerPressedEventArgs pressed) return;

        Point now = e.GetPosition(_list);
        if (Math.Abs(now.X - _pressedAt.X) + Math.Abs(now.Y - _pressedAt.Y) <= DragStartDistance) return;

        _pressed = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(RowsFormat, _selectionAtPress.ToArray()));
        _ = DragRowsAsync(pressed, data);
    }

    private async Task DragRowsAsync(PointerPressedEventArgs pressed, DataTransfer data)
    {
        _dragOrigin = this;
        try
        {
            await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Copy);
        }
        finally
        {
            _dragOrigin = null;
        }
    }

    // Rows go from this computer to a server or back. Between two servers both sides hold server rows, and what
    // counts is that they come from the panel opposite.
    private bool ComesFromOtherSide(FileRow[] rows) =>
        rows[0].Remote is null == Source.IsRemote || (Counterpart is not null && ReferenceEquals(_dragOrigin, Counterpart));

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool fromOtherPanel = e.DataTransfer.TryGetValue(RowsFormat) is { Length: > 0 } rows && ComesFromOtherSide(rows);
        bool fromOutside = Source.IsRemote && e.DataTransfer.Contains(DataFormat.File);
        bool server = AcceptsServers && e.DataTransfer.Contains(HostListView.HostsFormat);
        e.DragEffects = fromOtherPanel || fromOutside || server ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        _dropHint.IsVisible = server;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        _dropHint.IsVisible = false;

        if (AcceptsServers && e.DataTransfer.TryGetValue(HostListView.HostsFormat) is { Length: > 0 } hosts)
        {
            ServerDropped?.Invoke(hosts[0]);
            return;
        }

        if (e.DataTransfer.TryGetValue(RowsFormat) is { Length: > 0 } rows)
        {
            if (ComesFromOtherSide(rows)) RowsDropped?.Invoke(rows);
            return;
        }

        if (Source.IsRemote && e.DataTransfer.TryGetFiles() is { Length: > 0 } items)
        {
            List<string> paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0) PathsDropped?.Invoke(paths);
        }
    }
}

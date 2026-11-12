using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Elio.Core;

namespace Elio.UI.Files;

/// <summary>One side of the file screen: a folder's contents, with the usual things you can do to them.</summary>
public sealed class FilePanel : UserControl
{
    public static readonly DataFormat<FileRow[]> RowsFormat = DataFormat.CreateInProcessFormat<FileRow[]>("elio-file-rows");

    private const double DragStartDistance = 6;
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
    private string _columns = FullColumns;
    private List<FileRow> _rows = [];
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

        _path = new TextBox { FontSize = 12, MinHeight = 28, Padding = new Thickness(8, 4), Tag = "panel-path" };
        _path.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _ = NavigateAsync(_path.Text?.Trim() ?? string.Empty);
        };

        Button up = ToolButton(ElioStyle.IconUp, L.T("Parent folder"), "panel-up");
        up.Click += (_, _) => GoUp();
        Button refresh = ToolButton(ElioStyle.IconRefresh, L.T("Refresh"), "panel-refresh");
        refresh.Click += (_, _) => _ = RefreshAsync();
        Button newFolder = ToolButton(ElioStyle.IconFolderPlus, L.T("New folder"), "panel-new-folder");
        newFolder.Click += (_, _) => _ = NewFolderAsync();

        var tools = new DockPanel { Margin = new Thickness(8, 8, 8, 4) };
        tools.Children.Add(Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 6, 0), Children = { up, refresh } }, Dock.Left));
        tools.Children.Add(Docked(new Border { Margin = new Thickness(4, 0, 0, 0), Child = newFolder }, Dock.Right));
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
        _list.SizeChanged += (_, e) => FitColumns(e.NewSize.Width);

        var footer = new Border { Padding = new Thickness(12, 4), BorderBrush = ElioColors.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = _status };

        var layout = new DockPanel();
        if (title is not null) layout.Children.Add(Docked(TitleRow(title), Dock.Top));
        layout.Children.Add(Docked(tools, Dock.Top));
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

    public IReadOnlyList<FileRow> Rows => _rows;

    public IReadOnlyList<FileRow> SelectedRows => _list.SelectedItems?.OfType<FileRow>().ToList() ?? [];

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

        Add(1, row.IsLink ? $"{row.Name} ↗" : row.Name, ElioColors.Text);
        Add(2, row.IsDirectory ? string.Empty : FileText.Size(row.Size), ElioColors.TextMuted);
        Add(3, row.ModifiedUtc == DateTime.MinValue ? string.Empty : row.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), ElioColors.TextMuted);
        Add(4, row.Permissions, ElioColors.TextMuted);
        return grid;
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
        items.Add(Item(L.T("Refresh"), () => _ = RefreshAsync()));
        return new ContextMenu { ItemsSource = items };
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
        _status.Text = selected.Count > 0
            ? L.T("{0} selected, {1}", selected.Count, FileText.Size(selected.Where(r => !r.IsDirectory).Sum(r => r.Size)))
            : L.T("{0} items", _rows.Count);
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

    public Task RefreshAsync() => Source.IsRemote && CurrentPath.Length == 0 ? StartAsync() : NavigateAsync(CurrentPath, keepSelection: true);

    private void GoUp()
    {
        if (Source.Parent(CurrentPath) is string parent) _ = NavigateAsync(parent);
    }

    /// <summary>Shows a folder. A newer request replaces one still loading.</summary>
    public async Task NavigateAsync(string path, bool keepSelection = false)
    {
        _listing?.Cancel();
        var listing = new CancellationTokenSource();
        _listing = listing;
        HashSet<string> selected = keepSelection ? SelectedRows.Select(r => r.Name).ToHashSet() : [];

        try
        {
            List<FileRow> rows = await Source.ListAsync(path, listing.Token);
            if (listing.IsCancellationRequested) return;

            CurrentPath = path;
            _path.Text = path;
            _rows = rows;
            ApplySort();

            if (selected.Count > 0)
                foreach (FileRow row in _rows.Where(r => selected.Contains(r.Name))) _list.SelectedItems?.Add(row);

            ShowSummary();
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
        _sortDescending = _sortKey == key && !_sortDescending;
        _sortKey = key;
        _sort = key switch
        {
            "size" => (a, b) => a.Size.CompareTo(b.Size),
            "date" => (a, b) => a.ModifiedUtc.CompareTo(b.ModifiedUtc),
            "permissions" => (a, b) => string.CompareOrdinal(a.Permissions, b.Permissions),
            _ => (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name),
        };
        ApplySort();
    }

    private void ApplySort()
    {
        int direction = _sortDescending ? -1 : 1;
        _rows = _rows
            .Order(Comparer<FileRow>.Create((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : direction * _sort(a, b)))
            .ToList();
        _list.ItemsSource = _rows;
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

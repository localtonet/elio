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
using Elio.Connections;
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

    // Below this the path box has too little room between the buttons, as on a phone held upright, and goes on a line of its own.
    private const double ToolsWidth = 340;

    private static readonly StreamGeometry FolderIcon = StreamGeometry.Parse("M1 3h5l1.5 2H15v9H1z");
    private static readonly StreamGeometry FileIcon = StreamGeometry.Parse("M3 1h7l3 3v11H3z");

    // Two arrows, one each way: the two sides held against each other.
    private const string CompareIcon = "M3,5.5 H13 M10.5,3 L13,5.5 L10.5,8 M13,10.5 H3 M5.5,8 L3,10.5 L5.5,13";

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
    private readonly Button _foldersButton;
    private readonly Button _compareButton;
    private FilePanel? _counterpart;

    // How each name compared with the other side, by name, until something lists this side again.
    private IReadOnlyDictionary<string, FileMark>? _marks;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _contentSearch;
    private List<FileRow>? _contentRows;
    private string _contentQuery = string.Empty;
    private bool _contentMode;
    private string _columns = FullColumns;
    private readonly Control _toolsLeft;
    private readonly Control _toolsRight;
    private bool _toolsNarrow;

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

    // The listing that is still being waited for, if any. A job that ends meanwhile must not start a listing of its own over it.
    private CancellationTokenSource? _listingInFlight;
    private Comparison<FileRow> _sort = (a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
    private string _sortKey = "name";
    private bool _sortDescending;
    private PointerPressedEventArgs? _pressed;
    private Point _pressedAt;
    private List<FileRow> _selectionAtPress = [];

    // The panel a drag of rows started in, so that the one it ends on can tell its neighbour from itself.
    private static FilePanel? _dragOrigin;

    private readonly string? _transferLabel;

    // What Copy and Cut put aside. Both sides share it, so that what was copied on one can be pasted on the other.
    private static (FilePanel Origin, List<FileRow> Rows, bool Cut)? _clipboard;

    // Content searches show at most this many files: more than a list is useful for, and little enough to answer fast.
    private const int ContentLimit = 1000;

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
        _foldersButton = ToolButton(ElioStyle.IconPin, L.T("Folders you go to often"), "panel-folders");
        _foldersButton.Click += (_, _) => ShowFrequentFolders();
        Button newFolder = ToolButton(ElioStyle.IconFolderPlus, L.T("New folder"), "panel-new-folder");
        newFolder.Click += (_, _) => _ = NewFolderAsync();
        Button find = ToolButton(ElioStyle.IconSearch, L.T("Search this folder ({0})", PrimaryKey + "+F"), "panel-find");
        find.Click += (_, _) => ShowFind();
        _hiddenButton = ToolButton(ElioStyle.IconEye, L.T("Hide hidden files: on / off ({0})", HiddenKeys), "panel-hidden");
        _hiddenButton.Click += (_, _) => ToggleHidden();
        MarkHiddenButton();

        // Only where there is another side to compare with.
        _compareButton = ToolButton(CompareIcon, L.T("Compare and synchronise the two sides"), "panel-compare");
        _compareButton.Click += (_, _) => CompareMenuRequested?.Invoke(_compareButton);
        _compareButton.IsVisible = false;

        _find = new FileFindBar(AppServices.Settings.FileFilterWithRegex);
        _find.Changed += OnFindChanged;
        _find.Accepted += () => _ = AcceptFindAsync();
        _find.Closed += () => _ = CloseFindAsync();
        _find.Moved += MoveSelection;

        // The buttons either side of the path, or under it when the panel is too narrow for all three in a row.
        _toolsLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(0, 0, 6, 0), Children = { up, refresh, _foldersButton } };
        _toolsRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 0, 0, 0), Children = { _compareButton, _hiddenButton, find, newFolder } };
        var tools = new Grid { Margin = new Thickness(8, 8, 8, 4), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        tools.Children.Add(_toolsLeft);
        tools.Children.Add(_path);
        tools.Children.Add(_toolsRight);
        ArrangeTools(narrow: false);

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

    /// <summary>The panel on the other side. Rows dragged from it are copied here, and the two can be compared.</summary>
    public FilePanel? Counterpart
    {
        get => _counterpart;
        set
        {
            _counterpart = value;
            _compareButton.IsVisible = value is not null;
        }
    }

    /// <summary>The user wants the menu for comparing and synchronising the two sides, under this button.</summary>
    public event Action<Control>? CompareMenuRequested;

    /// <summary>The folder was listed again, or another one was: what was compared before may no longer be so.</summary>
    public event Action? Listed;

    /// <summary>Marks the names in the list by how they compared with the other side, or takes the marks away.</summary>
    public void ShowMarks(IReadOnlyDictionary<string, FileMark>? marks)
    {
        if (marks is null && _marks is null) return;

        _marks = marks;

        // The rows are made again with the marks in them, the way a change of columns does.
        _list.ItemTemplate = MakeTemplate();
    }

    /// <summary>Puts a problem on the status line.</summary>
    public void ShowProblem(string message) => ShowError(message);

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

    /// <summary>The user wants to edit a file in another program on this computer.</summary>
    public event Action<FileRow>? EditRequested;

    /// <summary>The user wants to look at a file without copying it.</summary>
    public event Action<FileRow>? PreviewRequested;

    /// <summary>The user wants to change a file in the editor of the app.</summary>
    public event Action<FileRow>? EditorRequested;

    /// <summary>What the list of folders gone to often is kept under: one name for this computer, one for each server.</summary>
    public string FolderKey { get; set; } = "local";

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
    private void ArrangeTools(bool narrow)
    {
        _toolsNarrow = narrow;
        Grid.SetRow(_path, 0);
        Grid.SetColumn(_path, narrow ? 0 : 1);
        Grid.SetColumnSpan(_path, narrow ? 3 : 1);
        _path.Margin = narrow ? new Thickness(0, 0, 0, 4) : default;
        Grid.SetRow(_toolsLeft, narrow ? 1 : 0);
        Grid.SetColumn(_toolsLeft, 0);
        Grid.SetRow(_toolsRight, narrow ? 1 : 0);
        Grid.SetColumn(_toolsRight, 2);
    }

    private void FitColumns(double width)
    {
        if ((width < ToolsWidth) != _toolsNarrow) ArrangeTools(width < ToolsWidth);

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

        // A comparison speaks of the names in the folder shown, not of what a search found below it.
        FileMark? mark = row.Where is null && !row.IsContentHit && _marks?.TryGetValue(row.Name, out FileMark m) == true ? m : null;
        if (!found && row.Where is not { Length: > 0 } && !row.IsContentHit && mark is null)
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

        if (row.IsContentHit)
            runs.Add(new Run($"  :{row.Line}  {row.Snippet}") { Foreground = ElioColors.TextMuted, FontSize = ElioStyle.SmallFontSize });

        if (mark is FileMark shown)
            runs.Add(new Run("  " + MarkText(shown)) { Foreground = shown is FileMark.CaseConflict or FileMark.KindDiffers or FileMark.Unreadable ? ElioColors.Danger : ElioColors.Accent, FontSize = ElioStyle.SmallFontSize, FontWeight = FontWeight.Medium });

        cell.Inlines = runs;
        return cell;
    }

    private static string MarkText(FileMark mark) => mark switch
    {
        FileMark.OnlyHere => L.T("only here"),
        FileMark.Newer => L.T("newer here"),
        FileMark.Older => L.T("older here"),
        FileMark.SizeDiffers => L.T("same time, other size"),
        FileMark.CaseConflict => L.T("name differs in case"),
        FileMark.KindDiffers => L.T("a file on one side, a folder on the other"),
        FileMark.Unreadable => L.T("could not be read"),
        _ => L.T("differs inside"),
    };

    private ContextMenu BuildMenu()
    {
        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        MenuItem transfer = Item(_transferLabel ?? (Source.IsRemote ? L.T("Download") : L.T("Upload")), () => RequestTransfer(Chosen()));
        MenuItem preview = Item(L.T("Preview"), RequestPreview);
        MenuItem edit = Item(L.T("Edit"), RequestEdit);

        // A phone has no other program to edit in.
        MenuItem? external = Source.IsRemote && !AppServices.MobileLayout
            ? Item(L.T("Edit in another program"), () => { if (SelectedRows.FirstOrDefault(r => !r.IsDirectory) is FileRow row) EditRequested?.Invoke(row); })
            : null;

        MenuItem newFile = Item(L.T("New file"), () => _ = NewFileAsync());
        MenuItem newFolder = Item(L.T("New folder"), () => _ = NewFolderAsync());
        MenuItem copy = Item(L.T("Copy"), CopySelected);
        MenuItem cut = Item(L.T("Cut"), CutSelected);
        MenuItem paste = Item(L.T("Paste"), () => _ = PasteAsync());
        MenuItem duplicate = Item(L.T("Duplicate"), () => _ = DuplicateSelectedAsync());
        MenuItem rename = Item(L.T("Rename"), () => _ = RenameSelectedAsync());
        // Not on Windows, whose files have no permissions of this kind: there is nothing for this computer's side to offer.
        MenuItem? permissions = Source.CanChangeMode ? Item(L.T("Permissions"), () => _ = ChangePermissionsAsync()) : null;
        MenuItem delete = Item(L.T("Delete"), () => _ = DeleteSelectedAsync());
        MenuItem extractHere = Item(L.T("Extract here"), () => _ = ExtractAsync(here: true));
        MenuItem extractTo = Item(L.T("Extract to a new folder"), () => _ = ExtractAsync(here: false));
        MenuItem compress = Item(L.T("Compress..."), () => _ = CompressAsync());
        MenuItem copyPath = Item(L.T("Copy path"), () => _ = CopyPathAsync());

        // Only for a result found in a folder below, where it is a way to get to the file.
        MenuItem inFolder = Item(L.T("Show in its folder"), () => { if (SelectedRows.FirstOrDefault() is FileRow row) _ = GoToAsync(row); });
        MenuItem refresh = Item(L.T("Refresh"), () => _ = RefreshAsync());

        var items = new List<Control> { transfer, preview, edit };
        if (external is not null) items.Add(external);
        items.Add(new Separator());
        items.AddRange([newFile, newFolder, copy, cut, paste, duplicate, rename]);
        if (permissions is not null) items.Add(permissions);
        items.Add(delete);
        items.Add(new Separator());
        items.AddRange([extractHere, extractTo, compress, new Separator(), copyPath, inFolder, refresh]);

        var menu = new ContextMenu { ItemsSource = items };
        menu.Opening += (_, _) =>
        {
            IReadOnlyList<FileRow> rows = SelectedRows;
            bool any = rows.Count > 0;
            bool real = any && Chosen().Count > 0;
            bool oneFile = rows.Count == 1 && !rows[0].IsDirectory;

            transfer.IsEnabled = real;
            preview.IsEnabled = any;
            edit.IsEnabled = oneFile;
            if (external is not null) external.IsEnabled = oneFile;
            copy.IsEnabled = cut.IsEnabled = duplicate.IsEnabled = delete.IsEnabled = real;
            rename.IsEnabled = real;
            rename.Header = rows.Count > 1 ? L.T("Rename several...") : L.T("Rename");
            if (permissions is not null) permissions.IsEnabled = real;
            paste.IsEnabled = CanPaste();
            copyPath.IsEnabled = any;

            bool archive = real && oneFile && Archives.Detect(rows[0].Name) != ArchiveKind.None && Source.CanRunCommands;
            extractHere.IsVisible = extractTo.IsVisible = archive;
            compress.IsVisible = Source.CanRunCommands;
            compress.IsEnabled = real;
            inFolder.IsVisible = rows is [{ Where: not null }] || rows is [{ IsContentHit: true }];
        };
        return menu;
    }

    // What is chosen, unless some of it only shows where a search found something: such a row is not a file in this
    // list, and there is nothing to rename, delete or copy by it.
    private IReadOnlyList<FileRow> Chosen()
    {
        IReadOnlyList<FileRow> rows = SelectedRows;
        return rows.Any(r => r.IsContentHit) ? [] : rows;
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
        _listingInFlight = listing;
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

            // A comparison holds only for the listings it was made from. Marks left on a newer one would be guesses.
            _marks = null;
            Listed?.Invoke();

            if (!sameFolder)
            {
                EndFind();
                FrequentFolders.Shared.Visit(FolderKey, path);
            }
            else if (searchSubfolders)
            {
                RestartSubfolderSearch();
            }

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
        finally
        {
            if (ReferenceEquals(_listingInFlight, listing)) _listingInFlight = null;
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
        bool inContents = _find.IsVisible && _find.SearchContents;
        bool inSubfolders = !inContents && _find.IncludeSubfolders && !filter.IsEmpty;

        // What the search found below is added to on the window's thread, so what is read away from it is a copy.
        // A search through what files say shows what it found in place of the folder, once it has found it.
        List<FileRow> pool = inContents && _contentRows is not null ? _contentRows : _rows;
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
        else if (row.IsContentHit) _ = GoToAsync(row);
        else RequestTransfer([row]);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when _find.IsVisible:
                _ = CloseFindAsync();
                break;
            case Key.Escape when _operation is not null:
                _operation.Cancel();
                break;
            case Key.Space when !_typeAhead.IsTyping(DateTime.UtcNow) && SelectedRows.Count > 0:
                RequestPreview();
                break;
            case Key.C when e.KeyModifiers == PrimaryModifier:
                CopySelected();
                break;
            case Key.X when e.KeyModifiers == PrimaryModifier:
                CutSelected();
                break;
            case Key.V when e.KeyModifiers == PrimaryModifier:
                _ = PasteAsync();
                break;
            case Key.D when e.KeyModifiers == PrimaryModifier:
                _ = DuplicateSelectedAsync();
                break;
            case Key.N when e.KeyModifiers == PrimaryModifier:
                _ = NewFileAsync();
                break;
            case Key.A when e.KeyModifiers == PrimaryModifier:
                _list.SelectAll();
                break;
            case Key.Enter when SelectedRows.Count == 1 && SelectedRows[0].IsDirectory:
                _ = NavigateAsync(SelectedRows[0].Path);
                break;
            case Key.Enter when SelectedRows is [{ IsContentHit: true } hit]:
                _ = GoToAsync(hit);
                break;
            case Key.Enter:
                RequestTransfer(Chosen());
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
            case Key.F4:
                RequestEdit();
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

    private FileFilter FilterNow() => _find.IsVisible && !_find.SearchContents ? FileFilter.Parse(_find.Text, _find.UseRegex) : FileFilter.None;

    private void OnFindChanged(bool typing)
    {
        if (_quiet) return;

        if (AppServices.Settings.FileFilterWithRegex != _find.UseRegex)
        {
            AppServices.Settings.FileFilterWithRegex = _find.UseRegex;
            AppServices.SaveSettings();
        }

        // Looking inside files takes a while, so it waits for Enter. Switching to or from it only changes what is shown.
        if (_find.SearchContents != _contentMode)
        {
            _contentMode = _find.SearchContents;
            _contentSearch?.Cancel();
            _contentRows = null;
            _deep.Stop();
            _find.ShowCount(string.Empty, problem: false);
            _ = RebuildAsync();
            return;
        }

        if (_contentMode) return;

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
        if (!_find.IsVisible || _find.SearchContents) return;

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
        if (_find.SearchContents)
        {
            // Enter again on what was searched goes to the file chosen; on something new it searches.
            if (_contentRows is not null && _find.Text.Trim() == _contentQuery && SelectedRows.FirstOrDefault() is FileRow hit) await GoToAsync(hit);
            else await SearchContentsAsync();
            return;
        }

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
        _contentSearch?.Cancel();
        _contentRows = null;
        _contentMode = false;
        _quiet = true;
        try
        {
            _find.IncludeSubfolders = false;
            _find.SearchContents = false;
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

    private void RequestPreview()
    {
        if (SelectedRows.FirstOrDefault() is FileRow row) PreviewRequested?.Invoke(row);
    }

    private void RequestEdit()
    {
        if (SelectedRows is [{ IsDirectory: false } row]) EditorRequested?.Invoke(row);
    }

    /// <summary>Moves the choice by one row and gives the row that is chosen then, so that a preview can follow the list.</summary>
    public FileRow? Step(int delta)
    {
        if (_shown.Count == 0) return null;

        MoveSelection(delta);
        return SelectedRows.FirstOrDefault();
    }

    /// <summary>Chooses the rows with these names, and shows the first of them.</summary>
    public void SelectNames(IEnumerable<string> names)
    {
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return;

        _list.SelectedItems?.Clear();
        for (int i = 0; i < _shown.Count; i++)
            if (wanted.Contains(_shown[i].Name)) _list.Selection.Select(i);

        if (_list.SelectedIndex >= 0) _list.ScrollIntoView(_list.SelectedIndex);
    }

    // The folders that are gone to most, which is where one usually wants to go. Kept apart for each server.
    private void ShowFrequentFolders()
    {
        const int Shown = 15;
        const int LongestPath = 56;

        var items = new List<Control>();
        foreach (string folder in FrequentFolders.Shared.Top(FolderKey, Shown + 1).Where(f => f != CurrentPath).Take(Shown))
        {
            string text = folder.Length <= LongestPath ? folder : "…" + folder[^(LongestPath - 1)..];
            var item = new MenuItem { Header = text };
            ToolTip.SetTip(item, folder);
            item.Click += (_, _) => _ = NavigateAsync(folder);
            items.Add(item);
        }

        if (items.Count == 0) items.Add(new MenuItem { Header = L.T("The folders you go to most will be listed here."), IsEnabled = false });

        if (CurrentPath.Length > 0 && FrequentFolders.Shared.Top(FolderKey, 1000).Contains(CurrentPath))
        {
            var forget = new MenuItem { Header = L.T("Take this folder off the list") };
            forget.Click += (_, _) => FrequentFolders.Shared.Forget(FolderKey, CurrentPath);
            items.Add(new Separator());
            items.Add(forget);
        }

        new ContextMenu { ItemsSource = items }.Open(_foldersButton);
    }

    private static string What(IReadOnlyList<FileRow> rows) => rows.Count == 1 ? $"\"{rows[0].Name}\"" : L.T("{0} items", rows.Count);

    private void CopySelected() => PutAside(cut: false);

    private void CutSelected() => PutAside(cut: true);

    private void PutAside(bool cut)
    {
        List<FileRow> rows = [.. Chosen()];
        if (rows.Count == 0) return;

        _clipboard = (this, rows, cut);
        ShowNotice(cut ? L.T("{0} cut. Paste moves them.", What(rows)) : L.T("{0} copied. Paste puts a copy where you are.", What(rows)));
    }

    // Copies go to either side. A cut only makes sense where it was made: moving between a computer and a server is a transfer.
    private bool CanPaste() => _clipboard is { } held && (ReferenceEquals(held.Origin, this) || (!held.Cut && ReferenceEquals(held.Origin, Counterpart)));

    private async Task PasteAsync()
    {
        if (_clipboard is not { } held) return;

        if (!ReferenceEquals(held.Origin, this))
        {
            if (CanPaste()) held.Origin.RequestTransfer(held.Rows);
            else ShowError(L.T("Something that was cut can only be pasted in the list it was cut from."));
            return;
        }

        await CopyIntoCurrentAsync(held.Rows, move: held.Cut);
        if (held.Cut) _clipboard = null;
    }

    private async Task DuplicateSelectedAsync()
    {
        IReadOnlyList<FileRow> rows = Chosen();
        if (rows.Count > 0) await CopyIntoCurrentAsync([.. rows], move: false);
    }

    private bool IsHere(FileRow row) =>
        string.Equals(Source.Parent(row.Path)?.TrimEnd('/', '\\'), CurrentPath.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    // Copies or moves rows into the folder shown. A copy that would take the name of something there gets a new one.
    private Task CopyIntoCurrentAsync(IReadOnlyList<FileRow> rows, bool move)
    {
        List<string> taken = _rows.Select(r => r.Name).ToList();
        var made = new List<string>();

        return WorkAsync(move ? L.T("Moving...") : L.T("Copying..."), async token =>
        {
            int skipped = 0;
            int number = 0;
            foreach (FileRow row in rows)
            {
                token.ThrowIfCancellationRequested();
                number++;
                ShowNotice(L.T("{0} {1} of {2}: {3}  (Esc stops)", move ? L.T("Moving") : L.T("Copying"), number, rows.Count, row.Name));

                bool exists = taken.Contains(row.Name, StringComparer.OrdinalIgnoreCase);
                if (move)
                {
                    if (IsHere(row)) continue;
                    if (exists) throw new IOException(L.T("\"{0}\" is already in this folder.", row.Name));

                    await Source.MoveAsync(row, Source.Combine(CurrentPath, row.Name), token);
                    made.Add(row.Name);
                    taken.Add(row.Name);
                }
                else
                {
                    string name = exists ? FileNaming.CopyName(row.Name, row.IsDirectory, taken) : row.Name;
                    skipped += await Source.CopyAsync(row, Source.Combine(CurrentPath, name), token);
                    made.Add(name);
                    taken.Add(name);
                }
            }

            return skipped > 0 ? L.T("{0} links were left out: a link is not followed.", skipped) : null;
        }, made);
    }

    // One long job at a time on a side, shown on the status line and stopped with Escape. What it made is chosen afterwards.
    private async Task WorkAsync(string description, Func<CancellationToken, Task<string?>> work, IReadOnlyList<string>? select = null)
    {
        if (_operation is not null)
        {
            ShowError(L.T("Wait for the job that is running, or press Esc to stop it."));
            return;
        }

        var operation = new CancellationTokenSource();
        _operation = operation;
        string startedIn = CurrentPath;
        ShowNotice(description);

        string? note = null;
        string? problem = null;
        try
        {
            note = await work(operation.Token);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            note = L.T("Stopped.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problem = ex.Message;
        }
        finally
        {
            _operation = null;
            operation.Dispose();
        }

        // Shown even when the job failed half way, since what it did is there now. Not when the user has gone to another
        // folder in the meantime, or is on the way to one: the list is theirs, and this would take them back.
        if (CurrentPath == startedIn && _listingInFlight is null)
        {
            await RefreshAsync(searchSubfolders: false);
            if (select is { Count: > 0 }) SelectNames(select);
        }

        if (problem is not null) ShowError(problem);
        else if (note is not null) ShowNotice(note);
    }

    private async Task NewFileAsync()
    {
        if (await _dialogs.AskTextAsync(L.T("New file"), L.T("File name"), null, L.T("Create")) is not string name) return;
        if (!FileNaming.IsPlainName(name))
        {
            ShowError(L.T("A name cannot contain / or \\."));
            return;
        }

        await RunAsync(() => Source.CreateFileAsync(Source.Combine(CurrentPath, name), CancellationToken.None));
        SelectNames([name]);
    }

    private async Task RenameSeveralAsync()
    {
        IReadOnlyList<FileRow> rows = Chosen();
        BulkRenamePlan? plan = await _dialogs.EditBulkRenameAsync(rows.Select(r => r.Name).ToList(), _rows.Select(r => r.Name).ToList());
        if (plan is null || plan.Steps.Count == 0) return;

        // Each row follows its file through the steps: a swap goes by a temporary name, which later steps know it by.
        Dictionary<string, FileRow> current = rows.ToDictionary(r => r.Name, StringComparer.Ordinal);
        List<string> renamed = plan.Entries.Where(e => e.Problem is null && e.New != e.Old).Select(e => e.New).ToList();

        await RunAsync(async () =>
        {
            foreach ((string from, string to) in plan.Steps)
            {
                FileRow row = current[from];
                await Source.RenameAsync(row, to, CancellationToken.None);
                current.Remove(from);
                current[to] = row with { Name = to, Path = Source.Combine(Source.Parent(row.Path) ?? CurrentPath, to) };
            }
        });

        SelectNames(renamed);
    }

    private async Task ExtractAsync(bool here)
    {
        if (Chosen() is not [{ IsDirectory: false } archive] || Archives.Detect(archive.Name) == ArchiveKind.None) return;

        string name = here ? string.Empty : FileNaming.Unused(Archives.FolderNameFor(archive.Name), _rows.Select(r => r.Name));
        string folder = here ? CurrentPath : Source.Combine(CurrentPath, name);

        await WorkAsync(L.T("Unpacking {0}...", archive.Name), async token =>
        {
            await Source.ExtractAsync(archive, folder, token);
            return null;
        }, here ? null : [name]);
    }

    private async Task CompressAsync()
    {
        IReadOnlyList<FileRow> rows = Chosen();
        if (rows.Count == 0 || !Source.CanRunCommands) return;

        string folderName = Path.GetFileName(CurrentPath.TrimEnd('/', '\\'));
        string suggestion = rows.Count == 1
            ? (rows[0].IsDirectory ? rows[0].Name : Path.GetFileNameWithoutExtension(rows[0].Name))
            : folderName.Length > 0 ? folderName : L.T("Archive");
        ArchiveChoice? choice = await _dialogs.AskArchiveAsync(suggestion, Source.IsRemote);
        if (choice is null) return;

        if (_rows.Any(r => string.Equals(r.Name, choice.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            ShowError(L.T("\"{0}\" is already in this folder.", choice.FileName));
            return;
        }

        List<string> names = rows.Select(r => r.Name).ToList();
        await WorkAsync(L.T("Packing {0}...", choice.FileName), async token =>
        {
            await Source.CompressAsync(CurrentPath, names, choice.FileName, choice.Kind, token);
            return null;
        }, [choice.FileName]);
    }

    // Looks inside the files below this folder. A word with a capital is looked for as written; one without finds either.
    private async Task SearchContentsAsync()
    {
        string query = _find.Text.Trim();
        _contentSearch?.Cancel();
        _contentRows = null;
        _contentQuery = query;

        if (query.Length == 0)
        {
            _find.ShowCount(string.Empty, problem: false);
            await RebuildAsync();
            return;
        }

        if (!Source.CanRunCommands)
        {
            _find.ShowCount(L.T("This server cannot be given commands."), problem: true);
            return;
        }

        var search = new CancellationTokenSource();
        _contentSearch = search;
        _find.ShowCount(L.T("Searching..."), problem: false);

        try
        {
            ContentSearchResult result = await Source.SearchContentAsync(CurrentPath, query, ignoreCase: !query.Any(char.IsUpper), _find.UseRegex, ContentLimit, search.Token);
            if (search.IsCancellationRequested) return;

            _contentRows = [.. result.Rows];
            await RebuildAsync();
            if (search.IsCancellationRequested) return;

            string count = result.Rows.Count == 0 ? L.T("No matches") : result.Truncated ? L.T("{0}+ files", result.Rows.Count) : L.T("{0} files", result.Rows.Count);
            _find.ShowCount(result.Note ?? count, problem: result.Note is not null);
            if (result.Rows.Count > 0) _list.SelectedIndex = 0;
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested)
        {
            // Replaced by a newer search, or the bar was closed.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!search.IsCancellationRequested) _find.ShowCount(ex.Message, problem: true);
        }
    }

    private async Task NewFolderAsync()
    {
        if (await _dialogs.AskTextAsync(L.T("New folder"), L.T("Folder name"), null, L.T("Create")) is not string name) return;
        await RunAsync(() => Source.CreateFolderAsync(Source.Combine(CurrentPath, name), CancellationToken.None));
    }

    private async Task RenameSelectedAsync()
    {
        if (Chosen().Count > 1)
        {
            await RenameSeveralAsync();
            return;
        }

        if (Chosen().Count != 1) return;
        FileRow row = Chosen()[0];
        if (await _dialogs.AskTextAsync(L.T("Rename"), L.T("New name"), row.Name, L.T("Change")) is not string name || name == row.Name) return;
        await RunAsync(() => Source.RenameAsync(row, name, CancellationToken.None));
    }

    // A job like the copies: shown on the status line as it goes, stopped with Esc, and what could not be changed told at the end.
    private async Task ChangePermissionsAsync()
    {
        IReadOnlyList<FileRow> rows = Chosen();
        if (rows.Count == 0 || !Source.CanChangeMode) return;

        // What they have now is offered only when it is the same for all of them.
        List<string> now = rows.Select(r => PermissionBits.DigitsOfLetters(r.Permissions)).Distinct().ToList();
        bool folders = rows.Any(r => r.IsDirectory && !r.IsLink);
        PermissionChoice? choice = await _dialogs.AskPermissionsAsync(What(rows), now is [string same] ? same : string.Empty, folders);
        if (choice is null) return;

        PermissionReport? report = null;
        await WorkAsync(L.T("Changing permissions...  (Esc stops)"), async token =>
        {
            var progress = new Progress<int>(done => ShowNotice(L.T("Changing permissions: {0} done  (Esc stops)", done)));
            report = await PermissionChanger.ApplyAsync(Source, rows, choice, progress, token);
            if (report.FailureCount > 0) return null;

            string text = report.Changed > 0 ? L.T("Permissions changed on {0} items.", report.Changed) : L.T("Permissions changed.");
            return report.LinksLeftOut > 0 ? text + " " + L.T("{0} links were left as they are.", report.LinksLeftOut) : text;
        }, rows.Select(r => r.Name).ToList());

        if (report is not { FailureCount: > 0 } failed) return;

        ShowError(L.T("{0} could not be changed.", failed.FailureCount));
        string list = string.Join("\n", failed.Failures.Take(20));
        if (failed.FailureCount > 20) list += "\n" + L.T("... and {0} more", failed.FailureCount - 20);
        if (failed.GaveUp) list += "\n\n" + L.T("So many failed one after another that the rest was not tried.");
        await _dialogs.ShowMessageAsync(L.T("Some permissions could not be changed"), list);
    }

    private async Task DeleteSelectedAsync()
    {
        IReadOnlyList<FileRow> rows = Chosen();
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
            // What was done before it failed is there now, so the list is made again before the reason is shown.
            try
            {
                await RefreshAsync(searchSubfolders: false);
            }
            catch (Exception refreshing) when (refreshing is not OutOfMemoryException)
            {
                // The connection is likely what failed, and the reason below says so.
            }

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

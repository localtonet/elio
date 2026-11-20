using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Elio.Core;

namespace Elio.UI.Files;

/// <summary>
/// A small editor for the text of one file, made to fill whatever it is put in: the file screen shows it over the two lists.
/// It works with a keyboard and without one: on a phone a row of the keys code needs sits above the soft keyboard.
/// </summary>
public sealed class FileEditorView : UserControl
{
    private const double CompactWidth = 520;
    private const double MinFontSize = 8;
    private const double MaxFontSize = 32;
    private const double DefaultFontSize = 13;

    private static readonly TimeSpan StatusDelay = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan FindDelay = TimeSpan.FromMilliseconds(200);

    private const string IconBack = "M10,3.5 L5.5,8 L10,12.5";
    private const string IconSave = "M3,2.5 H10.5 L13,5 V13.5 H3 Z M5.5,2.5 V5.5 H9.5 V2.5 M5.5,13.5 V9 H10.5 V13.5";
    private const string IconUndo = "M6.5,3.5 L3.5,6.5 L6.5,9.5 M3.5,6.5 H9.5 A3,3 0 0 1 9.5,12.5 H6";
    private const string IconRedo = "M9.5,3.5 L12.5,6.5 L9.5,9.5 M12.5,6.5 H6.5 A3,3 0 0 0 6.5,12.5 H10";
    private const string IconLine = "M6.5,2.5 L5.5,13.5 M10.5,2.5 L9.5,13.5 M3,6 H13.5 M2.5,10 H13";
    private const string IconMore = "M3.5,8 H3.6 M8,8 H8.1 M12.5,8 H12.6";

    // What a phone's keyboard makes slow to reach, and code uses all the time.
    private static readonly string[] Symbols =
        ["{", "}", "(", ")", "[", "]", "<", ">", "\"", "'", "`", ";", ":", "=", "/", "\\", "|", "&", "*", "#", "$", "%", "_", "-", "+", "~", "!"];

    private readonly DialogHost _dialogs;
    private readonly string _name;
    private readonly string _location;
    private readonly TextFormat _format;
    private readonly bool _readOnly;
    private readonly string _indent;
    private readonly bool _phone = AppServices.MobileLayout;
    private readonly double _target;

    private readonly TextBox _box;
    private readonly Ellipse _dirtyDot;
    private readonly Button _save;
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly Button _lineButton;
    private readonly Button _findButton;
    private readonly Button _more;
    private readonly MenuFlyout _menu;
    private readonly List<MenuItem> _compactItems = [];
    private readonly MenuItem _wrapItem;

    private readonly Border _findBar;
    private readonly TextBox _query;
    private readonly TextBox _replacement;
    private readonly TextBlock _count;
    private readonly Control _replaceRow;
    private readonly Button _caseButton;
    private readonly Button _regexButton;

    private readonly TextBlock _position;
    private readonly TextBlock _message;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _findTimer;

    private string _saved;
    private bool _dirty;
    private bool _compact;
    private bool _wrap;
    private bool _caseSensitive;
    private bool _regex;
    private bool _closing;
    private Task<bool>? _saveInFlight;
    private FindResult _found = FindResult.None;
    private (string Text, string Query, bool Case, bool Regex) _foundFor = (string.Empty, string.Empty, false, false);

    public FileEditorView(DialogHost dialogs, string name, string location, string text, TextFormat format, bool readOnly = false)
    {
        _dialogs = dialogs;
        _name = name;
        _location = location;
        _format = format;
        _readOnly = readOnly;
        _saved = TextCodec.Normalize(text);
        _indent = EditorText.DetectIndent(_saved);
        _target = _phone ? 40 : 30;
        _wrap = AppServices.Settings.EditorWrap;

        Background = ElioColors.Window;
        Tag = "file-editor";

        _box = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            IsReadOnly = readOnly,
            NewLine = "\n",
            FontFamily = ElioStyle.CodeFont,
            FontSize = ClampSize(AppServices.Settings.EditorFontSize),
            Padding = new Thickness(_phone ? 10 : 14, 10),
            CornerRadius = default,
            BorderThickness = new Thickness(0),
            Background = ElioColors.Window,
            Text = _saved,
            Tag = "editor-box",
        };
        ApplyWrap();
        ConfigureInput(_box);

        _dirtyDot = new Ellipse { Width = 7, Height = 7, Fill = ElioColors.Accent, VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Tag = "editor-dirty" };

        _save = Tool(IconSave, L.T("Save ({0})", PrimaryName + "+S"), "editor-save", () => _ = SaveAsync());
        _undo = Tool(IconUndo, L.T("Undo"), "editor-undo", Undo);
        _redo = Tool(IconRedo, L.T("Redo"), "editor-redo", Redo);
        _lineButton = Tool(IconLine, L.T("Go to line ({0})", PrimaryName + "+G"), "editor-line", () => _ = GoToLineAsync());
        _findButton = Tool(ElioStyle.IconSearch, L.T("Find and replace ({0})", PrimaryName + "+F"), "editor-find", () => ShowFind(replace: false));
        _more = Tool(IconMore, L.T("More"), "editor-more", () => { }, weight: 2.4);
        _save.IsEnabled = false;

        _wrapItem = new MenuItem { Header = L.T("Wrap long lines"), ToggleType = MenuItemToggleType.CheckBox, Tag = "editor-wrap" };
        _wrapItem.Click += (_, _) => SetWrap(!_wrap);
        _menu = BuildMenu();
        _more.Flyout = _menu;

        _caseButton = Switch("Aa", L.T("Match case: on / off"), "editor-find-case", () => SetCase(!_caseSensitive));
        _regexButton = Switch(".*", L.T("Read as a regular expression: on / off"), "editor-find-regex", () => SetRegex(!_regex));
        _query = new TextBox { MinHeight = _target - 4, MinWidth = 60, FontSize = 12, Padding = new Thickness(8, 3), VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(6), PlaceholderText = L.T("Find"), Tag = "editor-find-box" };
        _replacement = new TextBox { MinHeight = _target - 4, MinWidth = 60, FontSize = 12, Padding = new Thickness(8, 3), VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(6), PlaceholderText = L.T("Replace with"), Tag = "editor-replace-box" };
        ConfigureInput(_query);
        ConfigureInput(_replacement);
        _count = new TextBlock { FontSize = ElioStyle.SmallFontSize, Foreground = ElioColors.TextMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), IsHitTestVisible = false, Tag = "editor-find-count" };
        _query.InnerRightContent = _count;
        _findBar = BuildFindBar(out _replaceRow);

        _position = new TextBlock { FontSize = ElioStyle.SmallFontSize, Foreground = ElioColors.TextMuted, VerticalAlignment = VerticalAlignment.Center, Tag = "editor-position" };
        _message = new TextBlock { FontSize = ElioStyle.SmallFontSize, Foreground = ElioColors.TextMuted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(12, 0, 8, 0), Tag = "editor-message" };

        _statusTimer = new DispatcherTimer { Interval = StatusDelay };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            ShowPosition();
        };
        _findTimer = new DispatcherTimer { Interval = FindDelay };
        _findTimer.Tick += (_, _) =>
        {
            _findTimer.Stop();
            RunFind(select: false);
        };

        _box.PropertyChanged += OnBoxChanged;
        _box.AddHandler(KeyDownEvent, OnBoxKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
        _query.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        _replacement.AddHandler(KeyDownEvent, OnReplacementKeyDown, RoutingStrategies.Tunnel);
        _query.TextChanged += (_, _) => RestartFind();
        SizeChanged += (_, e) => FitWidth(e.NewSize.Width);

        Content = BuildLayout();
        _compactItems.ForEach(item => item.IsVisible = false);
        ShowPosition();
        ShowDirty();
        if (readOnly) SetMessage(L.T("Read only"), problem: false);
    }

    /// <summary>
    /// Writes the bytes of the file, as the file has them. Returns null when it worked, or what to tell the user when it
    /// did not. The editor stays as it is until it has worked.
    /// </summary>
    public Func<byte[], Task<string?>>? Saver { get; set; }

    /// <summary>Whether the text differs from what was last saved, or opened. Going back to that text makes it clean again.</summary>
    public bool IsDirty => _dirty;

    /// <summary>The text, with "\n" at the end of every line.</summary>
    public string Text => NormalizedText();

    /// <summary>The user wants out and nothing is lost: any unsaved change was asked about first.</summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Asks what to do about changes that were not saved: save them, drop them or go on editing.
    /// </summary>
    /// <returns>Whether the editor may be taken away.</returns>
    public async Task<bool> RequestCloseAsync()
    {
        if (!_dirty) return true;
        if (_closing) return false;

        _closing = true;
        try
        {
            int choice = await _dialogs.AskChoiceAsync(L.T("Unsaved changes"), L.T("{0} has changes that were not saved.", _name), L.T("Save"), L.T("Discard changes"));
            if (choice == 0) return await SaveAsync() && !_dirty;
            return choice == 1;
        }
        finally
        {
            _closing = false;
        }
    }

    /// <summary>Gives the keyboard to the text.</summary>
    public void FocusEditor() => Dispatcher.UIThread.Post(() => _box.Focus(), DispatcherPriority.Loaded);

    private static string PrimaryName => OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";

    private static KeyModifiers Primary => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private static double ClampSize(double size) =>
        double.IsNaN(size) || size <= 0 ? DefaultFontSize : Math.Clamp(size, MinFontSize, MaxFontSize);

    // A phone's keyboard would make the first letter a capital and mend what was typed, which code does not want.
    private static void ConfigureInput(TextBox box)
    {
        TextInputOptions.SetAutoCapitalization(box, false);
        TextInputOptions.SetContentType(box, TextInputContentType.Normal);
    }

    private string NormalizedText() => TextCodec.Normalize(_box.Text ?? string.Empty);

    #region Building

    private Button Tool(string icon, string tip, string tag, Action run, double weight = 1.4)
    {
        Button button = ElioStyle.IconButton(ElioStyle.Icon(icon, ElioColors.TextMuted, 16, weight), tip, _target);
        button.Tag = tag;

        // Pressing it must not take the keyboard from the text: on a phone that would put the soft keyboard away.
        button.Focusable = false;
        button.Click += (_, _) => run();
        return button;
    }

    private Button Switch(string text, string tip, string tag, Action toggle)
    {
        Button button = ElioStyle.IconButton(text, tip, _target - 4);
        button.FontSize = 12;
        button.Tag = tag;
        button.Focusable = false;
        button.Click += (_, _) => toggle();
        return button;
    }

    private Control BuildLayout()
    {
        // Lowest first: the key row rests on the keyboard and the status line sits on the key row.
        var layout = new DockPanel { LastChildFill = true };
        if (_phone && !_readOnly)
        {
            Control keys = BuildKeyRow();
            DockPanel.SetDock(keys, Dock.Bottom);
            layout.Children.Add(keys);
        }

        Control status = BuildStatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        layout.Children.Add(status);

        Control top = BuildTopBar();
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);

        DockPanel.SetDock(_findBar, Dock.Top);
        layout.Children.Add(_findBar);

        layout.Children.Add(_box);
        return layout;
    }

    private static T Docked<T>(T control, Dock dock) where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private Control BuildTopBar()
    {
        Button back = Tool(IconBack, L.T("Back"), "editor-back", () => _ = CloseAsync(), weight: 1.8);

        var title = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 0),
            Children =
            {
                // The mark that says there is something unsaved goes in front of the name, so that a long name is cut and not the mark.
                new DockPanel
                {
                    LastChildFill = true,
                    Children =
                    {
                        Docked(_dirtyDot, Dock.Left),
                        new TextBlock { Text = _name, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = ElioColors.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Tag = "editor-name" },
                    },
                },
                new TextBlock { Text = _location, FontSize = ElioStyle.SmallFontSize, Foreground = ElioColors.TextFaint, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "editor-location" },
            },
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _undo, _redo, _lineButton, _findButton, _save, _more } };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(_phone ? 4 : 8, 4) };
        Grid.SetColumn(title, 1);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(back);
        grid.Children.Add(title);
        grid.Children.Add(buttons);

        return new Border { Background = ElioColors.Chrome, BorderBrush = ElioColors.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = grid, Tag = "editor-top" };
    }

    private Control BuildStatusBar()
    {
        var info = new TextBlock { Text = TextCodec.NameOf(_format) + " · " + (_format.LineEnding == "\r\n" ? "CRLF" : "LF"), FontSize = ElioStyle.SmallFontSize, Foreground = ElioColors.TextFaint, VerticalAlignment = VerticalAlignment.Center, Tag = "editor-format" };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 0), Height = _phone ? 28 : 24 };
        Grid.SetColumn(_message, 1);
        Grid.SetColumn(info, 2);
        grid.Children.Add(_position);
        grid.Children.Add(_message);
        grid.Children.Add(info);
        return new Border { Background = ElioColors.Chrome, BorderBrush = ElioColors.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = grid, Tag = "editor-status" };
    }

    private Border BuildFindBar(out Control replaceRow)
    {
        Button previous = Tool(ElioStyle.IconUp, L.T("Previous match (Shift+F3)"), "editor-find-previous", () => Step(forward: false));
        Button next = Tool(ElioStyle.IconDown, L.T("Next match (F3)"), "editor-find-next", () => Step(forward: true));
        Button close = Tool(ElioStyle.IconClose, L.T("Close"), "editor-find-close", HideFind);
        foreach (Button b in new[] { previous, next, close }) b.Width = b.Height = _target - 4;

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Margin = new Thickness(4, 0, 0, 0), Children = { _caseButton, _regexButton, previous, next, close } };
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(tools, 1);
        searchRow.Children.Add(_query);
        searchRow.Children.Add(tools);

        Button replaceOne = TextButton(L.T("Replace"), "editor-replace", ReplaceCurrent);
        Button replaceAll = TextButton(L.T("All"), "editor-replace-all", ReplaceEverywhere);
        var replaceButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4, 0, 0, 0), Children = { replaceOne, replaceAll } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), IsVisible = false };
        Grid.SetColumn(replaceButtons, 1);
        row.Children.Add(_replacement);
        row.Children.Add(replaceButtons);
        replaceRow = row;

        return new Border
        {
            Background = ElioColors.Elevated,
            BorderBrush = ElioColors.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ElioStyle.CardRadius),
            Padding = new Thickness(5),
            Margin = new Thickness(8, 6, 8, 4),
            IsVisible = false,
            Tag = "editor-find-bar",
            Child = new StackPanel { Spacing = 4, Children = { searchRow, row } },
        };
    }

    private Button TextButton(string text, string tag, Action run)
    {
        var button = new Button
        {
            Content = text,
            Height = _target - 4,
            Padding = new Thickness(10, 0),
            FontSize = 12,
            Focusable = false,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(6),
            Tag = tag,
        };
        button.Click += (_, _) => run();
        return button;
    }

    private MenuFlyout BuildMenu()
    {
        MenuItem Item(string header, Action action, bool compactOnly = false)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            if (compactOnly) _compactItems.Add(item);
            return item;
        }

        var items = new List<object>
        {
            Item(L.T("Undo"), Undo, compactOnly: true),
            Item(L.T("Redo"), Redo, compactOnly: true),
            Item(L.T("Go to line"), () => _ = GoToLineAsync(), compactOnly: true),
            _wrapItem,
            new Separator(),
            Item(L.T("Larger text"), () => ChangeSize(1)),
            Item(L.T("Smaller text"), () => ChangeSize(-1)),
            Item(L.T("Reset text size"), () => SetSize(DefaultFontSize)),
        };

        var menu = new MenuFlyout { ItemsSource = items };
        menu.Opening += (_, _) =>
        {
            _wrapItem.IsChecked = _wrap;
            foreach (MenuItem item in _compactItems) item.IsVisible = _compact;
        };
        return menu;
    }

    // The keys a phone's keyboard does not have, in a row that scrolls sideways, none of which takes the keyboard away.
    private Control BuildKeyRow()
    {
        Button Make(string label, string tag)
        {
            return new Button
            {
                Content = label,
                MinWidth = 42,
                Height = 38,
                Padding = new Thickness(8, 0),
                Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(ElioStyle.RowRadius),
                Background = ElioColors.Control,
                Foreground = ElioColors.Text,
                FontSize = ElioStyle.BaseFontSize,
                Tag = tag,
            };
        }

        Button Key(string label, string tag, Action press)
        {
            Button button = Make(label, tag);
            button.Click += (_, _) => press();
            return button;
        }

        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4, 3) };
        keys.Children.Add(Key("Tab", "key-tab", InsertTab));
        keys.Children.Add(Key(L.T("Undo"), "key-undo", Undo));
        keys.Children.Add(Key(L.T("Redo"), "key-redo", Redo));
        keys.Children.Add(Key("←", "key-left", () => MoveCaret(-1, 0)));
        keys.Children.Add(Key("→", "key-right", () => MoveCaret(1, 0)));
        keys.Children.Add(Key("↑", "key-up", () => MoveCaret(0, -1)));
        keys.Children.Add(Key("↓", "key-down", () => MoveCaret(0, 1)));
        foreach (string symbol in Symbols) keys.Children.Add(Key(symbol, "key-text", () => Insert(symbol)));

        return new Border
        {
            Background = ElioColors.Chrome,
            BorderBrush = ElioColors.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Tag = "editor-keys",
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = keys,
            },
        };
    }

    #endregion

    // On a narrow screen what is used less goes into the menu, so nothing runs off the side.
    private void FitWidth(double width)
    {
        bool compact = width < CompactWidth;
        if (compact == _compact) return;

        _compact = compact;
        _undo.IsVisible = _redo.IsVisible = _lineButton.IsVisible = !compact;
    }

    #region Text

    private void OnBoxChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.TextProperty)
        {
            UpdateDirty();
            if (_findBar.IsVisible) RestartFind();
        }
        else if (e.Property != TextBox.CaretIndexProperty && e.Property != TextBox.SelectionStartProperty && e.Property != TextBox.SelectionEndProperty)
        {
            return;
        }

        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void UpdateDirty()
    {
        bool dirty = !string.Equals(NormalizedText(), _saved, StringComparison.Ordinal);
        if (dirty == _dirty) return;

        _dirty = dirty;
        ShowDirty();
    }

    private void ShowDirty()
    {
        _dirtyDot.IsVisible = _dirty;
        _save.IsEnabled = _dirty && !_readOnly;
        _save.Opacity = _save.IsEnabled ? 1 : 0.4;
        _undo.IsEnabled = _redo.IsEnabled = !_readOnly;
    }

    private void ShowPosition()
    {
        string text = _box.Text ?? string.Empty;
        (int line, int column) = EditorText.LineColumn(text, _box.CaretIndex);
        int selected = Math.Abs(_box.SelectionEnd - _box.SelectionStart);

        string shown = L.T("Ln {0}, Col {1}", line, column) + " · " + L.T("{0} lines", EditorText.LineCount(text));
        if (selected > 0) shown += " · " + L.T("{0} selected", selected);
        _position.Text = shown;

        if (_findBar.IsVisible) ShowCount();
    }

    private void SetMessage(string text, bool problem)
    {
        _message.Foreground = problem ? ElioColors.Danger : ElioColors.TextMuted;
        _message.Text = text;
    }

    // The order matters, as it was found by trying it: setting the caret collapses a selection, and a selection that
    // starts where the old one ended collapses too. With the end and the caret first, the start is the last thing set
    // and the selection that results has the caret at its end.
    private void SelectRange(int start, int end)
    {
        int length = (_box.Text ?? string.Empty).Length;
        start = Math.Clamp(start, 0, length);
        end = Math.Clamp(end, 0, length);
        _box.SelectionEnd = end;
        _box.CaretIndex = end;
        _box.SelectionStart = start;
    }

    /// <summary>Puts text in place of what is chosen, or at the caret, the way typing would, so that it can be undone.</summary>
    private void Insert(string text)
    {
        if (_readOnly) return;

        _box.Focus();
        _box.SelectedText = text;
    }

    private void Apply(TextEdit edit)
    {
        if (_readOnly) return;

        SelectRange(edit.Start, edit.Start + edit.Length);
        _box.SelectedText = edit.Replacement;
        SelectRange(edit.SelectionStart, edit.SelectionEnd);
    }

    private void Undo()
    {
        if (_readOnly) return;

        _box.Focus();
        _box.Undo();
    }

    private void Redo()
    {
        if (_readOnly) return;

        _box.Focus();
        _box.Redo();
    }

    private void InsertTab()
    {
        if (_readOnly) return;

        string text = NormalizedText();
        int start = Math.Min(_box.SelectionStart, _box.SelectionEnd);
        int end = Math.Max(_box.SelectionStart, _box.SelectionEnd);

        // A selection over several lines is moved in as a whole, as it is in any editor.
        if (end > start && text.AsSpan(start, end - start).Contains('\n')) Apply(EditorText.Indent(text, start, end, _indent));
        else Insert(EditorText.TabInsertion(text, start, _indent));
    }

    private void Outdent()
    {
        if (_readOnly) return;

        string text = NormalizedText();
        int start = Math.Min(_box.SelectionStart, _box.SelectionEnd);
        int end = Math.Max(_box.SelectionStart, _box.SelectionEnd);
        Apply(EditorText.Outdent(text, start, end, _indent));
    }

    private void MoveCaret(int columns, int lines)
    {
        _box.Focus();
        string text = _box.Text ?? string.Empty;
        int caret = _box.CaretIndex;

        int target;
        if (lines != 0)
        {
            (int line, int column) = EditorText.LineColumn(text, caret);
            target = EditorText.IndexAt(text, Math.Clamp(line + lines, 1, EditorText.LineCount(text)), column);
        }
        else
        {
            target = Math.Clamp(caret + columns, 0, text.Length);
        }

        SelectRange(target, target);
    }

    private async Task GoToLineAsync()
    {
        string text = _box.Text ?? string.Empty;
        int lines = EditorText.LineCount(text);
        (int current, _) = EditorText.LineColumn(text, _box.CaretIndex);

        string? answer = await _dialogs.AskTextAsync(L.T("Go to line"), L.T("Line number (1 to {0})", lines), current.ToString(), L.T("Go"));
        if (answer is not null && int.TryParse(answer, out int line))
        {
            int index = EditorText.IndexOfLine(_box.Text ?? string.Empty, Math.Clamp(line, 1, lines));
            _box.Focus();
            SelectRange(index, index);
        }
        else
        {
            _box.Focus();
        }
    }

    #endregion

    #region Settings

    private void ApplyWrap()
    {
        _box.TextWrapping = _wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ScrollViewer.SetHorizontalScrollBarVisibility(_box, _wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
    }

    private void SetWrap(bool wrap)
    {
        _wrap = wrap;
        ApplyWrap();
        AppServices.Settings.EditorWrap = wrap;
        AppServices.SaveSettings();
    }

    private void ChangeSize(double by) => SetSize(_box.FontSize + by);

    private void SetSize(double size)
    {
        _box.FontSize = ClampSize(size);
        AppServices.Settings.EditorFontSize = _box.FontSize;
        AppServices.SaveSettings();
    }

    #endregion

    #region Saving and closing

    private async Task CloseAsync()
    {
        if (await RequestCloseAsync()) CloseRequested?.Invoke();
    }

    private async Task<bool> SaveAsync()
    {
        if (_readOnly || !_dirty && _saveInFlight is null) return false;
        if (_saveInFlight is { } running) return await running;

        var done = new TaskCompletionSource<bool>();
        _saveInFlight = done.Task;
        bool saved = false;
        try
        {
            saved = await WriteAsync();
            return saved;
        }
        finally
        {
            _saveInFlight = null;

            // Anyone who asked for a save while this one was going gets this one's answer.
            done.TrySetResult(saved);
        }
    }

    private async Task<bool> WriteAsync()
    {
        if (Saver is not { } saver)
        {
            SetMessage(L.T("This file cannot be saved here."), problem: true);
            return false;
        }

        // What is written is the text as it is now. Whatever is typed while it is being written is not in it, and stays unsaved.
        string snapshot = NormalizedText();
        SetMessage(L.T("Saving..."), problem: false);

        string? problem;
        try
        {
            problem = await saver(TextCodec.Encode(snapshot, _format));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problem = ex.Message;
        }

        if (problem is not null)
        {
            SetMessage(problem, problem: true);
            return false;
        }

        // Clean again, unless more was typed while it was being written.
        _saved = snapshot;
        _dirty = !string.Equals(NormalizedText(), _saved, StringComparison.Ordinal);
        ShowDirty();
        SetMessage(L.T("Saved {0:HH:mm:ss}", DateTime.Now), problem: false);
        return true;
    }

    #endregion

    #region Find and replace

    private void ShowFind(bool replace)
    {
        _findBar.IsVisible = true;
        _replaceRow.IsVisible = replace && !_readOnly;

        // What is chosen in the text is what one most often wants to look for.
        int start = Math.Min(_box.SelectionStart, _box.SelectionEnd);
        int end = Math.Max(_box.SelectionStart, _box.SelectionEnd);
        string text = _box.Text ?? string.Empty;
        if (end > start && end - start <= 200 && !text.AsSpan(start, end - start).Contains('\n'))
            _query.Text = text.Substring(start, end - start);

        _query.Focus();
        _query.SelectAll();
        RunFind(select: false);
    }

    private void HideFind()
    {
        _findTimer.Stop();
        _findBar.IsVisible = false;
        _found = FindResult.None;
        FocusEditor();
    }

    private void SetCase(bool on)
    {
        _caseSensitive = on;
        _caseButton.Background = on ? ElioColors.Selection : ElioColors.Transparent;
        RestartFind();
    }

    private void SetRegex(bool on)
    {
        _regex = on;
        _regexButton.Background = on ? ElioColors.Selection : ElioColors.Transparent;
        RestartFind();
    }

    // Typing makes a burst of changes, and the text is only searched when it has paused.
    private void RestartFind()
    {
        _findTimer.Stop();
        _findTimer.Start();
    }

    private void RunFind(bool select)
    {
        _findTimer.Stop();
        string text = NormalizedText();
        string query = _query.Text ?? string.Empty;
        _found = EditorText.Find(text, query, _caseSensitive, _regex);
        _foundFor = (text, query, _caseSensitive, _regex);
        ShowCount();
        if (select) Step(forward: true, fromCaret: true);
    }

    private void ShowCount()
    {
        string query = _query.Text ?? string.Empty;
        if (query.Length == 0)
        {
            _count.Text = string.Empty;
            return;
        }

        if (_found.Invalid)
        {
            _count.Text = L.T("Not a valid pattern");
            return;
        }

        int total = _found.Matches.Count;
        if (total == 0)
        {
            _count.Text = L.T("No matches");
            return;
        }

        string all = _found.Capped ? total.ToString("N0") + "+" : total.ToString("N0");
        int start = Math.Min(_box.SelectionStart, _box.SelectionEnd);
        int length = Math.Abs(_box.SelectionEnd - _box.SelectionStart);
        int at = _found.Matches.FindIndex(m => m.Start == start && m.Length == length);
        _count.Text = at >= 0 ? L.T("{0} of {1}", at + 1, all) : L.T("{0} matches", all);
    }

    // What was found is only good for the text and the search it was found with, and a search is only run a moment after
    // the last key. Searching again is cheap next to being wrong.
    private void Refresh()
    {
        if (_foundFor != (NormalizedText(), _query.Text ?? string.Empty, _caseSensitive, _regex)) RunFind(select: false);
    }

    /// <summary>Chooses the next or the previous match, going round at the ends, and brings it into view.</summary>
    private void Step(bool forward, bool fromCaret = false)
    {
        Refresh();
        if (_found.Matches.Count == 0)
        {
            ShowCount();
            return;
        }

        int start = Math.Min(_box.SelectionStart, _box.SelectionEnd);
        int end = Math.Max(_box.SelectionStart, _box.SelectionEnd);

        // Past what is chosen now, so that stepping on moves on from the match that is already chosen.
        int index = forward
            ? EditorText.NextMatch(_found.Matches, fromCaret ? start : end)
            : EditorText.PreviousMatch(_found.Matches, start);
        Choose(_found.Matches[index]);
    }

    private void Choose(TextSpan match)
    {
        _box.Focus();
        SelectRange(match.Start, match.End);
        ShowCount();
    }

    private void ReplaceCurrent()
    {
        if (_readOnly || (_query.Text ?? string.Empty).Length == 0) return;

        Refresh();
        int start = Math.Min(_box.SelectionStart, _box.SelectionEnd);
        int length = Math.Abs(_box.SelectionEnd - _box.SelectionStart);
        int index = _found.Matches.FindIndex(m => m.Start == start && m.Length == length);

        // With nothing found chosen yet, the first press only goes to the first one.
        if (index < 0)
        {
            Step(forward: true, fromCaret: true);
            return;
        }

        string text = NormalizedText();
        TextSpan match = _found.Matches[index];
        string with = EditorText.Replacement(text, match, _query.Text ?? string.Empty, _replacement.Text ?? string.Empty, _caseSensitive, _regex);

        _box.Focus();
        SelectRange(match.Start, match.End);
        _box.SelectedText = with;

        RunFind(select: false);
        if (_found.Matches.Count == 0) return;

        SelectRange(match.Start + with.Length, match.Start + with.Length);
        Step(forward: true, fromCaret: true);
    }

    private void ReplaceEverywhere()
    {
        if (_readOnly || (_query.Text ?? string.Empty).Length == 0) return;

        string text = NormalizedText();
        (string result, int count, bool invalid) = EditorText.ReplaceAll(text, _query.Text ?? string.Empty, _replacement.Text ?? string.Empty, _caseSensitive, _regex);
        if (invalid)
        {
            _count.Text = L.T("Not a valid pattern");
            return;
        }

        if (count == 0)
        {
            ShowCount();
            return;
        }

        _box.Focus();
        SelectRange(0, (_box.Text ?? string.Empty).Length);
        _box.SelectedText = result;
        SelectRange(0, 0);
        RunFind(select: false);
        SetMessage(L.T("{0} replaced", count), problem: false);
    }

    #endregion

    #region Keys

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        bool primary = e.KeyModifiers == Primary;
        if (primary && e.Key == Key.S) _ = SaveAsync();
        else if (primary && e.Key == Key.F) ShowFind(replace: false);
        else if (primary && e.Key == Key.H && !OperatingSystem.IsMacOS()) ShowFind(replace: true);
        else if (OperatingSystem.IsMacOS() && e.KeyModifiers == (Primary | KeyModifiers.Alt) && e.Key == Key.F) ShowFind(replace: true);
        else if (primary && e.Key == Key.G) _ = GoToLineAsync();
        else if (e.Key == Key.F3 && (e.KeyModifiers == KeyModifiers.None || e.KeyModifiers == KeyModifiers.Shift) && _findBar.IsVisible) Step(forward: e.KeyModifiers == KeyModifiers.None);
        else if (e.Key == Key.Escape && _findBar.IsVisible) HideFind();
        else return;

        e.Handled = true;
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (_readOnly || e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;

        if (e.Key == Key.Tab)
        {
            if (e.KeyModifiers == KeyModifiers.Shift) Outdent();
            else InsertTab();
        }
        else if (e.Key == Key.Enter)
        {
            // The new line is made here, so that it is "\n" whatever the system calls a line break, and indented like the one above.
            string text = NormalizedText();
            Insert(EditorText.NewLineWithIndent(text, Math.Min(_box.SelectionStart, _box.SelectionEnd), _indent));
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        // Enter goes to the match and leaves the box, so that the match is seen chosen and typing goes into the text.
        RunFind(select: false);
        Step(forward: !e.KeyModifiers.HasFlag(KeyModifiers.Shift), fromCaret: true);
        e.Handled = true;
    }

    private void OnReplacementKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        ReplaceCurrent();
        e.Handled = true;
    }

    #endregion
}

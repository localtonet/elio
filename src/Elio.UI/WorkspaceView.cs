using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Elio.Connections;
using Elio.Localtonet;
using Elio.Core;
using Elio.Terminal;
using Elio.UI.Files;
using Elio.UI.Terminal;

namespace Elio.UI;

public abstract class PaneNode
{
    public SplitNode? Parent { get; internal set; }
}

/// <summary>One thing shown in a tab: a terminal or a file browser.</summary>
public abstract class ContentPane(HostEntry host) : PaneNode
{
    public HostEntry Host { get; } = host;

    internal WorkspaceTab Tab { get; set; } = null!;

    /// <summary>The workspace holding this pane right now. Tabs can move between windows.</summary>
    internal WorkspaceView Owner { get; set; } = null!;

    internal abstract Border Frame { get; }

    /// <summary>
    /// The card a pane is shown as: rounded, with a hairline around it that turns into a ring when the pane is
    /// the one being typed into.
    /// </summary>
    /// <remarks>
    /// Two borders, because the one that cuts the content to the rounded shape would cut the ring off too.
    /// </remarks>
    private protected static Border MakeFrame(Control content, IBrush? background) => new()
    {
        Margin = new Thickness(PaneMargin),
        BorderThickness = new Thickness(1),
        BorderBrush = ElioColors.Border,
        CornerRadius = new CornerRadius(ElioStyle.CardRadius),
        Background = background,
        Transitions = [new Avalonia.Animation.BrushTransition { Property = Border.BorderBrushProperty, Duration = ElioStyle.Fade }],
        Child = new Border { CornerRadius = new CornerRadius(ElioStyle.CardRadius - 1), ClipToBounds = true, Child = content },
    };

    /// <summary>The space around each pane. Two panes side by side are twice this apart, plus the divider.</summary>
    internal const double PaneMargin = 3;

    internal abstract void FocusContent();

    internal abstract Task ShutAsync();
}

/// <summary>The server's files beside this computer's, or beside another server's, in a tab of their own or next to a terminal.</summary>
public sealed class FilesPane : ContentPane
{
    public FilesView View { get; }

    /// <summary>The server shown on the left in place of this computer, if there is one.</summary>
    public HostEntry? Other { get; }

    internal override Border Frame { get; }

    internal FilesPane(HostEntry host, FilesView view, HostEntry? other = null) : base(host)
    {
        View = view;
        Other = other;
        Frame = MakeFrame(view, ElioColors.Chrome);
    }

    internal override void FocusContent() => View.Focus();

    internal override Task ShutAsync() => View.CloseAsync();
}

/// <summary>One terminal in a tab. Survives being moved between splits, tabs and windows.</summary>
public sealed class TerminalPane : ContentPane
{
    private const double BarHeight = 22;
    private static readonly Thickness TextInset = new(7, 5, 3, 2);

    private readonly Button _action = new() { Padding = new Thickness(14, 5), CornerRadius = new CornerRadius(ElioStyle.ControlRadius), HorizontalAlignment = HorizontalAlignment.Center, Background = ElioColors.Accent, Foreground = ElioColors.AccentText, Classes = { "primary" }, Tag = "pane-action" };
    private readonly Button _secondary = new() { Padding = new Thickness(14, 5), CornerRadius = new CornerRadius(ElioStyle.ControlRadius), HorizontalAlignment = HorizontalAlignment.Center, Tag = "pane-secondary" };
    private readonly TextBlock _statusText = new() { Foreground = ElioColors.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, LineHeight = 19, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _bar = new() { FontSize = ElioStyle.SmallFontSize, Foreground = ElioColors.TextMuted, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "pane-bar" };
    private readonly Border _status;
    private readonly TextBox _findBox = new() { Width = 200, MinHeight = 26, Padding = new Thickness(8, 3), FontSize = 12, CornerRadius = new CornerRadius(6), Tag = "find-box" };
    private readonly TextBlock _findCount = new() { FontSize = ElioStyle.SmallFontSize, Foreground = ElioColors.TextMuted, VerticalAlignment = VerticalAlignment.Center, MinWidth = 60, TextAlignment = TextAlignment.Center, Tag = "find-count" };
    private readonly Border _find;
    private Action? _onAction;
    private Action? _onSecondary;

    public TerminalView View { get; } = new();
    internal Grid Container { get; }
    internal override Border Frame { get; }

    /// <summary>What the pane is showing instead of a terminal, if anything.</summary>
    public string? StatusMessage => _status.IsVisible ? _statusText.Text : null;

    /// <summary>What the line under the terminal says.</summary>
    public string StatusBarText => _bar.Text ?? string.Empty;

    /// <summary>Whether this pane ignores input typed into the others while broadcasting.</summary>
    public bool LeftOutOfBroadcast { get; internal set; }

    /// <summary>How many times in a row the connection has been retried. Zero when it isn't being retried.</summary>
    internal int RetryAttempt { get; set; }

    internal CancellationTokenSource? RetryWait { get; set; }

    /// <summary>Stops a connection still being set up, such as one waiting for a browser approval.</summary>
    internal CancellationTokenSource? ConnectCancel { get; set; }

    internal IReadOnlyList<string> ForwardProblems { get; set; } = [];

    /// <summary>The folder a local shell should start in, when it is not the one its entry names.</summary>
    internal string? StartDirectory { get; set; }

    /// <summary>The folder the shell says it is in, once it has been seen to exist on this computer.</summary>
    internal string? CurrentDirectory { get; set; }

    /// <summary>The git repository that folder belongs to, if any.</summary>
    internal string? Repository { get; set; }

    /// <summary>
    /// How this pane gets its connection when it is not one of the kinds a saved entry describes,
    /// such as a tmux pane or a terminal someone else is sharing. Such panes are not restored at startup.
    /// </summary>
    internal Func<int, int, CancellationToken, Task<ITerminalTransport>>? OpenTransport { get; set; }

    /// <summary>The tmux session this pane shows a pane of, and which one.</summary>
    internal TmuxSession? Tmux { get; set; }

    internal string? TmuxPaneId { get; set; }

    /// <summary>What lets other people watch this terminal. Null for the kinds of pane that cannot be shared.</summary>
    internal SharedTransport? Shared { get; set; }

    /// <summary>The connection that carries this terminal's sharing over the internet, while it is shared that way.</summary>
    internal ShareRelayHost? Relay { get; set; }

    /// <summary>The name of the local shell when it runs in the background process, to find it again after a restart.</summary>
    internal string? ShellId { get; set; }

    /// <summary>That shell's connection, for ending it when the pane is closed on purpose.</summary>
    internal HostedShellTransport? Hosted { get; set; }

    /// <summary>The local shell's process, for seeing what runs under it. Zero when there is none.</summary>
    internal int ShellProcessId { get; set; }

    /// <summary>The AI tool running in this terminal, if one was recognised.</summary>
    internal string? Agent { get; set; }

    /// <summary>The last notification the running program sent, shown under the terminal.</summary>
    internal string? Note { get; set; }

    /// <summary>What the running program last said about its progress, to tell a task ending from one never started.</summary>
    internal ProgressState LastProgress { get; set; }

    internal override void FocusContent() => View.Focus();

    internal override async Task ShutAsync()
    {
        RetryWait?.Cancel();
        ConnectCancel?.Cancel();
        RetryAttempt = 0;

        // The room on the relay goes with the terminal it showed.
        if (Relay is ShareRelayHost relay)
        {
            Relay = null;
            await relay.DisposeAsync();
        }

        await View.CloseAsync();
    }

    internal TerminalPane(HostEntry host) : base(host)
    {
        _action.Click += (_, _) => _onAction?.Invoke();
        _secondary.Click += (_, _) => _onSecondary?.Invoke();
        _status = new Border
        {
            Background = ElioColors.Elevated,
            BorderBrush = ElioColors.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            BoxShadow = ElioStyle.PopoverShadow,
            Padding = new Thickness(22, 16),
            Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    _statusText,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Children = { _secondary, _action } },
                },
            },
        };
        _find = BuildFindBar();
        View.Margin = TextInset;
        Container = new Grid { Children = { View, _dropHint, _choices, _find, _status } };
        View.SuggestionsChanged += ShowChoices;
        // The line under the terminal is the foot of the same card, set off from it by a hairline.
        var bar = new Border { Background = ElioColors.Chrome, BorderBrush = ElioColors.Border, BorderThickness = new Thickness(0, 1, 0, 0), Height = BarHeight, Padding = new Thickness(10, 0), Child = _bar };
        DockPanel.SetDock(bar, Dock.Bottom);
        var layout = new DockPanel { Children = { bar } };
        if (AppServices.MobileLayout)
        {
            // Lowest of all, so it sits right on top of the phone's keyboard.
            var keys = new TerminalKeyRow(View);
            DockPanel.SetDock(keys, Dock.Bottom);
            layout.Children.Insert(0, keys);
        }

        layout.Children.Add(Container);
        Frame = MakeFrame(layout, null);
    }

    private Border BuildFindBar()
    {
        Button FindButton(string symbol, string tip, string tag, Action run)
        {
            // A name starting with M is one of the drawn icons. Anything else is shown as the text it is.
            Button button = ElioStyle.IconButton(symbol.StartsWith('M') ? ElioStyle.Icon(symbol, ElioColors.TextMuted, 14, 1.6) : symbol, tip, 24);
            button.FontSize = 12;
            button.CornerRadius = new CornerRadius(6);
            button.Tag = tag;
            button.Click += (_, _) => run();
            return button;
        }

        _findBox.TextChanged += (_, _) => RunFind(older: true);
        _findBox.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter) RunFind(older: !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            else if (e.Key == Key.Escape) HideFind();
            else return;

            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        return new Border
        {
            Background = ElioColors.Elevated,
            BorderBrush = ElioColors.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ElioStyle.CardRadius),
            BoxShadow = ElioStyle.PopoverShadow,
            Padding = new Thickness(5),
            Margin = new Thickness(0, 10, 18, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsVisible = false,
            Tag = "find-bar",
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Children =
                {
                    _findBox,
                    _findCount,
                    FindButton(".*", L.T("Read as a regular expression: on / off"), "find-regex", ToggleFindPattern),
                    FindButton(ElioStyle.IconUp, L.T("Older match"), "find-older", () => RunFind(older: true)),
                    FindButton(ElioStyle.IconDown, L.T("Newer match"), "find-newer", () => RunFind(older: false)),
                    FindButton(ElioStyle.IconClose, L.T("Close"), "find-close", HideFind),
                },
            },
        };
    }

    /// <summary>Opens the bar for searching this terminal's output, history included.</summary>
    // The mark on the button would need its own state. The count beside it says at once which way the text was read.
    private void ToggleFindPattern()
    {
        AppServices.Settings.FindWithRegex = !AppServices.Settings.FindWithRegex;
        AppServices.SaveSettings();
        View.Session?.Find(string.Empty);
        View.Session?.FindRegex(string.Empty);
        _findBox.PlaceholderText = AppServices.Settings.FindWithRegex ? L.T("Regular expression") : L.T("Text");
        RunFind(older: true);
    }

    // The commands on offer for the line being typed, just under the cursor.
    private readonly Border _choices = new()
    {
        Background = ElioColors.Elevated,
        BorderBrush = ElioColors.Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        BoxShadow = ElioStyle.PopoverShadow,
        Padding = new Thickness(4),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        MaxWidth = 520,
        IsVisible = false,
        Tag = "suggestions",
    };

    private const double ChoiceHeight = 24;

    private void ShowChoices()
    {
        IReadOnlyList<string> commands = View.SuggestionChoices;
        if (commands.Count == 0 || View.SuggestionAnchor is not Point anchor || View.TranslatePoint(anchor, Container) is not Point at)
        {
            _choices.IsVisible = false;
            return;
        }

        var rows = new StackPanel();
        for (int i = 0; i < commands.Count; i++)
        {
            int index = i;
            bool picked = i == View.SuggestionChoice;
            var row = new Border
            {
                Height = ChoiceHeight,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 0),
                Background = picked ? ElioColors.Selection : ElioColors.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = commands[i],
                    FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
                    FontSize = 12.5,
                    Foreground = ElioColors.Text,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            row.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                View.AcceptSuggestion(index);
            };
            rows.Children.Add(row);
        }

        rows.Children.Add(new TextBlock
        {
            Text = "Alt \u2191\u2193  \u00b7  \u2192",
            FontSize = 11,
            Foreground = ElioColors.TextFaint,
            Margin = new Thickness(8, 3, 8, 2),
        });
        _choices.Child = rows;

        // Under the line being typed, or above it when the bottom of the pane is too near.
        double height = (commands.Count * ChoiceHeight) + 30;
        double top = at.Y + 4;
        if (top + height > Container.Bounds.Height) top = Math.Max(0, at.Y - View.CellSize.Height - height - 4);
        double left = Math.Clamp(at.X, 0, Math.Max(0, Container.Bounds.Width - 240));
        _choices.Margin = new Thickness(left, top, 0, 0);
        _choices.IsVisible = true;
    }

    // Lit over the half of the pane a dragged server would open in.
    private readonly Border _dropHint = new()
    {
        Background = ElioColors.Selection,
        BorderBrush = ElioColors.Accent,
        BorderThickness = new Thickness(1.5),
        CornerRadius = new CornerRadius(8),
        Margin = new Thickness(4),
        IsHitTestVisible = false,
        IsVisible = false,
        Tag = "drop-hint",
    };

    /// <summary>Marks the side a dragged server would land on, or clears the mark.</summary>
    internal void ShowDropSide(DropSide? side)
    {
        _dropHint.IsVisible = side is not null;
        if (side is not DropSide where) return;

        bool sideways = where is DropSide.Left or DropSide.Right;
        _dropHint.Width = sideways ? Math.Max(0, (Container.Bounds.Width / 2) - 8) : double.NaN;
        _dropHint.Height = sideways ? double.NaN : Math.Max(0, (Container.Bounds.Height / 2) - 8);
        _dropHint.HorizontalAlignment = where == DropSide.Left ? HorizontalAlignment.Left : where == DropSide.Right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        _dropHint.VerticalAlignment = where == DropSide.Top ? VerticalAlignment.Top : where == DropSide.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
    }

    internal void ShowFind()
    {
        _find.IsVisible = true;
        _findBox.Focus();
        _findBox.SelectAll();
    }

    private void RunFind(bool older)
    {
        string text = _findBox.Text ?? string.Empty;
        bool pattern = AppServices.Settings.FindWithRegex;
        SearchResult found = (pattern ? View.Session?.FindRegex(text, older) : View.Session?.Find(text, older)) ?? default;
        _findCount.Text = text.Length == 0 ? string.Empty
            : found.Invalid ? L.T("Not a valid pattern")
            : found.TimedOut ? L.T("Took too long")
            : found.Total == 0 ? L.T("No matches") : L.T("{0} of {1}", found.Current, found.Total);
    }

    private void HideFind()
    {
        _find.IsVisible = false;
        View.Session?.Find(string.Empty);
        View.Session?.FindRegex(string.Empty);
        View.Focus();
    }

    internal void ShowStatus(string text, string? actionText = null, Action? action = null, string? secondaryText = null, Action? secondary = null)
    {
        _statusText.Text = text;
        _onAction = action;
        _onSecondary = secondary;
        _action.Content = actionText;
        _action.IsVisible = actionText is not null;
        _secondary.Content = secondaryText;
        _secondary.IsVisible = secondaryText is not null;
        _status.IsVisible = true;
    }

    internal void SetBar(string text, bool warning)
    {
        _bar.Text = text;
        _bar.Foreground = warning ? ElioColors.Danger : ElioColors.TextMuted;
    }

    internal void HideStatus() => _status.IsVisible = false;

    /// <summary>
    /// Fills the strip between the card's edge and the text with the terminal's own background, so the
    /// text does not start right at the rounded corner and the strip does not show as a frame.
    /// </summary>
    internal void MatchInset()
    {
        uint color = View.Appearance.Theme.Background;
        Container.Background = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
    }
}

public sealed class SplitNode : PaneNode
{
    public Orientation Orientation { get; internal set; }
    public PaneNode First { get; internal set; } = null!;
    public PaneNode Second { get; internal set; } = null!;

    /// <summary>The first side's share of the space.</summary>
    public double Ratio { get; internal set; } = 0.5;

    internal Grid? Visual { get; set; }
}

public sealed class WorkspaceTab
{
    public PaneNode Root { get; internal set; } = null!;
    public ContentPane Active { get; internal set; } = null!;

    /// <summary>The current pane, if it is a terminal.</summary>
    public TerminalPane? ActiveTerminal => Active as TerminalPane;
    internal Control? Visual { get; set; }
    /// <summary>The name the user gave the tab. Without one it is named after what it shows.</summary>
    public string? CustomTitle { get; internal set; }

    /// <summary>A shell working inside a git repository is named after it too, since that is what tells such tabs apart.</summary>
    /// <remarks>Files between two servers name both, in the order they are on screen.</remarks>
    public string Title => CustomTitle ?? Active switch
    {
        TerminalPane { Repository: string repository } => $"{Active.Host.Name} \u00b7 {repository}",
        FilesPane { Other: HostEntry other } => $"{other.Name} \u2194 {Active.Host.Name}",
        _ => Active.Host.Name,
    };

    /// <summary>The color the user marked the tab with, as 0xRRGGBB.</summary>
    public uint? Color { get; internal set; }

    /// <summary>Pinned tabs stay at the front and are not closed along with the others.</summary>
    public bool Pinned { get; internal set; }

    internal TabLayout Describe(PaneLayout root) => new() { Root = root, Title = CustomTitle, Color = Color, Pinned = Pinned };

    internal void Take(TabLayout saved) => (CustomTitle, Color, Pinned) = (saved.Title, saved.Color, saved.Pinned);

    /// <summary>The one pane filling the tab for now, with the others kept running behind it.</summary>
    public ContentPane? Zoomed { get; internal set; }

    /// <summary>A bell, a notification or a finished task in this tab while another tab was in front.</summary>
    public bool NeedsAttention { get; internal set; }

    /// <summary>The progress a program in this tab is reporting, if any is.</summary>
    /// <remarks>Several panes reporting at once are shown as one: their average, and an error if any has one.</remarks>
    public TerminalProgress Progress
    {
        get
        {
            var reports = Panes.Select(p => p.View.Session?.Progress ?? default).Where(p => p.State != ProgressState.None).ToList();
            if (reports.Count == 0) return default;

            var counted = reports.Where(p => p.State != ProgressState.Indeterminate).ToList();
            int percent = counted.Count == 0 ? 0 : (int)Math.Round(counted.Average(p => p.Percent));
            ProgressState state =
                reports.Any(p => p.State == ProgressState.Error) ? ProgressState.Error
                : reports.Any(p => p.State == ProgressState.Running) ? ProgressState.Running
                : reports.Any(p => p.State == ProgressState.Indeterminate) ? ProgressState.Indeterminate
                : ProgressState.Paused;
            return new TerminalProgress(state, percent);
        }
    }

    /// <summary>The title with what is going on in the tab in front of it.</summary>
    public string Label
    {
        get
        {
            TerminalProgress progress = Progress;
            string state = progress.State switch
            {
                ProgressState.Running => $"{progress.Percent}% ",
                ProgressState.Indeterminate => "\u2026 ",
                ProgressState.Paused => "\u2016 ",
                ProgressState.Error => "\u2715 ",
                _ => string.Empty,
            };
            string agent = Panes.Select(p => p.Agent).FirstOrDefault(a => a is not null) is string running ? $"{running} \u00b7 " : string.Empty;
            return (NeedsAttention ? "\u25cf " : string.Empty) + state + agent + Title;
        }
    }

    /// <summary>Typing in one pane reaches every pane of the tab.</summary>
    public bool Broadcast { get; internal set; }

    /// <summary>The terminals in this tab.</summary>
    public IEnumerable<TerminalPane> Panes => AllPanes.OfType<TerminalPane>();

    public IEnumerable<ContentPane> AllPanes => Walk(Root);

    private static IEnumerable<ContentPane> Walk(PaneNode node) => node switch
    {
        ContentPane pane => [pane],
        SplitNode split => Walk(split.First).Concat(Walk(split.Second)),
        _ => [],
    };
}

/// <summary>Connects Zmodem transfers in a terminal to the user: what to accept, what to send, how it is going.</summary>
internal sealed class PaneZmodemHost(TerminalPane pane) : IZmodemHost
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(120);

    private readonly Dictionary<Stream, string> _paths = [];
    private bool _acceptAll;
    private DateTime _lastProgress;

    private static Task<T> OnUi<T>(Func<Task<T>> action) => Dispatcher.UIThread.InvokeAsync(action);

    public async Task<Stream?> AcceptFileAsync(string name, long size, CancellationToken cancellation)
    {
        string safe = LocalNames.Safe(name);

        if (!_acceptAll)
        {
            string amount = size >= 0 ? FileText.Size(size) : L.T("size unknown");
            var (accept, forAll) = await OnUi(() => pane.Owner.Dialogs.AskZmodemFileAsync(safe, amount, ElioPaths.DownloadDirectory));
            _acceptAll = forAll && accept;
            if (!accept) return null;
        }

        Directory.CreateDirectory(ElioPaths.DownloadDirectory);
        string path = Unused(Path.Combine(ElioPaths.DownloadDirectory, safe));
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.Asynchronous);
        lock (_paths) _paths[file] = path;
        return file;
    }

    // A server must never be able to replace something the user already has.
    private static string Unused(string path)
    {
        string folder = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 1; File.Exists(path); i++) path = Path.Combine(folder, $"{stem} ({i}){extension}");
        return path;
    }

    public void FinishFile(Stream file, bool complete)
    {
        string path;
        lock (_paths)
        {
            path = _paths[file];
            _paths.Remove(file);
        }

        file.Dispose();
        if (!complete) File.Delete(path);
    }

    public Task<IReadOnlyList<string>> ChooseFilesAsync(CancellationToken cancellation) => OnUi(async () =>
    {
        WorkspaceView owner = pane.Owner;
        if (owner.PickLocalFiles is { } pick) return await pick();

        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { CanOpen: true } storage) return (IReadOnlyList<string>)[];

        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = L.T("Files to send to the server") });
        return (IReadOnlyList<string>)picked.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    });

    public void ShowProgress(string name, long done, long total)
    {
        if (DateTime.UtcNow - _lastProgress < ProgressInterval && done != total) return;
        _lastProgress = DateTime.UtcNow;

        string amount = total > 0 ? $"{FileText.Size(done)} / {FileText.Size(total)}" : FileText.Size(done);
        Dispatcher.UIThread.Post(() => pane.ShowStatus($"Zmodem: {LocalNames.Safe(name)}\n{amount}", L.T("Cancel"),() => pane.View.Session?.CancelZmodem()));
    }

    public void Finished(string? problem)
    {
        _acceptAll = false;
        Dispatcher.UIThread.Post(() =>
        {
            pane.HideStatus();
            if (problem is not null) _ = pane.Owner.Dialogs.ShowMessageAsync("Zmodem", problem);
        });
    }
}

/// <summary>Tabs of terminals, each divided into any number of nested panes.</summary>
public sealed partial class WorkspaceView : UserControl
{
    private const double SplitterSize = 2;
    private const double TabHeight = 24;

    // The room Windows takes at the right of the title bar for its four buttons: full screen, minimize, maximize and close.
    private const double CaptionButtonsWidth = 190;
    private const double DragStartDistance = 6;
    private const double TearOffDistance = 48;
    private const int MaxReconnectDelaySeconds = 30;
    private const int MaxNoteLength = 120;
    private const int MaxClosedTabs = 10;
    private const string PromptCommand = "PROMPT_COMMAND";
    private static readonly TimeSpan AgentCheckInterval = TimeSpan.FromSeconds(3);

    // The colors a tab can be marked with.
    private static readonly uint[] TabColors = [0xF7768E, 0xFF9E64, 0xE0AF68, 0x9ECE6A, 0x7DCFFF, 0x7AA2F7, 0xBB9AF7];
    private const double TabProgressHeight = 2;

    /// <summary>Every open window's workspace, for saving the layout.</summary>
    public static List<WorkspaceView> All { get; } = [];

    private readonly DialogHost _dialogs;
    private readonly List<WorkspaceTab> _tabs = [];
    private readonly TabStripPanel _tabStrip = new();
    private readonly Border _tabTrack;
    private readonly Panel _content = new();
    private readonly TextBlock _empty = new() { Text = AppServices.MobileLayout ? L.T("Tap a server in the list to connect.") : L.T("Double-click a server on the left to connect."), Foreground = ElioColors.TextMuted, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Control _emptyState;
    private readonly Dictionary<Guid, string> _sessionSecrets = [];
    private readonly SemaphoreSlim _hostKeyQuestion = new(1, 1);
    private ShortcutMap _shortcuts = new(AppServices.Settings.Shortcuts);
    private readonly TextBlock _broadcastText = new() { Foreground = ElioColors.AccentText, FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _broadcastBar;
    private readonly Border _updateBar = new() { IsVisible = false, Tag = "update-bar" };

    // Where the column of AI commands goes once it has been asked for.
    private readonly Border _aiHolder = new() { IsVisible = false };

    /// <summary>Whether the column that shows what AI tools run is open.</summary>
    public bool AiPanelOpen => _aiHolder.IsVisible;

    /// <summary>Opens the column beside the terminals that shows what AI tools run and what it printed.</summary>
    public void ShowAiPanel()
    {
        if (_aiHolder.Child is null)
        {
            var panel = new AiActivityPanel();
            panel.CloseRequested += () => _aiHolder.IsVisible = false;
            _aiHolder.Child = panel;
        }

        _aiHolder.IsVisible = true;
        _updateBar.IsVisible = false;
    }
    private WorkspaceTab? _current;
    private WorkspaceTab? _previous;
    private readonly DispatcherTimer _agentTimer;
    private bool _checkingAgents;
    private readonly List<TabLayout> _closedTabs = [];
    private bool _restoring;

    public IReadOnlyList<WorkspaceTab> Tabs => _tabs;

    public WorkspaceTab? CurrentTab => _current;

    /// <summary>Called when a tab should move to its own window.</summary>
    public Action<WorkspaceTab>? DetachTab { get; set; }

    /// <summary>Runs a shortcut that belongs to the window rather than the terminal.</summary>
    public Action<string>? RunCommand { get; set; }

    /// <summary>Asks which local files to send. Replaced in tests, where a system dialog can't be answered.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickLocalFiles { get; set; }

    internal DialogHost Dialogs => _dialogs;

    /// <summary>Looks up a saved server, for following jump hosts.</summary>
    public Func<Guid, HostEntry?> FindHost { get; set; } = _ => null;

    public WorkspaceView(DialogHost dialogs)
    {
        _dialogs = dialogs;
        All.Add(this);

        // The strip doubles as the title bar, so its empty space has to drag the window.
        // The tabs lie in a trough of their own, which is only as wide as they are: what is left of the bar
        // stays the bar itself, and that is what the drag below looks for.
        _tabTrack = new Border
        {
            Background = ElioColors.TabTrack,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _tabStrip,
        };
        var titleBar = new Border
        {
            Background = ElioColors.Window,
            Height = 38,
            Padding = new Thickness(AppServices.MobileLayout ? 46 : 6, 0, OperatingSystem.IsWindows() && !AppServices.MobileLayout ? CaptionButtonsWidth : 6, 0),
            Child = _tabTrack,
        };

        _emptyState = BuildWelcome();
        AcceptServersWhenEmpty();
        titleBar.PointerPressed += (_, e) =>
        {
            if (e.Source == titleBar && TopLevel.GetTopLevel(this) is Window window) window.BeginMoveDrag(e);
        };

        var stopBroadcast = new Button { Content = L.T("Stop broadcast"),Padding = new Thickness(10, 2), FontSize = ElioStyle.SmallFontSize, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Colors.White, 0.24), Foreground = ElioColors.AccentText, Tag = "broadcast-stop" };
        stopBroadcast.Click += (_, _) => ToggleBroadcast();
        _broadcastBar = new Border
        {
            Background = ElioColors.Danger,
            CornerRadius = new CornerRadius(ElioStyle.ControlRadius),
            Margin = new Thickness(2 * ContentPane.PaneMargin, 0, 2 * ContentPane.PaneMargin, ContentPane.PaneMargin),
            Padding = new Thickness(12, 4, 4, 4),
            IsVisible = false,
            Tag = "broadcast-bar",
            Child = new DockPanel { Children = { DockRight(stopBroadcast), _broadcastText } },
        };

        var layout = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        DockPanel.SetDock(_broadcastBar, Dock.Top);
        layout.Children.Add(titleBar);
        layout.Children.Add(_broadcastBar);
        DockPanel.SetDock(_updateBar, Dock.Top);
        layout.Children.Add(_updateBar);

        DockPanel.SetDock(_aiHolder, Dock.Right);
        layout.Children.Add(_aiHolder);

        // The panes bring half of the space around them, and this adds the other half at the window's edge.
        // The tab bar above already keeps its distance.
        layout.Children.Add(new Border { Padding = new Thickness(ContentPane.PaneMargin, 0, ContentPane.PaneMargin, ContentPane.PaneMargin), Child = _content });
        Content = new Border { Background = ElioColors.Window, Child = layout };

        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        AppServices.SettingsChanged += OnSettingsChanged;
        _agentTimer = new DispatcherTimer { Interval = AgentCheckInterval };
        _agentTimer.Tick += (_, _) => _ = CheckAgentsAsync();
        ShowCurrent();
    }

    /// <summary>
    /// Says, in a line above the terminals that takes nothing away from them, that a newer version can be had. It
    /// stays until it is closed or the page is opened, and asks for nothing.
    /// </summary>
    public void ShowUpdateNotice(string version, Uri page)
    {
        // The address came from the network. It is looked at again here, where it is about to be used.
        if (!UpdateCheck.IsReleasePage(page)) return;

        var open = new Button { Content = L.T("Open the download page"), Padding = new Thickness(10, 2), FontSize = ElioStyle.SmallFontSize, CornerRadius = new CornerRadius(6), Tag = "update-open" };
        Button dismiss = ElioStyle.IconButton(ElioStyle.Icon(ElioStyle.IconClose, ElioColors.TextMuted, 10, 2), L.T("Not now"), 22);
        dismiss.Margin = new Thickness(4, 0, 0, 0);
        dismiss.Tag = "update-dismiss";
        open.Click += async (_, _) =>
        {
            _updateBar.IsVisible = false;
            await AppServices.OpenUrlAsync(this, page);
        };
        dismiss.Click += (_, _) => _updateBar.IsVisible = false;

        _updateBar.Background = ElioColors.Elevated;
        _updateBar.BorderBrush = ElioColors.Border;
        _updateBar.BorderThickness = new Thickness(1);
        _updateBar.CornerRadius = new CornerRadius(ElioStyle.ControlRadius);
        _updateBar.Margin = new Thickness(2 * ContentPane.PaneMargin, 0, 2 * ContentPane.PaneMargin, ContentPane.PaneMargin);
        _updateBar.Padding = new Thickness(12, 4, 4, 4);
        _updateBar.Child = new DockPanel
        {
            Children =
            {
                DockRight(dismiss),
                DockRight(open),
                new TextBlock { Text = L.T("Elio {0} is available.", version), Foreground = ElioColors.Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        _updateBar.IsVisible = true;
    }

    /// <summary>
    /// Says something in the line above the terminals, with one thing to do about it. It takes nothing away from
    /// the terminals, asks for nothing, and stays until it is closed, acted on, or replaced by the next one.
    /// </summary>
    public void ShowNotice(string text, string actionText, Action action)
    {
        var act = new Button { Content = actionText, Padding = new Thickness(10, 2), FontSize = ElioStyle.SmallFontSize, CornerRadius = new CornerRadius(6), Tag = "notice-action" };
        Button dismiss = ElioStyle.IconButton(ElioStyle.Icon(ElioStyle.IconClose, ElioColors.TextMuted, 10, 2), L.T("Close"), 22);
        dismiss.Margin = new Thickness(4, 0, 0, 0);
        dismiss.Tag = "notice-dismiss";
        act.Click += (_, _) =>
        {
            _updateBar.IsVisible = false;
            action();
        };
        dismiss.Click += (_, _) => _updateBar.IsVisible = false;

        _updateBar.Background = ElioColors.Elevated;
        _updateBar.BorderBrush = ElioColors.Border;
        _updateBar.BorderThickness = new Thickness(1);
        _updateBar.CornerRadius = new CornerRadius(ElioStyle.ControlRadius);
        _updateBar.Margin = new Thickness(2 * ContentPane.PaneMargin, 0, 2 * ContentPane.PaneMargin, ContentPane.PaneMargin);
        _updateBar.Padding = new Thickness(12, 4, 4, 4);
        _updateBar.Child = new DockPanel
        {
            Children =
            {
                DockRight(dismiss),
                DockRight(act),
                new TextBlock { Text = text, Foreground = ElioColors.Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        _updateBar.IsVisible = true;
    }

    /// <summary>What the window shows before anything is open: the app's name and the few things worth doing first.</summary>
    private StackPanel BuildWelcome()
    {
        Border Choice(string icon, string text, string action, bool first)
        {
            var row = ElioStyle.Fading(new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(10, 8),
                Background = ElioColors.Transparent,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = "welcome-" + action,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        new Border
                        {
                            Width = 30,
                            Height = 30,
                            CornerRadius = new CornerRadius(8),
                            Background = first ? ElioColors.Accent : ElioColors.Control,
                            Child = ElioStyle.Icon(icon, first ? ElioColors.AccentText : ElioColors.Text, 16, 1.5),
                        },
                        new TextBlock { Text = text, Foreground = ElioColors.Text, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            });
            ElioStyle.HoverFill(row, () => ElioColors.Transparent);
            row.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                RunCommand?.Invoke(action);
            };
            return row;
        }

        var mark = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            BoxShadow = ElioStyle.PopoverShadow,
            Background = ElioColors.Accent,
            Child = ElioStyle.Mark(64, ElioColors.AccentText, ElioColors.AccentText),
        };

        _empty.FontSize = 13;
        _empty.Margin = new Thickness(0, 0, 0, 10);
        return new StackPanel
        {
            Spacing = 4,
            Width = 300,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                mark,
                new TextBlock { Text = "elio", FontSize = 26, FontWeight = FontWeight.Bold, FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"), Foreground = ElioColors.Text, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) },
                _empty,
                Choice(ElioStyle.IconTerminal, L.T("Local shell"), "local-shell", first: true),
                Choice(ElioStyle.IconPlus, L.T("Add server"), "add-server", first: false),
                Choice(ElioStyle.IconFolderPlus, L.T("Import OpenSSH config"), "import-ssh", first: false),
                Choice(ElioStyle.IconSearch, L.T("Command palette"), "palette", first: false),
            },
        };
    }

    private void OnSettingsChanged()
    {
        _shortcuts = new ShortcutMap(AppServices.Settings.Shortcuts);
        TerminalAppearance appearance = AppServices.Appearance;
        foreach (TerminalPane pane in _tabs.SelectMany(t => t.Panes))
        {
            pane.View.Appearance = appearance;
            pane.MatchInset();
            pane.View.Suggestions = AppServices.Settings.SuggestCommands;
        }
    }

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (_dialogs.IsVisible || !_shortcuts.Process(e, out string? action)) return;

        e.Handled = true;
        if (action is null) return;

        switch (action)
        {
            case "split-right": SplitActive(Orientation.Horizontal); break;
            case "split-down": SplitActive(Orientation.Vertical); break;
            case "close-pane": _ = CloseActivePaneAsync(); break;
            case "next-tab": Cycle(1); break;
            case "previous-tab": Cycle(-1); break;
            case "broadcast": ToggleBroadcast(); break;
            case "find": FindInActive(); break;
            case "reopen-tab": ReopenClosedTab(); break;
            case "last-tab": SelectPreviousTab(); break;
            case "zoom-pane": ToggleZoom(); break;
            case "clear": ClearActive(); break;
            case "group-tabs": GroupTabsByRepository(); break;
            case "previous-prompt": GoToPrompt(older: true); break;
            case "next-prompt": GoToPrompt(older: false); break;
            case "copy-output": _ = CopyLastOutputAsync(); break;
            case "history": _ = ShowHistoryAsync(); break;
            case "picture": if (_current?.ActiveTerminal is TerminalPane shown) _ = TakePictureAsync(shown); break;
            default: RunCommand?.Invoke(action); break;
        }
    }

    private void Cycle(int direction)
    {
        if (_tabs.Count < 2 || _current is null) return;
        Select(_tabs[(_tabs.IndexOf(_current) + direction + _tabs.Count) % _tabs.Count]);
    }

    private void LayoutChanged()
    {
        if (!_restoring) AppServices.SaveLayout();
    }

    /// <summary>Opens a server in a new tab.</summary>
    public async Task<TerminalPane?> OpenAsync(HostEntry host)
    {
        if (!await EnsureSecretsAsync(host)) return null;

        TerminalPane pane = CreatePane(host, interactive: true);
        AddTab(pane);
        LayoutChanged();
        return pane;
    }

    /// <summary>Opens several servers side by side in one new tab.</summary>
    public async Task OpenGridAsync(IReadOnlyList<HostEntry> hosts)
    {
        var ready = new List<HostEntry>();
        foreach (HostEntry host in hosts)
            if (await EnsureSecretsAsync(host)) ready.Add(host);
        if (ready.Count == 0) return;

        AddTab(BuildGrid(ready.Select(h => CreatePane(h, interactive: true)).ToList(), horizontal: true));
        LayoutChanged();
    }

    private static PaneNode BuildGrid(List<TerminalPane> panes, bool horizontal)
    {
        if (panes.Count == 1) return panes[0];

        int half = panes.Count / 2;
        PaneNode first = BuildGrid(panes[..half], !horizontal);
        PaneNode second = BuildGrid(panes[half..], !horizontal);
        var split = new SplitNode { Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical, First = first, Second = second, Ratio = (double)half / panes.Count };
        first.Parent = split;
        second.Parent = split;
        return split;
    }

    /// <summary>Types a command into the current pane, and into the others if the tab is broadcasting.</summary>
    public void TypeInActive(string command)
    {
        if (_current?.ActiveTerminal is not TerminalPane active) return;
        active.View.Type(command.ReplaceLineEndings("\r").TrimEnd('\r') + "\r");
        active.View.Focus();
    }

    /// <summary>Brings back the tab that was closed last, connecting again to what it showed.</summary>
    public void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0) return;

        TabLayout closed = _closedTabs[^1];
        _closedTabs.RemoveAt(_closedTabs.Count - 1);
        if (Import(closed.Root, FindHost) is not PaneNode root) return;

        AddTab(root).Take(closed);
        RebuildTabStrip();
        LayoutChanged();
    }

    /// <summary>Moves everything in one tab into another, beside or below what that one shows. Nothing is disconnected.</summary>
    public void MergeTabs(WorkspaceTab moved, WorkspaceTab into, Orientation orientation)
    {
        if (moved == into || !_tabs.Contains(moved) || !_tabs.Contains(into)) return;

        List<ContentPane> arriving = moved.AllPanes.ToList();
        RebuildVisual(moved);
        RebuildVisual(into);
        into.Zoomed = null;

        var split = new SplitNode { Orientation = orientation, First = into.Root, Second = moved.Root };
        into.Root.Parent = split;
        moved.Root.Parent = split;
        into.Root = split;
        foreach (ContentPane pane in arriving) pane.Tab = into;

        _tabs.Remove(moved);
        if (_previous == moved) _previous = null;
        Select(into);
        LayoutChanged();
    }

    /// <summary>Goes back to the tab that was in front before this one.</summary>
    public void SelectPreviousTab()
    {
        if (_previous is WorkspaceTab previous && _tabs.Contains(previous)) Select(previous);
    }

    /// <summary>Lets the current pane fill its tab, or puts it back among the others.</summary>
    public void ToggleZoom()
    {
        if (_current is not WorkspaceTab tab || !tab.AllPanes.Skip(1).Any()) return;

        CaptureRatios(tab.Root);
        ContentPane? zoomed = tab.Zoomed is null ? tab.Active : null;
        RebuildVisual(tab);
        tab.Zoomed = zoomed;
        ShowCurrent();
    }

    /// <summary>Empties the current pane's screen and history.</summary>
    public void ClearActive() => _current?.ActiveTerminal?.View.Session?.Clear();

    /// <summary>Closes every tab but this one.</summary>
    public async Task CloseOtherTabsAsync(WorkspaceTab keep)
    {
        foreach (WorkspaceTab tab in _tabs.Where(t => t != keep && !t.Pinned).ToList()) await CloseTabAsync(tab);
    }

    private async Task RenameTabAsync(WorkspaceTab tab)
    {
        if (await _dialogs.AskTextAsync(L.T("Rename tab"), L.T("Tab name"), tab.Title, L.T("Rename")) is not string name) return;

        // Giving it the name it would have anyway means going back to following what it shows.
        tab.CustomTitle = null;
        tab.CustomTitle = name == tab.Active.Host.Name || name == tab.Title ? null : name;
        RebuildTabStrip();
        LayoutChanged();
    }

    /// <summary>Opens the search bar of the current pane.</summary>
    public void FindInActive() => _current?.ActiveTerminal?.ShowFind();

    /// <summary>Sends typing in the current tab to all of its panes, or stops doing that.</summary>
    public void ToggleBroadcast()
    {
        if (_current is not WorkspaceTab tab) return;
        tab.Broadcast = !tab.Broadcast;
        UpdateActiveFrame(tab);
        tab.Active.FocusContent();
    }

    private void ToggleLeftOut(TerminalPane pane)
    {
        pane.LeftOutOfBroadcast = !pane.LeftOutOfBroadcast;
        UpdateActiveFrame(pane.Tab);
    }

    private static IEnumerable<TerminalSession> InputTargetsFor(TerminalPane pane)
    {
        WorkspaceTab tab = pane.Tab;
        IEnumerable<TerminalPane> panes = tab.Broadcast && !pane.LeftOutOfBroadcast ? tab.Panes.Where(p => !p.LeftOutOfBroadcast) : [pane];
        foreach (TerminalPane target in panes)
            if (target.View.Session is TerminalSession session) yield return session;
    }

    /// <summary>Starts or stops saving the current pane's output to a file.</summary>
    public void ToggleOutputLog()
    {
        if (_current?.ActiveTerminal is TerminalPane active) ToggleOutputLog(active);
    }

    private void ToggleOutputLog(TerminalPane pane)
    {
        if (pane.View.Session is not TerminalSession session) return;

        try
        {
            PrivateFiles.CreateDirectory(ElioPaths.LogDirectory);
            string safeName = string.Concat(pane.Host.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            session.ToggleLog(Path.Combine(ElioPaths.LogDirectory, $"{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
            UpdateBar(pane);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = _dialogs.ShowMessageAsync(L.T("Could not save output"), ex.Message);
        }
    }

    /// <summary>Checks how far a connection to the current pane's server gets.</summary>
    public Task DiagnoseActiveAsync() => _current is WorkspaceTab tab ? DiagnoseAsync(tab.Active.Host) : Task.CompletedTask;

    private async Task DiagnoseAsync(HostEntry host)
    {
        if (host.Kind is HostKind.Local or HostKind.Serial)
        {
            await _dialogs.ShowMessageAsync(L.T("Connection diagnostics"), L.T("Diagnostics are for connections made over the network."));
            return;
        }

        HostEntry first;
        try
        {
            first = PathOf(host)[0];
        }
        catch (InvalidOperationException ex)
        {
            await _dialogs.ShowMessageAsync(L.T("Connection diagnostics"), ex.Message);
            return;
        }

        List<DiagnosticStep> steps = await ConnectionDiagnostics.RunAsync(first.Address, Resolve(first).Port, CancellationToken.None, expectSsh: first.Kind == HostKind.Ssh);
        string report = string.Join('\n', steps.Select(s => $"{(s.Passed ? "✓" : "✕")}  {s.Name}: {s.Detail}"));
        if (first != host) report += "\n\n" + L.T("This result is for the first jump host, {0}.", first.Name);
        await _dialogs.ShowMessageAsync(L.T("Connection diagnostics: {0}", first.Address), report);
    }

    /// <summary>Runs a command without opening a shell, for showing results from many servers.</summary>
    public Task<CommandResult> RunOnHostAsync(HostEntry host, string command, CancellationToken cancellation)
    {
        try
        {
            if (host.Kind != HostKind.Ssh)
                return Task.FromResult(new CommandResult(null, string.Empty, string.Empty, TimeSpan.Zero, L.T("Commands can only be run on SSH servers.")));

            if (host.Localtonet)
            {
                // Approving in the browser for every server of a batch would bury the user in tabs.
                return KnownSecret(host) is null
                    ? Task.FromResult(new CommandResult(null, string.Empty, string.Empty, TimeSpan.Zero, L.T("No access key is saved for {0}. Without a key, every connection needs approval in the browser.", host.Name)))
                    : RunOnDeviceAsync(host, command, cancellation);
            }

            List<HostEntry> path = PathOf(host);
            if (path.FirstOrDefault(NeedsSecret) is HostEntry missing)
                return Task.FromResult(new CommandResult(null, string.Empty, string.Empty, TimeSpan.Zero, L.T("No password is saved for {0}. Connect once first.", missing.Name)));

            return SshCommandRunner.RunAsync(EndpointsFor(path), command, IsTrusted, cancellation);
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult(new CommandResult(null, string.Empty, string.Empty, TimeSpan.Zero, ex.Message));
        }
    }

    private WorkspaceTab AddTab(PaneNode root)
    {
        var tab = new WorkspaceTab { Root = root, Active = FirstPane(root) };
        foreach (ContentPane pane in tab.AllPanes)
        {
            pane.Tab = tab;
            pane.Owner = this;
        }

        _tabs.Add(tab);
        Select(tab);
        return tab;
    }

    private static ContentPane FirstPane(PaneNode node) => node is SplitNode split ? FirstPane(split.First) : (ContentPane)node;

    /// <summary>Divides the current pane, opening the same server beside it.</summary>
    public void SplitActive(Orientation orientation)
    {
        if (_current is not WorkspaceTab tab || TmuxSplits(tab.Active, orientation)) return;

        // A second shell beside the first starts where the first one is working.
        ContentPane added = CreatePane(tab.Active.Host, interactive: true, (tab.Active as TerminalPane)?.CurrentDirectory);
        InsertBeside(tab, added, orientation);
    }

    /// <summary>Opens a server's files in a new tab.</summary>
    public async Task<FilesPane?> OpenFilesAsync(HostEntry host)
    {
        if (!await HasFilesAsync(host) || !await EnsureSecretsAsync(host)) return null;

        FilesPane pane = CreateFilesPane(host);
        AddTab(pane);
        LayoutChanged();
        return pane;
    }

    /// <summary>Opens the current server's files next to what is on screen.</summary>
    public async Task<FilesPane?> OpenFilesBesideAsync()
    {
        if (_current is not WorkspaceTab tab || !await HasFilesAsync(tab.Active.Host) || !await EnsureSecretsAsync(tab.Active.Host)) return null;

        FilesPane pane = CreateFilesPane(tab.Active.Host);
        InsertBeside(tab, pane, Orientation.Horizontal);
        return pane;
    }

    // The file panel talks SFTP, which only an SSH server has.
    private async Task<bool> HasFilesAsync(HostEntry host)
    {
        if (host.Kind == HostKind.Ssh) return true;
        await _dialogs.ShowMessageAsync(L.T("Files"), L.T("The file panel only opens for SSH servers."));
        return false;
    }

    /// <summary>Opens two servers' files side by side in a new tab, to copy straight from one to the other.</summary>
    /// <param name="host">The server on the right, where a server's files always are.</param>
    /// <param name="other">The server on the left, where this computer's files usually are.</param>
    public async Task<FilesPane?> OpenFilesBetweenAsync(HostEntry host, HostEntry other)
    {
        if (!await HasFilesAsync(host) || !await HasFilesAsync(other) || !await EnsureSecretsAsync(host) || !await EnsureSecretsAsync(other)) return null;

        FilesPane pane = CreateFilesPane(host, other: other);
        AddTab(pane);
        LayoutChanged();
        return pane;
    }

    /// <summary>The server the current pane belongs to, if a pane is open.</summary>
    public HostEntry? ActiveHost => _current?.Active.Host;

    /// <summary>Puts a server's files on the left of a file pane, where this computer's were, keeping the right side where it is.</summary>
    private async Task ShowServerOnLeftAsync(FilesPane pane, HostEntry other)
    {
        if (!await HasFilesAsync(other) || !await CanCloseAsync([pane]) || !await EnsureSecretsAsync(other)) return;

        // The pane or its tab may have been closed while a question was open.
        WorkspaceTab tab = pane.Tab;
        if (!_tabs.Contains(tab) || !tab.AllPanes.Contains(pane)) return;

        FilesPane replacement = CreateFilesPane(pane.Host, other: other, path: pane.View.Remote.CurrentPath is { Length: > 0 } path ? path : null);
        replacement.Tab = tab;
        replacement.Parent = pane.Parent;
        ReplaceChild(tab, pane, replacement);
        if (tab.Active == pane) tab.Active = replacement;
        if (tab.Zoomed == pane) tab.Zoomed = replacement;
        RebuildVisual(tab);
        ShowCurrent();
        LayoutChanged();
        await pane.ShutAsync();
    }

    private FilesPane CreateFilesPane(HostEntry host, bool interactive = true, HostEntry? other = null, string? path = null)
    {
        FilesView? view = null;
        void Report(string text, Uri? url) => Dispatcher.UIThread.Post(() => view?.Remote.ShowNotice(text));
        void ReportOther(string text, Uri? url) => Dispatcher.UIThread.Post(() => view?.Local.ShowNotice(text));

        var server = new FileServer(host.Name, cancellation => ConnectFilesAsync(host, Report, cancellation));
        view = other is null
            ? new FilesView(server.Connect, _dialogs)
            : new FilesView(server, new FileServer(other.Name, cancellation => ConnectFilesAsync(other, ReportOther, cancellation)), _dialogs, sameServer: other.Id == host.Id);
        var pane = new FilesPane(host, view, other) { Owner = this };
        view.GotFocus += (_, _) => pane.Owner.MakeActive(pane);

        // A server dragged from the list onto the left side takes that side over.
        view.Local.AcceptsServers = true;
        view.Local.ServerDropped += dropped => _ = pane.Owner.ShowServerOnLeftAsync(pane, dropped);

        // Restored tabs shouldn't each stop the app to ask for a password.
        _ = view.StartAsync(interactive || MissingSecrets(host).Count == 0, interactive || other is null || MissingSecrets(other).Count == 0, path);
        return pane;
    }

    private void InsertBeside(WorkspaceTab tab, ContentPane added, Orientation orientation)
    {
        ContentPane existing = tab.Active;
        added.Tab = tab;
        added.Owner = this;
        tab.Zoomed = null;

        var split = new SplitNode { Orientation = orientation, First = existing, Second = added, Parent = existing.Parent };
        ReplaceChild(tab, existing, split);
        existing.Parent = split;
        added.Parent = split;
        tab.Active = added;
        RebuildVisual(tab);
        ShareEvenly(split);
        ShowCurrent();
        LayoutChanged();
    }

    private static void ReplaceChild(WorkspaceTab tab, PaneNode old, PaneNode replacement)
    {
        if (old.Parent is not SplitNode parent) tab.Root = replacement;
        else if (parent.First == old) parent.First = replacement;
        else parent.Second = replacement;
    }

    public async Task CloseActivePaneAsync()
    {
        if (_current is WorkspaceTab tab) await ClosePaneAsync(tab, tab.Active);
    }

    private async Task<bool> CanCloseAsync(IEnumerable<ContentPane> panes)
    {
        int running = panes.OfType<FilesPane>().SelectMany(p => p.View.Queue.Jobs).Count(j => !j.IsFinished);
        return running == 0 || await _dialogs.ConfirmAsync(L.T("Transfer in progress"), L.T("{0} transfers will be stopped. Unfinished files can be resumed later.", running), L.T("Close"), danger: true);
    }

    private async Task ClosePaneAsync(WorkspaceTab tab, ContentPane pane)
    {
        if (TmuxCloses(pane)) return;
        if (!await CanCloseAsync([pane])) return;

        if (pane.Parent is SplitNode parent)
        {
            tab.Zoomed = null;
            PaneNode survivor = parent.First == pane ? parent.Second : parent.First;
            ReplaceChild(tab, parent, survivor);
            survivor.Parent = parent.Parent;
            tab.Active = tab.AllPanes.First();
            RebuildVisual(tab);
            ShowCurrent();
        }
        else
        {
            RemoveTab(tab);
        }

        LayoutChanged();
        await EndKeptShellAsync(pane);
        await pane.ShutAsync();
    }


    private void RemoveTab(WorkspaceTab tab)
    {
        int index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        if (_current == tab) _current = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
        ShowCurrent();
    }

    private async Task CloseTabAsync(WorkspaceTab tab)
    {
        if (TmuxClosesTab(tab)) return;

        List<ContentPane> panes = tab.AllPanes.ToList();
        if (!await CanCloseAsync(panes)) return;

        CaptureRatios(tab.Root);
        _closedTabs.Add(tab.Describe(Export(tab.Root)));
        if (_closedTabs.Count > MaxClosedTabs) _closedTabs.RemoveAt(0);

        RemoveTab(tab);
        LayoutChanged();
        foreach (ContentPane pane in panes)
        {
            await EndKeptShellAsync(pane);
            await pane.ShutAsync();
        }
    }

    /// <summary>Ends every connection in this window.</summary>
    public async Task CloseAllAsync()
    {
        All.Remove(this);
        AppServices.SettingsChanged -= OnSettingsChanged;
        _agentTimer.Stop();
        foreach (ContentPane pane in _tabs.SelectMany(t => t.AllPanes).ToList())
        {
            // Shells are only kept for tabs that will come back.
            if (!AppServices.Settings.RestoreSessions) await EndKeptShellAsync(pane);
            await pane.ShutAsync();
        }
    }

    /// <summary>Takes a tab out of this window without touching its connections.</summary>
    public WorkspaceTab Release(WorkspaceTab tab)
    {
        CaptureRatios(tab.Root);
        RemoveTab(tab);
        return tab;
    }

    /// <summary>Shows a tab that came from another window.</summary>
    public void Adopt(WorkspaceTab tab)
    {
        foreach (ContentPane pane in tab.AllPanes)
        {
            pane.Owner = this;
            if (pane is TerminalPane terminal) ConfigurePaste(terminal);
        }

        _tabs.Add(tab);
        Select(tab);
        LayoutChanged();
    }

    public void Select(WorkspaceTab tab)
    {
        if (_current != tab) _previous = _current;
        _current = tab;
        tab.NeedsAttention = false;
        ShowCurrent();
    }

    /// <summary>Puts a tab at a new position in the strip.</summary>
    public void MoveTab(WorkspaceTab tab, int index)
    {
        _tabs.Remove(tab);
        _tabs.Insert(Math.Clamp(index, 0, _tabs.Count), tab);
        RebuildTabStrip();
        LayoutChanged();
    }

    /// <summary>Describes the open tabs for saving.</summary>
    public IEnumerable<TabLayout> ExportTabs()
    {
        foreach (WorkspaceTab tab in _tabs)
        {
            CaptureRatios(tab.Root);
            yield return tab.Describe(Export(tab.Root));
        }
    }

    private static PaneLayout Export(PaneNode node) => node switch
    {
        SplitNode split => new PaneLayout { Horizontal = split.Orientation == Orientation.Horizontal, Ratio = split.Ratio, First = Export(split.First), Second = Export(split.Second) },
        TerminalPane { OpenTransport: not null } => new PaneLayout(),
        ContentPane pane => new PaneLayout { HostId = pane.Host.Id, Files = pane is FilesPane, OtherHostId = (pane as FilesPane)?.Other?.Id, Directory = (pane as TerminalPane)?.CurrentDirectory, ShellId = (pane as TerminalPane)?.ShellId },
        _ => new PaneLayout(),
    };

    /// <summary>Brings back saved tabs. Servers whose password is unknown wait for a click rather than prompting one by one.</summary>
    public void Restore(SessionLayout layout, Func<Guid, HostEntry?> findHost)
    {
        _restoring = true;
        foreach (TabLayout saved in layout.Tabs)
            if (Import(saved.Root, findHost) is PaneNode root) AddTab(root).Take(saved);
        _restoring = false;
        EndForgottenShells();
    }

    /// <summary>
    /// Adds the tabs of a saved workspace to the ones already open. They connect as restored tabs do: a server whose
    /// password is unknown waits for a click. Returns how many tabs were opened.
    /// </summary>
    public int OpenTabs(IEnumerable<TabLayout> tabs, Func<Guid, HostEntry?> findHost)
    {
        int opened = 0;
        _restoring = true;
        try
        {
            foreach (TabLayout saved in tabs)
            {
                if (Import(saved.Root, findHost) is not PaneNode root) continue;
                AddTab(root).Take(saved);
                opened++;
            }
        }
        finally
        {
            _restoring = false;
        }

        if (opened == 0) return 0;

        // The names, colors and pins were put on after each tab was drawn, and nothing was saved while they came in.
        RebuildTabStrip();
        LayoutChanged();
        return opened;
    }

    private PaneNode? Import(PaneLayout saved, Func<Guid, HostEntry?> findHost)
    {
        if (saved.First is null || saved.Second is null)
        {
            HostEntry? host = saved.HostId is Guid id ? findHost(id) : null;
            if (!saved.Files) return host is null ? null : CreatePane(host, interactive: false, saved.Directory, shellId: saved.ShellId);

            // Of two servers' files, one may have been deleted since. The other's then open the usual way.
            HostEntry? other = saved.OtherHostId is Guid otherId && findHost(otherId) is { Kind: HostKind.Ssh } found ? found : null;
            return host is not null ? CreateFilesPane(host, interactive: false, other)
                : other is not null ? CreateFilesPane(other, interactive: false)
                : null;
        }

        PaneNode? first = Import(saved.First, findHost);
        PaneNode? second = Import(saved.Second, findHost);
        if (first is null || second is null) return first ?? second;

        var split = new SplitNode { Orientation = saved.Horizontal ? Orientation.Horizontal : Orientation.Vertical, First = first, Second = second, Ratio = saved.Ratio };
        first.Parent = split;
        second.Parent = split;
        return split;
    }

    private string? KnownSecret(HostEntry host) => SecretOf(host.Id);

    // What the vault holds under an id, or what was typed this session and not stored. The id is a server's, an
    // identity's, or the one a proxy password is filed under.
    private string? SecretOf(Guid id) =>
        _sessionSecrets.TryGetValue(id, out string? known) ? known : AppServices.Vault.Get(id);

    /// <summary>What a server connects with, its group's defaults and its identity taken into account.</summary>
    private static ResolvedHost Resolve(HostEntry host) => ConnectionResolver.Resolve(host, AppServices.Library);

    private ResolvedLogin LoginOf(HostEntry host) => ConnectionResolver.Login(Resolve(host), SecretOf);

    // An identity's key turned out to need a passphrase. It belongs with the key, so it goes into the identity's
    // entry in the vault, where every server that uses the identity finds it.
    private void RememberPassphrase(Identity identity, SecretAnswer answer)
    {
        IdentitySecret secret = IdentitySecret.Read(SecretOf(identity.Id)) ?? new IdentitySecret();
        secret.Passphrase = answer.Secret;
        string stored = secret.Write();
        _sessionSecrets[identity.Id] = stored;
        if (answer.Remember && AppServices.Vault.IsAvailable) AppServices.Vault.Set(identity.Id, stored);
    }

    private void Remember(HostEntry host, SecretAnswer answer)
    {
        _sessionSecrets[host.Id] = answer.Secret;
        if (answer.Remember && AppServices.Vault.IsAvailable) AppServices.Vault.Set(host.Id, answer.Secret);
    }

    private void Forget(HostEntry host)
    {
        _sessionSecrets.Remove(host.Id);
        AppServices.Vault.Remove(host.Id);
    }

    private async Task<bool> AskSecretAsync(HostEntry host, string message)
    {
        string note = AppServices.Vault.IsAvailable ? message : L.T("{0}. This system has no vault yet, so it is only remembered for this session.", message);
        SecretAnswer? answer = await _dialogs.AskSecretAsync($"{Resolve(host).User}@{host.Address}", note, AppServices.Vault.IsAvailable);
        if (answer is null) return false;
        Remember(host, answer);
        return true;
    }

    private bool NeedsSecret(HostEntry host) => host.Kind == HostKind.Ssh && !host.Localtonet && LoginOf(host).NeedsSecret;

    private async Task<CommandResult> RunOnDeviceAsync(HostEntry device, string command, CancellationToken cancellation)
    {
        try
        {
            return await ConnectDeviceAsync(device, null, (path, policy) => SshCommandRunner.RunAsync(path, command, policy, cancellation), cancellation);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new CommandResult(null, string.Empty, string.Empty, TimeSpan.Zero, ex.Message);
        }
    }

    /// <summary>
    /// Reaches a Localtonet device. With a saved access key it connects straight away; without one, the owner
    /// approves this connection in the browser, the same as `localtonet ssh`. The device's key is checked
    /// against the one Localtonet recorded when it was paired, so there is nothing to ask about here.
    /// </summary>
    /// <param name="report">Where to show progress. Null when no browser approval should be offered.</param>
    private async Task<T> ConnectDeviceAsync<T>(HostEntry device, Action<string, Uri?>? report, Func<IReadOnlyList<SshEndpoint>, HostKeyPolicy, Task<T>> connect, CancellationToken cancellation)
    {
        LocaltonetAccount account = AppServices.Localtonet;

        void NeedsApproval(Uri url)
        {
            if (report is null) throw new UnauthorizedAccessException(L.T("The access key for this device is not valid."));
            report(L.T("Approve this connection in the browser. To skip approval each time, add an access key to the device."), url);
            _ = Dispatcher.UIThread.InvokeAsync(() => AppServices.OpenUrlAsync(this, url));
        }

        DeviceSession session;
        try
        {
            session = await LocaltonetConnect.RequestAsync(account.Api, device.Address, KnownSecret(device), NeedsApproval, cancellation);
        }
        catch (InvalidAccessKeyException) when (report is not null)
        {
            Forget(device);
            session = await LocaltonetConnect.RequestAsync(account.Api, device.Address, null, NeedsApproval, cancellation);
        }

        report?.Invoke(L.T("Connecting to device {0}...", device.Name), null);
        LocaltonetLink link = await LocaltonetLink.OpenAsync(session, cancellation);
        try
        {
            return await connect([link.Endpoint], link.IsDeviceKey);
        }
        catch
        {
            await link.DisposeAsync();
            throw;
        }
    }

    /// <summary>The servers to log in to, jump hosts first and the server itself last.</summary>
    /// <exception cref="InvalidOperationException">A jump host was deleted, or jump hosts point at each other.</exception>
    private List<HostEntry> PathOf(HostEntry host)
    {
        var path = new List<HostEntry>();
        var visiting = new HashSet<Guid>();

        void Visit(HostEntry current)
        {
            if (!visiting.Add(current.Id)) throw new InvalidOperationException(L.T("{0} is in its own jump path.", current.Name));
            if (current.Kind != HostKind.Ssh)
            {
                if (current != host) throw new InvalidOperationException(L.T("{0} is not an SSH server and cannot be used as a jump host.", current.Name));
                path.Add(current);
                return;
            }

            if (current.Localtonet)
            {
                if (current != host) throw new InvalidOperationException(L.T("Localtonet devices cannot be used as jump hosts yet."));
                path.Add(current);
                return;
            }

            foreach (Guid id in Resolve(current).JumpHosts)
                Visit(FindHost(id) ?? throw new InvalidOperationException(L.T("The jump host set for {0} no longer exists.", current.Name)));

            visiting.Remove(current.Id);
            path.Add(current);
        }

        Visit(host);
        return path;
    }

    private List<HostEntry> MissingSecrets(HostEntry host)
    {
        try
        {
            return PathOf(host).Where(NeedsSecret).ToList();
        }
        catch (InvalidOperationException)
        {
            // The connection attempt reports a broken path inside the pane.
            return [];
        }
    }

    private async Task<bool> EnsureSecretsAsync(HostEntry host)
    {
        foreach (HostEntry step in MissingSecrets(host))
            if (!await AskSecretAsync(step, L.T("Password"))) return false;
        return true;
    }

    // Every connection this window makes gets its path here: a terminal, the file panel, Mosh's login, a command
    // run on many servers. The resolver decides what each server logs in with, and which proxy the path starts through.
    private List<SshEndpoint> EndpointsFor(IReadOnlyList<HostEntry> path) =>
        SshEndpoints.ForPath(path.Select(Resolve).ToList(), SecretOf);

    private static bool IsTrusted(SshEndpoint endpoint, HostKeyInfo key) =>
        AppServices.KnownHosts.Check(endpoint.Host, endpoint.Port, key.Fingerprint) == HostKeyStatus.Trusted;

    private TerminalPane CreatePane(HostEntry host, bool interactive, string? directory = null, Func<int, int, CancellationToken, Task<ITerminalTransport>>? open = null, string? shellId = null)
    {
        var pane = new TerminalPane(host) { Owner = this, StartDirectory = directory, OpenTransport = open, ShellId = shellId };
        pane.View.Appearance = AppServices.Appearance;
        pane.MatchInset();
        pane.View.History = AppServices.History;
        pane.View.HistoryScope = host.Id.ToString("N");
        AppServices.SeedHistory(host);
        pane.View.Suggestions = AppServices.Settings.SuggestCommands;
        pane.View.InputTargets = () => InputTargetsFor(pane);
        pane.View.ZmodemHost = new PaneZmodemHost(pane);
        ConfigurePaste(pane);
        AcceptServers(pane);
        AddCloseButton(pane);

        pane.View.ConnectFailed += message => pane.Owner.OnConnectFailed(pane, message);
        pane.View.SessionEnded += () => pane.Owner.OnSessionEnded(pane);
        pane.View.SessionStarted += session => OnSessionStarted(pane, session);
        pane.View.GridChanged += () => UpdateBar(pane);
        pane.View.GotFocus += (_, _) => pane.Owner.MakeActive(pane);
        pane.View.LinkRequested += link => _ = AppServices.OpenUrlAsync(pane.Owner, link);
        pane.View.ZoomRequested += factor =>
        {
            AppServices.Settings.FontSize = Math.Clamp(Math.Round(AppServices.Settings.FontSize * factor), 8, 32);
            AppServices.SaveSettings();
        };

        // A Windows shell reads paths its own way, unless it is one of the Unix shells that run there.
        bool windowsShell = host.Kind == HostKind.Local && OperatingSystem.IsWindows()
            && !host.Address.Contains("bash", StringComparison.OrdinalIgnoreCase) && !host.Address.Contains("wsl", StringComparison.OrdinalIgnoreCase);

        // Each of the two Windows shells has a way of its own to write a path safely. Told apart by the program
        // the entry starts; one that is neither gets the form both of them read the same way.
        string program = host.Address;
        pane.View.QuotePath = !windowsShell ? ShellQuote.Posix
            : program.Contains("powershell", StringComparison.OrdinalIgnoreCase) || program.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ? ShellQuote.PowerShell
            : program.Contains("cmd", StringComparison.OrdinalIgnoreCase) ? ShellQuote.Cmd
            : ShellQuote.Windows;
        var menu = new List<MenuItem>
        {
            MenuItemFor(L.T("Copy"), () => _ = pane.View.CopyAsync()),
            MenuItemFor(L.T("Paste"), () => _ = pane.View.PasteAsync()),
            MenuItemFor(L.T("Split right"), () => pane.Owner.SplitActive(Orientation.Horizontal)),
            MenuItemFor(L.T("Split down"), () => pane.Owner.SplitActive(Orientation.Vertical)),
        };
        menu.Add(MenuItemFor(L.T("Find in output"), pane.ShowFind));
        if (host.Kind == HostKind.Ssh) menu.Add(MenuItemFor(L.T("Open files beside"), () => _ = pane.Owner.OpenFilesBesideAsync()));
        if (host.Kind == HostKind.Serial) menu.Add(MenuItemFor(L.T("Send hex"), () => _ = pane.Owner.SendHexAsync(pane)));
        menu.Add(MenuItemFor(L.T("Turn broadcast input on / off"), () => pane.Owner.ToggleBroadcast()));
        menu.Add(MenuItemFor(L.T("Include this pane in broadcast / leave it out"), () => pane.Owner.ToggleLeftOut(pane)));
        menu.Add(MenuItemFor(L.T("Save output / stop"), () => pane.Owner.ToggleOutputLog(pane)));
        menu.Add(MenuItemFor(L.T("Take a picture of this terminal"), () => _ = pane.Owner.TakePictureAsync(pane)));
        if (open is null)
        {
            menu.Add(MenuItemFor(L.T("Share this terminal / stop"), () => { pane.Owner.MakeActive(pane); _ = pane.Owner.ShareActiveAsync(allowInput: false); }));
            menu.Add(MenuItemFor(L.T("Share and let others type / stop"), () => { pane.Owner.MakeActive(pane); _ = pane.Owner.ShareActiveAsync(allowInput: true); }));
        }
        if (host.Kind is HostKind.Ssh or HostKind.Telnet) menu.Add(MenuItemFor(L.T("Diagnose connection"), () => _ = pane.Owner.DiagnoseAsync(pane.Host)));
        menu.Add(MenuItemFor(L.T("Close pane"), () => _ = pane.Owner.ClosePaneAsync(pane.Tab, pane)));
        pane.View.ContextMenu = new ContextMenu { ItemsSource = menu };

        UpdateBar(pane);
        _ = ConnectAsync(pane, interactive);
        return pane;
    }

    private static void OnSessionStarted(TerminalPane pane, TerminalSession session)
    {
        pane.RetryAttempt = 0;
        pane.Note = null;
        pane.LastProgress = ProgressState.None;
        UpdateBar(pane);

        // These arrive on the thread output is read on. The pane may have moved to another window by then.
        session.Bell += () => Dispatcher.UIThread.Post(() => pane.Owner.OnAttention(pane, null));
        session.Notified += (title, body) => Dispatcher.UIThread.Post(() => pane.Owner.OnAttention(pane, body.Length > 0 ? body : title));
        session.ProgressChanged += progress => Dispatcher.UIThread.Post(() => pane.Owner.OnProgress(pane, progress));
        if (pane.Host.Kind == HostKind.Local) session.WorkingDirectoryChanged += reported => Dispatcher.UIThread.Post(() => pane.Owner.OnDirectory(pane, reported));

        HostEntry host = pane.Host;
        if (host.Kind == HostKind.Ssh && !string.IsNullOrWhiteSpace(host.RemoteCommand)) session.SendText($"exec {host.RemoteCommand.Trim()}\r");
        if (!string.IsNullOrWhiteSpace(host.LoginScript)) session.SendText(host.LoginScript.ReplaceLineEndings("\r").TrimEnd('\r') + "\r");
    }

    /// <summary>Marks the pane's tab when something in it asks to be looked at while it is not in front.</summary>
    private void OnAttention(TerminalPane pane, string? note)
    {
        if (note is not null)
        {
            // The text comes from the server. It is shown on one line, with nothing in it that could draw elsewhere.
            string clean = new(note.Where(c => !char.IsControl(c)).Take(MaxNoteLength).ToArray());
            pane.Note = clean.Length > 0 ? clean : null;
            UpdateBar(pane);
        }

        AskForAttention();
        if (pane.Tab == _current) return;
        pane.Tab.NeedsAttention = true;
        RebuildTabStrip();
    }

    // The window itself asks to be looked at when the user is in another program.
    private void AskForAttention()
    {
        if (TopLevel.GetTopLevel(this) is Window { IsActive: false } window) App.WantAttention(window);
    }

    /// <summary>
    /// Starts looking, every few seconds, at what runs under the local shells, to name the AI tools among them.
    /// It runs only while there is a local shell and the window is in front, so an idle app stays idle.
    /// </summary>
    private void WatchAgents()
    {
        if (_agentTimer.IsEnabled) return;
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.Activated -= OnWindowActivated;
            window.Deactivated -= OnWindowDeactivated;
            window.Activated += OnWindowActivated;
            window.Deactivated += OnWindowDeactivated;
            if (!window.IsActive) return;
        }

        _agentTimer.Start();
        _ = CheckAgentsAsync();
    }

    private void OnWindowActivated(object? sender, EventArgs e) => WatchAgents();

    private void OnWindowDeactivated(object? sender, EventArgs e) => _agentTimer.Stop();

    private async Task CheckAgentsAsync()
    {
        if (_checkingAgents) return;

        List<TerminalPane> shells = _tabs.SelectMany(t => t.Panes).Where(p => p.ShellProcessId != 0).ToList();
        if (shells.Count == 0)
        {
            _agentTimer.Stop();
            return;
        }

        _checkingAgents = true;
        try
        {
            int[] ids = shells.Select(p => p.ShellProcessId).Distinct().ToArray();
            Dictionary<int, List<string>> running = await Task.Run(() => ProcessTree.Descendants(ids));

            bool changed = false;
            foreach (TerminalPane pane in shells)
            {
                string? agent = running.TryGetValue(pane.ShellProcessId, out List<string>? names) ? CodingAgents.Among(names) : null;
                if (agent == pane.Agent) continue;
                pane.Agent = agent;
                changed = true;
            }

            if (changed) RebuildTabStrip();
        }
        finally
        {
            _checkingAgents = false;
        }
    }

    // Only a shell on this computer reports folders that mean something here, and even then WSL does not.
    private void OnDirectory(TerminalPane pane, string reported)
    {
        if (ReportedDirectory.ToLocalPath(reported) is not string path || !Directory.Exists(path) || path == pane.CurrentDirectory) return;

        pane.CurrentDirectory = path;
        pane.Repository = ReportedDirectory.RepositoryName(path);
        if (AppServices.Settings.GroupTabsByRepository) GroupTabsByRepository();
        RebuildTabStrip();
    }

    /// <summary>
    /// Brings tabs working in the same repository together, each group where its first tab already was.
    /// Pinned tabs stay at the front and tabs outside any repository stay where they are among the groups.
    /// </summary>
    private void GroupTabsByRepository()
    {
        static string? RepositoryOf(WorkspaceTab tab) => tab.Active is TerminalPane { Repository: string repository } ? repository : null;

        List<WorkspaceTab> loose = _tabs.Where(t => !t.Pinned).ToList();
        var ordered = _tabs.Where(t => t.Pinned).ToList();
        var placed = new HashSet<WorkspaceTab>();

        foreach (WorkspaceTab tab in loose)
        {
            if (!placed.Add(tab)) continue;
            ordered.Add(tab);

            if (RepositoryOf(tab) is not string repository) continue;
            foreach (WorkspaceTab other in loose.Where(o => RepositoryOf(o) == repository && placed.Add(o))) ordered.Add(other);
        }

        if (ordered.SequenceEqual(_tabs)) return;

        _tabs.Clear();
        _tabs.AddRange(ordered);
        RebuildTabStrip();
    }

    /// <summary>
    /// What a local shell is started with besides what the user set. Bash is asked to say which folder it is in
    /// each time it shows its prompt, which is what lets a split open in the same place.
    /// </summary>
    private static Dictionary<string, string> EnvironmentFor(HostEntry host)
    {
        var variables = new Dictionary<string, string>(host.Environment);
        bool bash = host.Address.Contains("bash", StringComparison.OrdinalIgnoreCase) && !host.Address.Contains("wsl", StringComparison.OrdinalIgnoreCase);
        if (bash && !variables.ContainsKey(PromptCommand) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PromptCommand)))
            variables[PromptCommand] = ReportedDirectory.BashPromptCommand;
        return variables;
    }

    private void OnProgress(TerminalPane pane, TerminalProgress progress)
    {
        bool finished = progress.State == ProgressState.None && pane.LastProgress is ProgressState.Running or ProgressState.Indeterminate;
        pane.LastProgress = progress.State;
        UpdateBar(pane);

        // A long task ending in a tab nobody is watching is the moment worth pointing out.
        if (finished) AskForAttention();
        if (finished && pane.Tab != _current) pane.Tab.NeedsAttention = true;
        RebuildTabStrip();
    }

    private void OnSessionEnded(TerminalPane pane)
    {
        pane.ShellProcessId = 0;
        pane.Agent = null;

        // Typing "exit" is an ending the user chose, so only a broken link comes back by itself.
        if (pane.View.Session is { ConnectionLost: true } && pane.Host.AutoReconnect)
        {
            ScheduleReconnect(pane, 1);
            return;
        }

        if (pane.OpenTransport is not null) pane.ShowStatus(L.T("Connection closed."));
        else if (pane.Host.Kind == HostKind.Local) pane.ShowStatus(L.T("Shell closed."), L.T("Reopen"),() => _ = ReconnectAsync(pane));
        else pane.ShowStatus(L.T("Connection closed."), L.T("Reconnect"), () => _ = ReconnectAsync(pane));
    }

    private void OnConnectFailed(TerminalPane pane, string message)
    {
        if (pane.RetryAttempt > 0)
        {
            ScheduleReconnect(pane, pane.RetryAttempt + 1);
            return;
        }

        if (pane.Host.Kind is HostKind.Local or HostKind.Serial) pane.ShowStatus(L.T("Could not open: {0}", message), L.T("Try again"), () => _ = ReconnectAsync(pane));
        else pane.ShowStatus(L.T("Could not connect: {0}", message), L.T("Try again"), () => _ = ReconnectAsync(pane), L.T("Diagnose"), () => _ = DiagnoseAsync(pane.Host));
    }

    /// <summary>Asks for bytes written as hex and sends them down the pane's serial line.</summary>
    private async Task SendHexAsync(TerminalPane pane)
    {
        if (await _dialogs.AskTextAsync(L.T("Send hex"), L.T("Bytes (for example 48 65 6c 6c 6f 0d 0a)"), null, L.T("Send")) is not string text) return;

        if (SerialTransport.ParseHex(text) is not byte[] bytes) await _dialogs.ShowMessageAsync(L.T("Send hex"), L.T("This text could not be read as hex bytes."));
        else pane.View.Session?.SendBytes(bytes);
        pane.View.Focus();
    }

    private void ScheduleReconnect(TerminalPane pane, int attempt)
    {
        int seconds = Math.Min(1 << Math.Min(attempt - 1, 16), MaxReconnectDelaySeconds);

        var wait = new CancellationTokenSource();
        pane.RetryWait?.Cancel();
        pane.RetryWait = wait;
        pane.RetryAttempt = attempt;

        pane.ShowStatus(L.T("Connection lost. Trying again in {0} s (attempt {1}).", seconds, attempt), L.T("Give up"),() =>
        {
            wait.Cancel();
            pane.RetryAttempt = 0;
            if (pane.Host.Kind == HostKind.Serial) pane.ShowStatus(L.T("Connection closed."), L.T("Reconnect"), () => _ = ReconnectAsync(pane));
            else pane.ShowStatus(L.T("Connection closed."), L.T("Reconnect"), () => _ = ReconnectAsync(pane), L.T("Diagnose"), () => _ = DiagnoseAsync(pane.Host));
        });

        _ = ReconnectAfterAsync(pane, TimeSpan.FromSeconds(seconds), wait.Token);
    }

    private static async Task ReconnectAfterAsync(TerminalPane pane, TimeSpan delay, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(delay, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await pane.View.CloseAsync();
        pane.Owner.StartConnecting(pane);
    }

    private static void UpdateBar(TerminalPane pane)
    {
        HostEntry host = pane.Host;
        var parts = new List<string>
        {
            host.Kind switch
            {
                HostKind.Telnet => $"{host.Address}:{host.Port}",
                HostKind.Serial or HostKind.Local => host.Address,
                _ => host.Localtonet ? $"Localtonet · {host.Name}" : $"{Resolve(host).User}@{host.Address}",
            },
        };
        if (Resolve(host).JumpHosts.Count is > 0 and int jumps) parts.Add(L.T("{0} jumps", jumps));

        TerminalSession? session = pane.View.Session;
        if (session?.Description is { } description) parts.Add(description);
        if (session?.Progress is { State: ProgressState.Running } running) parts.Add($"{running.Percent}%");
        if (pane.Note is { } note) parts.Add($"\u25cf {note}");
        if (session is not null && pane.View.GridSizeOnScreen is var (columns, rows)) parts.Add($"{columns}×{rows}");
        if (session?.LogPath is not null) parts.Add(L.T("● logging"));
        if (pane.Shared?.Share is { IsRunning: true } share) parts.Add(L.T("● shared, watching: {0}", share.ViewerCount));
        if (pane.ForwardProblems.Count > 0) parts.Add(L.T("⚠ {0} forwards could not be set up: {1}", pane.ForwardProblems.Count, pane.ForwardProblems[0]));

        pane.SetBar(string.Join("  ·  ", parts), pane.ForwardProblems.Count > 0);
    }

    private static MenuItem MenuItemFor(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void ConfigurePaste(TerminalPane pane) => pane.View.ConfirmPaste = text =>
    {
        int targets = InputTargetsFor(pane).Count();
        return _dialogs.ConfirmAsync(
            targets > 1 ? L.T("Pasting into {0} panes", targets) : L.T("Multi-line paste"),
            L.T("The pasted text may run immediately:\n\n{0}", text.Length <= 300 ? text : text[..300] + "…"),
            L.T("Paste"),
            danger: true);
    };

    private async Task ReconnectAsync(TerminalPane pane)
    {
        pane.RetryWait?.Cancel();
        pane.RetryAttempt = 0;
        await pane.View.CloseAsync();
        await pane.Owner.ConnectAsync(pane, interactive: true);
    }

    private async Task ConnectAsync(TerminalPane pane, bool interactive)
    {
        HostEntry host = pane.Host;

        // A restored tab shouldn't open the browser by itself.
        if (host.Localtonet && !interactive && KnownSecret(host) is null)
        {
            pane.ShowStatus(L.T("The connection to {0} needs approval in the browser.", host.Name), L.T("Connect"),() => _ = pane.Owner.ConnectAsync(pane, interactive: true));
            return;
        }

        foreach (HostEntry step in MissingSecrets(host))
        {
            if (!interactive || !await AskSecretAsync(step, L.T("Password")))
            {
                pane.ShowStatus(L.T("A password is needed for {0}.", step.Name), L.T("Connect"),() => _ = pane.Owner.ConnectAsync(pane, interactive: true));
                return;
            }
        }

        StartConnecting(pane);
    }

    private void StartConnecting(TerminalPane pane)
    {
        if (pane.RetryAttempt == 0)
        {
            HostEntry host = pane.Host;
            pane.ShowStatus(host.Kind switch
            {
                HostKind.Local => L.T("Opening {0}...", host.Name),
                HostKind.Serial => L.T("Opening port {0}...", host.Address),
                _ => host.Localtonet ? L.T("Asking Localtonet about {0}...", host.Name) : L.T("Connecting to {0}...", host.Address),
            });
        }

        pane.ConnectCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        pane.ConnectCancel = cancel;
        pane.View.Connect((columns, rows, _) => pane.Owner.ConnectWithChecksAsync(pane, columns, rows, cancel.Token));
    }

    private async Task<ITerminalTransport> ConnectWithChecksAsync(TerminalPane pane, int columns, int rows, CancellationToken cancellation)
    {
        HostEntry host = pane.Host;
        if (pane.OpenTransport is not null)
        {
            ITerminalTransport custom = await pane.OpenTransport(columns, rows, cancellation);
            pane.ForwardProblems = [];
            pane.HideStatus();
            return custom;
        }

        if (host.Kind != HostKind.Ssh)
        {
            ITerminalTransport opened = host.Kind switch
            {
                HostKind.Telnet => await TelnetTransport.ConnectAsync(host.Address, host.Port, columns, rows, cancellation),
                HostKind.Serial => await SerialTransport.OpenAsync(host.Address, host.Serial ?? new SerialSettings(), cancellation),
                _ => await OpenLocalShellAsync(pane, columns, rows, cancellation),
            };
            pane.ForwardProblems = [];
            pane.ShellProcessId = (opened as LocalShellTransport)?.ProcessId ?? pane.Hosted?.ProcessId ?? 0;
            pane.HideStatus();
            if (pane.ShellProcessId != 0) Dispatcher.UIThread.Post(WatchAgents);

            // The shell's name is only known now, and the saved layout needs it to find the shell again.
            if (pane.Hosted is not null) Dispatcher.UIThread.Post(LayoutChanged);

            // The console Windows gives a local shell rewrites what programs print, so tmux cannot talk through it.
            bool carriesTmux = host.Kind == HostKind.Telnet || (host.Kind == HostKind.Local && !OperatingSystem.IsWindows());
            return Shareable(pane, carriesTmux ? WatchForTmux(opened) : opened, columns, rows);
        }

        void Report(string text, Uri? url) => Dispatcher.UIThread.Post(() => pane.ShowStatus(
            text,
            url is null ? null : L.T("Open approval page"),
            url is null ? null : () => _ = AppServices.OpenUrlAsync(this, url),
            L.T("Give up"),
            () => pane.ConnectCancel?.Cancel()));

        if (Resolve(host).UseMosh && !host.Localtonet)
        {
            // Mosh has no login of its own: SSH starts its server, which answers with a port and a one-time key.
            CommandResult started = await SshCommandRunner.RunAsync(EndpointsFor(PathOf(host)), MoshTransport.StartCommand, IsTrusted, cancellation);
            if (MoshTransport.ParseServerReply(started.Output) is not (int port, string key))
            {
                string reason = started.Failure ?? (started.Error.Trim() is { Length: > 0 } error ? error : started.Output.Trim());
                throw new IOException(L.T("Mosh could not be started on the server: {0}", reason));
            }

            MoshTransport mosh = await Task.Run(() => MoshTransport.Connect(host.Address, port, key, columns, rows), cancellation);
            pane.ForwardProblems = [];
            pane.HideStatus();
            return Shareable(pane, mosh, columns, rows);
        }

        SshTransport transport = await WithLoginChecksAsync(
            host,
            () => pane.RetryAttempt = 0,
            Report,
            (path, policy) => SshTransport.ConnectAsync(path, columns, rows, policy, host.Forwards, new SshShellOptions(host.ForwardAgent, host.ForwardX11), cancellation),
            cancellation);
        pane.ForwardProblems = transport.ForwardProblems;
        pane.HideStatus();

        // A server never typed on from here has nothing to suggest yet, so its shell is asked what it remembers.
        if (AppServices.WantsHistoryFor(host))
        {
            _ = Task.Run(async () => AppServices.Remember(await transport.ReadShellHistoryAsync(AppServices.SeededCommands, CancellationToken.None), host.Id.ToString("N")));
        }

        return Shareable(pane, WatchForTmux(transport), columns, rows);
    }

    // Asks on the UI thread, because the transfer queue calls this from its own.
    private Task<SftpConnection> ConnectFilesAsync(HostEntry host, Action<string, Uri?> report, CancellationToken cancellation) =>
        Dispatcher.UIThread.InvokeAsync(() => WithLoginChecksAsync(host, () => { }, report, (path, policy) => SftpConnection.ConnectAsync(path, policy, cancellation), cancellation));

    /// <summary>Connects, stopping to ask the user about unknown keys and refused logins.</summary>
    private async Task<T> WithLoginChecksAsync<T>(HostEntry host, Action onGiveUp, Action<string, Uri?> report, Func<IReadOnlyList<SshEndpoint>, HostKeyPolicy, Task<T>> connect, CancellationToken cancellation)
    {
        if (host.Localtonet) return await ConnectDeviceAsync(host, report, connect, cancellation);

        OperationCanceledException GaveUp(string message)
        {
            onGiveUp();
            return new OperationCanceledException(message);
        }

        while (true)
        {
            List<HostEntry> hosts = PathOf(host);
            List<SshEndpoint> path = EndpointsFor(hosts);
            HostEntry HostOf(SshEndpoint endpoint) => hosts[path.IndexOf(endpoint)];

            try
            {
                return await connect(path, IsTrusted);
            }
            catch (HostKeyRejectedException rejected)
            {
                // The connection stops first and the question comes after, so no handshake times out behind the dialog.
                if (!await ConfirmHostKeyOnceAsync(rejected.Endpoint, rejected.Key)) throw GaveUp(L.T("The host key was not accepted."));
            }
            catch (LoginRefusedException refused) when (LoginOf(HostOf(refused.Endpoint)).TakesTypedPassword)
            {
                HostEntry step = HostOf(refused.Endpoint);
                string message = refused.Endpoint.Password is null ? L.T("Password") : L.T("Password not accepted, try again");
                Forget(step);
                if (!await AskSecretAsync(step, message)) throw GaveUp(L.T("Login cancelled."));
            }
            catch (LoginRefusedException refused) when (LoginOf(HostOf(refused.Endpoint)).Identity is Identity unwelcome)
            {
                // Asking for a password here would file it under the server, where the identity keeps it from ever
                // being looked at. The identity itself is what has to be put right, so the user is told which one.
                // It stays a refused login, which is what keeps the transfer queue from trying it again and again.
                throw new LoginRefusedException(refused.Endpoint, L.T("The server did not accept the identity \"{0}\": {1}", unwelcome.Name, refused.Message));
            }
            catch (KeyPassphraseNeededException needed) when (LoginOf(HostOf(needed.Endpoint)).Identity is Identity locked)
            {
                string note = AppServices.Vault.IsAvailable ? L.T("The password for this private key is needed") : L.T("{0}. This system has no vault yet, so it is only remembered for this session.", L.T("The password for this private key is needed"));
                SecretAnswer? answer = await _dialogs.AskSecretAsync(locked.Name, note, AppServices.Vault.IsAvailable);
                if (answer is null) throw GaveUp(L.T("Login cancelled."));
                RememberPassphrase(locked, answer);
            }
            catch (KeyPassphraseNeededException needed)
            {
                HostEntry step = HostOf(needed.Endpoint);
                Forget(step);
                if (!await AskSecretAsync(step, L.T("The password for this private key is needed"))) throw GaveUp(L.T("Login cancelled."));
            }
        }
    }

    /// <summary>Panes opening the same new server share one question instead of each asking in turn.</summary>
    private async Task<bool> ConfirmHostKeyOnceAsync(SshEndpoint target, HostKeyInfo key)
    {
        await _hostKeyQuestion.WaitAsync();
        try
        {
            if (IsTrusted(target, key)) return true;
            if (!await ConfirmHostKeyAsync(target, key)) return false;
            AppServices.KnownHosts.Trust(target.Host, target.Port, key.Algorithm, key.Fingerprint);
            return true;
        }
        finally
        {
            _hostKeyQuestion.Release();
        }
    }

    private Task<bool> ConfirmHostKeyAsync(SshEndpoint target, HostKeyInfo key)
    {
        if (AppServices.KnownHosts.Check(target.Host, target.Port, key.Fingerprint) == HostKeyStatus.Changed)
        {
            return _dialogs.ConfirmAsync(
                L.T("Host key changed"),
                L.T("{0} shows a different key. This is normal if the server was reinstalled; otherwise someone may be intercepting the connection.\n\nSaved: {1}\nCurrent: {2}", target.Host, AppServices.KnownHosts.Fingerprint(target.Host, target.Port), key.Fingerprint),
                L.T("Trust new key"),
                danger: true);
        }

        return _dialogs.ConfirmAsync(
            L.T("Connecting for the first time"),
            L.T("Host key of {0}:\n\n{1}\n{2}\n\nContinue if this key belongs to your server.", target.Host, key.Algorithm, key.Fingerprint),
            L.T("Trust and connect"));
    }

    private void ShowCurrent()
    {
        _content.Children.Clear();

        if (_current is WorkspaceTab tab)
        {
            tab.Visual ??= tab.Zoomed is ContentPane zoomed ? zoomed.Frame : Build(tab.Root);
            _content.Children.Add(tab.Visual);
            UpdateActiveFrame(tab);
            tab.Active.FocusContent();
        }
        else
        {
            _broadcastBar.IsVisible = false;
            _content.Children.Add(_emptyState);
        }

        RebuildTabStrip();
    }

    private void UpdateActiveFrame(WorkspaceTab tab)
    {
        bool several = tab.AllPanes.Skip(1).Any();
        foreach (ContentPane pane in tab.AllPanes)
        {
            bool marked = several && pane == tab.Active;
            pane.Frame.BorderBrush = tab.Broadcast && pane is TerminalPane { LeftOutOfBroadcast: false }
                ? ElioColors.Danger
                : marked ? ElioColors.AccentRing : ElioColors.Border;
            pane.Frame.BoxShadow = marked ? ElioStyle.PaneRing : ElioStyle.NoShadow;
        }

        if (tab != _current) return;

        _broadcastBar.IsVisible = tab.Broadcast;
        int receiving = tab.Panes.Count(p => !p.LeftOutOfBroadcast);
        _broadcastText.Text = L.T("Broadcast is on: what you type goes to {0} panes at once.", receiving);
    }

    private static void Detach(PaneNode node)
    {
        if (node is ContentPane pane)
        {
            if (pane.Frame.Parent is Panel panel) panel.Children.Remove(pane.Frame);
        }
        else if (node is SplitNode split)
        {
            Detach(split.First);
            Detach(split.Second);
        }
    }

    private static void CaptureRatios(PaneNode node)
    {
        if (node is not SplitNode split) return;

        if (split.Visual is Grid grid)
        {
            (double first, double second) = split.Orientation == Orientation.Horizontal
                ? (grid.ColumnDefinitions[0].ActualWidth, grid.ColumnDefinitions[2].ActualWidth)
                : (grid.RowDefinitions[0].ActualHeight, grid.RowDefinitions[2].ActualHeight);
            if (first + second > 0) split.Ratio = first / (first + second);
        }

        CaptureRatios(split.First);
        CaptureRatios(split.Second);
    }

    private void RebuildVisual(WorkspaceTab tab)
    {
        CaptureRatios(tab.Root);
        Detach(tab.Root);
        tab.Visual = null;
    }

    private static Control Build(PaneNode node)
    {
        if (node is ContentPane pane) return pane.Frame;

        var split = (SplitNode)node;
        Control first = Build(split.First);
        Control second = Build(split.Second);
        // The gap between two cards is the divider. It is given the whole gap to be grabbed by, not just its own sliver.
        var splitter = new GridSplitter { Background = ElioColors.Transparent, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ZIndex = 1 };
        var grid = new Grid();
        var firstSize = new GridLength(split.Ratio, GridUnitType.Star);
        var secondSize = new GridLength(1 - split.Ratio, GridUnitType.Star);
        var middle = new GridLength(SplitterSize);

        // A pane keeps the place it had in the split it was in before. Left alone, one that was second there and is
        // first here would sit on top of its neighbour and leave its own side empty.
        foreach (Control side in new[] { first, second })
        {
            Grid.SetColumn(side, 0);
            Grid.SetRow(side, 0);
        }

        if (split.Orientation == Orientation.Horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(firstSize));
            grid.ColumnDefinitions.Add(new ColumnDefinition(middle));
            grid.ColumnDefinitions.Add(new ColumnDefinition(secondSize));
            splitter.ResizeDirection = GridResizeDirection.Columns;
            splitter.Margin = new Thickness(-ContentPane.PaneMargin, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(second, 2);
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition(firstSize));
            grid.RowDefinitions.Add(new RowDefinition(middle));
            grid.RowDefinitions.Add(new RowDefinition(secondSize));
            splitter.ResizeDirection = GridResizeDirection.Rows;
            splitter.Margin = new Thickness(0, -ContentPane.PaneMargin);
            Grid.SetRow(splitter, 1);
            Grid.SetRow(second, 2);
        }

        grid.Children.Add(first);
        grid.Children.Add(splitter);
        grid.Children.Add(second);
        split.Visual = grid;
        return grid;
    }

    private void MakeActive(ContentPane pane)
    {
        WorkspaceTab tab = pane.Tab;
        if (tab.Active == pane) return;
        tab.Active = pane;
        UpdateActiveFrame(tab);
        RebuildTabStrip();
    }

    private void RebuildTabStrip()
    {
        _tabStrip.Children.Clear();
        foreach (WorkspaceTab tab in _tabs) _tabStrip.Children.Add(TabHeader(tab));
        _tabTrack.IsVisible = _tabs.Count > 0;
    }

    /// <summary>Which position a pointer at this horizontal offset in the strip would drop a tab into.</summary>
    private int DropIndex(double x)
    {
        for (int i = 0; i < _tabStrip.Children.Count; i++)
        {
            Rect bounds = _tabStrip.Children[i].Bounds;
            if (x < bounds.Left + (bounds.Width / 2)) return i;
        }
        return _tabStrip.Children.Count;
    }

    /// <summary>Finishes dragging a tab: dropped outside the strip it gets its own window, otherwise it moves along the strip.</summary>
    public void DropTab(WorkspaceTab tab, Point positionInStrip)
    {
        bool outside = positionInStrip.Y < -TearOffDistance || positionInStrip.Y > _tabStrip.Bounds.Height + TearOffDistance;
        if (outside && DetachTab is not null)
        {
            DetachTab(tab);
            return;
        }

        int from = _tabs.IndexOf(tab);
        int to = DropIndex(positionInStrip.X);
        MoveTab(tab, to > from ? to - 1 : to);
    }

    // Under this width a tab that is not in front shows its name instead of its close button.
    private const double NarrowTabWidth = 90;

    private Control TabHeader(WorkspaceTab tab)
    {
        bool selected = tab == _current;
        Button close = ElioStyle.IconButton(ElioStyle.Icon(ElioStyle.IconClose, ElioColors.TextMuted, 10, 2), null, 16);
        close.CornerRadius = new CornerRadius(8);
        close.Margin = new Thickness(8, 0, 0, 0);
        close.Click += async (_, _) => await CloseTabAsync(tab);

        // The tabs behind keep their close button out of sight until the pointer is on them. It is still there to be clicked.
        close.Opacity = selected ? 1 : 0;
        close.Transitions = [new Avalonia.Animation.DoubleTransition { Property = OpacityProperty, Duration = ElioStyle.Fade }];

        var header = new Border
        {
            Background = selected ? ElioColors.TabActive : ElioColors.Transparent,
            CornerRadius = new CornerRadius(ElioStyle.RowRadius),
            BoxShadow = selected ? ElioStyle.TabShadow : ElioStyle.NoShadow,
            Padding = new Thickness(10, 0, 4, 0),
            Height = TabHeight,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Grid
            {
                // A tab squeezed by its neighbors shows what fits of its name and nothing past its own edge.
                ClipToBounds = true,
                Children =
                {
                    new DockPanel
                    {
                        Children =
                        {
                            DockRight(close),
                            DockRight(TabPin(tab)),
                            TabColorDot(tab),
                            new TextBlock { Text = tab.Label, Foreground = selected ? ElioColors.Text : tab.NeedsAttention ? ElioColors.Accent : ElioColors.TextMuted, FontSize = 12, FontWeight = selected ? FontWeight.Medium : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                        },
                    },
                    TabProgressBar(tab),
                },
            },
        };

        if (selected)
        {
            header.PointerEntered += (_, _) => close.Opacity = 1;
        }
        else
        {
            ElioStyle.Fading(header);
            header.PointerEntered += (_, _) => (header.Background, close.Opacity) = (ElioColors.SurfaceHover, 1);
            header.PointerExited += (_, _) => (header.Background, close.Opacity) = (ElioColors.Transparent, 0);
        }

        Point? pressedAt = null;
        bool dragging = false;

        header.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
            pressedAt = e.GetPosition(_tabStrip);
            dragging = false;
            e.Pointer.Capture(header);
            e.Handled = true;
        };
        header.PointerMoved += (_, e) =>
        {
            if (pressedAt is not Point start) return;
            Point now = e.GetPosition(_tabStrip);
            if (dragging || Math.Abs(now.X - start.X) + Math.Abs(now.Y - start.Y) <= DragStartDistance) return;
            dragging = true;
            header.Opacity = 0.6;
        };
        header.PointerReleased += (_, e) =>
        {
            if (pressedAt is null) return;
            pressedAt = null;
            e.Pointer.Capture(null);
            header.Opacity = 1;
            if (dragging) DropTab(tab, e.GetPosition(_tabStrip));
            else Select(tab);
        };

        var moveToWindow = new MenuItem { Header = L.T("Move to new window"),IsEnabled = DetachTab is not null };
        moveToWindow.Click += (_, _) => DetachTab?.Invoke(tab);
        var closeItem = new MenuItem { Header = L.T("Close tab") };
        closeItem.Click += async (_, _) => await CloseTabAsync(tab);
        close.IsVisible = !tab.Pinned;

        // Squeezed between many others, a tab has no room for both its name and a button nobody sees until the
        // pointer is on it, so the button gives its place to the name. The tab in front keeps it. This follows the
        // width the strip hands out, which only falls under the limit when every tab is cut to the same share, so
        // taking the button away cannot widen the tab again.
        if (!selected && !tab.Pinned) header.SizeChanged += (_, e) => close.IsVisible = e.NewSize.Width >= NarrowTabWidth;

        var pinItem = new MenuItem { Header = tab.Pinned ? L.T("Unpin tab") : L.T("Pin tab") };
        pinItem.Click += (_, _) => TogglePinned(tab);
        var noColor = new MenuItem { Header = L.T("No color") };
        noColor.Click += (_, _) => SetTabColor(tab, null);
        var colorItems = new List<MenuItem> { noColor };
        foreach (uint color in TabColors)
        {
            var swatch = new MenuItem { Header = new Border { Width = 56, Height = 12, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color)) } };
            swatch.Click += (_, _) => SetTabColor(tab, color);
            colorItems.Add(swatch);
        }

        var colorItem = new MenuItem { Header = L.T("Tab color"), ItemsSource = colorItems };
        bool elsewhere = _current is not null && _current != tab;
        var beside = new MenuItem { Header = L.T("Show beside the current tab"), IsEnabled = elsewhere };
        beside.Click += (_, _) => MergeTabs(tab, _current!, Orientation.Horizontal);
        var below = new MenuItem { Header = L.T("Show below the current tab"), IsEnabled = elsewhere };
        below.Click += (_, _) => MergeTabs(tab, _current!, Orientation.Vertical);
        var renameItem = new MenuItem { Header = L.T("Rename tab") };
        renameItem.Click += async (_, _) => await RenameTabAsync(tab);
        var closeOthers = new MenuItem { Header = L.T("Close other tabs"), IsEnabled = _tabs.Count > 1 };
        closeOthers.Click += async (_, _) => await CloseOtherTabsAsync(tab);
        var reopen = new MenuItem { Header = L.T("Reopen closed tab"), IsEnabled = _closedTabs.Count > 0 };
        reopen.Click += (_, _) => ReopenClosedTab();
        header.ContextMenu = new ContextMenu { ItemsSource = new[] { renameItem, colorItem, pinItem, beside, below, moveToWindow, reopen, closeOthers, closeItem } };
        return header;
    }

    /// <summary>Marks a pinned tab, where the close button would otherwise be.</summary>
    private static Control TabPin(WorkspaceTab tab)
    {
        Control pin = ElioStyle.Icon(ElioStyle.IconPin, ElioColors.TextFaint, 12, 1.6);
        pin.Margin = new Thickness(8, 0, 4, 0);
        pin.IsVisible = tab.Pinned;
        pin.Tag = "tab-pin";
        return pin;
    }

    private static Control TabColorDot(WorkspaceTab tab)
    {
        uint color = tab.Color ?? 0;
        var dot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color)),
            IsVisible = tab.Color is not null,
            Tag = "tab-color",
        };
        DockPanel.SetDock(dot, Dock.Left);
        return dot;
    }

    private void SetTabColor(WorkspaceTab tab, uint? color)
    {
        tab.Color = color;
        RebuildTabStrip();
        LayoutChanged();
    }

    // Pinned tabs are kept together at the front, in the order they were pinned.
    private void TogglePinned(WorkspaceTab tab)
    {
        tab.Pinned = !tab.Pinned;
        _tabs.Remove(tab);
        _tabs.Insert(_tabs.Count(t => t.Pinned), tab);
        RebuildTabStrip();
        LayoutChanged();
    }

    /// <summary>A thin bar along the bottom of a tab, as long as the progress its programs report.</summary>
    private static Control TabProgressBar(WorkspaceTab tab)
    {
        TerminalProgress progress = tab.Progress;
        bool unknown = progress.State == ProgressState.Indeterminate;
        int done = unknown ? 100 : Math.Clamp(progress.Percent, 0, 100);

        var bar = new Border
        {
            Height = TabProgressHeight,
            CornerRadius = new CornerRadius(TabProgressHeight / 2),
            Background = progress.State == ProgressState.Error ? ElioColors.Danger : ElioColors.Accent,
            Opacity = unknown || progress.State == ProgressState.Paused ? 0.45 : 1,
        };
        var track = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{done}*,{100 - done}*"),
            VerticalAlignment = VerticalAlignment.Bottom,

            // Clear of the tab's rounded corners.
            Margin = new Thickness(-4, 0, 2, 2),
            IsVisible = progress.State != ProgressState.None,
            IsHitTestVisible = false,
            Tag = "tab-progress",
            Children = { bar },
        };
        return track;
    }

    private static Control DockRight(Control control)
    {
        DockPanel.SetDock(control, Dock.Right);
        return control;
    }
}

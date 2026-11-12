using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Elio.Terminal;
using SkiaSharp;
using System.Text.RegularExpressions;
using MouseButton = Elio.Terminal.MouseButton;

namespace Elio.UI.Terminal;

/// <summary>How a terminal looks and reacts.</summary>
public sealed record TerminalAppearance(double FontSize, bool Ligatures, TerminalTheme Theme, bool CopyOnSelect, bool RightClickPaste, IReadOnlyList<(Regex Pattern, uint Color)> Highlights)
{
    public static TerminalAppearance Default { get; } = new(14, true, TerminalTheme.Default, false, false, []);
}

/// <summary>Opens a connection for a terminal of the given size.</summary>
public delegate Task<ITerminalTransport> TerminalConnector(int columns, int rows, CancellationToken cancellationToken);

/// <summary>A terminal on screen: draws one session and passes it the keyboard and mouse.</summary>
public sealed class TerminalView : Control
{
    private const int WheelRowsPerNotch = 3;
    private const int ClipboardAttempts = 4;
    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(60);
    private const int HiddenColumns = 120;
    private const int HiddenRows = 32;
    private static readonly TimeSpan HiddenConnectDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(530);

    // The scrollbar is drawn thin so it stays out of the text's way, and is easier to grab than it looks.
    private static readonly TimeSpan DragScrollInterval = TimeSpan.FromMilliseconds(40);
    private const double DragScrollStep = 30;
    private const int DragScrollMaxRows = 6;

    private const float ScrollbarWidth = 4;
    private const float ScrollbarMargin = 2;
    private const float ScrollbarMinLength = 24;
    private const double ScrollbarGrabWidth = 12;
    private const byte ScrollbarAlpha = 0x66;

    private readonly DispatcherTimer _blinkTimer;
    private TerminalRenderer? _renderer;
    private TerminalSession? _session;
    private TerminalConnector? _pendingConnect;
    private TerminalAppearance _appearance = TerminalAppearance.Default;
    private bool _cursorOn = true;
    private bool _selecting;
    private bool _scrolling;
    private TerminalMods _stickyMods;
    private int _animationScheduled;
    private bool _touching;
    private bool _touchMoved;
    private double _touchY;
    private double _pinchScale = 1;
    private Point _dragPoint;
    private readonly DispatcherTimer _dragScrollTimer;
    private bool _mouseCaptured;
    private MouseButton _mouseButton;

    // Followed whether or not suggestions are on, so that turning them on does not have to wait for the next Enter.
    private readonly TypedLine _typed = new();
    private bool _suggestions;

    // Written on the render thread, read on the UI thread.
    private volatile bool _cursorBlinks;

    public TerminalSession? Session => _session;

    public TerminalAppearance Appearance
    {
        get => _appearance;
        set
        {
            _appearance = value;
            _session?.SetTheme(value.Theme);
            if (TopLevel.GetTopLevel(this) is not null) CreateRenderer();
            ApplySize();
            InvalidateVisual();
        }
    }

    /// <summary>Rows and columns the terminal currently shows. Null until the view is on screen.</summary>
    public (int Columns, int Rows)? GridSizeOnScreen => _renderer is null || Bounds.Width <= 0 ? null : GridSize(_renderer.Fonts);

    /// <summary>The size of one character cell. Empty until the view is on screen.</summary>
    public Size CellSize => _renderer is null ? default : new Size(_renderer.Fonts.CellWidth, _renderer.Fonts.CellHeight);

    public event Action<string>? ConnectFailed;

    public event Action? SessionEnded;

    /// <summary>Raised after a session starts, on the UI thread.</summary>
    public event Action<TerminalSession>? SessionStarted;

    /// <summary>Raised when the number of rows or columns may have changed.</summary>
    public event Action? GridChanged;

    /// <summary>Where typing goes when it should reach more than this terminal. Null means just this one.</summary>
    public Func<IEnumerable<TerminalSession>>? InputTargets { get; set; }

    /// <summary>Answers Zmodem transfers started from the server.</summary>
    public IZmodemHost? ZmodemHost { get; set; }

    /// <summary>Ctrl and Alt as pressed on the on-screen key row, waiting to apply to the next key.</summary>
    public TerminalMods StickyMods
    {
        get => _stickyMods;
        set
        {
            if (_stickyMods == value) return;
            _stickyMods = value;
            StickyModsChanged?.Invoke();
        }
    }

    public event Action? StickyModsChanged;

    /// <summary>
    /// Commands entered before, which the rest of a line is suggested from while <see cref="Suggestions"/>
    /// is on. Entering a command adds it. Several terminals can share one.
    /// </summary>
    public CommandHistory? History { get; set; }

    /// <summary>
    /// Whether to show, faintly after the cursor, the rest of the most recent command that starts with what
    /// has been typed. Right or End takes it, Ctrl+Right takes one word of it. Off unless turned on, and
    /// while it is off nothing is remembered and no key means anything other than what it always did.
    /// </summary>
    public bool Suggestions
    {
        get => _suggestions;
        set
        {
            if (_suggestions == value) return;
            _suggestions = value;
            InvalidateVisual();
        }
    }

    /// <summary>Where this terminal's commands are entered, such as a host's id. Suggestions from the same place come first.</summary>
    public string? HistoryScope { get; set; }

    /// <summary>Set while something secret is being typed here, so that it is neither remembered nor completed.</summary>
    public bool HistoryPaused { get; set; }

    /// <summary>Sends a key that has no character, with any waiting Ctrl or Alt.</summary>
    public void PressKey(TerminalKey key)
    {
        TerminalMods mods = StickyMods;
        StickyMods = TerminalMods.None;
        if (!TakeSuggestion(key, mods))
        {
            bool sent = false;
            if (key is TerminalKey.Enter or TerminalKey.NumPadEnter) TrackKey(key, mods);
            ForEachTarget(session => sent |= session.SendKey(key, mods, 0, null));
            if (sent && key is not (TerminalKey.Enter or TerminalKey.NumPadEnter)) TrackKey(key, mods);
        }

        Focus();
    }

    /// <summary>
    /// The rest of the line being typed, as the history suggests it, or null. Nothing is suggested unless
    /// the line on screen ends with what the keys say was typed: that is what keeps a password from being
    /// completed, and a line the shell changed on its own from being guessed at.
    /// </summary>
    private string? SuggestedRest()
    {
        string[] choices = Choices();

        // A shell that completes the line itself draws its own idea where this one would go, and takes Right for it.
        // Then the list is still shown, but nothing in it is picked, or drawn into the line, until the user picks.
        bool shellsOwn = choices.Length > 0 && !_picked && _session?.HasTextAfterCursor() == true;
        Publish(choices, shellsOwn ? -1 : _choice);
        if (choices.Length == 0 || shellsOwn) return null;

        // Only a command that goes on from what is typed can be drawn after the cursor. The others are in the list.
        string typed = _typed.Text!;
        return choices[_choice].StartsWith(typed, StringComparison.Ordinal) ? choices[_choice][typed.Length..] : null;
    }

    private bool _picked;

    private const int MaxChoices = 8;
    private string[] _shownChoices = [];
    private int _shownChoice;
    private int _choice;
    private string? _choiceFor;

    /// <summary>The commands being offered for the line that is being typed, the likeliest first. Empty when there are none.</summary>
    public IReadOnlyList<string> SuggestionChoices => _shownChoices;

    /// <summary>Which of them Right or End would take, or -1 while none is picked.</summary>
    public int SuggestionChoice => _shownChoice;

    /// <summary>Raised when the commands on offer, or the one picked among them, change.</summary>
    public event Action? SuggestionsChanged;

    /// <summary>Just under the cursor, where a list of the choices belongs. Null when there is no terminal.</summary>
    public Point? SuggestionAnchor => _session?.CursorCell() is (int column, int row)
        ? new Point(column * CellSize.Width, (row + 1) * CellSize.Height)
        : null;

    private string[] Choices()
    {
        if (!_suggestions || HistoryPaused || History is not CommandHistory history || _session is not TerminalSession session) return [];
        if (_typed.Text is not { Length: > 0 } typed) return [];
        if (session.AlternateScreen || (session.PromptMarksSeen && !session.CursorAtPrompt)) return [];
        if (!TypedLine.Echoed(typed, session.TextBeforeCursor(), settled: true)) return [];

        // A choice made for one stretch of typing does not carry over to the next.
        if (_choiceFor != typed)
        {
            _choiceFor = typed;
            _choice = 0;
            _picked = false;
        }

        string[] found = [.. history.Matches(typed, HistoryScope, MaxChoices)];
        if (_choice >= found.Length) _choice = 0;
        return found;
    }

    // Called while drawing, so whoever shows the list is told afterwards rather than in the middle of it.
    private void Publish(string[] choices, int picked = -1)
    {
        if (_shownChoice == picked && choices.AsSpan().SequenceEqual(_shownChoices)) return;

        _shownChoices = choices;
        _shownChoice = picked;
        Dispatcher.UIThread.Post(() => SuggestionsChanged?.Invoke());
    }

    private string? NoSuggestion()
    {
        Publish([]);
        return null;
    }

    /// <summary>Takes one of the commands on offer, as if Right had been pressed with it picked.</summary>
    public void AcceptSuggestion(int index)
    {
        if (index < 0 || index >= _shownChoices.Length) return;

        _choice = index;
        _picked = true;
        TakeSuggestion(TerminalKey.End, TerminalMods.None);
        Focus();
    }

    /// <summary>Right or End takes the whole suggestion, and Right with Ctrl or Alt its next word. Without a suggestion they are ordinary keys.</summary>
    private bool TakeSuggestion(TerminalKey key, TerminalMods mods)
    {
        if (!_suggestions) return false;

        // Alt with Up or Down walks through the commands on offer. Without a list they are the keys they always were.
        if (mods == TerminalMods.Alt && key is TerminalKey.ArrowDown or TerminalKey.ArrowUp && _shownChoices.Length > 0 && Choices() is { Length: > 0 } offered)
        {
            // The first press picks the first one, where none was picked yet.
            if (_shownChoice < 0) _choice = key == TerminalKey.ArrowDown ? 0 : offered.Length - 1;
            else _choice = (_choice + (key == TerminalKey.ArrowDown ? 1 : offered.Length - 1)) % offered.Length;

            _picked = true;
            Publish(offered, _choice);
            InvalidateVisual();
            return true;
        }

        bool whole = mods == TerminalMods.None && key is TerminalKey.ArrowRight or TerminalKey.End;
        bool word = key == TerminalKey.ArrowRight && mods is TerminalMods.Control or TerminalMods.Alt;
        if (!whole && !word) return false;

        string[] choices = Choices();
        if (choices.Length == 0 || _typed.Text is not string typed) return false;
        if (!_picked && _session?.HasTextAfterCursor() == true) return false;

        string suggestion = choices[_choice];
        if (!suggestion.StartsWith(typed, StringComparison.Ordinal))
        {
            // A command found by a word from its middle replaces the line: what was typed is rubbed out, one
            // backspace for each character, and the command written in its place. That works in every shell.
            if (word) return false;

            _typed.Replace(suggestion);
            string replacement = new string('\x7f', typed.Length) + suggestion;
            ForEachTarget(session => session.SendText(replacement));
            InvalidateVisual();
            return true;
        }

        string taken = word ? _typed.AcceptWord(suggestion) : _typed.Accept(suggestion);
        if (taken.Length == 0) return false;

        ForEachTarget(session => session.SendText(taken));
        InvalidateVisual();
        return true;
    }

    /// <summary>Follows text on its way to the shell, to know what line is being typed.</summary>
    private void TrackText(string text)
    {
        if (_session?.AlternateScreen != false)
        {
            // A full-screen program is not being typed a command line.
            _typed.Invalidate();
            return;
        }

        string line = text.EndsWith('\r') || text.EndsWith('\n') ? text[..^1] : text;
        if (text is "\u0003" or "\u0015")
        {
            _typed.Reset();
        }
        else
        {
            _typed.Type(line);
            if (line.Length != text.Length) EnterPressed();
        }

        if (_suggestions) InvalidateVisual();
    }

    /// <summary>Follows pasted text. A new line in it is not Enter: the shell may be holding the whole paste for editing.</summary>
    private void TrackPaste(string text)
    {
        if (_session?.AlternateScreen != false) _typed.Invalidate();
        else _typed.Type(text);

        if (_suggestions) InvalidateVisual();
    }

    /// <summary>Follows a key on its way to the shell. Anything that is not typing makes the line unknown until the next Enter.</summary>
    private void TrackKey(TerminalKey key, TerminalMods mods)
    {
        if (_session?.AlternateScreen != false)
        {
            _typed.Invalidate();
            return;
        }

        switch (key)
        {
            case TerminalKey.Enter or TerminalKey.NumPadEnter when mods == TerminalMods.None:
                EnterPressed();
                break;
            case TerminalKey.Backspace when mods is TerminalMods.None or TerminalMods.Shift:
                _typed.Backspace();
                break;

            // Ctrl+C and Ctrl+U leave a shell with an empty line, which is as good as known.
            case TerminalKey.C or TerminalKey.U when mods == TerminalMods.Control:
                _typed.Reset();
                break;
            default:
                _typed.Invalidate();
                break;
        }

        if (_suggestions) InvalidateVisual();
    }

    /// <summary>Called before Enter is sent, while the line is still on the cursor's row.</summary>
    private void EnterPressed()
    {
        string? line = _typed.Enter();
        if (line is null || !_suggestions || HistoryPaused || History is not CommandHistory history || _session is not TerminalSession session) return;

        // With a shell that marks its prompts, a line typed anywhere else is an answer to some program.
        if (session.PromptMarksSeen && !session.CursorAtPrompt) return;

        // A line the shell does not show is a password, or not the line the keys suggest.
        if (TypedLine.Echoed(line, session.TextBeforeCursor(), settled: false)) history.Add(line, HistoryScope);
    }

    /// <summary>Sends typed text, turning it into the control or escape sequence a waiting Ctrl or Alt asks for.</summary>
    public void TypeWithSticky(string text)
    {
        TerminalMods mods = StickyMods;
        StickyMods = TerminalMods.None;

        // Ctrl with a letter is that letter's control code; Alt puts an escape in front.
        if (mods.HasFlag(TerminalMods.Control) && text.Length == 1 && text[0] is >= '@' and <= '~') text = ((char)(text[0] & 0x1F)).ToString();
        if (mods.HasFlag(TerminalMods.Alt)) text = "\u001b" + text;
        TrackText(text);
        ForEachTarget(session => session.SendText(text));
    }

    /// <summary>Two fingers moved apart or together by this factor, asking for larger or smaller text.</summary>
    public event Action<double>? ZoomRequested;

    /// <summary>A web address in the output was clicked with Ctrl held.</summary>
    public event Action<Uri>? LinkRequested;

    /// <summary>
    /// How to write a dropped file's path for the shell in this terminal, so that the shell takes it as a name and
    /// runs nothing in it. Gives null for a path that shell has no safe way of writing.
    /// </summary>
    public Func<string, string?> QuotePath { get; set; } = ShellQuote.Posix;

    /// <summary>Asked before pasting text that would run as soon as it arrives.</summary>
    public Func<string, Task<bool>>? ConfirmPaste { get; set; }

    public TerminalView()
    {
        // A terminal grid runs left to right whatever language the rest of the app is in.
        FlowDirection = FlowDirection.LeftToRight;

        Focusable = true;
        FocusAdorner = null;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        // Dragging a selection past the top or bottom edge keeps moving through the history.
        _dragScrollTimer = new DispatcherTimer { Interval = DragScrollInterval };
        _dragScrollTimer.Tick += (_, _) => ScrollWhileDragging();

        DragDrop.SetAllowDrop(this, true);
        DragDrop.AddDragOverHandler(this, (_, e) => e.DragEffects = _session is not null && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        DragDrop.AddDropHandler(this, OnFilesDropped);

        // What makes a phone bring its keyboard up when the terminal is tapped.
        if (AppServices.MobileLayout)
        {
            var client = _keyboard = new KeyboardClient(this);
            AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = client);
        }

        // On a touch screen two fingers resize the text. The size is applied once they lift.
        GestureRecognizers.Add(new PinchGestureRecognizer());
        AddHandler(InputElement.PinchEvent, (_, e) => _pinchScale = e.Scale);
        AddHandler(InputElement.PinchEndedEvent, (_, _) =>
        {
            if (Math.Abs(_pinchScale - 1) > 0.05) ZoomRequested?.Invoke(_pinchScale);
            _pinchScale = 1;
        });

        _blinkTimer = new DispatcherTimer { Interval = BlinkInterval };
        _blinkTimer.Tick += (_, _) =>
        {
            if (!_cursorBlinks) return;
            _cursorOn = !_cursorOn;
            InvalidateVisual();
        };
    }

    /// <summary>Connects once the size of the terminal is known, so the shell starts at the right size.</summary>
    public void Connect(TerminalConnector connector)
    {
        _pendingConnect = connector;
        TryStartConnect();

        // A view in a background tab has no size yet. It connects anyway and is resized when first shown.
        DispatcherTimer.RunOnce(ConnectWithoutSize, HiddenConnectDelay);
    }

    private (int Columns, int Rows) GridSize(TerminalFonts fonts) =>
        (Math.Max(2, (int)(Bounds.Width / fonts.CellWidth)), Math.Max(1, (int)(Bounds.Height / fonts.CellHeight)));

    private TerminalPixels Pixels()
    {
        TerminalFonts fonts = _renderer!.Fonts;
        return new TerminalPixels(
            (int)(Bounds.Width * fonts.Scale),
            (int)(Bounds.Height * fonts.Scale),
            (int)(fonts.CellWidth * fonts.Scale),
            (int)(fonts.CellHeight * fonts.Scale));
    }

    private void ConnectWithoutSize()
    {
        if (_pendingConnect is not TerminalConnector connector) return;
        _pendingConnect = null;
        _ = RunConnectAsync(connector, (HiddenColumns, HiddenRows));
    }

    private void TryStartConnect()
    {
        if (_pendingConnect is null || _renderer is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        TerminalConnector connector = _pendingConnect;
        _pendingConnect = null;
        _ = RunConnectAsync(connector, GridSize(_renderer.Fonts));
    }

    private async Task RunConnectAsync(TerminalConnector connector, (int Columns, int Rows) size)
    {
        try
        {
            ITerminalTransport transport = await connector(size.Columns, size.Rows, CancellationToken.None);
            var session = new TerminalSession(transport, size.Columns, size.Rows, _appearance.Theme);
            session.Changed += () => Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
            session.Ended += () => Dispatcher.UIThread.Post(() =>
            {
                // A session replaced by a reconnect has nothing left to report.
                if (_session == session) SessionEnded?.Invoke();
            });
            session.ZmodemHost = ZmodemHost;
            _session = session;
            _typed.Reset();
            session.Start();
            ApplySize();
            InvalidateVisual();
            SessionStarted?.Invoke(session);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ConnectFailed?.Invoke(ex.Message);
        }
    }

    /// <summary>Ends the connection. Safe to call more than once.</summary>
    public async Task CloseAsync()
    {
        _blinkTimer.Stop();
        TerminalSession? session = _session;
        _session = null;
        if (session is not null) await session.DisposeAsync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // A view moving to another window or tab starts empty, so it asks for the whole screen once.
        CreateRenderer();
        TryStartConnect();
        InvalidateVisual();
    }

    private void CreateRenderer()
    {
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _renderer = new TerminalRenderer(TerminalFonts.For(scale, _appearance.FontSize), _appearance.Ligatures, _appearance.Highlights, _appearance.Theme.SelectionBackground);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blinkTimer.Stop();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        TryStartConnect();
        ApplySize();
    }

    private void ApplySize()
    {
        if (_session is null || _renderer is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var (columns, rows) = GridSize(_renderer.Fonts);
        TerminalPixels pixels = Pixels();
        _session.Resize(columns, rows, pixels.CellWidth, pixels.CellHeight);
        GridChanged?.Invoke();
    }

    public override void Render(DrawingContext context) =>
        context.Custom(new DrawOperation(this, new Rect(Bounds.Size), IsFocused, _cursorOn, IsFocused ? SuggestedRest() : NoSuggestion()));

    /// <summary>
    /// Tells the on-screen keyboard that this is somewhere to type. A terminal has no text of its own to
    /// offer for prediction or correction, so the keyboard is given none and what is typed goes straight through.
    /// </summary>
    private sealed class KeyboardClient(TerminalView owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;

        /// <summary>Asks the system to bring the on-screen keyboard up.</summary>
        public void Show() => RaiseInputPaneActivationRequested();

        public override bool SupportsPreedit => false;

        public override bool SupportsSurroundingText => false;

        public override string SurroundingText => string.Empty;

        public override Rect CursorRectangle => new(0, Math.Max(0, owner.Bounds.Height - 1), 1, 1);

        public override TextSelection Selection
        {
            get => default;
            set { }
        }
    }

    // Called on the render thread. One wake-up is enough however many frames ask for it.
    private void ScheduleAnimationFrame(TimeSpan wait)
    {
        if (Interlocked.Exchange(ref _animationScheduled, 1) != 0) return;

        Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(() =>
        {
            Volatile.Write(ref _animationScheduled, 0);
            if (_session is not null && TopLevel.GetTopLevel(this) is not null) InvalidateVisual();
        }, wait));
    }

    private sealed class DrawOperation(TerminalView owner, Rect bounds, bool focused, bool cursorOn, string? ghost) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point p) => bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (owner._session is not TerminalSession session || owner._renderer is not TerminalRenderer renderer) return;
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not ISkiaSharpApiLeaseFeature feature) return;

            using ISkiaSharpApiLease lease = feature.Lease();
            if (renderer.Draw(lease.SkCanvas, session, (float)bounds.Width, (float)bounds.Height, focused, cursorOn, ghost) is not TerminalFrame frame) return;

            owner._cursorBlinks = frame.CursorBlinking;
            if (renderer.NextAnimation is TimeSpan wait) owner.ScheduleAnimationFrame(wait);
            DrawScrollbar(lease.SkCanvas, session.ScrollPosition, frame.Foreground);
        }

        // Shown only while there is history to move through. A full-screen program has none.
        private void DrawScrollbar(SKCanvas canvas, ScrollPosition position, uint color)
        {
            if (!position.HasHistory) return;

            float track = (float)bounds.Height - (2 * ScrollbarMargin);
            float length = Math.Max(ScrollbarMinLength, track * position.Visible / position.Total);
            float top = ScrollbarMargin + ((track - length) * position.Offset / (position.Total - position.Visible));
            float left = (float)bounds.Width - ScrollbarWidth - ScrollbarMargin;

            using var paint = new SKPaint { Color = new SKColor((byte)(color >> 16), (byte)(color >> 8), (byte)color, ScrollbarAlpha), IsAntialias = true };
            canvas.DrawRoundRect(left, top, ScrollbarWidth, length, ScrollbarWidth / 2, ScrollbarWidth / 2, paint);
        }
    }

    // Typed rather than pasted: a path is not something that should arrive wrapped in paste markers.
    private void OnFilesDropped(object? sender, DragEventArgs e)
    {
        if (_session is null || e.DataTransfer.TryGetFiles() is not { Length: > 0 } items) return;

        var paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().Select(QuotePath).ToList();
        if (paths.Count == 0) return;

        // One of the names cannot be written safely for this shell. Typing the others would leave a command
        // line that looks complete and is not, so nothing is typed.
        e.Handled = true;
        if (paths.Contains(null)) return;

        Type(string.Join(' ', paths) + " ");
        Focus();
        e.Handled = true;
    }

    private Uri? LinkAt(Point point)
    {
        var (column, row) = CellAt(point);
        return _session!.RowCells(row) is string[] cells ? TerminalLinks.At(cells, column) : null;
    }

    private void ScrollWhileDragging()
    {
        if (!_selecting || _session is null || _renderer is null)
        {
            _dragScrollTimer.Stop();
            return;
        }

        // The further past the edge, the faster it goes.
        double past = _dragPoint.Y < 0 ? _dragPoint.Y : _dragPoint.Y - Bounds.Height;
        int rows = Math.Clamp((int)(past / DragScrollStep) + Math.Sign(past), -DragScrollMaxRows, DragScrollMaxRows);
        _session.Scroll(rows);
        UpdateSelection(SelectionPhase.Drag, new Point(Math.Clamp(_dragPoint.X, 0, Bounds.Width - 1), Math.Clamp(_dragPoint.Y, 0, Bounds.Height - 1)));
    }

    /// <summary>Moves through the history so that the scrollbar's middle sits under the pointer.</summary>
    private void ScrollToPointer(Point point)
    {
        ScrollPosition position = _session!.ScrollPosition;
        if (!position.HasHistory || Bounds.Height <= 0) return;

        double middle = Math.Clamp(point.Y / Bounds.Height, 0, 1) * position.Total;
        _session.ScrollToRow((long)Math.Clamp(middle - (position.Visible / 2.0), 0, position.Total - position.Visible));
    }

    private (int Column, int Row) CellAt(Point point)
    {
        TerminalFonts fonts = _renderer!.Fonts;
        var (columns, rows) = GridSize(fonts);
        return (Math.Clamp((int)(point.X / fonts.CellWidth), 0, columns - 1), Math.Clamp((int)(point.Y / fonts.CellHeight), 0, rows - 1));
    }

    private void UpdateSelection(SelectionPhase phase, Point point)
    {
        var (column, row) = CellAt(point);
        double scale = _renderer!.Fonts.Scale;
        _session!.UpdateSelection(phase, column, row, point.X * scale, point.Y * scale, Pixels(), GridSize(_renderer.Fonts).Columns);
    }

    private void ReportMouse(MouseAction action, MouseButton button, KeyModifiers modifiers, Point point, bool anyPressed)
    {
        double scale = _renderer!.Fonts.Scale;
        _session!.SendMouse(action, button, KeyMap.Mods(modifiers), (float)(point.X * scale), (float)(point.Y * scale), Pixels(), anyPressed);
    }

    private static MouseButton ButtonOf(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => MouseButton.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => MouseButton.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => MouseButton.Middle,
        _ => MouseButton.None,
    };

    private KeyboardClient? _keyboard;

    /// <summary>Whether this is a phone, where typing needs the on-screen keyboard brought up.</summary>
    public bool HasScreenKeyboard => _keyboard is not null;

    /// <summary>Brings the phone's keyboard up for this terminal, or puts it away if it is up.</summary>
    public void ToggleKeyboard()
    {
        if (_keyboard is null || TopLevel.GetTopLevel(this) is not { } top) return;

        if (IsFocused && top.InputPane?.State == Avalonia.Controls.Platform.InputPaneState.Open)
        {
            // The keyboard follows the focus, so letting go of it is what sends the keyboard away.
            top.FocusManager?.Focus(null);
            return;
        }

        Focus();
        _keyboard.Show();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_session is null || _renderer is null) return;

        PointerPoint point = e.GetCurrentPoint(this);

        // A finger scrolls rather than selects, as it does everywhere else on a phone.
        if (e.Pointer.Type == PointerType.Touch && !_session.MouseTracking)
        {
            _touching = true;
            _touchMoved = false;
            _touchY = point.Position.Y;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        MouseButton button = ButtonOf(point.Properties.PointerUpdateKind);

        if (button == MouseButton.Left && e.KeyModifiers.HasFlag(KeyModifiers.Control) && LinkAt(point.Position) is Uri link)
        {
            LinkRequested?.Invoke(link);
            e.Handled = true;
            return;
        }

        // Shift keeps selection available inside programs that take over the mouse.
        if (_session.MouseTracking && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _mouseButton = button;
            _mouseCaptured = true;

            // A click the program gets can put its cursor anywhere.
            _typed.Invalidate();
            e.Pointer.Capture(this);
            ReportMouse(MouseAction.Press, button, e.KeyModifiers, point.Position, anyPressed: true);
            e.Handled = true;
            return;
        }

        if (button == MouseButton.Right && _appearance.RightClickPaste)
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        if (button == MouseButton.Middle)
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        if (button != MouseButton.Left) return;

        if (point.Position.X >= Bounds.Width - ScrollbarGrabWidth && _session.ScrollPosition.HasHistory)
        {
            _scrolling = true;
            e.Pointer.Capture(this);
            ScrollToPointer(point.Position);
            e.Handled = true;
            return;
        }

        _selecting = true;
        e.Pointer.Capture(this);
        UpdateSelection(SelectionPhase.Press, point.Position);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_session is null || _renderer is null) return;

        Point position = e.GetPosition(this);
        if (_touching)
        {
            int rows = (int)((_touchY - position.Y) / _renderer.Fonts.CellHeight);
            if (rows == 0) return;

            _touchMoved = true;
            _touchY -= rows * _renderer.Fonts.CellHeight;
            _session.Scroll(rows);
            return;
        }

        if (_scrolling)
        {
            ScrollToPointer(position);
            return;
        }

        if (_selecting)
        {
            _dragPoint = position;
            bool outside = position.Y < 0 || position.Y > Bounds.Height;
            if (outside && !_dragScrollTimer.IsEnabled && !_session.AlternateScreen) _dragScrollTimer.Start();
            else if (!outside) _dragScrollTimer.Stop();

            UpdateSelection(SelectionPhase.Drag, position);
            return;
        }

        if (_mouseCaptured) ReportMouse(MouseAction.Motion, _mouseButton, e.KeyModifiers, position, anyPressed: true);
        else if (_session.MouseTracking) ReportMouse(MouseAction.Motion, MouseButton.None, e.KeyModifiers, position, anyPressed: false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_session is null || _renderer is null) return;

        Point position = e.GetPosition(this);
        if (_touching)
        {
            _touching = false;
            e.Pointer.Capture(null);

            // A tap that went nowhere may still have been on a link.
            if (!_touchMoved && LinkAt(position) is Uri tapped) LinkRequested?.Invoke(tapped);
        }
        else if (_scrolling)
        {
            _scrolling = false;
            e.Pointer.Capture(null);
        }
        else if (_selecting)
        {
            _selecting = false;
            _dragScrollTimer.Stop();
            e.Pointer.Capture(null);
            UpdateSelection(SelectionPhase.Release, position);
            if (_appearance.CopyOnSelect) _ = CopyAsync();
        }
        else if (_mouseCaptured)
        {
            _mouseCaptured = false;
            e.Pointer.Capture(null);
            ReportMouse(MouseAction.Release, _mouseButton, e.KeyModifiers, position, anyPressed: false);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_session is null || _renderer is null || e.Delta.Y == 0) return;

        e.Handled = true;
        bool up = e.Delta.Y > 0;

        if (_session.MouseTracking)
        {
            ReportMouse(MouseAction.Press, up ? MouseButton.WheelUp : MouseButton.WheelDown, e.KeyModifiers, e.GetPosition(this), anyPressed: false);
        }
        else if (_session.AlternateScreen)
        {
            // Full-screen programs have no history, and the wheel scrolls their content instead.
            TerminalKey arrow = up ? TerminalKey.ArrowUp : TerminalKey.ArrowDown;
            for (int i = 0; i < WheelRowsPerNotch; i++) _session.SendKey(arrow, TerminalMods.None, 0, null);
        }
        else
        {
            _session.Scroll((int)Math.Round(-e.Delta.Y * WheelRowsPerNotch));
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _cursorOn = true;
        _blinkTimer.Start();
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _blinkTimer.Stop();
        _cursorOn = true;
        InvalidateVisual();
    }

    /// <summary>Copies the selection. Returns false when nothing is selected.</summary>
    public async Task<bool> CopyAsync()
    {
        if (_session?.SelectedText() is not { Length: > 0 } text) return false;
        if (TopLevel.GetTopLevel(this)?.Clipboard is IClipboard clipboard) await clipboard.SetTextAsync(text);
        return true;
    }

    /// <summary>Pastes from the clipboard. The keyboard shortcut and the menu both end up here, exactly once.</summary>
    public async Task PasteAsync()
    {
        if (_session is null || TopLevel.GetTopLevel(this)?.Clipboard is not IClipboard clipboard) return;
        if (await ReadClipboardAsync(clipboard) is { Length: > 0 } text) await PasteTextAsync(text);
    }

    // Another program can hold the system clipboard for a moment, and a paste that silently does nothing is the worst outcome.
    private static async Task<string?> ReadClipboardAsync(IClipboard clipboard)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (await clipboard.TryGetTextAsync() is { Length: > 0 } text) return text;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException && attempt < ClipboardAttempts)
            {
                // Still held by the other program.
            }

            if (attempt >= ClipboardAttempts) return null;
            await Task.Delay(ClipboardRetryDelay);
        }
    }

    public async Task PasteTextAsync(string text)
    {
        if (_session is null) return;

        bool reachesOthers = InputTargets?.Invoke().Skip(1).Any() == true;
        if ((reachesOthers || !TerminalSession.IsPasteSafe(text)) && ConfirmPaste is not null && !await ConfirmPaste(text)) return;
        TrackPaste(text);
        ForEachTarget(session => session.Paste(text));
    }

    /// <summary>Sends text as if it had been typed, to every terminal typing currently reaches.</summary>
    public void Type(string text)
    {
        TrackText(text);
        ForEachTarget(session => session.SendText(text));
    }

    private void ForEachTarget(Action<TerminalSession> send)
    {
        if (InputTargets is not null)
        {
            foreach (TerminalSession session in InputTargets()) send(session);
        }
        else if (_session is not null)
        {
            send(_session);
        }
    }

    private static bool IsCopy(KeyEventArgs e) =>
        e.Key == Key.C && (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) || e.KeyModifiers == KeyModifiers.Meta);

    private static bool IsPaste(KeyEventArgs e) =>
        (e.Key == Key.V && (e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) || e.KeyModifiers == KeyModifiers.Meta))
        || (e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Shift);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_session is null) return;

        if (IsCopy(e))
        {
            _ = CopyAsync();
            e.Handled = true;
            return;
        }

        if (IsPaste(e))
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        bool printable = e.KeySymbol is { Length: > 0 } symbol && !char.IsControl(symbol[0]);

        // Plain characters arrive through text input, which also covers IMEs and dead keys.
        // AltGr is reported as Ctrl+Alt on Windows and produces a normal character too.
        if (printable && ctrl == alt) return;

        if (!KeyMap.TryGet(e.PhysicalKey, e.Key, out TerminalKey key)) return;
        TerminalMods mods = KeyMap.Mods(e.KeyModifiers);
        if (TakeSuggestion(key, mods))
        {
            e.Handled = true;
            return;
        }

        uint unshifted = KeyMap.Unshifted(e.Key, key);
        string? text = printable ? e.KeySymbol : null;
        bool sent = false;

        // Enter is looked at before it is sent, while the line it ends is still the one on screen. A key
        // that sends nothing, such as Shift on its own, changes nothing about the line.
        bool enter = key is TerminalKey.Enter or TerminalKey.NumPadEnter;
        if (enter) TrackKey(key, mods);
        ForEachTarget(session => sent |= session.SendKey(key, mods, unshifted, text));
        if (sent && !enter) TrackKey(key, mods);
        e.Handled = sent;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_session is null || string.IsNullOrEmpty(e.Text)) return;
        if (_stickyMods != TerminalMods.None) TypeWithSticky(e.Text);
        else
        {
            string typed = e.Text;
            TrackText(typed);
            ForEachTarget(session => session.SendText(typed));
        }

        e.Handled = true;
    }
}

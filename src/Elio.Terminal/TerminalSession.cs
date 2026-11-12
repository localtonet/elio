using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Elio.Terminal.Native;

namespace Elio.Terminal;

/// <summary>Joins one connection to one terminal engine. Drawing and input go through here.</summary>
public sealed partial class TerminalSession : IAsyncDisposable
{
    private const int ReadBufferSize = 32 * 1024;
    private const int MaxCluster = 16;

    // A notification comes from whatever is running on the server, so it does not get to be any length it likes.
    private const int MaxNotificationBytes = 1024;
    private const int MaxDirectoryBytes = 4096;

    private const long MaxImagePixels = TerminalImages.MaxPixels;

    private readonly ITerminalTransport _transport;

    // The engine is not thread-safe: connection output, keys and resizes all arrive from different threads.
    private readonly Lock _terminalGate = new();

    // The render state is only a copy, so drawing holds this one and output keeps flowing meanwhile.
    private readonly Lock _frameGate = new();

    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private GCHandle _self;

    private nint _terminal;
    private nint _renderState;
    private nint _rowIterator;
    private nint _rowCells;
    private nint _keyEncoder;
    private nint _keyEvent;
    private nint _selectionGesture;
    private nint _gesturePress;
    private nint _gestureDrag;
    private nint _gestureRelease;
    private nint _mouseEncoder;
    private nint _mouseEvent;
    private nint _search;
    private nint _imageIterator;
    private uint _selectionBackground;
    private const int ZmodemMarkerLength = 6;

    private OutputLog? _log;
    private volatile ZmodemTransfer? _zmodem;
    private readonly ZmodemDetector _zmodemDetector = new();

    private TerminalCell[] _rowBuffer = [];
    private int _changedPending;
    private int _columns;
    private int _rows;
    private int _disposed;

    /// <summary>
    /// The screen has something new. Raised on any thread, and not again until the next
    /// <see cref="ReadFrame"/>, so a flood of output costs one redraw per frame.
    /// </summary>
    public event Action? Changed;

    /// <summary>The connection is over.</summary>
    public event Action? Ended;

    /// <summary>The running program rang the bell. Raised on the thread output arrives on, once per batch of output.</summary>
    public event Action? Bell;

    /// <summary>The running program asked for a notification, with a title that may be empty and a body.</summary>
    public event Action<string, string>? Notified;

    /// <summary>The running program reported how far along it is, or that it is no longer reporting.</summary>
    public event Action<TerminalProgress>? ProgressChanged;

    /// <summary>What the running program last reported about its progress.</summary>
    public TerminalProgress Progress { get; private set; }

    /// <summary>The shell moved to another folder, and said so.</summary>
    public event Action<string>? WorkingDirectoryChanged;

    /// <summary>The folder the shell says it is in, as it reported it. Null until it reports one, which not every shell does.</summary>
    public string? WorkingDirectory { get; private set; }

    private bool _directoryPending;

    // Set by the engine in the middle of taking output, and raised once it has finished.
    private bool _bellPending;
    private (string Title, string Body)? _notePending;
    private TerminalProgress? _progressPending;

    public string? EndReason { get; private set; }

    /// <summary>Set once the session is over: the link broke, rather than the program on the other side finishing.</summary>
    public bool ConnectionLost { get; private set; }

    /// <summary>A short note about the connection, for the status bar.</summary>
    public string? Description => _transport.Description;

    /// <summary>Answers Zmodem transfers. Without one, the sequences that start them are ordinary output.</summary>
    public IZmodemHost? ZmodemHost { get; set; }

    /// <summary>The file output is being saved to, if any.</summary>
    public string? LogPath => _log?.Path;

    public unsafe TerminalSession(ITerminalTransport transport, int columns, int rows, TerminalTheme? theme = null, int scrollbackLines = 10_000)
    {
        _transport = transport;
        _columns = columns;
        _rows = rows;

        fixed (nint* terminal = &_terminal) Check(Ghostty.TerminalNew(0, terminal, (ushort)columns, (ushort)rows));
        fixed (nint* state = &_renderState) Check(Ghostty.RenderStateNew(0, state));
        fixed (nint* iterator = &_rowIterator) Check(Ghostty.RowIteratorNew(0, iterator));
        fixed (nint* cells = &_rowCells) Check(Ghostty.RowCellsNew(0, cells));
        fixed (nint* encoder = &_keyEncoder) Check(Ghostty.KeyEncoderNew(0, encoder));
        fixed (nint* keyEvent = &_keyEvent) Check(Ghostty.KeyEventNew(0, keyEvent));
        fixed (nint* gesture = &_selectionGesture) Check(Ghostty.SelectionGestureNew(0, gesture));
        fixed (nint* press = &_gesturePress) Check(Ghostty.GestureEventNew(0, press, Ghostty.GestureEventPress));
        fixed (nint* drag = &_gestureDrag) Check(Ghostty.GestureEventNew(0, drag, Ghostty.GestureEventDrag));
        fixed (nint* release = &_gestureRelease) Check(Ghostty.GestureEventNew(0, release, Ghostty.GestureEventRelease));
        fixed (nint* mouseEncoder = &_mouseEncoder) Check(Ghostty.MouseEncoderNew(0, mouseEncoder));
        fixed (nint* mouseEvent = &_mouseEvent) Check(Ghostty.MouseEventNew(0, mouseEvent));
        fixed (nint* images = &_imageIterator) Check(Ghostty.ImageIteratorNew(0, images));

        ApplyTheme(theme ?? TerminalTheme.Default);

        _self = GCHandle.Alloc(this);
        Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptUserdata, (void*)GCHandle.ToIntPtr(_self)));
        Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptWritePty, (delegate* unmanaged<nint, nint, byte*, nuint, void>)&OnEngineReply));
        Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptBell, (delegate* unmanaged<nint, nint, void>)&OnEngineBell));
        Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptPwdChanged, (delegate* unmanaged<nint, nint, void>)&OnEngineDirectory));
        Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptDesktopNotification, (delegate* unmanaged<nint, nint, Ghostty.DesktopNotification*, void>)&OnEngineNotification));
        Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptProgressReport, (delegate* unmanaged<nint, nint, Ghostty.ProgressReport*, void>)&OnEngineProgress));
        Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptSemanticPrompt, (delegate* unmanaged<nint, nint, Ghostty.SemanticPrompt*, void>)&OnEnginePrompt));

        // The engine also limits the history by size, to about 400 rows of a wide terminal unless told
        // otherwise, and whichever limit is reached first wins. The number of lines is the one that is meant.
        nuint scrollback = (nuint)scrollbackLines;
        nuint unlimited = nuint.MaxValue;
        Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptScrollbackMaxBytes, &unlimited));
        Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptScrollbackMaxLines, &scrollback));
    }

    private static void Check(int result)
    {
        if (result != Ghostty.Success) throw new InvalidOperationException($"libghostty-vt returned {result}.");
    }

    private static Ghostty.Rgb ToRgb(uint color) => new() { R = (byte)(color >> 16), G = (byte)(color >> 8), B = (byte)color };

    /// <summary>Changes the colors of a running terminal.</summary>
    public void SetTheme(TerminalTheme theme)
    {
        lock (_terminalGate)
        {
            if (_disposed != 0) return;
            ApplyTheme(theme);
        }
        SignalChanged();
    }

    private unsafe void ApplyTheme(TerminalTheme theme)
    {
        _selectionBackground = theme.SelectionBackground;

        Ghostty.Rgb foreground = ToRgb(theme.Foreground);
        Ghostty.Rgb background = ToRgb(theme.Background);
        Ghostty.Rgb cursor = ToRgb(theme.Cursor);
        Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptColorForeground, &foreground));
        Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptColorBackground, &background));
        Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptColorCursor, &cursor));

        Ghostty.Rgb* palette = stackalloc Ghostty.Rgb[256];
        Ghostty.ColorPaletteDefault(palette);
        for (int i = 0; i < theme.Ansi.Length; i++) palette[i] = ToRgb(theme.Ansi[i]);
        Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptColorPalette, palette));
    }

    public void Start()
    {
        _ = ReadLoopAsync();
        _ = WriteLoopAsync();
    }

    // Runs inside the engine's write, so it may only queue. The bytes are the terminal answering a program's question.
    [UnmanagedCallersOnly]
    private static unsafe void OnEngineReply(nint terminal, nint userdata, byte* data, nuint length)
    {
        var session = (TerminalSession)GCHandle.FromIntPtr(userdata).Target!;
        session._outgoing.Writer.TryWrite(new ReadOnlySpan<byte>(data, (int)length).ToArray());
    }

    // These three also run inside the engine's write, so they only take note. Feed raises the events afterwards.
    [UnmanagedCallersOnly]
    private static void OnEngineBell(nint terminal, nint userdata) =>
        ((TerminalSession)GCHandle.FromIntPtr(userdata).Target!)._bellPending = true;

    [UnmanagedCallersOnly]
    private static void OnEngineDirectory(nint terminal, nint userdata) =>
        ((TerminalSession)GCHandle.FromIntPtr(userdata).Target!)._directoryPending = true;

    [UnmanagedCallersOnly]
    private static unsafe void OnEngineNotification(nint terminal, nint userdata, Ghostty.DesktopNotification* notification)
    {
        static string Text(Ghostty.NativeString text) =>
            text.Pointer is null || text.Length == 0 ? string.Empty : Encoding.UTF8.GetString(text.Pointer, (int)Math.Min(text.Length, MaxNotificationBytes));

        var session = (TerminalSession)GCHandle.FromIntPtr(userdata).Target!;
        session._notePending = (Text(notification->Title), Text(notification->Body));
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnEngineProgress(nint terminal, nint userdata, Ghostty.ProgressReport* report)
    {
        var session = (TerminalSession)GCHandle.FromIntPtr(userdata).Target!;
        var state = report->State is >= 0 and <= (int)ProgressState.Paused ? (ProgressState)report->State : ProgressState.None;
        session._progressPending = new TerminalProgress(state, Math.Clamp((int)report->Progress, 0, 100));
    }

    private async Task ReadLoopAsync()
    {
        byte[] buffer = new byte[ReadBufferSize];
        try
        {
            while (true)
            {
                int count = await _transport.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (count <= 0) break;
                await DeliverAsync(buffer, count).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Closed on purpose.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            EndReason = ex.Message;
        }

        _zmodem?.ConnectionEnded();
        ConnectionLost = _transport.ConnectionLost;
        _outgoing.Writer.TryComplete();
        Ended?.Invoke();
    }

    private async ValueTask DeliverAsync(byte[] buffer, int count)
    {
        if (_zmodem is ZmodemTransfer running)
        {
            if (!await running.ReceiveAsync(buffer[..count], _stop.Token).ConfigureAwait(false)) Feed(buffer.AsSpan(0, count));
            return;
        }

        if (ZmodemHost is IZmodemHost host && _zmodemDetector.Find(buffer.AsSpan(0, count)) is var (end, serverSends))
        {
            // What came before the marker is ordinary output.
            int before = Math.Max(0, end - ZmodemMarkerLength);
            if (before > 0) Feed(buffer.AsSpan(0, before));

            var transfer = new ZmodemTransfer(serverSends, buffer[end..count], (data, cancellation) => _transport.WriteAsync(data, cancellation), host);
            _zmodem = transfer;
            _ = RunZmodemAsync(transfer);
            return;
        }

        Feed(buffer.AsSpan(0, count));
    }

    private async Task RunZmodemAsync(ZmodemTransfer transfer)
    {
        try
        {
            await transfer.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            _zmodem = null;
            foreach (byte[] leftover in transfer.TakeLeftovers()) Feed(leftover);
        }
    }

    /// <summary>A Zmodem transfer is using the connection, so typing is held back.</summary>
    public bool ZmodemActive => _zmodem is not null;

    /// <summary>Stops a running Zmodem transfer.</summary>
    public void CancelZmodem() => _zmodem?.Cancel();

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (byte[] data in _outgoing.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                await _transport.WriteAsync(data, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Closed on purpose.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            EndReason ??= ex.Message;
        }
    }

    /// <summary>Puts program output into the terminal.</summary>
    public unsafe void Feed(ReadOnlySpan<byte> data)
    {
        bool bell;
        (string Title, string Body)? note;
        TerminalProgress? progress;
        string? directory = null;
        CommandFinished[]? finished = null;

        lock (_terminalGate)
        {
            if (_disposed != 0) return;
            _log?.Write(data);
            _contentVersion++;
            fixed (byte* pointer = data) Write(pointer, (nuint)data.Length);

            if (_finishedPending.Count > 0)
            {
                finished = [.. _finishedPending];
                _finishedPending.Clear();
            }

            (bell, note, progress) = (_bellPending, _notePending, _progressPending);
            (_bellPending, _notePending, _progressPending) = (false, null, null);
            if (progress is TerminalProgress latest) Progress = latest;

            if (_directoryPending)
            {
                _directoryPending = false;
                Ghostty.NativeString folder = default;
                if (Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataPwd, &folder) == Ghostty.Success && folder.Pointer is not null && folder.Length is > 0 and < MaxDirectoryBytes)
                {
                    string value = Encoding.UTF8.GetString(folder.Pointer, (int)folder.Length);
                    if (value != WorkingDirectory) directory = WorkingDirectory = value;
                }
            }
        }

        SignalChanged();
        if (bell) Bell?.Invoke();
        if (note is var (title, body)) Notified?.Invoke(title, body);
        if (progress is TerminalProgress reported) ProgressChanged?.Invoke(reported);
        if (directory is not null) WorkingDirectoryChanged?.Invoke(directory);
        if (finished is not null)
        {
            foreach (CommandFinished command in finished) CommandFinished?.Invoke(command);
        }
    }

    private void SignalChanged()
    {
        if (Interlocked.Exchange(ref _changedPending, 1) == 0) Changed?.Invoke();
    }

    /// <summary>Sends text the user typed or pasted, exactly as given.</summary>
    public void SendText(string text)
    {
        if (ZmodemActive) return;
        JumpToBottom();
        _outgoing.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Sends bytes that are not text, such as a hex sequence for a serial device.</summary>
    public void SendBytes(ReadOnlySpan<byte> data)
    {
        if (ZmodemActive || data.IsEmpty) return;
        _outgoing.Writer.TryWrite(data.ToArray());
    }

    /// <summary>Encodes a key the way the running program asked for. Returns false if the key produces nothing.</summary>
    public unsafe bool SendKey(TerminalKey key, TerminalMods mods, uint unshiftedCodepoint, string? text)
    {
        if (ZmodemActive) return true;

        byte[] utf8 = text is null ? [] : Encoding.UTF8.GetBytes(text);
        Span<byte> encoded = stackalloc byte[128];
        nuint written = 0;

        lock (_terminalGate)
        {
            if (_disposed != 0) return false;
            Check(Ghostty.KeyEncoderSetFromTerminal(_keyEncoder, _terminal));
            Check(Ghostty.KeyEventSetAction(_keyEvent, Ghostty.KeyActionPress));
            Check(Ghostty.KeyEventSetKey(_keyEvent, (int)key));
            Check(Ghostty.KeyEventSetMods(_keyEvent, (ushort)mods));
            Check(Ghostty.KeyEventSetUnshiftedCodepoint(_keyEvent, unshiftedCodepoint));

            // The event borrows the text, so it has to be encoded before this pointer goes away.
            fixed (byte* textPointer = utf8)
            fixed (byte* output = encoded)
            {
                Check(Ghostty.KeyEventSetUtf8(_keyEvent, new Ghostty.NativeString { Pointer = textPointer, Length = (nuint)utf8.Length }));
                Check(Ghostty.KeyEncoderEncode(_keyEncoder, _keyEvent, output, (nuint)encoded.Length, &written));
                Check(Ghostty.KeyEventSetUtf8(_keyEvent, default));
            }
        }

        if (written == 0) return false;
        JumpToBottom();
        _outgoing.Writer.TryWrite(encoded[..(int)written].ToArray());
        return true;
    }

    public void Resize(int columns, int rows, int cellWidthPixels, int cellHeightPixels)
    {
        if (columns == _columns && rows == _rows) return;
        _columns = columns;
        _rows = rows;

        lock (_terminalGate)
        {
            if (_disposed != 0) return;
            _contentVersion++;
            Check(Ghostty.TerminalResize(_terminal, (ushort)columns, (ushort)rows, (uint)cellWidthPixels, (uint)cellHeightPixels));
        }

        _ = ResizeTransportAsync(columns, rows);
        SignalChanged();
    }

    private async Task ResizeTransportAsync(int columns, int rows)
    {
        try
        {
            await _transport.ResizeAsync(columns, rows, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            EndReason ??= ex.Message;
        }
    }

    /// <summary>Starts saving output to a file, or stops if it was already saving.</summary>
    public void ToggleLog(string path)
    {
        lock (_terminalGate)
        {
            if (_disposed != 0) return;

            if (_log is not null)
            {
                _log.Dispose();
                _log = null;
            }
            else
            {
                _log = new OutputLog(path);
            }
        }
    }

    /// <summary>The running program has asked to receive the mouse.</summary>
    public unsafe bool MouseTracking
    {
        get
        {
            byte tracking = 0;
            lock (_terminalGate)
            {
                if (_disposed != 0) return false;
                Check(Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataMouseTracking, &tracking));
            }
            return tracking != 0;
        }
    }

    /// <summary>A full-screen program like vim is showing, which has no history to scroll.</summary>
    public unsafe bool AlternateScreen
    {
        get
        {
            int screen = 0;
            lock (_terminalGate)
            {
                if (_disposed != 0) return false;
                Check(Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataActiveScreen, &screen));
            }
            return screen == Ghostty.ScreenAlternate;
        }
    }

    /// <summary>Reports the mouse to the running program.</summary>
    public unsafe void SendMouse(MouseAction action, MouseButton button, TerminalMods mods, float x, float y, TerminalPixels size, bool anyButtonPressed)
    {
        Span<byte> encoded = stackalloc byte[128];
        nuint written = 0;

        lock (_terminalGate)
        {
            if (_disposed != 0) return;

            var encoderSize = new Ghostty.MouseEncoderSize
            {
                Size = (nuint)sizeof(Ghostty.MouseEncoderSize),
                ScreenWidth = (uint)size.Width,
                ScreenHeight = (uint)size.Height,
                CellWidth = (uint)size.CellWidth,
                CellHeight = (uint)size.CellHeight,
            };
            byte pressed = anyButtonPressed ? (byte)1 : (byte)0;

            Check(Ghostty.MouseEncoderSetFromTerminal(_mouseEncoder, _terminal));
            Check(Ghostty.MouseEncoderSet(_mouseEncoder, Ghostty.MouseEncoderOptSize, &encoderSize));
            Check(Ghostty.MouseEncoderSet(_mouseEncoder, Ghostty.MouseEncoderOptAnyButtonPressed, &pressed));
            Check(Ghostty.MouseEventSetAction(_mouseEvent, (int)action));
            Check(button == MouseButton.None ? Ghostty.MouseEventClearButton(_mouseEvent) : Ghostty.MouseEventSetButton(_mouseEvent, (int)button));
            Check(Ghostty.MouseEventSetMods(_mouseEvent, (ushort)mods));
            Check(Ghostty.MouseEventSetPosition(_mouseEvent, new Ghostty.MousePosition { X = x, Y = y }));

            fixed (byte* output = encoded)
                Check(Ghostty.MouseEncoderEncode(_mouseEncoder, _mouseEvent, output, (nuint)encoded.Length, &written));
        }

        if (written > 0) _outgoing.Writer.TryWrite(encoded[..(int)written].ToArray());
    }

    /// <summary>
    /// Feeds a mouse gesture to the selection logic, which handles dragging and double and triple clicks.
    /// </summary>
    public unsafe void UpdateSelection(SelectionPhase phase, int column, int row, double x, double y, TerminalPixels size, int columns)
    {
        const double RepeatDistancePixels = 8;
        const ulong RepeatIntervalNs = 500_000_000;

        lock (_terminalGate)
        {
            if (_disposed != 0) return;

            nint gestureEvent = phase switch
            {
                SelectionPhase.Press => _gesturePress,
                SelectionPhase.Drag => _gestureDrag,
                _ => _gestureRelease,
            };

            if (phase != SelectionPhase.Release)
            {
                var point = new Ghostty.Point { Tag = Ghostty.PointTagViewport, X = (ushort)column, Y = (uint)row };
                Ghostty.GridRef reference = default;
                if (Ghostty.TerminalGridRef(_terminal, point, &reference) != Ghostty.Success) return;

                var position = new Ghostty.SurfacePosition { X = x, Y = y };
                Check(Ghostty.GestureEventSet(gestureEvent, Ghostty.GestureOptRef, &reference));
                Check(Ghostty.GestureEventSet(gestureEvent, Ghostty.GestureOptPosition, &position));
            }

            if (phase == SelectionPhase.Press)
            {
                double distance = RepeatDistancePixels;
                ulong interval = RepeatIntervalNs;
                ulong now = (ulong)(System.Diagnostics.Stopwatch.GetTimestamp() * (1_000_000_000.0 / System.Diagnostics.Stopwatch.Frequency));
                Check(Ghostty.GestureEventSet(gestureEvent, Ghostty.GestureOptRepeatDistance, &distance));
                Check(Ghostty.GestureEventSet(gestureEvent, Ghostty.GestureOptRepeatIntervalNs, &interval));
                Check(Ghostty.GestureEventSet(gestureEvent, Ghostty.GestureOptTimeNs, &now));
            }

            if (phase == SelectionPhase.Drag)
            {
                var geometry = new Ghostty.GestureGeometry { Columns = (uint)columns, CellWidth = (uint)size.CellWidth, ScreenHeight = (uint)size.Height };
                Check(Ghostty.GestureEventSet(gestureEvent, Ghostty.GestureOptGeometry, &geometry));
            }

            Ghostty.Selection selection = default;
            int result = Ghostty.SelectionGestureEvent(_selectionGesture, _terminal, gestureEvent, &selection);
            if (result == Ghostty.Success) Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptSelection, &selection));
            else if (phase == SelectionPhase.Press) Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptSelection, null));
        }

        SignalChanged();
    }

    public unsafe void ClearSelection()
    {
        lock (_terminalGate)
        {
            if (_disposed != 0) return;
            Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptSelection, null));
        }
        SignalChanged();
    }

    /// <summary>The selected text, with soft-wrapped lines joined back together. Null if nothing is selected.</summary>
    public unsafe string? SelectedText()
    {
        lock (_terminalGate)
        {
            if (_disposed != 0) return null;

            Ghostty.Selection selection = default;
            if (Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataSelection, &selection) != Ghostty.Success) return null;
            return PlainText(&selection);
        }
    }

    /// <summary>The text inside a selection, as it would be copied. Called with the terminal held.</summary>
    private unsafe string PlainText(Ghostty.Selection* selection)
    {
        nint formatter = 0;
        Check(Ghostty.FormatterTerminalNew(0, &formatter, _terminal));
        try
        {
            int emit = Ghostty.FormatterFormatPlain;
            byte yes = 1;
            Check(Ghostty.FormatterSet(formatter, Ghostty.FormatterOptEmit, &emit));
            Check(Ghostty.FormatterSet(formatter, Ghostty.FormatterOptUnwrap, &yes));
            Check(Ghostty.FormatterSet(formatter, Ghostty.FormatterOptTrim, &yes));
            Check(Ghostty.FormatterSet(formatter, Ghostty.FormatterOptSelection, selection));

            byte* output = null;
            nuint length = 0;
            Check(Ghostty.FormatterFormatAlloc(formatter, 0, &output, &length));
            string text = Encoding.UTF8.GetString(output, (int)length);
            Ghostty.Free(0, output, length);
            return text;
        }
        finally
        {
            Ghostty.FormatterFree(formatter);
        }
    }

    /// <summary>False when the text could run commands the moment it is pasted, which deserves a warning.</summary>
    public static unsafe bool IsPasteSafe(string text)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        fixed (byte* data = utf8) return Ghostty.PasteIsSafe(data, (nuint)utf8.Length);
    }

    /// <summary>Sends pasted text the way the running program expects it. Every kind of paste goes through here.</summary>
    public unsafe void Paste(string text)
    {
        if (ZmodemActive) return;

        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        byte[] encoded = new byte[(utf8.Length * 2) + 32];
        nuint written = 0;

        lock (_terminalGate)
        {
            if (_disposed != 0) return;

            var mode = new Ghostty.ModeConfig { Mode = Ghostty.ModeBracketedPaste };
            Check(Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataMode, &mode));

            fixed (byte* data = utf8)
            fixed (byte* output = encoded)
                Check(Ghostty.PasteEncode(data, (nuint)utf8.Length, mode.Value != 0, output, (nuint)encoded.Length, &written));
        }

        JumpToBottom();
        _outgoing.Writer.TryWrite(encoded.AsSpan(0, (int)written).ToArray());
    }

    /// <summary>Moves through the history. Negative goes back in time.</summary>
    public void Scroll(int rows)
    {
        var behavior = new Ghostty.ScrollViewport { Tag = Ghostty.ScrollViewportDelta, Delta = rows };
        lock (_terminalGate)
        {
            if (_disposed != 0) return;
            Check(Ghostty.TerminalScrollViewport(_terminal, behavior));
        }
        SignalChanged();
    }

    /// <summary>
    /// Looks for text on the screen and in the history, and moves to one of the places it was found. That place
    /// becomes the selection, so it is drawn like one and can be copied like one.
    /// </summary>
    /// <param name="text">What to look for. Letters match whatever their case. Empty ends the search.</param>
    /// <param name="older">Whether to move towards older output, which is where a search usually starts from.</param>
    public unsafe SearchResult Find(string text, bool older = true)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        SearchResult found = default;

        // The two kinds of search share the selection, so only one of them is ever in charge of it.
        lock (_searchGate) ForgetRegex();

        lock (_terminalGate)
        {
            if (_disposed != 0) return default;
            if (_search == 0)
            {
                fixed (nint* search = &_search) Check(Ghostty.SearchNew(0, search, _terminal));
            }

            // Giving the same text again keeps what was found, so each call only has to move on.
            fixed (byte* pointer = utf8)
            {
                var needle = new Ghostty.NativeString { Pointer = pointer, Length = (nuint)utf8.Length };
                Check(Ghostty.SearchSet(_search, Ghostty.SearchOptNeedle, utf8.Length == 0 ? null : &needle));
            }

            Ghostty.Selection match = default;
            bool selected = false;
            if (utf8.Length > 0)
            {
                Check(Ghostty.SearchRun(_search));
                selected = (older ? Ghostty.SearchSelectNext(_search) : Ghostty.SearchSelectPrevious(_search)) == Ghostty.Success
                    && Ghostty.SearchGet(_search, Ghostty.SearchDataSelectedMatch, &match) == Ghostty.Success;

                nuint total = 0;
                nuint index = 0;
                Check(Ghostty.SearchGet(_search, Ghostty.SearchDataTotalMatches, &total));
                if (selected && Ghostty.SearchGet(_search, Ghostty.SearchDataSelectedIndex, &index) != Ghostty.Success) index = 0;
                found = new SearchResult(selected ? (int)index + 1 : 0, (int)total);
            }

            Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptSelection, selected ? &match : null));
        }

        SignalChanged();
        return found;
    }

    /// <summary>
    /// The pictures on screen as of the last <see cref="ReadFrame"/>, where each goes and how large. Programs
    /// put them there with the Kitty graphics protocol.
    /// </summary>
    public unsafe void ReadImages(List<TerminalImage> into)
    {
        into.Clear();
        lock (_frameGate)
        {
            if (_disposed != 0) return;

            nint iterator = _imageIterator;
            if (Ghostty.RenderStateGet(_renderState, Ghostty.RenderDataImageIterator, &iterator) != Ghostty.Success) return;

            while (Ghostty.ImageIteratorNext(iterator))
            {
                uint id = 0;
                ulong generation = 0;
                int layer = 0, z = 0, column = 0, row = 0;
                uint offsetX = 0, offsetY = 0, width = 0, height = 0, sourceX = 0, sourceY = 0, sourceWidth = 0, sourceHeight = 0;

                Ghostty.ImageGet(iterator, Ghostty.ImageDataImageId, &id);
                Ghostty.ImageGet(iterator, Ghostty.ImageDataGeneration, &generation);
                Ghostty.ImageGet(iterator, Ghostty.ImageDataLayer, &layer);
                Ghostty.ImageGet(iterator, Ghostty.ImageDataZ, &z);
                Ghostty.ImageGet(iterator, Ghostty.ImageDataViewportCol, &column);
                Ghostty.ImageGet(iterator, Ghostty.ImageDataViewportRow, &row);
                Ghostty.ImageGet(iterator, Ghostty.ImageDataCellOffsetX, &offsetX);
                Ghostty.ImageGet(iterator, Ghostty.ImageDataCellOffsetY, &offsetY);
                Ghostty.ImageGet(iterator, Ghostty.ImageDataPixelWidth, &width);
                Ghostty.ImageGet(iterator, Ghostty.ImageDataPixelHeight, &height);
                Ghostty.ImageGet(iterator, Ghostty.ImageDataSourceX, &sourceX);
                Ghostty.ImageGet(iterator, Ghostty.ImageDataSourceY, &sourceY);
                Ghostty.ImageGet(iterator, Ghostty.ImageDataSourceWidth, &sourceWidth);
                Ghostty.ImageGet(iterator, Ghostty.ImageDataSourceHeight, &sourceHeight);

                if (width == 0 || height == 0 || sourceWidth == 0 || sourceHeight == 0) continue;
                into.Add(new TerminalImage(id, generation, layer, z, column, row, (int)offsetX, (int)offsetY, (int)width, (int)height, (int)sourceX, (int)sourceY, (int)sourceWidth, (int)sourceHeight));
            }
        }

        into.Sort(static (a, b) => a.Z.CompareTo(b.Z));
    }

    /// <summary>
    /// Moves animated pictures on to the frame that is due. Meant to be called once for each frame drawn,
    /// before <see cref="ReadFrame"/>, always with the same clock.
    /// </summary>
    /// <param name="now">The time in milliseconds on a clock that only moves forward.</param>
    /// <returns>When the next frame is due on that clock, or null if nothing is animated.</returns>
    public unsafe long? AdvanceAnimations(long now)
    {
        lock (_terminalGate)
        {
            if (_disposed != 0) return null;

            nint graphics = 0;
            if (Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataKittyGraphics, &graphics) != Ghostty.Success || graphics == 0) return null;

            ulong next = 0;
            return Ghostty.KittyGraphicsAnimationTick(graphics, (ulong)Math.Max(0, now), &next) == Ghostty.Success ? (long)next : null;
        }
    }

    /// <summary>A copy of one picture's pixels as RGBA, or null if it is gone or has not fully arrived.</summary>
    public unsafe ImagePixels? ReadImagePixels(uint id)
    {
        lock (_terminalGate)
        {
            if (_disposed != 0) return null;

            nint graphics = 0;
            if (Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataKittyGraphics, &graphics) != Ghostty.Success || graphics == 0) return null;

            nint image = Ghostty.KittyGraphicsImage(graphics, id);
            if (image == 0) return null;

            uint width = 0, height = 0;
            int format = 0;
            Ghostty.NativeString pixels = default;
            if (Ghostty.KittyGraphicsImageGet(image, Ghostty.KittyImageDataWidth, &width) != Ghostty.Success
                || Ghostty.KittyGraphicsImageGet(image, Ghostty.KittyImageDataHeight, &height) != Ghostty.Success
                || Ghostty.KittyGraphicsImageGet(image, Ghostty.KittyImageDataFormat, &format) != Ghostty.Success
                || Ghostty.KittyGraphicsImageGet(image, Ghostty.KittyImageDataPixels, &pixels) != Ghostty.Success
                || pixels.Pointer is null) return null;

            int perPixel = format switch
            {
                Ghostty.KittyFormatRgba => 4,
                Ghostty.KittyFormatRgb => 3,
                Ghostty.KittyFormatGrayAlpha => 2,
                Ghostty.KittyFormatGray => 1,
                _ => 0,
            };
            long count = (long)width * height;
            if (perPixel == 0 || count == 0 || count > MaxImagePixels || (long)pixels.Length < count * perPixel) return null;

            // Whatever form the program sent, the renderer gets four bytes a pixel.
            byte[] rgba = new byte[count * 4];
            var source = new ReadOnlySpan<byte>(pixels.Pointer, (int)(count * perPixel));
            for (int i = 0, o = 0; i < source.Length; i += perPixel, o += 4)
            {
                bool gray = perPixel <= 2;
                rgba[o] = source[i];
                rgba[o + 1] = gray ? source[i] : source[i + 1];
                rgba[o + 2] = gray ? source[i] : source[i + 2];
                rgba[o + 3] = perPixel switch { 4 => source[i + 3], 2 => source[i + 1], _ => (byte)255 };
            }

            return new ImagePixels((int)width, (int)height, rgba);
        }
    }

    /// <summary>The text of one row on screen, a string per cell. Null if there is no such row.</summary>
    public string[]? RowCells(int row)
    {
        string[]? cells = null;
        ReadFrame((y, span) =>
        {
            if (y != row) return;

            cells = new string[span.Length];
            for (int i = 0; i < span.Length; i++)
                cells[i] = span[i].Cluster ?? (span[i].Codepoint == 0 ? " " : char.ConvertFromUtf32((int)span[i].Codepoint));
        }, FrameRead.Peek);
        return cells;
    }

    /// <summary>Empties the screen and the history, then asks the shell to draw its prompt again.</summary>
    public void Clear()
    {
        if (ZmodemActive) return;

        // Home, erase the screen, erase the history. The form feed is what Ctrl+L sends.
        Feed("\u001b[H\u001b[2J\u001b[3J"u8);
        _outgoing.Writer.TryWrite([0x0C]);
    }

    /// <summary>Where the screen is in the history, for drawing a scrollbar. Meant to be read once per frame.</summary>
    public unsafe ScrollPosition ScrollPosition
    {
        get
        {
            Ghostty.Scrollbar bar = default;
            lock (_terminalGate)
            {
                if (_disposed != 0 || Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataScrollbar, &bar) != Ghostty.Success) return default;
            }

            return new ScrollPosition((long)bar.Total, (long)bar.Offset, (long)bar.Length);
        }
    }

    /// <summary>Puts the given row of the history at the top of the screen.</summary>
    public void ScrollToRow(long row)
    {
        var behavior = new Ghostty.ScrollViewport { Tag = Ghostty.ScrollViewportRow, Delta = (nint)Math.Max(0, row) };
        lock (_terminalGate)
        {
            if (_disposed != 0) return;
            Check(Ghostty.TerminalScrollViewport(_terminal, behavior));
        }
        SignalChanged();
    }

    private void JumpToBottom()
    {
        var behavior = new Ghostty.ScrollViewport { Tag = Ghostty.ScrollViewportBottom };
        lock (_terminalGate)
        {
            if (_disposed != 0) return;
            Check(Ghostty.TerminalScrollViewport(_terminal, behavior));
        }
    }

    /// <summary>Takes a copy of the screen and reports the rows that changed since the last call.</summary>
    /// <returns>Null once the session is closed.</returns>
    public unsafe TerminalFrame? ReadFrame(RowVisitor visitor, FrameRead read)
    {
        lock (_frameGate)
        {
            if (_disposed != 0) return null;
            if (read != FrameRead.Peek) Volatile.Write(ref _changedPending, 0);

            lock (_terminalGate) Check(Ghostty.RenderStateUpdate(_renderState, _terminal));

            ushort columns = 0;
            ushort rows = 0;
            int dirty = Ghostty.DirtyFalse;
            var colors = new Ghostty.RenderColors { Size = (nuint)sizeof(Ghostty.RenderColors) };
            var cursor = new Ghostty.RenderCursor { Size = (nuint)sizeof(Ghostty.RenderCursor) };

            Check(Ghostty.RenderStateGet(_renderState, Ghostty.RenderDataCols, &columns));
            Check(Ghostty.RenderStateGet(_renderState, Ghostty.RenderDataRows, &rows));
            Check(Ghostty.RenderStateGet(_renderState, Ghostty.RenderDataDirty, &dirty));
            Check(Ghostty.RenderStateGet(_renderState, Ghostty.RenderDataColors, &colors));
            Check(Ghostty.RenderStateGet(_renderState, Ghostty.RenderDataCursor, &cursor));

            if (dirty != Ghostty.DirtyFalse || read != FrameRead.Changes)
                VisitRows(visitor, columns, dirty == Ghostty.DirtyFull || read != FrameRead.Changes, ref colors);

            if (read != FrameRead.Peek) Check(Ghostty.RenderStateClean(_renderState));

            uint cursorColor = cursor.Visible != 0 && colors.CursorHasValue != 0 ? colors.Cursor.ToUInt32() : colors.Foreground.ToUInt32();
            return new TerminalFrame(
                columns,
                rows,
                colors.Background.ToUInt32(),
                colors.Foreground.ToUInt32(),
                cursorColor,
                cursor.Visible != 0 && cursor.ViewportHasValue != 0,
                cursor.ViewportX,
                cursor.ViewportY,
                (CursorShape)cursor.VisualStyle,
                cursor.Blinking != 0);
        }
    }

    private unsafe void VisitRows(RowVisitor visitor, int columns, bool everyRow, ref Ghostty.RenderColors colors)
    {
        if (_rowBuffer.Length < columns) _rowBuffer = new TerminalCell[columns];

        nint iterator = _rowIterator;
        Check(Ghostty.RenderStateGet(_renderState, Ghostty.RenderDataRowIterator, &iterator));

        int y = 0;
        while (Ghostty.RowIteratorNext(iterator))
        {
            byte rowDirty = 0;
            Check(Ghostty.RowGet(iterator, Ghostty.RowDataDirty, &rowDirty));
            if (everyRow || rowDirty != 0)
            {
                ReadRow(iterator, columns, ref colors);
                visitor(y, _rowBuffer.AsSpan(0, columns));
            }
            y++;
        }
    }

    private unsafe void ReadRow(nint iterator, int columns, ref Ghostty.RenderColors colors)
    {
        nint cells = _rowCells;
        Check(Ghostty.RowGet(iterator, Ghostty.RowDataCells, &cells));

        uint* codepoints = stackalloc uint[MaxCluster];
        var buffer = new Ghostty.CodepointBuffer { Pointer = codepoints, Capacity = MaxCluster };
        Ghostty.Style style = default;
        byte selected = 0;

        int* keys = stackalloc int[] { Ghostty.CellDataGraphemesBuf, Ghostty.CellDataStyle, Ghostty.CellDataSelected };
        void** values = stackalloc void*[] { &buffer, &style, &selected };

        uint defaultForeground = colors.Foreground.ToUInt32();
        uint defaultBackground = colors.Background.ToUInt32();

        int x = 0;
        while (x < columns && Ghostty.RowCellsNext(cells))
        {
            Check(Ghostty.RowCellsGetMulti(cells, 3, keys, values, null));

            ref TerminalCell cell = ref _rowBuffer[x++];
            int length = Math.Min((int)buffer.Length, MaxCluster);
            cell.Codepoint = length > 0 ? codepoints[0] : 0;
            cell.Cluster = length > 1 ? ClusterText(codepoints, length) : null;

            uint foreground = Resolve(style.Foreground, ref colors, defaultForeground);
            uint background = Resolve(style.Background, ref colors, defaultBackground);
            if ((style.Flags & Ghostty.StyleFlagInverse) != 0) (foreground, background) = (background, foreground);
            if (selected != 0) background = _selectionBackground;

            cell.Foreground = foreground;
            cell.Background = background;
            cell.UnderlineColor = Resolve(style.Underline, ref colors, foreground);
            cell.Underline = (UnderlineStyle)((style.Flags & Ghostty.StyleUnderlineMask) >> Ghostty.StyleUnderlineShift);
            cell.Flags = TranslateFlags(style.Flags) | (background != defaultBackground ? CellFlags.HasBackground : CellFlags.None);
        }

        for (; x < columns; x++) _rowBuffer[x] = default;
    }

    private static unsafe string ClusterText(uint* codepoints, int length)
    {
        var text = new StringBuilder(length * 2);
        for (int i = 0; i < length; i++) text.Append(char.ConvertFromUtf32((int)codepoints[i]));
        return text.ToString();
    }

    private static uint Resolve(in Ghostty.StyleColor color, ref Ghostty.RenderColors colors, uint fallback) => color.Tag switch
    {
        Ghostty.StyleColorRgb => (uint)((color.V0 << 16) | (color.V1 << 8) | color.V2),
        Ghostty.StyleColorPalette => colors.PaletteColor(color.V0),
        _ => fallback,
    };

    private static CellFlags TranslateFlags(ushort flags)
    {
        var result = CellFlags.None;
        if ((flags & Ghostty.StyleFlagBold) != 0) result |= CellFlags.Bold;
        if ((flags & Ghostty.StyleFlagItalic) != 0) result |= CellFlags.Italic;
        if ((flags & Ghostty.StyleFlagFaint) != 0) result |= CellFlags.Faint;
        if ((flags & Ghostty.StyleFlagInvisible) != 0) result |= CellFlags.Invisible;
        if ((flags & Ghostty.StyleFlagStrikethrough) != 0) result |= CellFlags.Strikethrough;
        if ((flags & Ghostty.StyleFlagOverline) != 0) result |= CellFlags.Overline;
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _stop.CancelAsync().ConfigureAwait(false);
        _outgoing.Writer.TryComplete();
        await _transport.DisposeAsync().ConfigureAwait(false);

        lock (_frameGate)
        lock (_terminalGate)
        {
            _log?.Dispose();
            if (_search != 0) Ghostty.SearchFree(_search);
            Ghostty.TrackedGridRefFree(_outputStart);
            Ghostty.TrackedGridRefFree(_lastOutputStart);
            Ghostty.TrackedGridRefFree(_lastOutputEnd);
            Ghostty.ImageIteratorFree(_imageIterator);
            Ghostty.MouseEventFree(_mouseEvent);
            Ghostty.MouseEncoderFree(_mouseEncoder);
            Ghostty.GestureEventFree(_gestureRelease);
            Ghostty.GestureEventFree(_gestureDrag);
            Ghostty.GestureEventFree(_gesturePress);
            Ghostty.SelectionGestureFree(_selectionGesture, _terminal);
            Ghostty.KeyEventFree(_keyEvent);
            Ghostty.KeyEncoderFree(_keyEncoder);
            Ghostty.RowCellsFree(_rowCells);
            Ghostty.RowIteratorFree(_rowIterator);
            Ghostty.RenderStateFree(_renderState);
            Ghostty.TerminalFree(_terminal);
        }

        _self.Free();
        _stop.Dispose();
    }
}

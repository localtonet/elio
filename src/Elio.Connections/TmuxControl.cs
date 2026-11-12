using System.Buffers;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Elio.Terminal;

namespace Elio.Connections;

/// <summary>What tmux answered to one command.</summary>
public sealed class TmuxReply
{
    private readonly IReadOnlyList<byte[]> _raw;
    private string[]? _lines;

    internal TmuxReply(bool success, IReadOnlyList<byte[]> raw) => (Success, _raw) = (success, raw);

    /// <summary>False if tmux reported an error, or the session ended before it answered.</summary>
    public bool Success { get; }

    /// <summary>The output, or the error message, one entry per line.</summary>
    public IReadOnlyList<string> Lines => _lines ??= [.. _raw.Select(Encoding.UTF8.GetString)];

    public string Text => string.Join('\n', Lines);

    internal IReadOnlyList<byte[]> RawLines => _raw;

    internal static TmuxReply Failed { get; } = new(false, []);
}

/// <summary>One tmux window, shown by the UI as a tab.</summary>
/// <remarks>The same object lives as long as the window does; its properties change as tmux reports changes.</remarks>
public sealed class TmuxWindow
{
    private volatile string _name;
    private volatile bool _active;
    private volatile TmuxLayoutNode _layout;
    private volatile string? _activePaneId;

    internal TmuxWindow(string id, string name, TmuxLayoutNode layout, string layoutText)
    {
        Id = id;
        _name = name;
        _layout = layout;
        LayoutText = layoutText;
    }

    /// <summary>The window's id, such as "@1". It never changes and is never reused within a tmux server.</summary>
    public string Id { get; }

    public string Name
    {
        get => _name;
        internal set => _name = value;
    }

    /// <summary>Whether this is the session's current window.</summary>
    public bool Active
    {
        get => _active;
        internal set => _active = value;
    }

    /// <summary>How the window is divided into panes. Replaced as a whole on every change.</summary>
    public TmuxLayoutNode Layout
    {
        get => _layout;
        internal set => _layout = value;
    }

    /// <summary>The pane that has the focus inside this window, if known.</summary>
    public string? ActivePaneId
    {
        get => _activePaneId;
        internal set => _activePaneId = value;
    }

    internal string LayoutText { get; set; }
}

/// <summary>
/// A tmux session driven through control mode ("tmux -CC"): tmux stops drawing and instead reports, line by
/// line, what its windows and panes do, so each pane can be shown as a terminal of our own.
/// </summary>
/// <remarks>
/// <para>How the UI uses it: one tab per entry of <see cref="Windows"/>, split according to
/// <see cref="TmuxWindow.Layout"/>, and one terminal per layout leaf fed by <see cref="OpenPane"/>.</para>
/// <para>Sizes: tmux has one size per client, the grid of the whole window. Either call
/// <see cref="SetClientSize"/> with the columns and rows the tab's split view offers in total (dividers
/// count one cell each), or just let every pane terminal report its own size through its transport's
/// ResizeAsync; the session then adds those up along the layout tree and tells tmux the result.</para>
/// <para>Events are raised on the thread that reads the connection, never while a lock is held.
/// Every method may be called from any thread.</para>
/// </remarks>
public sealed class TmuxSession
{
    // Enough to scroll back through a screenful of recent work without making attach slow over a thin link.
    private const int Scrollback = 2000;

    // Keeps one command line well under any pty line limit while a large paste still takes few commands.
    private const int KeysPerCommand = 256;

    private const string WindowFormat = "#{window_id} #{window_active} #{window_layout} #{window_name}";
    private const string PaneFormat = "#{pane_id} #{window_id} #{pane_active}";
    private const string StateFormat = "#{cursor_x},#{cursor_y},#{alternate_on},#{cursor_flag},#{keypad_cursor_flag},#{keypad_flag},#{mouse_standard_flag},#{mouse_button_flag},#{mouse_any_flag},#{mouse_sgr_flag},#{scroll_region_upper},#{scroll_region_lower},#{pane_height}";

    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _write;
    private readonly Lock _gate = new();
    private readonly Queue<Pending> _pending = new();
    private readonly Dictionary<string, TmuxWindow> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaneTransport> _panes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Columns, int Rows)> _reported = new(StringComparer.Ordinal);
    private volatile TmuxWindow[] _windows = [];
    private Task _sendTail = Task.CompletedTask;
    private bool _closed;
    private bool _explicitSize;
    private (int Columns, int Rows) _sentSize;

    // Everything below is touched only by the thread that feeds the stream in.
    private readonly ArrayBufferWriter<byte> _line = new(4096);
    private bool _escapeSeen;
    private bool _inBlock;
    private string _blockNumber = string.Empty;
    private Pending? _current;
    private List<byte[]>? _blockLines;
    private bool _ready;
    private bool _greeted;

    /// <param name="write">Sends bytes to the tmux client's input.</param>
    internal TmuxSession(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write)
    {
        _write = write;
    }

    /// <summary>The session's windows in tab order. A snapshot: a later change gives a new list.</summary>
    public IReadOnlyList<TmuxWindow> Windows => _windows;

    /// <summary>The session's id, such as "$0", once tmux has said it.</summary>
    public string? SessionId { get; private set; }

    public string? SessionName { get; private set; }

    /// <summary>True once control mode has ended. Nothing can be sent any more.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_gate) return _closed;
        }
    }

    /// <summary>Why tmux left control mode, in tmux's own words, if it gave a reason.</summary>
    public string? ExitReason { get; private set; }

    public event Action<TmuxWindow>? WindowAdded;

    public event Action<TmuxWindow>? WindowClosed;

    public event Action<TmuxWindow>? WindowRenamed;

    /// <summary>The window's <see cref="TmuxWindow.Layout"/> was replaced: panes were added, removed or resized.</summary>
    public event Action<TmuxWindow>? LayoutChanged;

    /// <summary>Another window became the current one, or the focused pane inside a window changed.</summary>
    public event Action<TmuxWindow>? ActiveChanged;

    /// <summary>A pane entered or left a tmux mode such as copy mode. The argument is the pane id.</summary>
    public event Action<string>? PaneModeChanged;

    public event Action? SessionRenamed;

    /// <summary>Control mode ended. Every pane transport now reads 0.</summary>
    public event Action? Closed;

    /// <summary>The first full picture of windows and panes has arrived.</summary>
    internal event Action? Ready;

    /// <summary>The stream left control mode at the point <see cref="Feed"/> stopped consuming.</summary>
    internal bool Terminated { get; private set; }

    // ---------------------------------------------------------------- commands for the UI

    public Task<TmuxReply> NewWindow() => Send("new-window");

    public Task<TmuxReply> KillWindow(string windowId) => Send($"kill-window -t {Id(windowId, '@')}");

    /// <summary>Splits a pane in two. Horizontal puts the new pane to the right (tmux's -h), otherwise below.</summary>
    public Task<TmuxReply> SplitPane(string paneId, bool horizontal) => Send($"split-window {(horizontal ? "-h" : "-v")} -t {Id(paneId, '%')}");

    public Task<TmuxReply> KillPane(string paneId) => Send($"kill-pane -t {Id(paneId, '%')}");

    public Task<TmuxReply> SelectWindow(string windowId) => Send($"select-window -t {Id(windowId, '@')}");

    /// <summary>Tells tmux which pane has the focus, so that tmux's own idea of "the current pane" follows the UI.</summary>
    public Task<TmuxReply> SelectPane(string paneId) => Send($"select-pane -t {Id(paneId, '%')}");

    public Task<TmuxReply> RenameWindow(string windowId, string name) => Send($"rename-window -t {Id(windowId, '@')} {Quote(name)}");

    /// <summary>Asks tmux to let go of this client. The session keeps running on the server.</summary>
    public Task<TmuxReply> Detach() => Send("detach");

    /// <summary>Sends any single tmux command and returns its reply.</summary>
    /// <remarks>
    /// One command only: tmux answers "a ; b" with two replies, which would be handed to the wrong callers.
    /// </remarks>
    public Task<TmuxReply> SendCommandAsync(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.AsSpan().IndexOfAny('\r', '\n') >= 0) throw new ArgumentException("A tmux command is a single line.", nameof(command));
        return Send(command);
    }

    /// <summary>
    /// Tells tmux the grid of the whole window: what the tab's split view offers in total, in cells, with
    /// every divider between panes counted as one cell. After the first call, sizes reported by the pane
    /// transports are no longer used.
    /// </summary>
    public void SetClientSize(int columns, int rows)
    {
        lock (_gate) _explicitSize = true;
        SendClientSize(columns, rows);
    }

    /// <summary>
    /// A terminal's view of one pane. Reading gives what is on the pane's screen now (with some scrollback)
    /// and then its output as it arrives; writing types into it; resizing reports how many cells the UI gives it.
    /// </summary>
    /// <remarks>
    /// Reads give 0 once the pane or the session is gone. Disposing the transport only stops listening;
    /// use <see cref="KillPane"/> to close the pane itself.
    /// </remarks>
    public ITerminalTransport OpenPane(string paneId)
    {
        var pane = new PaneTransport(this, Id(paneId, '%'));
        PaneTransport? previous;
        bool closed;
        lock (_gate)
        {
            closed = _closed;
            _panes.Remove(paneId, out previous);
            if (!closed) _panes[paneId] = pane;
        }

        previous?.Complete();
        if (closed) pane.Complete();
        return pane;
    }

    // ---------------------------------------------------------------- sending

    private static string Id(string id, char sigil)
    {
        // Ids go into command lines as they are, so nothing but the plain form may pass.
        if (!IsId(id, sigil)) throw new ArgumentException($"'{id}' is not a tmux id of the form {sigil}1.", nameof(id));
        return id;
    }

    private static bool IsId(string? id, char sigil) =>
        id is not null && id.Length >= 2 && id.Length <= 12 && id[0] == sigil && !id.AsSpan(1).ContainsAnyExceptInRange('0', '9');

    private static string Quote(string text)
    {
        // Single quotes are the one place where tmux reads every character as itself. A single quote in
        // the text closes them, is written escaped on its own, and opens them again.
        var quoted = new StringBuilder(text.Length + 2).Append('\'');
        foreach (char c in text)
        {
            if (char.IsControl(c)) continue;
            if (c == '\'') quoted.Append("'\\''");
            else quoted.Append(c);
        }

        return quoted.Append('\'').ToString();
    }

    private Task<TmuxReply> Send(string command, Action<TmuxReply>? onReply = null)
    {
        var pending = new Pending(onReply);
        byte[] line = Encoding.UTF8.GetBytes(command + "\n");

        // tmux answers in the order it was asked, so the queue and the wire must agree on that order.
        lock (_gate)
        {
            if (_closed) return Task.FromResult(TmuxReply.Failed);
            _pending.Enqueue(pending);
            _sendTail = WriteAfter(_sendTail, line);
        }

        return pending.Completion.Task;
    }

    private async Task WriteAfter(Task previous, byte[] line)
    {
        // Always continues elsewhere: a write can block on a full pty and must not do so under the lock.
        await previous.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);

        try
        {
            await _write(line, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The connection is gone. Whatever kind of transport it was, the session is over.
            Close(null);
        }
    }

    private Task SendKeys(string paneId, ReadOnlyMemory<byte> data)
    {
        Task last = Task.CompletedTask;
        var command = new StringBuilder();

        // Hex keeps every byte intact: no quoting rules, no key names, and UTF-8 passes through as it is.
        for (int start = 0; start < data.Length; start += KeysPerCommand)
        {
            ReadOnlySpan<byte> part = data.Span.Slice(start, Math.Min(KeysPerCommand, data.Length - start));
            command.Clear().Append("send-keys -H -t ").Append(paneId);
            foreach (byte b in part) command.Append(' ').Append(b.ToString("x2", CultureInfo.InvariantCulture));

            Send(command.ToString());
            lock (_gate) last = _sendTail;
        }

        return last;
    }

    private void Refresh()
    {
        Send($"list-windows -F '{WindowFormat}'", ApplyWindows);
        Send($"list-panes -s -F '{PaneFormat}'", ApplyPanes);
    }

    // ---------------------------------------------------------------- sizes

    private void SendClientSize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 2, 10000);
        rows = Math.Clamp(rows, 2, 10000);

        lock (_gate)
        {
            if (_sentSize == (columns, rows)) return;
            _sentSize = (columns, rows);
        }

        Send(string.Create(CultureInfo.InvariantCulture, $"refresh-client -C {columns}x{rows}"));
    }

    private void ReportPaneSize(string paneId, int columns, int rows)
    {
        (int Columns, int Rows)? total = null;
        lock (_gate)
        {
            _reported[paneId] = (columns, rows);
            if (_explicitSize) return;

            foreach (TmuxWindow window in _windows)
            {
                if (window.Layout.Find(paneId) is null) continue;
                total = Offered(window.Layout);
                break;
            }
        }

        if (total is { } size) SendClientSize(size.Columns, size.Rows);
    }

    // What the UI offers the whole window, worked out from what it offers each pane. Null until every
    // pane of the window has a size, so a half laid out tab does not resize tmux to something odd.
    private (int Columns, int Rows)? Offered(TmuxLayoutNode node)
    {
        if (node.Kind == TmuxLayoutKind.Pane) return node.PaneId is not null && _reported.TryGetValue(node.PaneId, out var size) ? size : null;

        int along = node.Children.Count - 1;
        int across = int.MaxValue;
        foreach (TmuxLayoutNode child in node.Children)
        {
            if (Offered(child) is not { } size) return null;

            // Across the split the smallest child decides, so that no pane is promised cells it does not have.
            along += node.Kind == TmuxLayoutKind.Columns ? size.Columns : size.Rows;
            across = Math.Min(across, node.Kind == TmuxLayoutKind.Columns ? size.Rows : size.Columns);
        }

        return node.Kind == TmuxLayoutKind.Columns ? (along, across) : (across, along);
    }

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// Takes the next bytes of the control stream. Returns how many belonged to it: fewer than given once
    /// tmux has left control mode, in which case <see cref="Terminated"/> is set and the rest is ordinary output.
    /// </summary>
    internal int Feed(ReadOnlySpan<byte> data)
    {
        int i = 0;
        while (i < data.Length)
        {
            if (_escapeSeen)
            {
                _escapeSeen = false;
                if (data[i] == (byte)'\\')
                {
                    Terminated = true;
                    Close(ExitReason);
                    return i + 1;
                }

                _line.Write("\u001b"u8);
            }
            else if (_line.WrittenCount == 0 && !_inBlock && data[i] == 0x1b)
            {
                // ESC \ ends control mode. Inside a reply an ESC is just pane content (colours from
                // capture-pane), and notifications never carry a raw one, so only this spot can mean the end.
                _escapeSeen = true;
                i++;
                continue;
            }

            int end = data[i..].IndexOf((byte)'\n');
            if (end < 0)
            {
                _line.Write(data[i..]);
                break;
            }

            _line.Write(data.Slice(i, end));
            i += end + 1;

            // A pty in between may have turned the line ends into CR LF.
            ReadOnlySpan<byte> line = _line.WrittenSpan;
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            HandleLine(line);
            _line.ResetWrittenCount();
        }

        return data.Length;
    }

    private void HandleLine(ReadOnlySpan<byte> line)
    {
        if (_inBlock)
        {
            bool ok = line.StartsWith("%end "u8);
            if ((ok || line.StartsWith("%error "u8)) && Field(line, 2).SequenceEqual(Encoding.ASCII.GetBytes(_blockNumber)))
            {
                _inBlock = false;
                Pending? pending = _current;
                var reply = new TmuxReply(ok, _blockLines ?? []);
                (_current, _blockLines) = (null, null);
                if (pending is not null)
                {
                    pending.OnReply?.Invoke(reply);
                    pending.Completion.TrySetResult(reply);
                }
                else if (!_greeted)
                {
                    // The first block is tmux's answer to the attach itself. Nothing is sent before it
                    // says yes: after a refused attach tmux is already gone, and whatever was written
                    // would be read by the shell as commands typed by the user.
                    _greeted = true;
                    if (ok) Refresh();
                    else if (reply.Text.Length > 0) ExitReason = reply.Text;
                }

                return;
            }

            _blockLines?.Add(line.ToArray());
            return;
        }

        if (line.Length == 0 || line[0] != (byte)'%') return;

        if (line.StartsWith("%output "u8))
        {
            ReadOnlySpan<byte> rest = line[8..];
            int space = rest.IndexOf((byte)' ');
            if (space > 0) Output(Encoding.ASCII.GetString(rest[..space]), rest[(space + 1)..]);
            return;
        }

        if (line.StartsWith("%extended-output "u8))
        {
            // "%extended-output %1 <age> ... : data", sent instead of %output when flow control is on.
            ReadOnlySpan<byte> rest = line[17..];
            int space = rest.IndexOf((byte)' ');
            int colon = rest.IndexOf(" : "u8);
            if (space > 0 && colon >= space) Output(Encoding.ASCII.GetString(rest[..space]), rest[(colon + 3)..]);
            return;
        }

        string text = Encoding.UTF8.GetString(line);
        int split = text.IndexOf(' ');
        string name = split < 0 ? text : text[..split];
        string arguments = split < 0 ? string.Empty : text[(split + 1)..];
        string[] words = arguments.Split(' ');

        switch (name)
        {
            case "%begin":
                _inBlock = true;
                _blockNumber = words.Length > 1 ? words[1] : string.Empty;

                // Bit 0 says the command came from this client. tmux also wraps things nobody here asked
                // for in a block, the attach itself for one, and those must not use up a waiting caller.
                bool ours = words.Length > 2 && int.TryParse(words[2], NumberStyles.None, CultureInfo.InvariantCulture, out int flags) && (flags & 1) != 0;
                _current = null;
                if (ours)
                {
                    lock (_gate) _pending.TryDequeue(out _current);
                }

                _blockLines = _current is null && _greeted ? null : [];
                break;

            case "%window-add":
            case "%session-window-changed" when words.Length > 1 && !Known(words[1]):
                Refresh();
                break;

            case "%session-window-changed" when words.Length > 1:
                SetActiveWindow(words[1]);
                break;

            // A killed window has already left the session by the time tmux reports it, so it is announced
            // as "unlinked". Windows of other sessions come the same way and are simply not found here.
            case "%window-close":
            case "%unlinked-window-close":
                RemoveWindow(words[0]);
                break;

            case "%window-renamed" when words.Length > 1:
                RenameLocal(words[0], arguments[(words[0].Length + 1)..]);
                break;

            case "%layout-change" when words.Length > 1:
                ChangeLayout(words[0], words[1]);
                break;

            case "%window-pane-changed" when words.Length > 1:
                SetActivePane(words[0], words[1]);
                break;

            case "%session-changed":
                SessionId = words[0];
                SessionName = words.Length > 1 ? arguments[(words[0].Length + 1)..] : null;
                _greeted = true;
                Refresh();
                SessionRenamed?.Invoke();
                break;

            case "%session-renamed":
                // Newer tmux puts the session id in front of the name.
                SessionName = words.Length > 1 && words[0].StartsWith('$') ? arguments[(words[0].Length + 1)..] : arguments;
                SessionRenamed?.Invoke();
                break;

            case "%pane-mode-changed":
                PaneModeChanged?.Invoke(words[0]);
                break;

            case "%pause":
                // Only sent if flow control was switched on by hand. Nothing here holds output back, so carry on.
                // The id comes from the server and goes into a command, so anything but a plain id is left alone.
                if (IsId(words[0], '%')) Send($"refresh-client -A '{words[0]}:continue'");
                break;

            case "%exit":
                if (arguments.Length > 0) ExitReason = arguments;
                break;

            // %sessions-changed, %continue, %unlinked-window-add, %client-*, %paste-buffer-* and the rest
            // are about things outside this session's windows.
        }
    }

    private static ReadOnlySpan<byte> Field(ReadOnlySpan<byte> line, int index)
    {
        for (; index > 0; index--)
        {
            int space = line.IndexOf((byte)' ');
            if (space < 0) return default;
            line = line[(space + 1)..];
        }

        int end = line.IndexOf((byte)' ');
        return end < 0 ? line : line[..end];
    }

    private void Output(string paneId, ReadOnlySpan<byte> escaped)
    {
        PaneTransport? pane;
        lock (_gate) _panes.TryGetValue(paneId, out pane);
        pane?.Output(Unescape(escaped));
    }

    /// <summary>Undoes tmux's escaping of pane output: a backslash and three octal digits stand for one byte.</summary>
    internal static byte[] Unescape(ReadOnlySpan<byte> escaped)
    {
        byte[] result = new byte[escaped.Length];
        int count = 0;
        for (int i = 0; i < escaped.Length; i++)
        {
            if (escaped[i] == (byte)'\\' && i + 3 < escaped.Length && Octal(escaped[i + 1]) && Octal(escaped[i + 2]) && Octal(escaped[i + 3]))
            {
                result[count++] = (byte)(((escaped[i + 1] - '0') << 6) | ((escaped[i + 2] - '0') << 3) | (escaped[i + 3] - '0'));
                i += 3;
            }
            else
            {
                result[count++] = escaped[i];
            }
        }

        return count == result.Length ? result : result[..count];

        static bool Octal(byte b) => b is >= (byte)'0' and <= (byte)'7';
    }

    // ---------------------------------------------------------------- the model

    private bool Known(string windowId)
    {
        lock (_gate) return _byId.ContainsKey(windowId);
    }

    private void ApplyWindows(TmuxReply reply)
    {
        if (!reply.Success) return;

        var added = new List<TmuxWindow>();
        var removed = new List<TmuxWindow>();
        var renamed = new List<TmuxWindow>();
        var relaid = new List<TmuxWindow>();
        var activated = new List<TmuxWindow>();

        lock (_gate)
        {
            var order = new List<TmuxWindow>();
            foreach (string line in reply.Lines)
            {
                string[] words = line.Split(' ', 4);
                if (words.Length < 3 || !TmuxLayout.TryParse(words[2], out TmuxLayoutNode? layout)) continue;

                string name = words.Length > 3 ? words[3] : string.Empty;
                bool active = words[1] == "1";

                if (_byId.TryGetValue(words[0], out TmuxWindow? window))
                {
                    if (window.Name != name)
                    {
                        window.Name = name;
                        renamed.Add(window);
                    }

                    if (window.LayoutText != words[2])
                    {
                        AdoptLayout(window, layout, words[2]);
                        relaid.Add(window);
                    }

                    if (window.Active != active)
                    {
                        window.Active = active;
                        if (active) activated.Add(window);
                    }
                }
                else
                {
                    window = new TmuxWindow(words[0], name, layout, words[2]) { Active = active };
                    _byId[window.Id] = window;
                    added.Add(window);
                }

                order.Add(window);
            }

            foreach (TmuxWindow window in _byId.Values.Except(order).ToList())
            {
                _byId.Remove(window.Id);
                removed.Add(window);
            }

            _windows = [.. order];
        }

        PrunePanes();
        foreach (TmuxWindow window in removed) WindowClosed?.Invoke(window);
        foreach (TmuxWindow window in added) WindowAdded?.Invoke(window);
        foreach (TmuxWindow window in renamed) WindowRenamed?.Invoke(window);
        foreach (TmuxWindow window in relaid) LayoutChanged?.Invoke(window);
        foreach (TmuxWindow window in activated) ActiveChanged?.Invoke(window);
    }

    private void ApplyPanes(TmuxReply reply)
    {
        if (!reply.Success) return;

        var changed = new List<TmuxWindow>();
        lock (_gate)
        {
            foreach (string line in reply.Lines)
            {
                string[] words = line.Split(' ');
                if (words.Length < 3 || words[2] != "1" || !_byId.TryGetValue(words[1], out TmuxWindow? window)) continue;
                if (window.ActivePaneId == words[0]) continue;

                window.ActivePaneId = words[0];
                changed.Add(window);
            }
        }

        if (!_ready)
        {
            _ready = true;
            Ready?.Invoke();
            return;
        }

        foreach (TmuxWindow window in changed) ActiveChanged?.Invoke(window);
    }

    // Must be called with the lock held.
    private void AdoptLayout(TmuxWindow window, TmuxLayoutNode layout, string text)
    {
        TmuxLayoutNode old = window.Layout;

        // Same outer size means the panes inside were rearranged (a split, a closed pane, a dragged
        // divider). The UI will follow tmux here, so tmux's new pane sizes replace what the UI said before;
        // otherwise a stale size of the pane that was just halved would be added up into a wider window.
        // A changed outer size is tmux following a size we sent, and then the UI's word stays as it is.
        if (old.Width == layout.Width && old.Height == layout.Height)
        {
            foreach (TmuxLayoutNode pane in layout.Panes())
            {
                if (pane.PaneId is null) continue;
                TmuxLayoutNode? before = old.Find(pane.PaneId);
                if (before is null || before.Width != pane.Width || before.Height != pane.Height) _reported[pane.PaneId] = (pane.Width, pane.Height);
            }
        }

        window.Layout = layout;
        window.LayoutText = text;
    }

    private void ChangeLayout(string windowId, string text)
    {
        TmuxWindow? window;
        lock (_gate)
        {
            // A window not known yet is on its way in through a refresh, which brings its layout along.
            if (!_byId.TryGetValue(windowId, out window) || window.LayoutText == text) return;
            if (!TmuxLayout.TryParse(text, out TmuxLayoutNode? layout)) return;
            AdoptLayout(window, layout, text);
        }

        PrunePanes();
        LayoutChanged?.Invoke(window);
    }

    private void RemoveWindow(string windowId)
    {
        TmuxWindow? window;
        lock (_gate)
        {
            if (!_byId.Remove(windowId, out window)) return;
            _windows = [.. _windows.Where(w => w != window)];
        }

        PrunePanes();
        WindowClosed?.Invoke(window);
    }

    private void RenameLocal(string windowId, string name)
    {
        TmuxWindow? window;
        lock (_gate)
        {
            if (!_byId.TryGetValue(windowId, out window) || window.Name == name) return;
            window.Name = name;
        }

        WindowRenamed?.Invoke(window);
    }

    private void SetActiveWindow(string windowId)
    {
        TmuxWindow? window;
        lock (_gate)
        {
            if (!_byId.TryGetValue(windowId, out window) || window.Active) return;
            foreach (TmuxWindow other in _windows) other.Active = other == window;
        }

        ActiveChanged?.Invoke(window);
    }

    private void SetActivePane(string windowId, string paneId)
    {
        TmuxWindow? window;
        lock (_gate)
        {
            if (!_byId.TryGetValue(windowId, out window) || window.ActivePaneId == paneId) return;
            window.ActivePaneId = paneId;
        }

        ActiveChanged?.Invoke(window);
    }

    // Ends the transports of panes that no window shows any more.
    private void PrunePanes()
    {
        List<PaneTransport> gone = [];
        lock (_gate)
        {
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (TmuxWindow window in _windows)
            {
                foreach (TmuxLayoutNode pane in window.Layout.Panes())
                {
                    if (pane.PaneId is not null) live.Add(pane.PaneId);
                }
            }

            foreach (string id in _panes.Keys.Where(id => !live.Contains(id)).ToList())
            {
                _panes.Remove(id, out PaneTransport? pane);
                gone.Add(pane!);
            }

            foreach (string id in _reported.Keys.Where(id => !live.Contains(id)).ToList()) _reported.Remove(id);
        }

        foreach (PaneTransport pane in gone) pane.Complete();
    }

    /// <summary>Ends the session on our side: waiting callers get a failed reply and every pane reads 0.</summary>
    internal void Close(string? reason)
    {
        List<Pending> waiting;
        List<PaneTransport> panes;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            waiting = [.. _pending];
            _pending.Clear();
            panes = [.. _panes.Values];
            _panes.Clear();
        }

        ExitReason ??= reason;
        foreach (Pending pending in waiting) pending.Completion.TrySetResult(TmuxReply.Failed);
        foreach (PaneTransport pane in panes) pane.Complete();
        Closed?.Invoke();
    }

    // ---------------------------------------------------------------- showing a pane

    private void Prime(PaneTransport pane)
    {
        // The cursor is asked for first and the screen second. If the pane printed something in between,
        // the two do not belong together and are asked for again.
        pane.Attempts++;
        Send($"display-message -p -t {pane.PaneId} '{StateFormat}'", reply =>
        {
            pane.State = reply.Success && reply.Lines.Count > 0 ? reply.Lines[0] : null;
            pane.Stale = false;
            pane.StateSeen = true;
        });
        Send($"capture-pane -p -e -J -S -{Scrollback} -t {pane.PaneId}", reply =>
        {
            if (!reply.Success)
            {
                // tmux does not know the pane (any more).
                RemovePane(pane);
                return;
            }

            if (pane.Stale && pane.Attempts < 3)
            {
                pane.StateSeen = false;
                Prime(pane);
                return;
            }

            pane.Push(Snapshot(pane.State, reply.RawLines));
            pane.Live = true;
        });
    }

    private void RemovePane(PaneTransport pane)
    {
        lock (_gate)
        {
            if (_panes.TryGetValue(pane.PaneId, out PaneTransport? current) && current == pane) _panes.Remove(pane.PaneId);
        }

        pane.Complete();
    }

    // Turns the captured screen into bytes that repaint it on an empty terminal of the same size.
    private static byte[] Snapshot(string? state, IReadOnlyList<byte[]> lines)
    {
        int[] values = [.. (state ?? string.Empty).Split(',').Select(v => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : -1)];
        int Value(int index) => index < values.Length ? values[index] : -1;

        var output = new ArrayBufferWriter<byte>(4096);
        void Text(string text) => output.Write(Encoding.ASCII.GetBytes(text));

        if (Value(2) == 1) Text("\x1b[?1049h");
        Text("\x1b[m");

        // The capture is the scrollback followed by every row of the screen, blank ones included. Written
        // out in order, the scrollback rolls off the top and the screen ends up exactly where it was.
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) Text("\r\n");
            output.Write(lines[i]);
        }

        // Colours carry over from one captured line to the next and are left on at the end.
        Text("\x1b[m");

        // Setting the scroll region moves the cursor home, so it comes before the cursor.
        int top = Value(10), bottom = Value(11), height = Value(12);
        if (top >= 0 && bottom > top && height > 0 && (top > 0 || bottom < height - 1)) Text(string.Create(CultureInfo.InvariantCulture, $"\x1b[{top + 1};{bottom + 1}r"));

        if (Value(0) >= 0 && Value(1) >= 0) Text(string.Create(CultureInfo.InvariantCulture, $"\x1b[{Value(1) + 1};{Value(0) + 1}H"));

        // The modes a program switched on before we were watching: without them a full screen program
        // that is already running would get the wrong arrow keys and no mouse.
        if (Value(3) == 0) Text("\x1b[?25l");
        if (Value(4) == 1) Text("\x1b[?1h");
        if (Value(5) == 1) Text("\x1b=");
        if (Value(6) == 1) Text("\x1b[?1000h");
        if (Value(7) == 1) Text("\x1b[?1002h");
        if (Value(8) == 1) Text("\x1b[?1003h");
        if (Value(9) == 1) Text("\x1b[?1006h");

        return output.WrittenSpan.ToArray();
    }

    private sealed class Pending(Action<TmuxReply>? onReply)
    {
        /// <summary>Runs on the reading thread, at the reply's exact place in the stream.</summary>
        public Action<TmuxReply>? OnReply { get; } = onReply;

        public TaskCompletionSource<TmuxReply> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PaneTransport(TmuxSession session, string paneId) : ITerminalTransport
    {
        // How long the first read waits for the terminal to say how big it is before the screen is fetched.
        private static readonly TimeSpan SizeGrace = TimeSpan.FromMilliseconds(250);

        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource _sized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _rest;
        private int _offset;
        private int _started;

        public string PaneId { get; } = paneId;

        public string Description => PaneId;

        // The fields below belong to the session's reading thread.

        /// <summary>The screen has been delivered; output is passed on from here.</summary>
        public bool Live { get; set; }

        /// <summary>The pane printed something after its cursor was read and before its screen was.</summary>
        public bool Stale { get; set; }

        public bool StateSeen { get; set; }

        public string? State { get; set; }

        public int Attempts { get; set; }

        public void Output(byte[] data)
        {
            // Before the screen has been captured, output is already part of what the capture will show.
            if (Live) _chunks.Writer.TryWrite(data);
            else if (StateSeen) Stale = true;
        }

        public void Push(byte[] data) => _chunks.Writer.TryWrite(data);

        public void Complete() => _chunks.Writer.TryComplete();

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) == 0) _ = StartAsync();

            while (_rest is null || _offset == _rest.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
                if (_chunks.Reader.TryRead(out _rest)) _offset = 0;
            }

            int taken = Math.Min(buffer.Length, _rest.Length - _offset);
            _rest.AsMemory(_offset, taken).CopyTo(buffer);
            _offset += taken;
            return taken;
        }

        // The screen is fetched once tmux has heard the pane's real size, so that what is captured is
        // already wrapped the way it will be shown. A terminal that never reports a size still gets its screen.
        private async Task StartAsync()
        {
            await Task.WhenAny(_sized.Task, Task.Delay(SizeGrace)).ConfigureAwait(false);
            session.Prime(this);
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            if (data.Length > 0) await session.SendKeys(PaneId, data).ConfigureAwait(false);
        }

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
        {
            if (columns > 0 && rows > 0) session.ReportPaneSize(PaneId, columns, rows);
            _sized.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            session.RemovePane(this);
            return ValueTask.CompletedTask;
        }
    }
}

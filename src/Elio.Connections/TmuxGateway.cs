using System.Buffers;
using System.Text;
using Elio.Core;
using Elio.Terminal;

namespace Elio.Connections;

/// <summary>
/// Sits between a terminal and its connection and watches for tmux entering control mode ("tmux -CC new",
/// "tmux -CC attach"). Until then every byte passes through untouched. From then on the stream belongs to a
/// <see cref="TmuxSession"/>, the terminal shows only a one-line notice, and when tmux leaves control mode
/// the terminal gets its shell back.
/// </summary>
/// <remarks>
/// <para>The work happens inside <see cref="ReadAsync"/>: the terminal's own read loop is what drives the
/// tmux session, so it has to keep reading while attached, as a terminal does anyway.</para>
/// <para><see cref="Attached"/> and <see cref="Detached"/> are raised on the thread that reads.</para>
/// <para>While attached, what is typed into this terminal is dropped: tmux would take it for commands.
/// The UI ends the attachment with <see cref="Detach"/>.</para>
/// </remarks>
public sealed class TmuxGatewayTransport(ITerminalTransport inner) : ITerminalTransport
{
    // ESC P 1000 p: the device control string tmux opens control mode with.
    private static ReadOnlySpan<byte> Marker => "\u001bP1000p"u8;

    private readonly byte[] _scratch = new byte[32 * 1024];
    private readonly ArrayBufferWriter<byte> _shown = new(4096);
    private int _shownOffset;
    private int _matched;
    private volatile TmuxSession? _control;
    private volatile TmuxSession? _session;
    private bool _ended;

    /// <summary>tmux entered control mode and its windows and panes are known.</summary>
    public event Action<TmuxSession>? Attached;

    /// <summary>Control mode ended, by a detach, the session closing or the connection dropping.</summary>
    public event Action<TmuxSession>? Detached;

    /// <summary>The attached session, or null while this is an ordinary terminal.</summary>
    public TmuxSession? Session => _session;

    public bool ConnectionLost => inner.ConnectionLost;

    public string? Description => inner.Description;

    /// <summary>Asks tmux to detach. <see cref="Detached"/> follows once tmux has let go.</summary>
    public void Detach() => _ = _control?.Detach();

    /// <summary>
    /// Stops treating the stream as control mode without asking tmux. For a tmux that was killed or a
    /// connection that hangs, where the end of control mode will never arrive. Takes effect with the next output.
    /// </summary>
    public void Abandon() => _control?.Close(null);

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            int waiting = _shown.WrittenCount - _shownOffset;
            if (waiting > 0)
            {
                int taken = Math.Min(waiting, buffer.Length);
                _shown.WrittenSpan.Slice(_shownOffset, taken).CopyTo(buffer.Span);
                _shownOffset += taken;
                if (_shownOffset == _shown.WrittenCount)
                {
                    _shown.ResetWrittenCount();
                    _shownOffset = 0;
                }

                return taken;
            }

            if (_ended) return 0;

            int count = await inner.ReadAsync(_scratch, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                _ended = true;
                Leave(notice: false);
                continue;
            }

            Process(_scratch.AsSpan(0, count));
        }
    }

    private void Process(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (_control is { } control)
            {
                // Abandoned, or the connection refused a write: from here on it is ordinary output again.
                if (control.IsClosed && !control.Terminated)
                {
                    Leave(notice: true);
                    continue;
                }

                int used = control.Feed(data);
                data = data[used..];
                if (control.Terminated) Leave(notice: true);
                continue;
            }

            // The marker may arrive in pieces. Bytes that could be its beginning are held back until the
            // next byte settles it, so a split marker is neither missed nor half shown.
            byte b = data[0];
            data = data[1..];
            if (b == Marker[_matched])
            {
                if (++_matched == Marker.Length)
                {
                    _matched = 0;
                    Enter();
                }

                continue;
            }

            _shown.Write(Marker[.._matched]);
            _matched = 0;
            if (b == Marker[0]) _matched = 1;
            else _shown.Write(new ReadOnlySpan<byte>(in b));

            // Nothing is being held back, so everything up to the next ESC can go out in one piece.
            if (_matched == 0)
            {
                int next = data.IndexOf(Marker[0]);
                if (next < 0) next = data.Length;
                _shown.Write(data[..next]);
                data = data[next..];
            }
        }
    }

    private void Enter()
    {
        var control = new TmuxSession(inner.WriteAsync);
        control.Ready += () =>
        {
            _session = control;
            Show(L.T("tmux is attached and its windows are shown as tabs. Detach from tmux to use this terminal again."));
            Attached?.Invoke(control);
        };
        _control = control;
    }

    private void Leave(bool notice)
    {
        if (_control is not { } control) return;

        _control = null;
        control.Close(null);

        bool wasAttached = _session == control;
        _session = null;

        // tmux gives a reason when it was not a plain detach: the server went away, or the attach was refused.
        string? reason = control.ExitReason;
        if (notice && wasAttached) Show(reason is null ? L.T("tmux detached.") : L.T("tmux detached: {0}", reason));
        else if (notice) Show(reason is null ? L.T("tmux did not attach.") : L.T("tmux did not attach: {0}", reason));

        if (wasAttached) Detached?.Invoke(control);
    }

    private void Show(string text) => _shown.Write(Encoding.UTF8.GetBytes(text + "\r\n"));

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        _control is not { IsClosed: false } ? inner.WriteAsync(data, cancellationToken) : ValueTask.CompletedTask;

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken) => inner.ResizeAsync(columns, rows, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _control?.Close(null);
        await inner.DisposeAsync().ConfigureAwait(false);
    }
}

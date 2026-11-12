using System.Net.Sockets;
using Elio.Core;
using Elio.Terminal;

namespace Elio.Connections;

/// <summary>Takes the Telnet commands out of what a server sends and works out the answers.</summary>
/// <remarks>Kept apart from the socket so it can be tried byte by byte.</remarks>
internal sealed class TelnetProtocol
{
    private const byte Iac = 255;
    private const byte Dont = 254;
    private const byte Do = 253;
    private const byte Wont = 252;
    private const byte Will = 251;
    private const byte SubBegin = 250;
    private const byte SubEnd = 240;

    private const byte OptBinary = 0;
    private const byte OptEcho = 1;
    private const byte OptSuppressGoAhead = 3;
    private const byte OptTerminalType = 24;
    private const byte OptWindowSize = 31;

    private const int MaxSubnegotiation = 512;

    private enum State
    {
        Data,
        Command,
        Option,
        Sub,
        SubCommand,
    }

    private readonly List<byte> _sub = [];

    // What has been agreed, so a server repeating itself is not answered again and again.
    private readonly bool?[] _weDo = new bool?[256];
    private readonly bool?[] _theyDo = new bool?[256];
    private State _state;
    private byte _verb;
    private bool _afterReturn;
    private int _columns;
    private int _rows;

    public TelnetProtocol(int columns, int rows) => (_columns, _rows) = (columns, rows);

    /// <summary>The other side speaks Telnet, rather than being some plain service on a port.</summary>
    public bool Negotiated { get; private set; }

    private bool Binary => _weDo[OptBinary] == true;

    /// <summary>Reads what arrived. Text goes to <paramref name="text"/>, answers to send back go to <paramref name="replies"/>.</summary>
    /// <returns>How many bytes of text there are.</returns>
    public int Receive(ReadOnlySpan<byte> data, Span<byte> text, List<byte> replies)
    {
        int written = 0;
        foreach (byte b in data)
        {
            switch (_state)
            {
                case State.Data:
                    if (b == Iac)
                    {
                        _state = State.Command;
                    }
                    else if (_afterReturn && b == 0 && _theyDo[OptBinary] != true)
                    {
                        // A carriage return on its own travels as CR NUL.
                        _afterReturn = false;
                    }
                    else
                    {
                        _afterReturn = b == (byte)'\r';
                        text[written++] = b;
                    }

                    break;

                case State.Command:
                    if (b == Iac)
                    {
                        text[written++] = Iac;
                        _state = State.Data;
                    }
                    else if (b is Do or Dont or Will or Wont)
                    {
                        _verb = b;
                        _state = State.Option;
                    }
                    else if (b == SubBegin)
                    {
                        _sub.Clear();
                        _state = State.Sub;
                    }
                    else
                    {
                        // Go-ahead, no-op and the like need no answer.
                        _state = State.Data;
                    }

                    break;

                case State.Option:
                    Negotiated = true;
                    Negotiate(_verb, b, replies);
                    _state = State.Data;
                    break;

                case State.Sub:
                    if (b == Iac) _state = State.SubCommand;
                    else if (_sub.Count < MaxSubnegotiation) _sub.Add(b);
                    break;

                case State.SubCommand:
                    if (b == SubEnd)
                    {
                        Subnegotiate(replies);
                        _state = State.Data;
                    }
                    else
                    {
                        if (b == Iac && _sub.Count < MaxSubnegotiation) _sub.Add(Iac);
                        _state = State.Sub;
                    }

                    break;
            }
        }

        return written;
    }

    private void Negotiate(byte verb, byte option, List<byte> replies)
    {
        if (verb is Do or Dont)
        {
            bool agree = verb == Do && option is OptTerminalType or OptWindowSize or OptSuppressGoAhead or OptBinary;
            if (verb == Dont) agree = false;
            if (_weDo[option] == agree) return;

            _weDo[option] = agree;
            replies.AddRange([Iac, agree ? Will : Wont, option]);
            if (agree && option == OptWindowSize) AppendWindowSize(replies);
        }
        else
        {
            bool agree = verb == Will && option is OptEcho or OptSuppressGoAhead or OptBinary;
            if (verb == Wont) agree = false;
            if (_theyDo[option] == agree) return;

            _theyDo[option] = agree;
            replies.AddRange([Iac, agree ? Do : Dont, option]);
        }
    }

    private void Subnegotiate(List<byte> replies)
    {
        // "Send your terminal type."
        if (_sub.Count == 2 && _sub[0] == OptTerminalType && _sub[1] == 1)
        {
            replies.AddRange([Iac, SubBegin, OptTerminalType, 0]);
            replies.AddRange("XTERM-256COLOR"u8);
            replies.AddRange([Iac, SubEnd]);
        }
    }

    private void AppendWindowSize(List<byte> output)
    {
        output.AddRange([Iac, SubBegin, OptWindowSize]);
        foreach (int value in (ReadOnlySpan<int>)[_columns >> 8, _columns, _rows >> 8, _rows])
        {
            byte b = (byte)value;
            output.Add(b);
            if (b == Iac) output.Add(Iac);
        }

        output.AddRange([Iac, SubEnd]);
    }

    /// <summary>Records the new size, and writes the message telling the server if it asked to be told.</summary>
    public void Resize(int columns, int rows, List<byte> output)
    {
        (_columns, _rows) = (columns, rows);
        if (_weDo[OptWindowSize] == true) AppendWindowSize(output);
    }

    /// <summary>Prepares typed bytes for the wire.</summary>
    public void Send(ReadOnlySpan<byte> data, List<byte> output)
    {
        foreach (byte b in data)
        {
            output.Add(b);

            // The zero is only for a real Telnet server. A plain service would take it as input.
            if (b == Iac) output.Add(Iac);
            else if (b == (byte)'\r' && Negotiated && !Binary) output.Add(0);
        }
    }
}

/// <summary>A terminal on a server over Telnet. Nothing is encrypted, which is the protocol's nature.</summary>
public sealed class TelnetTransport : ITerminalTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly TelnetProtocol _protocol;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly byte[] _raw = new byte[32 * 1024];
    private volatile bool _lost;

    private TelnetTransport(TcpClient client, int columns, int rows)
    {
        _client = client;
        _stream = client.GetStream();
        _protocol = new TelnetProtocol(columns, rows);
    }

    /// <inheritdoc />
    public bool ConnectionLost => _lost;

    /// <inheritdoc />
    public string Description => "Telnet · " + L.T("unencrypted");

    public static async Task<TelnetTransport> ConnectAsync(string host, int port, int columns, int rows, CancellationToken cancellation)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(ConnectTimeout);
            try
            {
                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                throw new TimeoutException(L.T("{0}:{1} did not answer in {2:0} seconds.", host, port, ConnectTimeout.TotalSeconds));
            }

            // An idle session sends nothing, so without this a server that went away would go unnoticed.
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            return new TelnetTransport(client, columns, rows);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var replies = new List<byte>();
        try
        {
            while (true)
            {
                // Text never grows while the commands are taken out, so it fits where it is going.
                int count = await _stream.ReadAsync(_raw.AsMemory(0, Math.Min(_raw.Length, buffer.Length)), cancellationToken).ConfigureAwait(false);
                if (count == 0) return 0;

                replies.Clear();
                int text;
                lock (_protocol) text = _protocol.Receive(_raw.AsSpan(0, count), buffer.Span, replies);
                if (replies.Count > 0) await SendRawAsync(replies, cancellationToken).ConfigureAwait(false);
                if (text > 0) return text;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _lost = !cancellationToken.IsCancellationRequested;
            throw new IOException(ex.Message, ex);
        }
    }

    private async ValueTask SendRawAsync(List<byte> data, CancellationToken cancellation)
    {
        byte[] bytes = [.. data];
        await _sending.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _lost = !cancellation.IsCancellationRequested;
            throw new IOException(ex.Message, ex);
        }
        finally
        {
            _sending.Release();
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var wire = new List<byte>(data.Length + 8);
        lock (_protocol) _protocol.Send(data.Span, wire);
        return SendRawAsync(wire, cancellationToken);
    }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        var wire = new List<byte>();
        lock (_protocol) _protocol.Resize(columns, rows, wire);
        return wire.Count == 0 ? ValueTask.CompletedTask : SendRawAsync(wire, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

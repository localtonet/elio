using System.IO.Ports;
using System.Text;
using Elio.Core;
using Elio.Terminal;

namespace Elio.Connections;

/// <summary>Turns what a serial device sends into what the terminal should show, and typing into what the device expects.</summary>
/// <remarks>Kept apart from the port so it can be tried without a device.</remarks>
internal sealed class SerialCodec(SerialSettings settings)
{
    private const int DumpWidth = 16;

    private readonly byte[] _line = new byte[DumpWidth];
    private int _lineLength;
    private long _lineStart;
    private bool _afterReturn;

    /// <summary>The most bytes one received byte can turn into.</summary>
    public int Growth => settings.HexDump ? DumpWidth * 5 + 16 : 2;

    public void Incoming(ReadOnlySpan<byte> data, List<byte> output)
    {
        if (settings.HexDump)
        {
            Dump(data, output);
            return;
        }

        foreach (byte b in data)
        {
            if (settings.LfToCrLf && b == (byte)'\n' && !_afterReturn) output.Add((byte)'\r');
            _afterReturn = b == (byte)'\r';
            output.Add(b);
        }
    }

    // The unfinished line is drawn again in place each time more arrives, so a slow trickle shows up at once.
    private void Dump(ReadOnlySpan<byte> data, List<byte> output)
    {
        foreach (byte b in data)
        {
            _line[_lineLength++] = b;
            if (_lineLength < DumpWidth) continue;

            AppendDumpLine(output);
            output.AddRange("\r\n"u8);
            _lineStart += DumpWidth;
            _lineLength = 0;
        }

        if (_lineLength > 0) AppendDumpLine(output);
    }

    private void AppendDumpLine(List<byte> output)
    {
        var text = new StringBuilder(DumpWidth * 4 + 16);
        text.Append('\r').Append(_lineStart.ToString("x8")).Append("  ");
        for (int i = 0; i < DumpWidth; i++)
        {
            text.Append(i < _lineLength ? _line[i].ToString("x2") : "  ").Append(' ');
            if (i == (DumpWidth / 2) - 1) text.Append(' ');
        }

        text.Append(" |");
        for (int i = 0; i < _lineLength; i++) text.Append(_line[i] is >= 0x20 and < 0x7f ? (char)_line[i] : '.');
        text.Append('|');
        output.AddRange(Encoding.ASCII.GetBytes(text.ToString()));
    }

    public void Outgoing(ReadOnlySpan<byte> data, List<byte> output)
    {
        foreach (byte b in data)
        {
            if (b != (byte)'\r' || settings.Enter == SerialEnter.Cr) output.Add(b);
            else if (settings.Enter == SerialEnter.Lf) output.Add((byte)'\n');
            else output.AddRange("\r\n"u8);
        }
    }

    /// <summary>Reads bytes written as hex, such as "48 65 6c 6c 6f 0d" or "0x48,0x65". Null if it isn't that.</summary>
    public static byte[]? ParseHex(string text)
    {
        var digits = new StringBuilder();
        foreach (string word in text.Split([' ', ',', ';', ':', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string part = word.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? word[2..] : word;
            if (part.Length == 0 || part.Length % 2 != 0 && part.Length != 1) return null;
            digits.Append(part.Length == 1 ? "0" + part : part);
        }

        if (digits.Length == 0) return null;

        try
        {
            return Convert.FromHexString(digits.ToString());
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>A device on a serial port: a router's console, a microcontroller, a modem.</summary>
public sealed class SerialTransport : ITerminalTransport
{
    private readonly SerialPort _port;
    private readonly Stream _stream;
    private readonly SerialCodec _codec;
    private readonly List<byte> _shown = [];
    private readonly byte[] _raw = new byte[4096];
    private int _shownOffset;
    private volatile bool _lost;

    private SerialTransport(SerialPort port, SerialSettings settings)
    {
        _port = port;
        _stream = port.BaseStream;
        _codec = new SerialCodec(settings);

        string parity = settings.Parity switch { SerialParity.Odd => "O", SerialParity.Even => "E", _ => "N" };
        Description = $"{settings.BaudRate} {settings.DataBits}{parity}{settings.StopBits}" + (settings.HexDump ? "  ·  " + L.T("hex") : string.Empty);
    }

    /// <inheritdoc />
    public bool ConnectionLost => _lost;

    /// <inheritdoc />
    public string Description { get; }

    /// <summary>Reads bytes written as hex, such as "48 65 6c 6c 6f 0d" or "0x48,0x65". Null if it isn't that.</summary>
    public static byte[]? ParseHex(string text) => SerialCodec.ParseHex(text);

    /// <summary>The serial ports this computer has right now.</summary>
    public static IReadOnlyList<string> PortNames()
    {
        try
        {
            return SerialPort.GetPortNames().Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    /// <exception cref="IOException">The port is missing, in use, or does not take these settings.</exception>
    public static Task<SerialTransport> OpenAsync(string portName, SerialSettings settings, CancellationToken cancellation) => Task.Run(() =>
    {
        // Elsewhere a missing device is reported the same way as one that is in use.
        if (!OperatingSystem.IsWindows() && portName.StartsWith('/') && !File.Exists(portName)) throw new IOException($"{portName}: {L.T("There is no such port.")}");

        SerialPort? port = null;
        try
        {
            port = new SerialPort(
                portName,
                settings.BaudRate,
                settings.Parity switch { SerialParity.Odd => Parity.Odd, SerialParity.Even => Parity.Even, _ => Parity.None },
                settings.DataBits,
                settings.StopBits == 2 ? StopBits.Two : StopBits.One)
            {
                Handshake = settings.Flow switch { SerialFlow.Hardware => Handshake.RequestToSend, SerialFlow.Software => Handshake.XOnXOff, _ => Handshake.None },

                // Many boards hold themselves in reset, or stay silent, until these lines are raised.
                DtrEnable = true,
                RtsEnable = settings.Flow != SerialFlow.Hardware,
            };
            port.Open();
            return new SerialTransport(port, settings);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            port?.Dispose();
            string reason = ex switch
            {
                UnauthorizedAccessException => L.T("The port is in use by another program, or access is not allowed."),
                PlatformNotSupportedException => L.T("Serial ports cannot be used on this system."),
                FileNotFoundException => L.T("There is no such port."),
                _ => ex.Message,
            };
            throw new IOException($"{portName}: {reason}", ex);
        }
    }, cancellation);

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (_shownOffset >= _shown.Count)
        {
            _shown.Clear();
            _shownOffset = 0;

            int count;
            try
            {
                count = await _stream.ReadAsync(_raw, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
            {
                // A pulled cable shows up here, not as an orderly end.
                _lost = !cancellationToken.IsCancellationRequested;
                throw new IOException($"{_port.PortName}: {ex.Message}", ex);
            }

            if (count == 0)
            {
                _lost = true;
                return 0;
            }

            _codec.Incoming(_raw.AsSpan(0, count), _shown);
        }

        int taken = Math.Min(_shown.Count - _shownOffset, buffer.Length);
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_shown).Slice(_shownOffset, taken).CopyTo(buffer.Span);
        _shownOffset += taken;
        return taken;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var wire = new List<byte>(data.Length + 8);
        _codec.Outgoing(data.Span, wire);

        try
        {
            await _stream.WriteAsync(wire.ToArray(), cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException or TimeoutException)
        {
            _lost = !cancellationToken.IsCancellationRequested;
            throw new IOException($"{_port.PortName}: {ex.Message}", ex);
        }
    }

    // A serial line has no notion of a screen size.
    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        // Closing can wait on the driver, and on a device that was unplugged it can throw.
        _ = Task.Run(() =>
        {
            try
            {
                _port.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Already gone.
            }
        });
        return ValueTask.CompletedTask;
    }
}

namespace Elio.Terminal;

/// <summary>
/// The byte stream behind one terminal: SSH, Telnet, serial, a local shell or a Localtonet device.
/// The terminal only ever sees this, so a new kind of connection never touches drawing or input.
/// </summary>
/// <remarks>
/// Output is pulled rather than pushed. A flood of output then waits in the connection until the
/// terminal has room, instead of piling up in memory and starving the keyboard.
/// </remarks>
public interface ITerminalTransport : IAsyncDisposable
{
    /// <summary>The link broke, as opposed to the program on the other side finishing.</summary>
    bool ConnectionLost => false;

    /// <summary>A short note about the connection, for the status bar.</summary>
    string? Description => null;

    /// <summary>Waits for output and copies it into <paramref name="buffer"/>. Returns 0 once the session is over.</summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken);
}

namespace Elio.Terminal;

/// <summary>Writes terminal output to a file as plain text, leaving out colors and cursor movement.</summary>
internal sealed class OutputLog(string path) : IDisposable
{
    private enum State
    {
        Text,
        Escape,
        Control,
        OperatingSystem,
        OperatingSystemEscape,
    }

    private const byte EscapeByte = 0x1B;
    private const byte Bell = 0x07;

    // What scrolls past in a terminal is often not for other users of the computer, so the file is the owner's alone.
    private readonly FileStream _file = Elio.Core.PrivateFiles.Open(path, FileMode.Append, FileShare.Read);
    private State _state;

    public string Path { get; } = path;

    public void Write(ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            switch (_state)
            {
                case State.Text:
                    if (value == EscapeByte) _state = State.Escape;
                    else if (value >= 0x20 || value is (byte)'\n' or (byte)'\t') _file.WriteByte(value);
                    break;
                case State.Escape:
                    _state = value switch
                    {
                        (byte)'[' => State.Control,
                        (byte)']' => State.OperatingSystem,
                        _ => State.Text,
                    };
                    break;
                case State.Control:
                    if (value is >= 0x40 and <= 0x7E) _state = State.Text;
                    break;
                case State.OperatingSystem:
                    if (value == Bell) _state = State.Text;
                    else if (value == EscapeByte) _state = State.OperatingSystemEscape;
                    break;
                case State.OperatingSystemEscape:
                    _state = State.Text;
                    break;
            }
        }

        _file.Flush();
    }

    public void Dispose() => _file.Dispose();
}

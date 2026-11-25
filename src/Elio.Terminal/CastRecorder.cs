using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Elio.Terminal;

/// <summary>
/// Records what a terminal shows as an asciicast (version 2): a line describing the terminal, then one line per piece
/// of output with the seconds since the start. asciinema plays it back, and so does anything that reads the format.
/// Only output is recorded, never what is typed: a password typed at a prompt is not shown on the screen either.
/// </summary>
public sealed class CastRecorder : IDisposable
{
    private readonly Stream _file;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Output arrives in pieces that may end in the middle of a character; the decoder keeps the start of it for the next piece.
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly Func<double>? _elapsed;
    private bool _disposed;

    public string Path { get; }

    /// <param name="elapsed">Stands in for the clock, in tests.</param>
    public CastRecorder(string path, int columns, int rows, string? title = null, DateTimeOffset? started = null, Func<double>? elapsed = null)
        : this(Elio.Core.PrivateFiles.Open(path, FileMode.CreateNew, FileShare.Read), path, columns, rows, title, started, elapsed)
    {
    }

    /// <summary>Records into a stream, which it then owns.</summary>
    public CastRecorder(Stream file, string path, int columns, int rows, string? title = null, DateTimeOffset? started = null, Func<double>? elapsed = null)
    {
        _file = file;
        _elapsed = elapsed;
        Path = path;

        using (var header = new Utf8JsonWriter(_file))
        {
            header.WriteStartObject();
            header.WriteNumber("version", 2);
            header.WriteNumber("width", Math.Max(1, columns));
            header.WriteNumber("height", Math.Max(1, rows));
            header.WriteNumber("timestamp", (started ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());
            if (title is { Length: > 0 }) header.WriteString("title", title);
            header.WriteStartObject("env");
            header.WriteString("TERM", "xterm-256color");
            header.WriteEndObject();
            header.WriteEndObject();
        }

        _file.WriteByte((byte)'\n');
        _file.Flush();
    }

    private double Now => _elapsed?.Invoke() ?? _clock.Elapsed.TotalSeconds;

    /// <summary>Adds a piece of output.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed || data.IsEmpty) return;

        char[] chars = new char[_decoder.GetCharCount(data, flush: false)];
        int count = _decoder.GetChars(data, chars, flush: false);
        if (count == 0) return;

        Event("o", new string(chars, 0, count));
    }

    /// <summary>The terminal changed size: players resize with it.</summary>
    public void Resize(int columns, int rows)
    {
        if (_disposed) return;
        Event("r", string.Create(CultureInfo.InvariantCulture, $"{columns}x{rows}"));
    }

    private void Event(string code, string text)
    {
        using (var line = new Utf8JsonWriter(_file))
        {
            line.WriteStartArray();

            // Microseconds are as fine as players go; rounding keeps the file small and the same everywhere.
            line.WriteRawValue(Math.Round(Now, 6).ToString("0.######", CultureInfo.InvariantCulture));
            line.WriteStringValue(code);
            line.WriteStringValue(text);
            line.WriteEndArray();
        }

        _file.WriteByte((byte)'\n');
        _file.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _file.Dispose();
    }
}

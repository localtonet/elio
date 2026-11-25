using System.IO.Pipelines;
using System.Threading.Channels;
using Elio.Terminal;

namespace Elio.Tests;

/// <summary>Two connected ends of an in-memory connection: what one writes, the other reads.</summary>
internal static class DuplexPipe
{
    public static (Stream Left, Stream Right) Create()
    {
        // Never makes a writer wait for the reader, as a socket with room in its buffers would not.
        var options = new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0);
        var toRight = new Pipe(options);
        var toLeft = new Pipe(options);
        return (new End(toLeft.Reader.AsStream(), toRight.Writer.AsStream()), new End(toRight.Reader.AsStream(), toLeft.Writer.AsStream()));
    }

    private sealed class End(Stream reading, Stream writing) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => reading.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => reading.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => writing.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await writing.WriteAsync(buffer, cancellationToken);
            await writing.FlushAsync(cancellationToken);
        }

        public override void Flush() => writing.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                writing.Dispose();
                reading.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>A connection for a terminal that the test plays the other end of: it says what to read and sees what was written.</summary>
internal sealed class ScriptedTransport : ITerminalTransport
{
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<byte[]> _written = Channel.CreateUnbounded<byte[]>();
    private byte[] _rest = [];
    private int _offset;

    public List<(int Columns, int Rows)> Sizes { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>Output the terminal will read next.</summary>
    public void Say(byte[] data) => _incoming.Writer.TryWrite(data);

    public void Say(string text) => Say(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>The connection is over: reads give 0 once what was said has been read.</summary>
    public void End() => _incoming.Writer.TryComplete();

    /// <summary>The next thing the terminal wrote.</summary>
    public async Task<byte[]> HeardAsync(TimeSpan? patience = null) => await _written.Reader.ReadAsync().AsTask().WaitAsync(patience ?? Fuzz.Patience);

    /// <summary>Everything written so far, taken out of the record.</summary>
    public byte[] HeardSoFar()
    {
        var all = new List<byte>();
        while (_written.Reader.TryRead(out byte[]? data)) all.AddRange(data);
        return [.. all];
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (_offset == _rest.Length)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken)) return 0;
            if (_incoming.Reader.TryRead(out byte[]? next)) (_rest, _offset) = (next, 0);
        }

        int taken = Math.Min(buffer.Length, _rest.Length - _offset);
        _rest.AsMemory(_offset, taken).CopyTo(buffer);
        _offset += taken;
        return taken;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        _written.Writer.TryWrite(data.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
    {
        lock (Sizes) Sizes.Add((columns, rows));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

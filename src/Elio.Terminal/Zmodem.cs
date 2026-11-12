using System.Text;
using System.Threading.Channels;
using Elio.Core;

namespace Elio.Terminal;

/// <summary>What a Zmodem transfer needs from the app. A server can start one at any time, so nothing happens without asking here first.</summary>
public interface IZmodemHost
{
    /// <summary>The server offers a file. Returns where to save it, or null to skip it.</summary>
    Task<Stream?> AcceptFileAsync(string name, long size, CancellationToken cancellation);

    /// <summary>A file that was accepted has ended, successfully or not.</summary>
    void FinishFile(Stream file, bool complete);

    /// <summary>The server is waiting for files. Returns the paths to send, or nothing to refuse.</summary>
    Task<IReadOnlyList<string>> ChooseFilesAsync(CancellationToken cancellation);

    void ShowProgress(string name, long done, long total);

    /// <summary>The transfer is over. The argument says what went wrong, if anything did.</summary>
    void Finished(string? problem);
}

/// <summary>Spots the start of a Zmodem transfer in terminal output, even when it is split across reads.</summary>
internal sealed class ZmodemDetector
{
    private static ReadOnlySpan<byte> Signature => "**\u0018B0"u8;

    private int _matched;

    /// <summary>Where the marker ends in this data, and whether the server wants to send. Null if it isn't there.</summary>
    public (int End, bool ServerSends)? Find(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            byte value = data[i];

            if (_matched == Signature.Length)
            {
                _matched = 0;
                if (value == (byte)'0') return (i + 1, true);
                if (value == (byte)'1') return (i + 1, false);
                continue;
            }

            if (value == Signature[_matched]) _matched++;
            else _matched = value == Signature[0] ? 1 : 0;
        }

        return null;
    }
}

/// <summary>
/// One Zmodem exchange, written as straight-line code that pulls bytes as it needs them. Used by
/// <c>sz</c> and <c>rz</c> on the server to move files through an ordinary terminal connection.
/// </summary>
internal sealed class ZmodemTransfer
{
    private const byte Pad = (byte)'*';
    private const byte Dle = 0x18;
    private const byte Can = 0x18;
    private const byte FormatBin16 = (byte)'A';
    private const byte FormatHex = (byte)'B';
    private const byte FormatBin32 = (byte)'C';

    private const byte ZRQINIT = 0;
    private const byte ZRINIT = 1;
    private const byte ZSINIT = 2;
    private const byte ZACK = 3;
    private const byte ZFILE = 4;
    private const byte ZSKIP = 5;
    private const byte ZNAK = 6;
    private const byte ZABORT = 7;
    private const byte ZFIN = 8;
    private const byte ZRPOS = 9;
    private const byte ZDATA = 10;
    private const byte ZEOF = 11;
    private const byte ZFERR = 12;
    private const byte ZCAN = 16;

    private const byte EndFrame = (byte)'h';
    private const byte MoreNoAck = (byte)'i';
    private const byte MoreAck = (byte)'j';
    private const byte EndAck = (byte)'k';

    private const byte CanFullDuplex = 0x01;
    private const byte CanOverlapIo = 0x02;
    private const byte CanCrc32 = 0x20;
    private const byte BinaryFile = 1;

    private const int MaxSubpacket = 8192;
    private const int MaxRetries = 10;
    private const int CancelBytes = 8;

    // The most a file may grow to when the server did not say how large it is. Positions are four bytes wide,
    // so nothing well-behaved goes past this anyway.
    private const long MaxUnannouncedSize = 4L * 1024 * 1024 * 1024;
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OverAndOutWait = TimeSpan.FromMilliseconds(400);

    private readonly record struct Header(byte Type, uint Position, byte Flags, byte Format);

    private sealed class Aborted(string message) : Exception(message);

    private readonly Channel<byte[]> _incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64) { SingleWriter = true, SingleReader = true });
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _send;
    private readonly IZmodemHost _host;
    private readonly CancellationTokenSource _cancel = new();
    private readonly bool _serverSends;
    private byte[] _current;
    private int _offset;
    private int _cancels;

    public ZmodemTransfer(bool serverSends, byte[] firstBytes, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send, IZmodemHost host)
    {
        _serverSends = serverSends;
        _send = send;
        _host = host;

        // The marker was consumed while looking for it, so the header starts again from the top.
        byte[] marker = [Pad, Pad, Dle, FormatHex, (byte)'0', (byte)(serverSends ? '0' : '1')];
        _current = [.. marker, .. firstBytes];
    }

    /// <summary>Hands over bytes from the server. Returns false once the exchange no longer wants them.</summary>
    public async ValueTask<bool> ReceiveAsync(byte[] data, CancellationToken cancellation)
    {
        try
        {
            await _incoming.Writer.WriteAsync(data, cancellation).ConfigureAwait(false);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    /// <summary>The user wants it stopped.</summary>
    public void Cancel() => _cancel.Cancel();

    /// <summary>The connection went away.</summary>
    public void ConnectionEnded() => _incoming.Writer.TryComplete();

    /// <summary>What arrived after the exchange finished, which belongs to the terminal.</summary>
    public List<byte[]> TakeLeftovers()
    {
        _incoming.Writer.TryComplete();
        var leftovers = new List<byte[]>();
        if (_offset < _current.Length) leftovers.Add(_current[_offset..]);
        while (_incoming.Reader.TryRead(out byte[]? more)) leftovers.Add(more);
        return leftovers;
    }

    public async Task RunAsync()
    {
        string? problem = null;
        try
        {
            if (_serverSends) await ReceiveFilesAsync().ConfigureAwait(false);
            else await SendFilesAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Aborted or OperationCanceledException or ChannelClosedException or IOException or UnauthorizedAccessException)
        {
            // Stopping it on purpose needs no explanation. Going quiet does.
            if (!_cancel.IsCancellationRequested) problem = ex is OperationCanceledException ? L.T("The server stopped answering.") : ex.Message;
            await TrySendCancelAsync().ConfigureAwait(false);
        }

        _host.Finished(problem);
    }

    private async Task TrySendCancelAsync()
    {
        try
        {
            byte[] cancel = [.. Enumerable.Repeat(Can, CancelBytes), .. Enumerable.Repeat((byte)8, CancelBytes)];
            await _send(cancel, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // Nothing left to tell.
        }
    }

    private ValueTask<byte> NextAsync() => _offset < _current.Length ? new ValueTask<byte>(_current[_offset++]) : FillAsync();

    private async ValueTask<byte> FillAsync()
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
        limit.CancelAfter(SilenceLimit);
        _current = await _incoming.Reader.ReadAsync(limit.Token).ConfigureAwait(false);
        _offset = 1;
        return _current[0];
    }

    // Headers trail a few bytes (line ends, flow control) that mean nothing, so only a header's first byte counts.
    private bool HeaderIsWaiting()
    {
        while (true)
        {
            while (_offset < _current.Length)
            {
                CountCancel(_current[_offset]);
                if (_current[_offset] == Pad) return true;
                _offset++;
            }

            if (!_incoming.Reader.TryRead(out byte[]? more)) return false;
            _current = more;
            _offset = 0;
        }
    }

    // ---- CRCs --------------------------------------------------------------------------------

    private static ushort Crc16(ushort crc, byte value)
    {
        crc ^= (ushort)(value << 8);
        for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        return crc;
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        return table;
    }

    private static uint Crc32(uint crc, byte value) => Crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);

    // ---- Reading ------------------------------------------------------------------------------

    // Five cancel bytes in a row is how the other side says it is giving up.
    private void CountCancel(byte value)
    {
        _cancels = value == Can ? _cancels + 1 : 0;
        if (_cancels >= 5) throw new Aborted(L.T("The other side cancelled the transfer."));
    }

    /// <summary>Reads one byte of escaped data. Values above 255 are frame-end markers.</summary>
    private async ValueTask<int> NextDecodedAsync()
    {
        while (true)
        {
            byte value = await NextAsync().ConfigureAwait(false);
            if (value is 0x11 or 0x13 or 0x91 or 0x93) continue;
            if (value != Dle) return value;

            while (true)
            {
                value = await NextAsync().ConfigureAwait(false);
                CountCancel(value);
                if (value is 0x11 or 0x13 or 0x91 or 0x93) continue;

                _cancels = value == Can ? _cancels : 0;
                if (value is EndFrame or MoreNoAck or MoreAck or EndAck) return 0x100 | value;
                if (value == (byte)'l') return 0x7F;
                if (value == (byte)'m') return 0xFF;
                if (value == Can) continue;
                if ((value & 0x60) == 0x40) return value ^ 0x40;
                return -1;
            }
        }
    }

    private async ValueTask<byte> NextHexAsync()
    {
        static int Digit(byte b) => b switch
        {
            >= (byte)'0' and <= (byte)'9' => b - '0',
            >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
            >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
            _ => -1,
        };

        int high = Digit(await NextAsync().ConfigureAwait(false));
        int low = Digit(await NextAsync().ConfigureAwait(false));
        return high < 0 || low < 0 ? throw new FormatException() : (byte)((high << 4) | low);
    }

    private async Task<Header> ReadHeaderAsync()
    {
        while (true)
        {
            byte value = await NextAsync().ConfigureAwait(false);
            CountCancel(value);
            if (value != Pad) continue;

            do value = await NextAsync().ConfigureAwait(false);
            while (value == Pad);
            if (value != Dle) continue;

            byte format = await NextAsync().ConfigureAwait(false);
            try
            {
                Header? header = format switch
                {
                    FormatHex => await ReadHexHeaderAsync().ConfigureAwait(false),
                    FormatBin16 => await ReadBinaryHeaderAsync(format, 2).ConfigureAwait(false),
                    FormatBin32 => await ReadBinaryHeaderAsync(format, 4).ConfigureAwait(false),
                    _ => null,
                };
                if (header is Header good) return good;
            }
            catch (FormatException)
            {
                // Damaged. The next header will do.
            }
        }
    }

    private static Header MakeHeader(ReadOnlySpan<byte> raw, byte format) =>
        new(raw[0], (uint)(raw[1] | (raw[2] << 8) | (raw[3] << 16) | (raw[4] << 24)), raw[4], format);

    private async Task<Header?> ReadHexHeaderAsync()
    {
        byte[] raw = new byte[5];
        ushort crc = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = await NextHexAsync().ConfigureAwait(false);
            crc = Crc16(crc, raw[i]);
        }

        ushort sent = (ushort)((await NextHexAsync().ConfigureAwait(false) << 8) | await NextHexAsync().ConfigureAwait(false));
        return crc == sent ? MakeHeader(raw, FormatHex) : null;
    }

    private async Task<Header?> ReadBinaryHeaderAsync(byte format, int crcBytes)
    {
        byte[] raw = new byte[5];
        ushort crc16 = 0;
        uint crc32 = 0xFFFFFFFF;

        for (int i = 0; i < raw.Length; i++)
        {
            int value = await NextDecodedAsync().ConfigureAwait(false);
            if (value is < 0 or > 255) return null;
            raw[i] = (byte)value;
            crc16 = Crc16(crc16, raw[i]);
            crc32 = Crc32(crc32, raw[i]);
        }

        uint sent = 0;
        for (int i = 0; i < crcBytes; i++)
        {
            int value = await NextDecodedAsync().ConfigureAwait(false);
            if (value is < 0 or > 255) return null;
            sent = crcBytes == 2 ? (sent << 8) | (uint)value : sent | ((uint)value << (8 * i));
        }

        bool good = crcBytes == 2 ? crc16 == sent : ~crc32 == sent;
        return good ? MakeHeader(raw, format) : null;
    }

    /// <summary>Reads one block of file data. Returns how it ended, or 0 if it was damaged.</summary>
    private async Task<(byte End, int Length)> ReadSubpacketAsync(byte[] buffer, byte format)
    {
        bool wide = format == FormatBin32;
        ushort crc16 = 0;
        uint crc32 = 0xFFFFFFFF;
        int length = 0;

        while (true)
        {
            int value = await NextDecodedAsync().ConfigureAwait(false);
            if (value < 0) return (0, 0);

            if (value > 255)
            {
                byte end = (byte)value;
                crc16 = Crc16(crc16, end);
                crc32 = Crc32(crc32, end);

                uint sent = 0;
                int crcBytes = wide ? 4 : 2;
                for (int i = 0; i < crcBytes; i++)
                {
                    int part = await NextDecodedAsync().ConfigureAwait(false);
                    if (part is < 0 or > 255) return (0, 0);
                    sent = wide ? sent | ((uint)part << (8 * i)) : (sent << 8) | (uint)part;
                }

                return (wide ? ~crc32 == sent : crc16 == sent) ? (end, length) : ((byte)0, 0);
            }

            if (length >= buffer.Length) return (0, 0);
            buffer[length++] = (byte)value;
            crc16 = Crc16(crc16, (byte)value);
            crc32 = Crc32(crc32, (byte)value);
        }
    }

    // ---- Writing ------------------------------------------------------------------------------

    private ValueTask SendAsync(byte[] data) => _send(data, _cancel.Token);

    private static void PutEscaped(List<byte> target, byte value)
    {
        if (value is Dle or 0x10 or 0x90 or 0x11 or 0x91 or 0x13 or 0x93)
        {
            target.Add(Dle);
            target.Add((byte)(value ^ 0x40));
        }
        else
        {
            target.Add(value);
        }
    }

    private static byte[] PositionBytes(uint position) => [(byte)position, (byte)(position >> 8), (byte)(position >> 16), (byte)(position >> 24)];

    private ValueTask SendHexHeaderAsync(byte type, byte[] data)
    {
        var text = new StringBuilder("**\u0018B");
        ushort crc = Crc16(0, type);
        text.Append(type.ToString("x2"));
        foreach (byte b in data)
        {
            crc = Crc16(crc, b);
            text.Append(b.ToString("x2"));
        }

        text.Append(crc.ToString("x4"));
        List<byte> bytes = [.. Encoding.ASCII.GetBytes(text.ToString()), 0x0D, 0x8A];
        if (type is not (ZFIN or ZACK)) bytes.Add(0x11);
        return SendAsync([.. bytes]);
    }

    private void AppendBinaryHeader(List<byte> target, byte type, byte[] data)
    {
        target.Add(Pad);
        target.Add(Dle);
        target.Add(FormatBin32);

        uint crc = Crc32(0xFFFFFFFF, type);
        PutEscaped(target, type);
        foreach (byte b in data)
        {
            crc = Crc32(crc, b);
            PutEscaped(target, b);
        }

        crc = ~crc;
        for (int i = 0; i < 4; i++) PutEscaped(target, (byte)(crc >> (8 * i)));
    }

    private static void AppendSubpacket(List<byte> target, ReadOnlySpan<byte> data, byte end)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = Crc32(crc, b);
            PutEscaped(target, b);
        }

        crc = ~Crc32(crc, end);
        target.Add(Dle);
        target.Add(end);
        for (int i = 0; i < 4; i++) PutEscaped(target, (byte)(crc >> (8 * i)));
    }

    // ---- The server is sending ----------------------------------------------------------------

    private async Task ReceiveFilesAsync()
    {
        byte[] block = new byte[MaxSubpacket * 4];
        Stream? file = null;
        string name = string.Empty;
        long size = 0;
        long position = 0;

        try
        {
            while (true)
            {
                Header header = await ReadHeaderAsync().ConfigureAwait(false);

                switch (header.Type)
                {
                    case ZRQINIT:
                        await SendReadyAsync().ConfigureAwait(false);
                        break;

                    case ZSINIT:
                        await ReadSubpacketAsync(block, header.Format).ConfigureAwait(false);
                        await SendHexHeaderAsync(ZACK, new byte[4]).ConfigureAwait(false);
                        break;

                    case ZFILE:
                        var (end, length) = await ReadSubpacketAsync(block, header.Format).ConfigureAwait(false);
                        if (end == 0)
                        {
                            await SendHexHeaderAsync(ZNAK, new byte[4]).ConfigureAwait(false);
                            break;
                        }

                        (name, size) = ParseFileInfo(block.AsSpan(0, length));
                        file = await _host.AcceptFileAsync(name, size, _cancel.Token).ConfigureAwait(false);
                        position = 0;
                        if (file is null) await SendHexHeaderAsync(ZSKIP, new byte[4]).ConfigureAwait(false);
                        else await SendHexHeaderAsync(ZRPOS, PositionBytes(0)).ConfigureAwait(false);
                        break;

                    case ZDATA:
                        if (file is null) break;
                        if (header.Position != (uint)position)
                        {
                            await SendHexHeaderAsync(ZRPOS, PositionBytes((uint)position)).ConfigureAwait(false);
                            break;
                        }

                        position = await ReceiveDataAsync(file, block, header.Format, name, size, position).ConfigureAwait(false);
                        break;

                    case ZEOF:
                        if (file is null || header.Position != (uint)position) break;
                        await file.FlushAsync(_cancel.Token).ConfigureAwait(false);
                        _host.FinishFile(file, complete: true);
                        file = null;
                        await SendReadyAsync().ConfigureAwait(false);
                        break;

                    case ZFIN:
                        await SendHexHeaderAsync(ZFIN, new byte[4]).ConfigureAwait(false);
                        await ConsumeOverAndOutAsync().ConfigureAwait(false);
                        return;

                    case ZCAN or ZABORT or ZFERR:
                        throw new Aborted(L.T("The other side stopped the transfer."));
                }
            }
        }
        finally
        {
            if (file is not null) _host.FinishFile(file, complete: false);
        }
    }

    private ValueTask SendReadyAsync() => SendHexHeaderAsync(ZRINIT, [0, 0, 0, CanFullDuplex | CanOverlapIo | CanCrc32]);

    private static (string Name, long Size) ParseFileInfo(ReadOnlySpan<byte> info)
    {
        int zero = info.IndexOf((byte)0);
        string name = Encoding.UTF8.GetString(zero >= 0 ? info[..zero] : info);
        long size = -1;

        if (zero >= 0)
        {
            string[] parts = Encoding.ASCII.GetString(info[(zero + 1)..]).TrimEnd('\0').Split(' ');
            if (parts.Length > 0 && long.TryParse(parts[0], out long parsed)) size = parsed;
        }

        return (name, size);
    }

    private async Task<long> ReceiveDataAsync(Stream file, byte[] block, byte format, string name, long size, long position)
    {
        while (true)
        {
            var (end, length) = await ReadSubpacketAsync(block, format).ConfigureAwait(false);
            if (end == 0)
            {
                // Ask for everything from the last good byte again.
                await SendHexHeaderAsync(ZRPOS, PositionBytes((uint)position)).ConfigureAwait(false);
                return position;
            }

            // The user agreed to a file of the size the server gave, not to whatever it goes on to send. Without
            // this a server could fill the disk. A size of nothing is what some senders give when they do not know.
            if (position + length > (size > 0 ? size : MaxUnannouncedSize)) throw new Aborted(L.T("The server sent more data than expected, so the transfer was stopped."));

            await file.WriteAsync(block.AsMemory(0, length), _cancel.Token).ConfigureAwait(false);
            position += length;
            _host.ShowProgress(name, position, size);

            if (end is EndAck or MoreAck) await SendHexHeaderAsync(ZACK, PositionBytes((uint)position)).ConfigureAwait(false);
            if (end is EndAck or EndFrame) return position;
        }
    }

    // "OO" is the last thing sent, and it would otherwise land on the user's screen. It may also never come.
    private async Task ConsumeOverAndOutAsync()
    {
        using var limit = new CancellationTokenSource(OverAndOutWait);
        try
        {
            for (int i = 0; i < 2; i++)
            {
                if (_offset >= _current.Length)
                {
                    _current = await _incoming.Reader.ReadAsync(limit.Token).ConfigureAwait(false);
                    _offset = 0;
                }

                if (_current[_offset] != (byte)'O') return;
                _offset++;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException)
        {
            // It was only a courtesy.
        }
    }

    // ---- The server is receiving --------------------------------------------------------------

    private async Task SendFilesAsync()
    {
        Header first = await ReadHeaderAsync().ConfigureAwait(false);
        if (first.Type != ZRINIT) throw new Aborted(L.T("Unexpected start of a Zmodem transfer."));

        IReadOnlyList<string> files = await _host.ChooseFilesAsync(_cancel.Token).ConfigureAwait(false);
        if (files.Count == 0)
        {
            await _cancel.CancelAsync().ConfigureAwait(false);
            throw new OperationCanceledException();
        }

        for (int i = 0; i < files.Count; i++) await SendFileAsync(files[i], files.Count - i).ConfigureAwait(false);

        await SendHexHeaderAsync(ZFIN, new byte[4]).ConfigureAwait(false);
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            Header reply = await ReadHeaderAsync().ConfigureAwait(false);
            if (reply.Type != ZFIN) continue;

            await SendAsync("OO"u8.ToArray()).ConfigureAwait(false);
            return;
        }
    }

    private async Task SendFileAsync(string path, int filesLeft)
    {
        var info = new FileInfo(path);
        string name = info.Name;
        long size = info.Length;
        long seconds = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();

        var frame = new List<byte>();
        AppendBinaryHeader(frame, ZFILE, [0, 0, 0, BinaryFile]);
        byte[] description = [.. Encoding.UTF8.GetBytes(name), 0, .. Encoding.ASCII.GetBytes($"{size} {Convert.ToString(seconds, 8)} 100644 0 {filesLeft} {size}"), 0];
        AppendSubpacket(frame, description, EndAck);
        byte[] announcement = [.. frame];
        await SendAsync(announcement).ConfigureAwait(false);

        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, MaxSubpacket * 4, FileOptions.Asynchronous);

        for (int attempt = 0; attempt < MaxRetries;)
        {
            Header reply = await ReadHeaderAsync().ConfigureAwait(false);
            switch (reply.Type)
            {
                case ZRPOS:
                    long next = await StreamFromAsync(source, name, size, reply.Position).ConfigureAwait(false);
                    if (next < 0) return;
                    attempt++;
                    break;

                case ZSKIP:
                    return;

                case ZRINIT or ZNAK:
                    attempt++;
                    await SendAsync(announcement).ConfigureAwait(false);
                    break;

                case ZCAN or ZABORT or ZFERR or ZFIN:
                    throw new Aborted(L.T("The other side stopped the transfer."));
            }
        }

        throw new Aborted(L.T("Too many retries."));
    }

    /// <summary>Sends from a position to the end of the file. Returns -1 when the file was accepted, or the position to resend from.</summary>
    private async Task<long> StreamFromAsync(Stream source, string name, long size, uint start)
    {
        long position = start;
        source.Position = position;

        var frame = new List<byte>(MaxSubpacket * 2);
        AppendBinaryHeader(frame, ZDATA, PositionBytes((uint)position));
        await SendAsync([.. frame]).ConfigureAwait(false);

        byte[] block = new byte[MaxSubpacket];
        while (true)
        {
            int read = await source.ReadAsync(block, _cancel.Token).ConfigureAwait(false);
            bool last = position + read >= size || read == 0;

            frame.Clear();
            AppendSubpacket(frame, block.AsSpan(0, read), last ? EndFrame : MoreNoAck);
            await SendAsync([.. frame]).ConfigureAwait(false);
            position += read;
            _host.ShowProgress(name, position, size);

            if (last) break;

            // The receiver only speaks up mid-stream to say something arrived damaged.
            if (HeaderIsWaiting())
            {
                Header interruption = await ReadHeaderAsync().ConfigureAwait(false);
                if (interruption.Type == ZRPOS) return await StreamFromAsync(source, name, size, interruption.Position).ConfigureAwait(false);
                if (interruption.Type is ZCAN or ZABORT or ZFERR) throw new Aborted(L.T("The other side stopped the transfer."));
            }
        }

        frame.Clear();
        AppendBinaryHeader(frame, ZEOF, PositionBytes((uint)position));
        await SendAsync([.. frame]).ConfigureAwait(false);

        while (true)
        {
            Header reply = await ReadHeaderAsync().ConfigureAwait(false);
            switch (reply.Type)
            {
                case ZRINIT:
                    return -1;
                case ZRPOS:
                    return await StreamFromAsync(source, name, size, reply.Position).ConfigureAwait(false);
                case ZCAN or ZABORT or ZFERR:
                    throw new Aborted(L.T("The other side stopped the transfer."));
            }
        }
    }
}

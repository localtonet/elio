using System.Text;
using Elio.Terminal;

namespace Elio.Tests;

/// <summary>
/// What the other end of a Zmodem transfer says, written and read the way lrzsz does: hex headers, binary headers
/// with 16 or 32 bit checks, data in escaped subpackets.
/// </summary>
internal static class ZmodemWire
{
    public const byte Pad = (byte)'*';
    public const byte Dle = 0x18;

    public const byte ZRQINIT = 0, ZRINIT = 1, ZSINIT = 2, ZACK = 3, ZFILE = 4, ZSKIP = 5, ZNAK = 6, ZABORT = 7, ZFIN = 8, ZRPOS = 9, ZDATA = 10, ZEOF = 11, ZFERR = 12, ZCAN = 16;
    public const byte EndFrame = (byte)'h', MoreNoAck = (byte)'i', MoreAck = (byte)'j', EndAck = (byte)'k';

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
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

    public static uint Crc32(uint crc, byte value) => Table[(crc ^ value) & 0xFF] ^ (crc >> 8);

    public static ushort Crc16(ushort crc, byte value)
    {
        crc ^= (ushort)(value << 8);
        for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        return crc;
    }

    private static byte[] Raw(byte type, uint position) => [type, (byte)position, (byte)(position >> 8), (byte)(position >> 16), (byte)(position >> 24)];

    private static void Escape(List<byte> output, byte value)
    {
        if (value is 0x18 or 0x10 or 0x90 or 0x11 or 0x91 or 0x13 or 0x93)
        {
            output.Add(Dle);
            output.Add((byte)(value ^ 0x40));
        }
        else
        {
            output.Add(value);
        }
    }

    public static byte[] Hex(byte type, uint position = 0)
    {
        byte[] raw = Raw(type, position);
        ushort crc = 0;
        foreach (byte b in raw) crc = Crc16(crc, b);
        string text = "**\u0018B" + Convert.ToHexString(raw).ToLowerInvariant() + crc.ToString("x4");
        return [.. Encoding.ASCII.GetBytes(text), 0x0D, 0x8A, .. type is ZFIN or ZACK ? Array.Empty<byte>() : [0x11]];
    }

    public static byte[] Binary32(byte type, uint position = 0)
    {
        var output = new List<byte> { Pad, Dle, (byte)'C' };
        uint crc = 0xFFFFFFFF;
        foreach (byte b in Raw(type, position))
        {
            crc = Crc32(crc, b);
            Escape(output, b);
        }

        crc = ~crc;
        for (int i = 0; i < 4; i++) Escape(output, (byte)(crc >> (8 * i)));
        return [.. output];
    }

    public static byte[] Binary16(byte type, uint position = 0)
    {
        var output = new List<byte> { Pad, Dle, (byte)'A' };
        ushort crc = 0;
        foreach (byte b in Raw(type, position))
        {
            crc = Crc16(crc, b);
            Escape(output, b);
        }

        Escape(output, (byte)(crc >> 8));
        Escape(output, (byte)crc);
        return [.. output];
    }

    public static byte[] Subpacket32(ReadOnlySpan<byte> data, byte end)
    {
        var output = new List<byte>(data.Length + 16);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = Crc32(crc, b);
            Escape(output, b);
        }

        crc = ~Crc32(crc, end);
        output.Add(Dle);
        output.Add(end);
        for (int i = 0; i < 4; i++) Escape(output, (byte)(crc >> (8 * i)));
        return [.. output];
    }

    public static byte[] Subpacket16(ReadOnlySpan<byte> data, byte end)
    {
        var output = new List<byte>(data.Length + 16);
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc = Crc16(crc, b);
            Escape(output, b);
        }

        crc = Crc16(crc, end);
        output.Add(Dle);
        output.Add(end);
        Escape(output, (byte)(crc >> 8));
        Escape(output, (byte)crc);
        return [.. output];
    }

    public static byte[] FileInfo(string name, long size) => [.. Encoding.UTF8.GetBytes(name), 0, .. Encoding.ASCII.GetBytes($"{size} 14000000000 100644 0 1 {size}"), 0];

    /// <summary>
    /// A whole sz session sending one file, as one stream of bytes, starting with the ZRQINIT whose first bytes are what
    /// gives a Zmodem start away.
    /// </summary>
    public static byte[] SendSession(string name, byte[] content, bool wide = true, int piece = 1024, long? announced = null)
    {
        Func<byte, uint, byte[]> header = wide ? Binary32 : Binary16;
        Func<byte[], byte, byte[]> subpacket = wide ? (d, e) => Subpacket32(d, e) : (d, e) => Subpacket16(d, e);
        var stream = new List<byte>();
        stream.AddRange(Hex(ZRQINIT));
        stream.AddRange(header(ZFILE, 1u << 24));
        stream.AddRange(subpacket(FileInfo(name, announced ?? content.Length), EndAck));
        stream.AddRange(DataFrames(content, 0, piece, wide));
        stream.AddRange(header(ZEOF, (uint)content.Length));
        stream.AddRange(Hex(ZFIN));
        stream.AddRange("OO"u8.ToArray());
        return [.. stream];
    }

    public static byte[] DataFrames(byte[] content, int from, int piece = 1024, bool wide = true)
    {
        var stream = new List<byte>();
        stream.AddRange(wide ? Binary32(ZDATA, (uint)from) : Binary16(ZDATA, (uint)from));
        for (int at = from; at < content.Length || at == from; at += piece)
        {
            int length = Math.Min(piece, content.Length - at);
            byte end = at + length >= content.Length ? EndFrame : MoreNoAck;
            ReadOnlySpan<byte> part = content.AsSpan(at, Math.Max(0, length));
            stream.AddRange(wide ? Subpacket32(part, end) : Subpacket16(part, end));
            if (end == EndFrame) break;
        }

        return [.. stream];
    }

    /// <summary>One frame read back from what the transfer sent.</summary>
    public sealed record Frame(byte Type, uint Position, char Format, List<(byte[] Data, byte End)> Subpackets);

    /// <summary>Reads the complete frames in what was sent so far. An unfinished one at the end is left out.</summary>
    public static List<Frame> Parse(byte[] sent)
    {
        var frames = new List<Frame>();
        int i = 0;
        while (true)
        {
            int start = Array.IndexOf(sent, Pad, i);
            if (start < 0) return frames;
            i = start;
            while (i < sent.Length && sent[i] == Pad) i++;
            if (i + 1 >= sent.Length) return frames;
            if (sent[i] != Dle)
            {
                continue;
            }

            char format = (char)sent[i + 1];
            i += 2;
            Frame? frame = format switch
            {
                'B' => ReadHex(sent, ref i),
                'C' => ReadBinary(sent, ref i),
                _ => null,
            };
            if (frame is null) return frames;
            frames.Add(frame);
        }
    }

    private static Frame? ReadHex(byte[] sent, ref int i)
    {
        if (i + 14 > sent.Length) return null;
        byte[] raw = Convert.FromHexString(Encoding.ASCII.GetString(sent, i, 10));
        i += 14;
        return new Frame(raw[0], (uint)(raw[1] | (raw[2] << 8) | (raw[3] << 16) | (raw[4] << 24)), 'B', []);
    }

    // -1 when the data ran out; 0x100 and up for the end of a subpacket.
    private static int Next(byte[] sent, ref int i)
    {
        if (i >= sent.Length) return -1;
        byte value = sent[i++];
        if (value != Dle) return value;
        if (i >= sent.Length) return -1;
        value = sent[i++];
        return value switch
        {
            EndFrame or MoreNoAck or MoreAck or EndAck => 0x100 | value,
            (byte)'l' => 0x7F,
            (byte)'m' => 0xFF,
            _ => value ^ 0x40,
        };
    }

    private static Frame? ReadBinary(byte[] sent, ref int i)
    {
        byte[] raw = new byte[9];
        for (int k = 0; k < raw.Length; k++)
        {
            int value = Next(sent, ref i);
            if (value is < 0 or > 255) return null;
            raw[k] = (byte)value;
        }

        var frame = new Frame(raw[0], (uint)(raw[1] | (raw[2] << 8) | (raw[3] << 16) | (raw[4] << 24)), 'C', []);
        if (raw[0] is not (ZFILE or ZDATA or ZSINIT)) return frame;

        while (true)
        {
            var data = new List<byte>();
            int value;
            while ((value = Next(sent, ref i)) is >= 0 and <= 255) data.Add((byte)value);
            if (value < 0) return null;
            for (int k = 0; k < 4; k++)
            {
                if (Next(sent, ref i) < 0) return null;
            }

            byte end = (byte)value;
            frame.Subpackets.Add(([.. data], end));
            if (end is EndFrame or EndAck) return frame;
        }
    }
}

/// <summary>
/// A Zmodem transfer against a scripted sz or rz on the other end, and against what a broken or hostile one might
/// send instead. The transfer runs on its own; the test feeds it the server's bytes in pieces and reads what it sends.
/// </summary>
public sealed class ZmodemTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private sealed class Host : IZmodemHost
    {
        public List<(string Name, long Size)> Offered { get; } = [];

        public List<(MemoryStream File, bool Complete)> Closed { get; } = [];

        public Func<string, long, Stream?> Accept { get; set; } = (_, _) => new MemoryStream();

        public IReadOnlyList<string> ToSend { get; set; } = [];

        public TaskCompletionSource<string?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int FinishedCalls;

        public long LastProgress { get; private set; }

        public Task<Stream?> AcceptFileAsync(string name, long size, CancellationToken cancellation)
        {
            lock (Offered) Offered.Add((name, size));
            return Task.FromResult(Accept(name, size));
        }

        public void FinishFile(Stream file, bool complete)
        {
            lock (Closed) Closed.Add(((MemoryStream)file, complete));
        }

        public Task<IReadOnlyList<string>> ChooseFilesAsync(CancellationToken cancellation) => Task.FromResult(ToSend);

        public void ShowProgress(string name, long done, long total) => LastProgress = done;

        public void Finished(string? problem)
        {
            Interlocked.Increment(ref FinishedCalls);
            Done.TrySetResult(problem);
        }
    }

    /// <summary>What the transfer sent back, collected as it goes.</summary>
    private sealed class Outbox
    {
        private readonly List<byte> _sent = [];

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation)
        {
            lock (_sent) _sent.AddRange(data.ToArray());
            return ValueTask.CompletedTask;
        }

        public byte[] Sent
        {
            get
            {
                lock (_sent) return [.. _sent];
            }
        }

        public List<ZmodemWire.Frame> Frames => ZmodemWire.Parse(Sent);

        public async Task<List<ZmodemWire.Frame>> WaitForAsync(Func<List<ZmodemWire.Frame>, bool> condition)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                List<ZmodemWire.Frame> frames = Frames;
                if (condition(frames)) return frames;
                if (clock.Elapsed > Patience) Assert.Fail($"The transfer never sent what was waited for. It sent: {string.Join(", ", frames.Select(f => f.Type))}");
                await Task.Delay(5);
            }
        }
    }

    private static readonly byte[] CancelSequence = [.. Enumerable.Repeat((byte)0x18, 8), .. Enumerable.Repeat((byte)8, 8)];

    /// <summary>Runs a transfer that receives what the server streams, cut into pieces, then the connection ends.</summary>
    private static async Task<(string? Problem, Outbox Outbox, List<byte[]> Leftovers)> ReceiveAsync(byte[] fromServer, Host host, int seed = 1, bool endConnection = true, Action<ZmodemTransfer>? meanwhile = null, bool wholeAtOnce = false)
    {
        Assert.Equal("**\u0018B00"u8.ToArray(), fromServer[..6]);
        var random = new Random(seed);
        List<byte[]> pieces = wholeAtOnce ? [fromServer[6..]] : [.. Fuzz.Pieces(random, fromServer[6..], 1 + random.Next(4096))];
        var outbox = new Outbox();
        var transfer = new ZmodemTransfer(serverSends: true, pieces.Count > 0 ? pieces[0] : [], outbox.SendAsync, host);

        Task run = transfer.RunAsync();
        Task feeding = Task.Run(async () =>
        {
            foreach (byte[] piece in pieces.Skip(1))
            {
                if (!await transfer.ReceiveAsync(piece, CancellationToken.None)) return;
            }

            if (endConnection) transfer.ConnectionEnded();
        });
        meanwhile?.Invoke(transfer);

        await run.WaitAsync(Patience);
        List<byte[]> leftovers = transfer.TakeLeftovers();
        await feeding.WaitAsync(Patience);
        return (await host.Done.Task.WaitAsync(Patience), outbox, leftovers);
    }

    private static byte[] Content(int length)
    {
        // Every byte value, the ones that have to be escaped included, many times over.
        byte[] content = new byte[length];
        for (int i = 0; i < content.Length; i++) content[i] = (byte)((i * 7) + (i >> 8));
        return content;
    }

    // ---- Spotting a transfer in terminal output ------------------------------------------------

    [Theory]
    [InlineData("rz\r**\u0018B00000000000000\r\n", 9, true)]
    [InlineData("rz waiting to receive.**\u0018B0100000023be50\r\n", 28, false)]
    public void Detector_FindsTheStartAndWhichWayFilesGo(string output, int end, bool serverSends) =>
        Assert.Equal((end, serverSends), new ZmodemDetector().Find(Encoding.ASCII.GetBytes(output)));

    [Fact]
    public void Detector_FindsAStartSplitAcrossReadsAtAnyPoint()
    {
        byte[] output = "$ sz notes.txt\r\nrz\r**\u0018B00000000000000\r\n"u8.ToArray();
        for (int cut = 1; cut < output.Length; cut++)
        {
            var detector = new ZmodemDetector();
            var first = detector.Find(output.AsSpan(0, cut));
            var second = first is null ? detector.Find(output.AsSpan(cut)) : null;

            int end = first?.End ?? (cut + second!.Value.End);
            Assert.Equal(25, end);
            Assert.True((first ?? second)!.Value.ServerSends);
        }
    }

    [Theory]
    [InlineData("**\u0018B2")]
    [InlineData("**\u0018A00")]
    [InlineData("*\u0018B00")]
    [InlineData("**B00")]
    [InlineData("plain text with ** stars and B00")]
    public void Detector_IsNotFooledByLookalikes(string output) => Assert.Null(new ZmodemDetector().Find(Encoding.ASCII.GetBytes(output)));

    [Fact]
    public void Detector_FindsAStartAfterAStrayStar() =>
        Assert.NotNull(new ZmodemDetector().Find("***\u0018B00000000000000"u8));

    [Fact]
    public void Detector_OnlyEverReportsARealStart()
    {
        var random = new Random(31);
        for (int round = 0; round < 3000; round++)
        {
            var detector = new ZmodemDetector();
            var seen = new List<byte>();
            foreach (byte[] piece in Fuzz.Pieces(random, Fuzz.Garbage(random, 200, "**\u0018B01"u8), 16))
            {
                if (detector.Find(piece) is not var (end, serverSends))
                {
                    seen.AddRange(piece);
                    continue;
                }

                seen.AddRange(piece[..end]);
                Assert.True(seen.Count >= 6, $"round {round}");
                Assert.Equal([.. "**\u0018B0"u8, (byte)(serverSends ? '0' : '1')], seen.TakeLast(6));
                break;
            }
        }
    }

    // ---- The server sends ----------------------------------------------------------------------

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    public async Task Receive_OneFile_ArrivesWhole(bool wide, int seed)
    {
        byte[] content = Content(20_000);
        var host = new Host();

        var (problem, outbox, _) = await ReceiveAsync(ZmodemWire.SendSession("notes.txt", content, wide), host, seed);

        Assert.Null(problem);
        Assert.Equal(("notes.txt", 20_000L), Assert.Single(host.Offered));
        var (file, complete) = Assert.Single(host.Closed);
        Assert.True(complete);
        Assert.Equal(content, file.ToArray());
        Assert.Equal(20_000, host.LastProgress);
        Assert.Equal([ZmodemWire.ZRINIT, ZmodemWire.ZRPOS, ZmodemWire.ZRINIT, ZmodemWire.ZFIN], outbox.Frames.Select(f => f.Type));
        Assert.Equal(1, host.FinishedCalls);
    }

    [Fact]
    public async Task Receive_FileTheUserTurnsDown_IsSkippedAndTheNextOneTaken()
    {
        byte[] first = Content(3000), second = Content(5000);
        var host = new Host { Accept = (name, _) => name == "unwanted.bin" ? null : new MemoryStream() };
        var stream = new List<byte>(ZmodemWire.Hex(ZmodemWire.ZRQINIT));
        stream.AddRange(ZmodemWire.Binary32(ZmodemWire.ZFILE));
        stream.AddRange(ZmodemWire.Subpacket32(ZmodemWire.FileInfo("unwanted.bin", first.Length), ZmodemWire.EndAck));
        stream.AddRange(ZmodemWire.DataFrames(first, 0));
        stream.AddRange(ZmodemWire.Binary32(ZmodemWire.ZFILE));
        stream.AddRange(ZmodemWire.Subpacket32(ZmodemWire.FileInfo("wanted.bin", second.Length), ZmodemWire.EndAck));
        stream.AddRange(ZmodemWire.DataFrames(second, 0));
        stream.AddRange(ZmodemWire.Binary32(ZmodemWire.ZEOF, (uint)second.Length));
        stream.AddRange(ZmodemWire.Hex(ZmodemWire.ZFIN));

        var (problem, outbox, _) = await ReceiveAsync([.. stream], host);

        Assert.Null(problem);
        Assert.Equal(second, Assert.Single(host.Closed).File.ToArray());
        Assert.Contains(outbox.Frames, f => f.Type == ZmodemWire.ZSKIP);
    }

    [Fact]
    public async Task Receive_DamagedBlock_IsAskedForAgainFromTheLastGoodByte()
    {
        byte[] content = Content(4096);
        var host = new Host();
        byte[] good = ZmodemWire.Subpacket32(content.AsSpan(0, 1024), ZmodemWire.MoreNoAck);
        byte[] damaged = ZmodemWire.Subpacket32(content.AsSpan(1024, 1024), ZmodemWire.EndFrame);
        damaged[10] ^= 0x01;

        var stream = new List<byte>(ZmodemWire.Hex(ZmodemWire.ZRQINIT));
        stream.AddRange(ZmodemWire.Binary32(ZmodemWire.ZFILE));
        stream.AddRange(ZmodemWire.Subpacket32(ZmodemWire.FileInfo("a.bin", content.Length), ZmodemWire.EndAck));
        stream.AddRange(ZmodemWire.Binary32(ZmodemWire.ZDATA, 0));
        stream.AddRange(good);
        stream.AddRange(damaged);

        // What sz does on ZRPOS: everything again from the position asked for.
        stream.AddRange(ZmodemWire.DataFrames(content, 1024));
        stream.AddRange(ZmodemWire.Binary32(ZmodemWire.ZEOF, (uint)content.Length));
        stream.AddRange(ZmodemWire.Hex(ZmodemWire.ZFIN));

        var (problem, outbox, _) = await ReceiveAsync([.. stream], host);

        Assert.Null(problem);
        Assert.Equal(content, Assert.Single(host.Closed).File.ToArray());
        Assert.Contains(outbox.Frames, f => f.Type == ZmodemWire.ZRPOS && f.Position == 1024);
    }

    [Fact]
    public async Task Receive_DataAtAPositionThatDoesNotFollow_IsNotWritten()
    {
        byte[] content = Content(2000);
        var host = new Host();
        var stream = new List<byte>(ZmodemWire.Hex(ZmodemWire.ZRQINIT));
        stream.AddRange(ZmodemWire.Binary32(ZmodemWire.ZFILE));
        stream.AddRange(ZmodemWire.Subpacket32(ZmodemWire.FileInfo("a.bin", content.Length), ZmodemWire.EndAck));
        stream.AddRange(ZmodemWire.DataFrames(content, 1000));
        stream.AddRange(ZmodemWire.DataFrames(content, 0));
        stream.AddRange(ZmodemWire.Binary32(ZmodemWire.ZEOF, (uint)content.Length));
        stream.AddRange(ZmodemWire.Hex(ZmodemWire.ZFIN));

        var (problem, _, _) = await ReceiveAsync([.. stream], host);

        Assert.Null(problem);
        Assert.Equal(content, Assert.Single(host.Closed).File.ToArray());
    }

    [Fact]
    public async Task Receive_MoreThanTheServerAnnounced_IsStoppedAndTheFileDropped()
    {
        byte[] content = Content(10_000);
        var host = new Host();

        var (problem, outbox, _) = await ReceiveAsync(ZmodemWire.SendSession("small.txt", content, announced: 100), host);

        Assert.NotNull(problem);
        var (file, complete) = Assert.Single(host.Closed);
        Assert.False(complete);
        Assert.True(file.ToArray().Length <= 100);
        Assert.EndsWith(Convert.ToHexString(CancelSequence), Convert.ToHexString(outbox.Sent), StringComparison.Ordinal);
    }

    // The connection stays open in these: only seeing the cancel can end them in time.
    [Fact]
    public async Task Receive_TheServerCancellingInTheMiddleOfAFile_EndsWithAProblemAndAnIncompleteFile()
    {
        byte[] session = ZmodemWire.SendSession("a.bin", Content(5000));

        // What lrzsz sends to give up: ten CAN and ten backspaces.
        byte[] cancelled = [.. session[..(session.Length - 3000)], .. Enumerable.Repeat((byte)0x18, 10), .. Enumerable.Repeat((byte)8, 10)];

        var host = new Host();
        var (problem, _, _) = await ReceiveAsync(cancelled, host, endConnection: false);

        Assert.NotNull(problem);
        Assert.False(Assert.Single(host.Closed).Complete);
    }

    private static byte[] UpToTheData() =>
        [.. ZmodemWire.Hex(ZmodemWire.ZRQINIT), .. ZmodemWire.Binary32(ZmodemWire.ZFILE), .. ZmodemWire.Subpacket32(ZmodemWire.FileInfo("a.bin", 5000), ZmodemWire.EndAck)];

    [Fact]
    public async Task Receive_FiveCansBetweenFrames_EndTheTransfer()
    {
        var host = new Host();

        var (problem, _, _) = await ReceiveAsync([.. UpToTheData(), .. Enumerable.Repeat((byte)0x18, 5)], host, endConnection: false);

        Assert.NotNull(problem);
        Assert.False(Assert.Single(host.Closed).Complete);
    }

    [Fact]
    public async Task Receive_FiveCansInsideABlock_EndTheTransfer()
    {
        byte[] content = Content(5000);
        byte[] data = [.. ZmodemWire.Binary32(ZmodemWire.ZDATA, 0), .. ZmodemWire.Subpacket32(content.AsSpan(0, 1000), ZmodemWire.MoreNoAck), .. content.AsSpan(1000, 10)];
        var host = new Host();

        var (problem, _, _) = await ReceiveAsync([.. UpToTheData(), .. data, .. Enumerable.Repeat((byte)0x18, 5)], host, endConnection: false);

        Assert.NotNull(problem);
    }

    [Fact]
    public async Task Receive_ConnectionLostHalfway_EndsWithAProblemAndAnIncompleteFile()
    {
        byte[] session = ZmodemWire.SendSession("a.bin", Content(5000));
        var host = new Host();

        var (problem, _, _) = await ReceiveAsync(session[..(session.Length / 2)], host);

        Assert.NotNull(problem);
        Assert.False(Assert.Single(host.Closed).Complete);
        Assert.Equal(1, host.FinishedCalls);
    }

    [Fact]
    public async Task Receive_CancelledByTheUser_EndsWithoutAProblemAndTellsTheServer()
    {
        byte[] session = ZmodemWire.SendSession("a.bin", Content(5000));
        var host = new Host();

        var (problem, outbox, _) = await ReceiveAsync(session[..(session.Length / 2)], host, endConnection: false, meanwhile: transfer =>
        {
            Assert.True(SpinWait.SpinUntil(() => host.Closed.Count > 0 || host.Offered.Count > 0, Patience));
            transfer.Cancel();
        });

        Assert.Null(problem);
        Assert.False(Assert.Single(host.Closed).Complete);
        Assert.EndsWith(Convert.ToHexString(CancelSequence), Convert.ToHexString(outbox.Sent), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receive_WhatFollowsTheEnd_IsGivenBackToTheTerminal()
    {
        byte[] session = [.. ZmodemWire.SendSession("a.bin", Content(100)), .. "\r\n$ "u8];
        var host = new Host();

        var (problem, _, leftovers) = await ReceiveAsync(session, host, endConnection: false, wholeAtOnce: true);

        Assert.Null(problem);
        Assert.EndsWith("\r\n$ ", Encoding.Latin1.GetString([.. leftovers.SelectMany(piece => piece)]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receive_TheOverAndOutAfterLrzszsLastHeader_IsNotShown()
    {
        byte[] session = [.. ZmodemWire.SendSession("a.bin", Content(100)), .. "\r\n$ "u8];
        var host = new Host();

        var (_, _, leftovers) = await ReceiveAsync(session, host, endConnection: false, wholeAtOnce: true);

        Assert.Equal("\r\n$ ", Encoding.Latin1.GetString([.. leftovers.SelectMany(piece => piece)]));
    }

    [Fact]
    public async Task Receive_NewFileBeforeTheLastEnded_FinishesTheLastOneAsIncomplete()
    {
        byte[] content = Content(3000);
        var host = new Host();
        var stream = new List<byte>(ZmodemWire.Hex(ZmodemWire.ZRQINIT));
        stream.AddRange(ZmodemWire.Binary32(ZmodemWire.ZFILE));
        stream.AddRange(ZmodemWire.Subpacket32(ZmodemWire.FileInfo("first.bin", content.Length), ZmodemWire.EndAck));
        stream.AddRange(ZmodemWire.DataFrames(content[..1000], 0));
        stream.AddRange(ZmodemWire.Binary32(ZmodemWire.ZFILE));
        stream.AddRange(ZmodemWire.Subpacket32(ZmodemWire.FileInfo("second.bin", content.Length), ZmodemWire.EndAck));
        stream.AddRange(ZmodemWire.DataFrames(content, 0));
        stream.AddRange(ZmodemWire.Binary32(ZmodemWire.ZEOF, (uint)content.Length));
        stream.AddRange(ZmodemWire.Hex(ZmodemWire.ZFIN));

        await ReceiveAsync([.. stream], host);

        Assert.Equal(2, host.Offered.Count);
        Assert.Equal(2, host.Closed.Count);
        Assert.False(host.Closed[0].Complete);
    }

    [Fact]
    public async Task Receive_Garbage_EndsCleanlyOnceTheConnectionDoes()
    {
        var random = new Random(8);
        for (int round = 0; round < 100; round++)
        {
            byte[] garbage = [.. "**\u0018B00"u8, .. Fuzz.Garbage(random, 3000, "**\u0018BAC0123456789abcdefhijklm\r\n"u8)];
            var host = new Host();

            var (_, _, _) = await ReceiveAsync(garbage, host, round);

            Assert.Equal(1, host.FinishedCalls);
            Assert.True(host.Closed.Count <= host.Offered.Count, $"round {round}");
        }
    }

    [Fact]
    public async Task Receive_DamagedSessions_EndCleanlyAndNeverWriteMoreThanAnnounced()
    {
        var random = new Random(9);
        byte[] content = Content(3000);
        byte[][] sessions = [ZmodemWire.SendSession("a.bin", content), ZmodemWire.SendSession("b.bin", content, wide: false, piece: 500)];

        for (int round = 0; round < 200; round++)
        {
            byte[] session = sessions[round % 2];
            byte[] damaged = [.. session[..6], .. Fuzz.Mutate(random, session[6..])];
            var host = new Host();

            await ReceiveAsync(damaged, host, round);

            Assert.True(host.FinishedCalls == 1, $"round {round}");
            Assert.True(host.Closed.Count <= host.Offered.Count, $"round {round}");
            foreach (var (file, _) in host.Closed)
            {
                long announced = host.Offered.Max(o => o.Size);
                Assert.True(announced <= 0 || file.Length <= announced, $"round {round}: {file.Length} bytes written of {announced} announced");
            }
        }
    }

    // ---- The server receives -------------------------------------------------------------------

    private static async Task<(ZmodemTransfer Transfer, Outbox Outbox, Task Run)> StartSendingAsync(Host host)
    {
        // rz announces itself with ZRINIT, which is what starts this direction.
        byte[] ready = ZmodemWire.Hex(ZmodemWire.ZRINIT, 0x23u << 24);
        Assert.Equal("**\u0018B01"u8.ToArray(), ready[..6]);
        var outbox = new Outbox();
        var transfer = new ZmodemTransfer(serverSends: false, ready[6..], outbox.SendAsync, host);
        Task run = transfer.RunAsync();
        await Task.Yield();
        return (transfer, outbox, run);
    }

    private static byte[] Received(ZmodemWire.Frame data) => [.. data.Subpackets.SelectMany(s => s.Data)];

    [Fact]
    public async Task Send_OneFile_IsAnnouncedStreamedAndEnded()
    {
        string path = Path.Combine(TestData.Root, "zmodem-" + Guid.NewGuid().ToString("N") + ".bin");
        byte[] content = Content(20_000);
        File.WriteAllBytes(path, content);
        try
        {
            var host = new Host { ToSend = [path] };
            var (transfer, outbox, run) = await StartSendingAsync(host);

            List<ZmodemWire.Frame> frames = await outbox.WaitForAsync(f => f.Any(x => x.Type == ZmodemWire.ZFILE));
            byte[] info = frames.Single(f => f.Type == ZmodemWire.ZFILE).Subpackets[0].Data;
            Assert.StartsWith(Path.GetFileName(path) + "\0" + content.Length + " ", Encoding.ASCII.GetString(info), StringComparison.Ordinal);

            await transfer.ReceiveAsync(ZmodemWire.Hex(ZmodemWire.ZRPOS, 0), CancellationToken.None);
            frames = await outbox.WaitForAsync(f => f.Any(x => x.Type == ZmodemWire.ZEOF));
            Assert.Equal(content, Received(frames.Single(f => f.Type == ZmodemWire.ZDATA)));
            Assert.Equal((uint)content.Length, frames.Single(f => f.Type == ZmodemWire.ZEOF).Position);

            await transfer.ReceiveAsync(ZmodemWire.Hex(ZmodemWire.ZRINIT), CancellationToken.None);
            await outbox.WaitForAsync(f => f.Any(x => x.Type == ZmodemWire.ZFIN));
            await transfer.ReceiveAsync(ZmodemWire.Hex(ZmodemWire.ZFIN), CancellationToken.None);

            await run.WaitAsync(Patience);
            Assert.Null(await host.Done.Task);
            Assert.EndsWith("OO", Encoding.ASCII.GetString(outbox.Sent), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Send_ResumedFromAPositionTheReceiverAlreadyHas_SendsOnlyTheRest()
    {
        string path = Path.Combine(TestData.Root, "zmodem-" + Guid.NewGuid().ToString("N") + ".bin");
        byte[] content = Content(10_000);
        File.WriteAllBytes(path, content);
        try
        {
            var host = new Host { ToSend = [path] };
            var (transfer, outbox, run) = await StartSendingAsync(host);
            await outbox.WaitForAsync(f => f.Any(x => x.Type == ZmodemWire.ZFILE));

            await transfer.ReceiveAsync(ZmodemWire.Hex(ZmodemWire.ZRPOS, 6000), CancellationToken.None);
            List<ZmodemWire.Frame> frames = await outbox.WaitForAsync(f => f.Any(x => x.Type == ZmodemWire.ZEOF));

            ZmodemWire.Frame data = frames.Single(f => f.Type == ZmodemWire.ZDATA);
            Assert.Equal(6000u, data.Position);
            Assert.Equal(content[6000..], Received(data));
            transfer.Cancel();
            await run.WaitAsync(Patience);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Send_NothingChosen_EndsQuietlyAndTellsTheServer()
    {
        var host = new Host { ToSend = [] };
        var (_, outbox, run) = await StartSendingAsync(host);

        await run.WaitAsync(Patience);

        Assert.Null(await host.Done.Task);
        Assert.Equal(CancelSequence, outbox.Sent);
    }

    [Fact]
    public async Task Send_ReceiverThatAnswersGarbage_EndsCleanly()
    {
        string path = Path.Combine(TestData.Root, "zmodem-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, Content(50_000));
        try
        {
            var random = new Random(4);
            for (int round = 0; round < 20; round++)
            {
                var host = new Host { ToSend = [path] };
                var (transfer, outbox, run) = await StartSendingAsync(host);
                await outbox.WaitForAsync(f => f.Any(x => x.Type == ZmodemWire.ZFILE));

                foreach (byte[] piece in Fuzz.Pieces(random, Fuzz.Garbage(random, 2000, "**\u0018BC0123456789abcdef\r\n"u8), 100))
                {
                    if (!await transfer.ReceiveAsync(piece, CancellationToken.None)) break;
                }

                transfer.ConnectionEnded();
                await run.WaitAsync(Patience);
                Assert.Equal(1, host.FinishedCalls);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}

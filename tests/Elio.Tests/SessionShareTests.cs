using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Elio.Connections;

namespace Elio.Tests;

/// <summary>
/// The sealed messages between a shared terminal and its viewers. The password is stretched with far fewer rounds
/// than the real ones here, which changes how long a guess takes and nothing else.
/// </summary>
public sealed class ShareChannelTests : IDisposable
{
    private const string Password = "correct-horse-battery-staple";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly int _iterations = ShareChannel.Iterations;
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(ShareChannel.SaltSize);
    private readonly byte[] _viewerNonce = RandomNumberGenerator.GetBytes(ShareChannel.NonceSize);
    private readonly byte[] _hostNonce = RandomNumberGenerator.GetBytes(ShareChannel.NonceSize);

    public ShareChannelTests() => ShareChannel.Iterations = 1_000;

    public void Dispose() => ShareChannel.Iterations = _iterations;

    private ShareChannel Channel(Stream stream, string password, bool host) =>
        ShareChannel.Create(stream, ShareChannel.Stretch(password, _salt), _viewerNonce, _hostNonce, host);

    // The bytes one message from the viewer is on the wire.
    private async Task<byte[]> SealedByViewerAsync(params (ShareFrame Type, byte[] Payload)[] messages)
    {
        using var wire = new MemoryStream();
        using ShareChannel viewer = Channel(wire, Password, host: false);
        foreach (var (type, payload) in messages) await viewer.SendAsync(type, payload, CancellationToken.None);
        return wire.ToArray();
    }

    private Task<(ShareFrame Type, ReadOnlyMemory<byte> Payload)> HostReadsAsync(byte[] wire, string password = Password)
    {
        ShareChannel host = Channel(new MemoryStream(wire), password, host: true);
        return host.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(Patience);
    }

    [Theory]
    [InlineData("ABCDE-FGHJK-MNPQR-STUVW", "abcde fghjk mnpqr stuvw")]
    [InlineData("ABCDE-FGHJK-MNPQR-STUVW", "ABCDEFGHJKMNPQRSTUVW")]
    [InlineData("ABCDE-FGHJK-MNPQR-STUVW", "  abcde-FGHJK-mnpqr-STUVW\t")]
    public void Stretch_IgnoresHowThePasswordWasGroupedOrCased(string shown, string typed) =>
        Assert.Equal(ShareChannel.Stretch(shown, _salt), ShareChannel.Stretch(typed, _salt));

    [Fact]
    public void Stretch_AnotherPasswordOrSalt_GivesAnotherSecret()
    {
        byte[] secret = ShareChannel.Stretch(Password, _salt);

        Assert.NotEqual(secret, ShareChannel.Stretch(Password + "!", _salt));
        Assert.NotEqual(secret, ShareChannel.Stretch(Password, RandomNumberGenerator.GetBytes(ShareChannel.SaltSize)));
        Assert.Equal(ShareChannel.SecretSize, secret.Length);
    }

    [Fact]
    public async Task Messages_GoBothWaysInOrder()
    {
        var (hostEnd, viewerEnd) = DuplexPipe.Create();
        using ShareChannel host = Channel(hostEnd, Password, host: true);
        using ShareChannel viewer = Channel(viewerEnd, Password.ToUpperInvariant(), host: false);
        byte[] largest = RandomNumberGenerator.GetBytes(ShareChannel.MaxPayload);

        await viewer.SendAsync(ShareFrame.Hello, new[] { ShareChannel.Version }, CancellationToken.None);
        await viewer.SendAsync(ShareFrame.Input, "ls\r"u8.ToArray(), CancellationToken.None);
        await host.SendAsync(ShareFrame.Output, largest, CancellationToken.None);
        await host.SendAsync(ShareFrame.Goodbye, Array.Empty<byte>(), CancellationToken.None);

        var hello = await host.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(Patience);
        var input = await host.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(Patience);
        var output = await viewer.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(Patience);
        var goodbye = await viewer.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(Patience);

        Assert.Equal(ShareFrame.Hello, hello.Type);
        Assert.Equal([ShareChannel.Version], hello.Payload.ToArray());
        Assert.Equal(ShareFrame.Input, input.Type);
        Assert.Equal("ls\r"u8.ToArray(), input.Payload.ToArray());
        Assert.Equal(ShareFrame.Output, output.Type);
        Assert.Equal(largest, output.Payload.ToArray());
        Assert.Equal((ShareFrame.Goodbye, 0), (goodbye.Type, goodbye.Payload.Length));
    }

    [Fact]
    public async Task Message_SealedWithAnotherPassword_DoesNotOpen()
    {
        byte[] wire = await SealedByViewerAsync((ShareFrame.Hello, [ShareChannel.Version]));

        await Assert.ThrowsAnyAsync<CryptographicException>(() => HostReadsAsync(wire, "correct-horse-battery-stapler"));
    }

    [Fact]
    public async Task Message_SentBackToItsOwnSender_DoesNotOpen()
    {
        using var wire = new MemoryStream();
        using (ShareChannel host = Channel(wire, Password, host: true))
            await host.SendAsync(ShareFrame.Output, "secret output"u8.ToArray(), CancellationToken.None);

        // A relay that plays the host's own output back to it, hoping it is taken for typing.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => HostReadsAsync(wire.ToArray()));
    }

    [Fact]
    public async Task Messages_ReplayedOrReordered_DoNotOpen()
    {
        byte[] first = await SealedByViewerAsync((ShareFrame.Input, "a"u8.ToArray()));
        byte[] both = await SealedByViewerAsync((ShareFrame.Input, "a"u8.ToArray()), (ShareFrame.Input, "b"u8.ToArray()));
        byte[] second = both[first.Length..];

        // The second message delivered first, and the first delivered twice.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => HostReadsAsync(second));
        using ShareChannel host = Channel(new MemoryStream([.. first, .. first]), Password, host: true);
        await host.ReceiveAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => host.ReceiveAsync(CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Message_WithAnyByteChanged_DoesNotOpen()
    {
        byte[] wire = await SealedByViewerAsync((ShareFrame.Input, "rm -rf /tmp/x\r"u8.ToArray()));

        for (int i = 4; i < wire.Length; i++)
        {
            byte[] changed = (byte[])wire.Clone();
            changed[i] ^= 0x20;
            await Assert.ThrowsAnyAsync<CryptographicException>(() => HostReadsAsync(changed));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(ShareChannel.MaxPayload + 2)]
    [InlineData(int.MaxValue)]
    public async Task Message_WithAnImpossibleLength_IsRefusedBeforeItsBodyIsAwaited(int length)
    {
        // Only the length is sent and the connection stays open: waiting for the body would hang.
        var (sender, receiver) = DuplexPipe.Create();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        await sender.WriteAsync(header);
        using ShareChannel host = Channel(receiver, Password, host: true);

        long allocated = 0;
        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                await host.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(Patience);
            }
            finally
            {
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
        });
        Assert.True(allocated < 64 * 1024, $"{allocated} bytes were set aside for a message that was refused.");
    }

    [Fact]
    public async Task Message_CutShort_EndsTheConnection()
    {
        byte[] wire = await SealedByViewerAsync((ShareFrame.Input, "abc"u8.ToArray()));

        await Assert.ThrowsAsync<EndOfStreamException>(() => HostReadsAsync(wire[..^1]));
    }

    [Fact]
    public async Task Receive_GarbageFromAStranger_OnlyEverFailsAsExpected()
    {
        var random = new Random(99);
        for (int round = 0; round < 500; round++)
        {
            byte[] garbage = round % 2 == 0 ? Fuzz.Garbage(random, 300) : Fuzz.Mutate(random, await SealedByViewerAsync((ShareFrame.Hello, [2])));
            if (garbage.Length >= 4 && random.Next(3) == 0) BinaryPrimitives.WriteInt32BigEndian(garbage, random.Next(1, 200));

            Exception? error = await Record.ExceptionAsync(() => HostReadsAsync(garbage));

            // A change that left the bytes as they were (a byte set to its own value) still opens, which is fine too.
            Assert.True(error is null or CryptographicException or EndOfStreamException, $"round {round}: {error}");
        }
    }

    [Fact]
    public void Greetings_OfAnotherKindOrVersion_AreNotTakenForOnes()
    {
        byte[] viewer = ShareChannel.Greeting(out byte[] nonce);
        byte[] host = ShareChannel.Greeting(_salt, out byte[] hostNonce);

        Assert.Equal(nonce, ShareChannel.ReadGreeting(viewer));
        Assert.True(ShareChannel.ReadHostGreeting(host, out byte[] readNonce, out byte[] readSalt));
        Assert.Equal(hostNonce, readNonce);
        Assert.Equal(_salt, readSalt);

        byte[] older = [.. "ELIOSHR1"u8, .. nonce];
        Assert.Null(ShareChannel.ReadGreeting(older));
        Assert.Null(ShareChannel.ReadGreeting(viewer[..^1]));
        Assert.Null(ShareChannel.ReadGreeting(host));
        Assert.False(ShareChannel.ReadHostGreeting(viewer, out _, out _));
        Assert.False(ShareChannel.ReadHostGreeting([.. "SSH-2.0-"u8, .. host[8..]], out _, out _));
    }

    [Theory]
    [InlineData(80, 24, 80, 24)]
    [InlineData(-5, 70_000, 0, 65535)]
    public void Size_IsClampedToWhatFitsAndReadsBack(int columns, int rows, int expectedColumns, int expectedRows) =>
        Assert.Equal((expectedColumns, expectedRows), ShareChannel.ReadSize(ShareChannel.Size(columns, rows)));
}

/// <summary>
/// Sharing a terminal and watching it, over this computer's loopback address. The terminal is a scripted
/// connection; the viewer is the real viewing end, or one written here to break the rules.
/// </summary>
public sealed class SessionShareTests : IAsyncLifetime
{
    private const string Password = "correct-horse-battery-staple";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly int _iterations = ShareChannel.Iterations;
    private readonly ScriptedTransport _inner = new();
    private readonly SharedTransport _shared;
    private readonly CancellationTokenSource _stop = new();
    private readonly MemoryStream _shown = new();
    private readonly Task _terminal;

    public SessionShareTests()
    {
        ShareChannel.Iterations = 1_000;
        _shared = new SharedTransport(_inner, 80, 24);

        // The terminal's own read loop, which is what lets viewers in and passes output on to them.
        _terminal = Task.Run(async () =>
        {
            byte[] buffer = new byte[4096];
            while (true)
            {
                int count = await _shared.ReadAsync(buffer, _stop.Token);
                if (count == 0) return;
                lock (_shown) _shown.Write(buffer, 0, count);
            }
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _shared.Share.Stop();
        await _stop.CancelAsync();
        await _shared.DisposeAsync();
        await Task.WhenAny(_terminal, Task.Delay(Patience));
        ShareChannel.Iterations = _iterations;
    }

    private int Start(bool allowInput = true)
    {
        _shared.Share.Start(IPAddress.Loopback, 0, allowInput, Password);
        return _shared.Share.Port;
    }

    private Task<SharedSessionTransport> WatchAsync(string password = Password) =>
        SharedSessionTransport.ConnectAsync("127.0.0.1", _shared.Share.Port, password, CancellationToken.None).WaitAsync(Patience);

    private static async Task<string> ReadUntilAsync(SharedSessionTransport viewer, string expected)
    {
        var seen = new StringBuilder();
        byte[] buffer = new byte[4096];
        using var limit = new CancellationTokenSource(Patience);
        while (!seen.ToString().Contains(expected, StringComparison.Ordinal))
        {
            int count = await viewer.ReadAsync(buffer, limit.Token);
            if (count == 0) Assert.Fail($"The share ended before \"{expected}\" arrived. Seen: {seen}");
            seen.Append(Encoding.UTF8.GetString(buffer, 0, count));
        }

        return seen.ToString();
    }

    private async Task UntilShownAsync(string text)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            lock (_shown)
            {
                if (Encoding.UTF8.GetString(_shown.ToArray()).Contains(text, StringComparison.Ordinal)) return;
            }

            if (clock.Elapsed > Patience) Assert.Fail($"The terminal never showed \"{text}\".");
            await Task.Delay(10);
        }
    }

    [Fact]
    public void Start_WithAPasswordShortOnceSpacesAndDashesAreLeftOut_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => _shared.Share.Start(IPAddress.Loopback, 0, true, "abc-def ghi-jk"));
        Assert.False(_shared.Share.IsRunning);
    }

    [Fact]
    public void Start_WithoutAPassword_MakesALongOne()
    {
        _shared.Share.Start(IPAddress.Loopback, 0, allowInput: false);

        string[] groups = _shared.Share.Password.Split('-');
        Assert.Equal(4, groups.Length);
        Assert.All(groups, group => Assert.Matches("^[A-HJ-NP-Z2-9]{5}$", group));
        Assert.True(_shared.Share.Port > 0);
    }

    [Fact]
    public async Task Viewer_WithThePassword_SeesTheScreenThenTheOutputAndMayType()
    {
        Start();
        _inner.Say("before anyone watched\r\n");
        await UntilShownAsync("before anyone watched");

        await using SharedSessionTransport viewer = await WatchAsync("CORRECT horse BATTERY staple");
        Assert.True(viewer.CanType);
        Assert.Equal((80, 24), (viewer.HostColumns, viewer.HostRows));

        // Without a way to draw the screen, a new viewer is given a reset and the recent output.
        Assert.StartsWith("\u001bcbefore anyone watched", await ReadUntilAsync(viewer, "before anyone watched"), StringComparison.Ordinal);

        _inner.Say("after\r\n");
        await ReadUntilAsync(viewer, "after");

        await viewer.WriteAsync("ls\r"u8.ToArray(), CancellationToken.None);
        Assert.Equal("ls\r"u8.ToArray(), await _inner.HeardAsync(Patience));
        Assert.Equal(1, _shared.Share.ViewerCount);
    }

    [Fact]
    public async Task Viewer_IsToldOfResizesAndOfTheEnd()
    {
        Start();
        await using SharedSessionTransport viewer = await WatchAsync();
        (int Columns, int Rows) told = default;
        viewer.HostResized += (columns, rows) => told = (columns, rows);
        _inner.Say("one\r\n");
        await ReadUntilAsync(viewer, "one");

        await _shared.ResizeAsync(120, 40, CancellationToken.None);
        _inner.Say("two\r\n");
        await ReadUntilAsync(viewer, "two");
        Assert.Equal((120, 40), (viewer.HostColumns, viewer.HostRows));
        Assert.Equal((120, 40), told);
        Assert.Contains((120, 40), _inner.Sizes);

        _shared.Share.Stop();
        byte[] buffer = new byte[64];
        Assert.Equal(0, await viewer.ReadAsync(buffer, CancellationToken.None).AsTask().WaitAsync(Patience));
        Assert.Equal(0, _shared.Share.ViewerCount);
    }

    [Fact]
    public async Task Viewer_WithTheWrongPassword_IsToldSoAndSeesNothing()
    {
        Start();
        _inner.Say("private\r\n");
        await UntilShownAsync("private");

        await Assert.ThrowsAsync<WrongSharePasswordException>(() => WatchAsync("correct-horse-battery-staples"));
        Assert.Equal(0, _shared.Share.ViewerCount);
    }

    [Fact]
    public async Task Address_AfterFiveWrongPasswords_IsTurnedAwayEvenWithTheRightOne()
    {
        Start();
        for (int i = 0; i < 5; i++) await Assert.ThrowsAsync<WrongSharePasswordException>(() => WatchAsync("wrong password number " + i));

        IOException refused = await Assert.ThrowsAsync<IOException>(() => WatchAsync());
        Assert.IsNotType<WrongSharePasswordException>(refused);
    }

    [Fact]
    public async Task ViewOnlyShare_IgnoresTypingEvenFromAViewerThatSendsItAnyway()
    {
        int port = Start(allowInput: false);
        await using SharedSessionTransport polite = await WatchAsync();
        Assert.False(polite.CanType);
        await polite.WriteAsync("polite\r"u8.ToArray(), CancellationToken.None);

        // A viewer of its own making, which ignores being told it may not type.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(ShareChannel.Greeting(out byte[] viewerNonce));
        byte[] greeting = new byte[ShareChannel.HostGreetingSize];
        await stream.ReadExactlyAsync(greeting).AsTask().WaitAsync(Patience);
        Assert.True(ShareChannel.ReadHostGreeting(greeting, out byte[] hostNonce, out byte[] salt));
        using ShareChannel channel = ShareChannel.Create(stream, ShareChannel.Stretch(Password, salt), viewerNonce, hostNonce, host: false);
        await channel.SendAsync(ShareFrame.Hello, new[] { ShareChannel.Version }, CancellationToken.None);
        var (type, payload) = await channel.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(Patience);
        Assert.Equal((ShareFrame.Welcome, (byte)0), (type, payload.Span[0]));

        await channel.SendAsync(ShareFrame.Input, "rude\r"u8.ToArray(), CancellationToken.None);
        await channel.SendAsync(ShareFrame.Goodbye, Array.Empty<byte>(), CancellationToken.None);

        // The share hangs up once it has read the goodbye, and so after the typing before it.
        Exception? ended = await Record.ExceptionAsync(async () =>
        {
            while (true) await channel.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(Patience);
        });
        Assert.NotNull(ended);
        Assert.IsNotType<TimeoutException>(ended);
        Assert.Empty(_inner.HeardSoFar());
    }

    [Fact]
    public async Task Stranger_WhoCannotSealAHello_GetsNothingOfTheTerminal()
    {
        int port = Start();
        _inner.Say("private output\r\n");
        await UntilShownAsync("private output");

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(ShareChannel.Greeting(out _));
        byte[] greeting = new byte[ShareChannel.HostGreetingSize];
        await stream.ReadExactlyAsync(greeting).AsTask().WaitAsync(Patience);

        // A plausible length and then noise where the sealed hello should be.
        byte[] noise = RandomNumberGenerator.GetBytes(4 + 2 + 16);
        BinaryPrimitives.WriteInt32BigEndian(noise, 2);
        await stream.WriteAsync(noise);

        Assert.Equal(0, await ReadUntilHungUpAsync(stream));
    }

    [Fact]
    public async Task Stranger_SpeakingSomethingElse_IsHungUpOn()
    {
        int port = Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync("GET / HTTP/1.1\r\nHost: share.example.invalid\r\nAccept: */*\r\n\r\n"u8.ToArray());

        Assert.Equal(0, await ReadUntilHungUpAsync(stream));
    }

    // How many bytes arrive before the other end hangs up, quietly or with a reset.
    private static async Task<long> ReadUntilHungUpAsync(Stream stream)
    {
        long total = 0;
        byte[] buffer = new byte[4096];
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer).AsTask().WaitAsync(Patience)) > 0) total += count;
        }
        catch (IOException)
        {
            // Reset: hung up all the same.
        }

        return total;
    }

    [Theory]
    [InlineData("example.com", 4000, "elio-share://example.com:4000")]
    [InlineData("::1", 4000, "elio-share://[::1]:4000")]
    [InlineData("[fe80::1]", 22, "elio-share://[fe80::1]:22")]
    public void Link_IsFormattedAndReadBack(string host, int port, string link)
    {
        Assert.Equal(link, SessionShare.FormatLink(host, port));
        Assert.True(SessionShare.TryParseLink(link, out string readHost, out int readPort));
        Assert.Equal((host.Trim('[', ']'), port), (readHost, readPort));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com:4000")]
    [InlineData("elio-share://example.com")]
    [InlineData("elio-share://example.com:0")]
    [InlineData("elio-share://:4000")]
    [InlineData("not a link")]
    public void Link_ThatIsNotADirectShare_IsRefused(string? link) =>
        Assert.False(SessionShare.TryParseLink(link, out _, out _));
}

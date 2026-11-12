using System.Net;
using System.Net.Sockets;
using System.Text;
using Elio.Connections;
using Elio.Core;

namespace Elio.Tests;

/// <summary>
/// Connecting through a proxy, against a pretend proxy that says whatever each case needs. The handshakes are
/// SSH.NET's and work on a socket, so the pretend proxy listens on this computer's loopback address, on a port
/// the system picks. Nothing leaves the machine: the server's name is one that cannot exist, which is also how
/// these show that the name goes to the proxy as it is and is never looked up here.
/// </summary>
public sealed class ProxyConnectTests
{
    private const string Target = "ssh.internal.invalid";
    private const int TargetPort = 2222;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private static readonly byte[] SocksGranted = [5, 0, 0, 1, 0, 0, 0, 0, 0, 0];

    /// <summary>What a connection attempt ended with, and what the pretend proxy made of it.</summary>
    /// <param name="After">How long it took until whoever asked for the connection had an answer.</param>
    private sealed record Attempt<T>(Exception? Error, T Seen, TimeSpan After)
    {
        public ProxyFailedException Refused => Assert.IsType<ProxyFailedException>(Error);
    }

    private static async Task<Attempt<T>> ConnectThroughAsync<T>(ProxyKind kind, string? user, string? password, Func<NetworkStream, Task<T>> proxy, TimeSpan? timeout = null, CancellationToken cancellation = default)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task<T> serving = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            await using NetworkStream stream = client.GetStream();
            return await proxy(stream);
        });

        var endpoint = new SshEndpoint(Target, TargetPort, "root", "server-password", Proxy: new SshProxy(kind, "127.0.0.1", port, user, password));
        Exception? error = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // No host key is ever trusted here, and no case gets as far as being shown one.
            using SshChain chain = await SshChain.OpenAsync([endpoint], (_, _) => false, timeout ?? Patience, cancellation);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        TimeSpan after = clock.Elapsed;
        return new Attempt<T>(error, await serving.WaitAsync(Patience), after);
    }

    private static async Task<byte[]> ReadAsync(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(Patience);
        return buffer;
    }

    /// <summary>Reads up to and including what ends a line or a block of headers.</summary>
    private static async Task<string> ReadUntilAsync(NetworkStream stream, string ending)
    {
        var text = new StringBuilder();
        byte[] one = new byte[1];
        while (!text.ToString().EndsWith(ending, StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one).AsTask().WaitAsync(Patience) == 0) break;
            text.Append((char)one[0]);
        }

        return text.ToString();
    }

    /// <summary>Waits for the other side to hang up, saying nothing, as a proxy that has stopped answering would.</summary>
    private static async Task<bool> StaySilentAsync(NetworkStream stream)
    {
        try
        {
            while (await stream.ReadAsync(new byte[64]).AsTask().WaitAsync(Patience) > 0) { }
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            // Hung up on, which is what was waited for.
        }

        return true;
    }

    /// <summary>The first thing an SSH client says once it believes it is talking to the server.</summary>
    private static Task<string> ReadSshGreetingAsync(NetworkStream stream) => ReadUntilAsync(stream, "\n");

    private static void AssertGotThrough(Exception? error, string greeting)
    {
        // The pretend proxy has no server behind it and hangs up, so the attempt still fails, but as an SSH
        // connection that broke, not as a proxy that refused. And SSH was being spoken through the tunnel.
        Assert.NotNull(error);
        Assert.IsNotType<ProxyFailedException>(error);
        Assert.StartsWith("SSH-2.0-", greeting, StringComparison.Ordinal);
    }

    // SOCKS5.

    /// <summary>The name and port a SOCKS5 connect request asks for.</summary>
    private static async Task<(byte AddressType, string Host, int Port)> ReadSocksRequestAsync(NetworkStream stream)
    {
        byte[] head = await ReadAsync(stream, 4);
        Assert.Equal([5, 1, 0], head[..3]);

        string host = head[3] == 3 ? Encoding.ASCII.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1))[0])) : string.Empty;
        byte[] port = await ReadAsync(stream, 2);
        return (head[3], host, (port[0] << 8) | port[1]);
    }

    [Fact]
    public async Task Socks5_ProxyThatAsksForNoLogin_IsGivenTheServersNameAndCarriesSsh()
    {
        var attempt = await ConnectThroughAsync(ProxyKind.Socks5, null, null, async stream =>
        {
            byte[] hello = await ReadAsync(stream, 4);
            await stream.WriteAsync(new byte[] { 5, 0 });
            var request = await ReadSocksRequestAsync(stream);
            await stream.WriteAsync(SocksGranted);
            return (Hello: hello, Request: request, Greeting: await ReadSshGreetingAsync(stream));
        });

        // Both ways of logging in are offered: none, and user name with password.
        Assert.Equal([5, 2, 0, 2], attempt.Seen.Hello);

        // Address type 3 is a name: it was sent as typed, for the proxy to look up.
        Assert.Equal(((byte)3, Target, TargetPort), attempt.Seen.Request);
        AssertGotThrough(attempt.Error, attempt.Seen.Greeting);
    }

    [Fact]
    public async Task Socks5_ProxyThatAsksForALogin_GetsTheUserNameAndPassword()
    {
        var attempt = await ConnectThroughAsync(ProxyKind.Socks5, "me", "proxy-password", async stream =>
        {
            await ReadAsync(stream, 4);
            await stream.WriteAsync(new byte[] { 5, 2 });

            byte[] head = await ReadAsync(stream, 2);
            string user = Encoding.ASCII.GetString(await ReadAsync(stream, head[1]));
            string password = Encoding.ASCII.GetString(await ReadAsync(stream, (await ReadAsync(stream, 1))[0]));
            await stream.WriteAsync(new byte[] { 1, 0 });

            var request = await ReadSocksRequestAsync(stream);
            await stream.WriteAsync(SocksGranted);
            return (Version: head[0], User: user, Password: password, Request: request, Greeting: await ReadSshGreetingAsync(stream));
        });

        Assert.Equal(((byte)1, "me", "proxy-password"), (attempt.Seen.Version, attempt.Seen.User, attempt.Seen.Password));
        Assert.Equal(Target, attempt.Seen.Request.Host);
        AssertGotThrough(attempt.Error, attempt.Seen.Greeting);
    }

    [Fact]
    public async Task Socks5_ProxyThatAsksForALoginWhenNoneIsSet_Fails_SayingTheLoginWasTurnedDown()
    {
        var attempt = await ConnectThroughAsync(ProxyKind.Socks5, null, null, async stream =>
        {
            await ReadAsync(stream, 4);
            await stream.WriteAsync(new byte[] { 5, 2 });

            // An empty user name and an empty password are what there is to send.
            byte[] login = await ReadAsync(stream, 3);
            await stream.WriteAsync(new byte[] { 1, 1 });
            return login;
        });

        Assert.Equal([1, 0, 0], attempt.Seen);
        Assert.Contains("authentication failed", attempt.Refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"127.0.0.1:", attempt.Refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Socks5_ProxyThatTakesNoneOfTheOfferedLogins_Fails()
    {
        var attempt = await ConnectThroughAsync(ProxyKind.Socks5, null, null, async stream =>
        {
            await ReadAsync(stream, 4);
            await stream.WriteAsync(new byte[] { 5, 0xFF });
            return true;
        });

        Assert.Contains("No acceptable authentication", attempt.Refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Socks5_ProxyThatRejectsThePassword_Fails()
    {
        var attempt = await ConnectThroughAsync(ProxyKind.Socks5, "me", "wrong", async stream =>
        {
            await ReadAsync(stream, 4);
            await stream.WriteAsync(new byte[] { 5, 2 });
            await ReadAsync(stream, 2 + "me".Length + 1 + "wrong".Length);
            await stream.WriteAsync(new byte[] { 1, 1 });
            return true;
        });

        Assert.Contains("authentication failed", attempt.Refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(5, "refused")]
    [InlineData(4, "Host unreachable")]
    [InlineData(2, "not allowed")]
    public async Task Socks5_ProxyThatCannotReachTheServer_Fails_WithItsReason(byte reply, string reason)
    {
        var attempt = await ConnectThroughAsync(ProxyKind.Socks5, null, null, async stream =>
        {
            await ReadAsync(stream, 4);
            await stream.WriteAsync(new byte[] { 5, 0 });
            await ReadSocksRequestAsync(stream);
            await stream.WriteAsync(new byte[] { 5, reply, 0, 1, 0, 0, 0, 0, 0, 0 });
            return true;
        });

        Assert.Contains(reason, attempt.Refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Socks5_SomethingThatIsNotASocksProxy_Fails()
    {
        var attempt = await ConnectThroughAsync(ProxyKind.Socks5, null, null, async stream =>
        {
            await ReadAsync(stream, 4);
            await stream.WriteAsync("HTTP/1.1 400 Bad Request\r\n\r\n"u8.ToArray());
            return true;
        });

        Assert.Contains("SOCKS", attempt.Refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Socks5_ProxyThatNeverAnswers_GivesUpAfterTheTimeout()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var attempt = await ConnectThroughAsync(ProxyKind.Socks5, null, null, StaySilentAsync, timeout: TimeSpan.FromMilliseconds(400));

        Assert.Contains("in time", attempt.Refused.Message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public async Task Socks5_CancelledWhileTheProxyIsSilent_StopsWaiting()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        // Whoever asked stops waiting at once. The attempt itself, which SSH.NET makes on a thread of its own,
        // is left to run into its timeout, so that is kept short here for the pretend proxy to be let go.
        var attempt = await ConnectThroughAsync(ProxyKind.Socks5, null, null, StaySilentAsync, timeout: TimeSpan.FromSeconds(4), cancellation: cancel.Token);

        Assert.IsAssignableFrom<OperationCanceledException>(attempt.Error);
        Assert.True(attempt.After < TimeSpan.FromSeconds(3), $"Took {attempt.After}, which is the timeout and not the cancelling.");
    }

    // HTTP CONNECT.

    private static Task<string> ReadHttpRequestAsync(NetworkStream stream) => ReadUntilAsync(stream, "\r\n\r\n");

    private static ValueTask ReplyAsync(NetworkStream stream, string text) => stream.WriteAsync(Encoding.ASCII.GetBytes(text));

    [Fact]
    public async Task Http_ProxyThatAllowsIt_IsAskedForTheServerByName_WithoutALogin_AndCarriesSsh()
    {
        var attempt = await ConnectThroughAsync(ProxyKind.Http, null, null, async stream =>
        {
            string request = await ReadHttpRequestAsync(stream);

            // Headers after the status line have to be read through before SSH starts.
            await ReplyAsync(stream, "HTTP/1.1 200 Connection established\r\nProxy-Agent: pretend\r\nVia: 1.1 pretend\r\n\r\n");
            return (Request: request, Greeting: await ReadSshGreetingAsync(stream));
        });

        Assert.StartsWith($"CONNECT {Target}:{TargetPort} HTTP/1.", attempt.Seen.Request, StringComparison.Ordinal);
        Assert.DoesNotContain("Proxy-Authorization", attempt.Seen.Request, StringComparison.OrdinalIgnoreCase);
        AssertGotThrough(attempt.Error, attempt.Seen.Greeting);
    }

    [Fact]
    public async Task Http_ProxyWithALogin_GetsItAsBasicAuthorization()
    {
        var attempt = await ConnectThroughAsync(ProxyKind.Http, "me", "proxy-password", async stream =>
        {
            string request = await ReadHttpRequestAsync(stream);
            await ReplyAsync(stream, "HTTP/1.0 200 OK\r\n\r\n");
            return (Request: request, Greeting: await ReadSshGreetingAsync(stream));
        });

        string expected = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("me:proxy-password"));
        Assert.Contains(expected + "\r\n", attempt.Seen.Request, StringComparison.Ordinal);
        AssertGotThrough(attempt.Error, attempt.Seen.Greeting);
    }

    [Theory]
    [InlineData("407 Proxy Authentication Required", "Proxy-Authenticate: Basic realm=\"pretend\"\r\n")]
    [InlineData("403 Forbidden", "")]
    [InlineData("502 Bad Gateway", "Content-Length: 0\r\n")]
    public async Task Http_ProxyThatSaysNo_Fails_WithItsStatus(string status, string headers)
    {
        var attempt = await ConnectThroughAsync(ProxyKind.Http, null, null, async stream =>
        {
            await ReadHttpRequestAsync(stream);
            await ReplyAsync(stream, $"HTTP/1.1 {status}\r\n{headers}\r\n");
            return true;
        });

        string[] parts = status.Split(' ', 2);
        Assert.Contains(parts[0], attempt.Refused.Message, StringComparison.Ordinal);
        Assert.Contains(parts[1], attempt.Refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_ProxyThatRejectsTheLogin_Fails()
    {
        var attempt = await ConnectThroughAsync(ProxyKind.Http, "me", "wrong", async stream =>
        {
            await ReadHttpRequestAsync(stream);
            await ReplyAsync(stream, "HTTP/1.1 407 Proxy Authentication Required\r\n\r\n");
            return true;
        });

        Assert.Contains("407", attempt.Refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_SomethingThatIsNotAnHttpProxy_Fails()
    {
        var attempt = await ConnectThroughAsync(ProxyKind.Http, null, null, async stream =>
        {
            await ReadHttpRequestAsync(stream);
            await stream.WriteAsync(new byte[] { 5, 0, 0x7F, 0x10, 0x0D, 0x0A, 0xFF, 0x00 });
            return true;
        });

        Assert.Contains("status line", attempt.Refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Http_ProxyThatNeverAnswers_GivesUpAfterTheTimeout()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var attempt = await ConnectThroughAsync(ProxyKind.Http, null, null, StaySilentAsync, timeout: TimeSpan.FromMilliseconds(400));

        Assert.Contains("in time", attempt.Refused.Message, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Took {clock.Elapsed}.");
    }

    [Fact]
    public async Task Http_ProxyThatStopsHalfwayThroughItsHeaders_GivesUpAfterTheTimeout()
    {
        var attempt = await ConnectThroughAsync(
            ProxyKind.Http,
            null,
            null,
            async stream =>
            {
                await ReadHttpRequestAsync(stream);
                await ReplyAsync(stream, "HTTP/1.1 200 Connection established\r\nProxy-Agent: pre");
                return await StaySilentAsync(stream);
            },
            timeout: TimeSpan.FromMilliseconds(400));

        Assert.Contains("in time", attempt.Refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoProxyListeningThere_FailsAsAnyRefusedConnectionDoes()
    {
        // A port that was free a moment ago, with nothing on it now.
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        var endpoint = new SshEndpoint(Target, TargetPort, "root", Proxy: new SshProxy(ProxyKind.Socks5, "127.0.0.1", port));

        CommandResult result = await SshCommandRunner.RunAsync([endpoint], "true", (_, _) => false, CancellationToken.None);

        Assert.NotNull(result.Failure);
        Assert.False(result.Succeeded);
    }
}

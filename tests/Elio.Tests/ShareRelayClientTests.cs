using System.Net;
using System.Net.WebSockets;
using System.Text;
using Elio.Connections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Elio.Tests;

/// <summary>
/// Sharing a terminal through a sync server: the app's two ends of the relay against the real server on the loopback
/// address, and against a pretend server that misbehaves.
/// </summary>
public sealed class ShareRelayClientTests : IAsyncLifetime
{
    private const string Password = "correct-horse-battery-staple";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly int _iterations = ShareChannel.Iterations;

    public Task InitializeAsync()
    {
        ShareChannel.Iterations = 1_000;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        ShareChannel.Iterations = _iterations;
        return Task.CompletedTask;
    }

    // ---- Links ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("elio-share://sync.example.com/k7qm2xpd9rk7qm2xpd9r", "wss://sync.example.com/v1/share/join/k7qm2xpd9rk7qm2xpd9r")]
    [InlineData("elio-share://127.0.0.1:8080/k7qm2xpd9rk7qm2xpd9r?plain=1", "ws://127.0.0.1:8080/v1/share/join/k7qm2xpd9rk7qm2xpd9r")]
    [InlineData("elio-share://example.com/sync/K7QM2XPD9RK7QM2XPD9R", "wss://example.com/sync/v1/share/join/k7qm2xpd9rk7qm2xpd9r")]
    [InlineData("  ELIO-SHARE://example.com:9443/k7qm2xpd9rk7qm2xpd9r/  ", "wss://example.com:9443/v1/share/join/k7qm2xpd9rk7qm2xpd9r")]
    public void Link_ToARoom_LeadsToItsJoinAddress(string link, string expected)
    {
        Assert.True(ShareRelayLink.TryParse(link, out Uri? join));
        Assert.Equal(expected, join.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("elio-share://192.168.1.5:4000")]
    [InlineData("elio-share://example.com/short")]
    [InlineData("elio-share://example.com/k7qm2xpd9rk7qm2xpd9r0")]
    [InlineData("elio-share://example.com/k7qm2xpd9rk7qm2xpd9l")]
    [InlineData("elio-share://example.com/k7qm2xpd9rk7qm2xpd9rk7qm2xpd9rk7")]
    [InlineData("elio-share://user:pass@example.com/k7qm2xpd9rk7qm2xpd9r")]
    [InlineData("https://example.com/k7qm2xpd9rk7qm2xpd9r")]
    [InlineData("elio-share://example.com/../../v1/vault")]
    public void Link_ThatIsNotToARoom_IsRefused(string? link) => Assert.False(ShareRelayLink.TryParse(link, out _));

    [Theory]
    [InlineData("https://sync.example.com/", "elio-share://sync.example.com/k7qm2xpd9rk7qm2xpd9r")]
    [InlineData("http://127.0.0.1:5000/", "elio-share://127.0.0.1:5000/k7qm2xpd9rk7qm2xpd9r?plain=1")]
    [InlineData("https://example.com/sync/", "elio-share://example.com/sync/k7qm2xpd9rk7qm2xpd9r")]
    public void Link_MadeForAServer_ReadsBackToTheSameRoom(string server, string expected)
    {
        string link = ShareRelayLink.Format(new Uri(server), "k7qm2xpd9rk7qm2xpd9r");

        Assert.Equal(expected, link);
        Assert.True(ShareRelayLink.TryParse(link, out Uri? join));
        Assert.EndsWith("/v1/share/join/k7qm2xpd9rk7qm2xpd9r", join.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_OfAnyText_NeverThrows()
    {
        var random = new Random(3);
        byte[] valid = "elio-share://sync.example.com:8443/path/k7qm2xpd9rk7qm2xpd9r?plain=1"u8.ToArray();
        for (int round = 0; round < 2000; round++)
        {
            string text = Encoding.UTF8.GetString(round % 2 == 0 ? Fuzz.Mutate(random, valid) : Fuzz.Garbage(random, 80));
            if (ShareRelayLink.TryParse(text, out Uri? join)) Assert.True(join.Scheme is "ws" or "wss", text);
            SessionShare.TryParseLink(text, out _, out _);
        }
    }

    [Theory]
    [InlineData("ftp://sync.example.com/")]
    [InlineData("file:///etc/passwd")]
    public void Endpoint_OfAServerThatIsNotHttp_IsRefused(string server) =>
        Assert.Throws<ArgumentException>(() => ShareRelayWire.Endpoint(new Uri(server), "v1/share/host"));

    // ---- Through the real server -----------------------------------------------------------------

    [Fact]
    public async Task Terminal_SharedThroughTheServer_IsWatchedAndTypedInto()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        var inner = new ScriptedTransport();
        await using var shared = new SharedTransport(inner, 100, 30);
        shared.Share.Start(IPAddress.Loopback, 0, allowInput: true, Password);
        using var stop = new CancellationTokenSource();
        Task terminal = Task.Run(async () =>
        {
            byte[] buffer = new byte[4096];
            while (await shared.ReadAsync(buffer, stop.Token) > 0)
            {
            }
        });

        await using ShareRelayHost relay = await ShareRelayHost.StartAsync(server.Address, null, shared.Share.Port, CancellationToken.None).WaitAsync(Patience);
        Assert.StartsWith($"elio-share://127.0.0.1:{server.Address.Port}/", relay.Link, StringComparison.Ordinal);
        Assert.EndsWith("?plain=1", relay.Link, StringComparison.Ordinal);

        await using ShareRelayViewer door = await ShareRelayViewer.OpenAsync(relay.Link, CancellationToken.None).WaitAsync(Patience);
        await using SharedSessionTransport viewer = await SharedSessionTransport.ConnectAsync("127.0.0.1", door.LocalPort, Password, CancellationToken.None).WaitAsync(Patience);
        Assert.Equal((100, 30), (viewer.HostColumns, viewer.HostRows));

        inner.Say("through the relay\r\n");
        var seen = new StringBuilder();
        byte[] chunk = new byte[4096];
        while (!seen.ToString().Contains("through the relay", StringComparison.Ordinal))
        {
            int count = await viewer.ReadAsync(chunk, CancellationToken.None).AsTask().WaitAsync(Patience);
            Assert.NotEqual(0, count);
            seen.Append(Encoding.UTF8.GetString(chunk, 0, count));
        }

        await viewer.WriteAsync("whoami\r"u8.ToArray(), CancellationToken.None);
        Assert.Equal("whoami\r"u8.ToArray(), await inner.HeardAsync(Patience));

        shared.Share.Stop();
        await stop.CancelAsync();
        await Task.WhenAny(terminal, Task.Delay(Patience));
    }

    [Fact]
    public async Task WrongPassword_ThroughTheServer_IsToldSo()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        var inner = new ScriptedTransport();
        await using var shared = new SharedTransport(inner, 80, 24);
        shared.Share.Start(IPAddress.Loopback, 0, allowInput: false, Password);
        await using ShareRelayHost relay = await ShareRelayHost.StartAsync(server.Address, null, shared.Share.Port, CancellationToken.None).WaitAsync(Patience);
        await using ShareRelayViewer door = await ShareRelayViewer.OpenAsync(relay.Link, CancellationToken.None).WaitAsync(Patience);

        await Assert.ThrowsAsync<WrongSharePasswordException>(() => SharedSessionTransport.ConnectAsync("127.0.0.1", door.LocalPort, "not the password at all", CancellationToken.None).WaitAsync(Patience));
        shared.Share.Stop();
    }

    [Fact]
    public async Task Viewer_OfARoomThatIsNotThere_IsToldItIsNotFound()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();

        string link = ShareRelayLink.Format(server.Address, "abcdefghjkmnpqrstuvw");
        ShareRelayException error = await Assert.ThrowsAsync<ShareRelayException>(() => ShareRelayViewer.OpenAsync(link, CancellationToken.None).WaitAsync(Patience));

        Assert.Equal(ShareRelayFailure.NotFound, error.Failure);
    }

    [Fact]
    public async Task Host_OnAServerThatDoesNotRelay_IsToldSo()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => o.ShareRelay = false);

        ShareRelayException error = await Assert.ThrowsAsync<ShareRelayException>(() => ShareRelayHost.StartAsync(server.Address, null, 1, CancellationToken.None).WaitAsync(Patience));

        Assert.Equal(ShareRelayFailure.NotOffered, error.Failure);
    }

    [Fact]
    public async Task Host_WithoutAnAccountWhereOneIsNeeded_IsToldItIsNotSignedIn()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => o.ShareWithoutAccount = false);

        ShareRelayException error = await Assert.ThrowsAsync<ShareRelayException>(() => ShareRelayHost.StartAsync(server.Address, null, 1, CancellationToken.None).WaitAsync(Patience));

        Assert.Equal(ShareRelayFailure.NotSignedIn, error.Failure);
    }

    [Fact]
    public async Task Host_OverTheRoomLimit_IsToldItIsLimited()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => o.ShareRoomsPerAccount = 1);
        await using ShareRelayHost first = await ShareRelayHost.StartAsync(server.Address, null, 1, CancellationToken.None).WaitAsync(Patience);

        ShareRelayException error = await Assert.ThrowsAsync<ShareRelayException>(() => ShareRelayHost.StartAsync(server.Address, null, 1, CancellationToken.None).WaitAsync(Patience));

        Assert.Equal(ShareRelayFailure.Limited, error.Failure);
    }

    // ---- Against a server that misbehaves --------------------------------------------------------

    /// <summary>A pretend relay on the loopback address: whatever the test has it do with each host that connects.</summary>
    private static async Task<WebApplication> PretendServerAsync(Func<WebSocket, Task> host)
    {
        WebApplication app = WebApplication.CreateSlimBuilder(["--urls=http://127.0.0.1:0", "--Logging:LogLevel:Default=None"]).Build();
        app.UseWebSockets();
        app.MapGet("/v1/share/host", async (HttpContext context) =>
        {
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            await host(socket);
        });
        await app.StartAsync();
        return app;
    }

    private static Uri AddressOf(WebApplication app) => new(app.Urls.First().TrimEnd('/') + "/");

    private static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"id\":42}")]
    [InlineData("{\"id\":\"../../v1/vault\"}")]
    [InlineData("{\"id\":\"short\"}")]
    [InlineData("[\"abcdefghjkmnpqrstuvw\"]")]
    public async Task Host_ToldSomethingOtherThanARoom_RefusesItAsNotASyncServer(string first)
    {
        await using WebApplication app = await PretendServerAsync(async socket =>
        {
            await SendTextAsync(socket, first);
            await Task.Delay(Patience);
        });

        ShareRelayException error = await Assert.ThrowsAsync<ShareRelayException>(() => ShareRelayHost.StartAsync(AddressOf(app), null, 1, CancellationToken.None).WaitAsync(Patience));

        Assert.Equal(ShareRelayFailure.Protocol, error.Failure);
    }

    [Fact]
    public async Task Host_WhoseServerHangsUp_IsToldAndCanBeDisposed()
    {
        await using WebApplication app = await PretendServerAsync(async socket =>
        {
            await SendTextAsync(socket, "{\"id\":\"abcdefghjkmnpqrstuvw\"}");
            await Task.Delay(300);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        });

        ShareRelayHost relay = await ShareRelayHost.StartAsync(AddressOf(app), null, 1, CancellationToken.None).WaitAsync(Patience);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        relay.Closed += () => closed.TrySetResult();

        await closed.Task.WaitAsync(Patience);
        Assert.NotNull(relay.CloseReason);
        await relay.DisposeAsync().AsTask().WaitAsync(Patience);
    }

    [Fact]
    public async Task Host_SentTooMuchAtOnceByTheServer_IsToldAndCanBeDisposed()
    {
        await using WebApplication app = await PretendServerAsync(async socket =>
        {
            await SendTextAsync(socket, "{\"id\":\"abcdefghjkmnpqrstuvw\"}");
            await Task.Delay(300);
            await socket.SendAsync(new byte[70_000], WebSocketMessageType.Binary, true, CancellationToken.None);
            await Task.Delay(Patience);
        });

        ShareRelayHost relay = await ShareRelayHost.StartAsync(AddressOf(app), null, 1, CancellationToken.None).WaitAsync(Patience);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        relay.Closed += () => closed.TrySetResult();

        await closed.Task.WaitAsync(Patience);
        await relay.DisposeAsync().AsTask().WaitAsync(Patience);
    }

    [Fact]
    public async Task Host_SentGarbageByTheServer_NeverOpensAnythingAndCanBeDisposed()
    {
        var random = new Random(17);
        await using WebApplication app = await PretendServerAsync(async socket =>
        {
            await SendTextAsync(socket, "{\"id\":\"abcdefghjkmnpqrstuvw\"}");
            for (int i = 0; i < 200; i++)
            {
                bool text = i % 3 == 0;

                // Text that is not UTF-8 is a broken WebSocket, which rightly ends the connection; that is not what is tested here.
                byte[] frame = text ? Fuzz.Garbage(random, 300, "{}\":,abcdefghjk0123456789 id"u8) : Fuzz.Garbage(random, 300);
                if (text) frame = [.. frame.Select(b => (byte)(b & 0x7F))];

                // Kind "opened" would rightly make the host connect to its own share port; that is not what is tested here either.
                if (frame.Length > 4 && frame[4] == 1) frame[4] = 3;
                await socket.SendAsync(frame, text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, CancellationToken.None);
            }

            await Task.Delay(Patience);
        });

        ShareRelayHost relay = await ShareRelayHost.StartAsync(AddressOf(app), null, 1, CancellationToken.None).WaitAsync(Patience);
        bool closed = false;
        relay.Closed += () => closed = true;
        await Task.Delay(500);

        Assert.False(closed);
        await relay.DisposeAsync().AsTask().WaitAsync(Patience);
    }
}

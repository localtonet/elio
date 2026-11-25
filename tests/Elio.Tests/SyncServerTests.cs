using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Elio.SyncServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Elio.Tests;

/// <summary>
/// The sync server as a client meets it, over HTTP on the loopback address, with the limits that README "Running
/// your own sync server" describes: the size of a store, new stores per address and day, sync codes on or off, and
/// which proxies are believed about who a request came from.
/// </summary>
public sealed class SyncServerTests
{
    private static JsonContent Upload(long expected, string keyring = "{\"kdf\":\"argon2id\"}", string? data = "c2VhbGVk") =>
        JsonContent.Create(new { expectedVersion = expected, keyring, data });

    private static async Task<long> VersionOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("version").GetInt64();
    }

    [Fact]
    public async Task Health_IsAnsweredWithoutAProof()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();

        HttpResponseMessage response = await server.Client().GetAsync("healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-sync-code")]
    [InlineData("esk_0123")]
    [InlineData("esk_ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    public async Task Vault_WithoutAProofItTakes_IsUnauthorized(string? proof)
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        HttpClient client = server.Client(proof);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("v1/vault")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsync("v1/vault", Upload(0))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("v1/vault")).StatusCode);
    }

    [Fact]
    public async Task Vault_IsStoredAndVersionedAndAStaleUploadIsAConflict()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        HttpClient client = server.Client(RunningSyncServer.NewOwnKey());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("v1/vault")).StatusCode);
        Assert.Equal(1, await VersionOf(await client.PutAsync("v1/vault", Upload(0, data: "first"))));
        Assert.Equal(2, await VersionOf(await client.PutAsync("v1/vault", Upload(1, data: "second"))));

        // Another device that had not seen version 2, and one that thinks there is nothing yet.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync("v1/vault", Upload(1, data: "stale"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync("v1/vault", Upload(0, data: "fresh start"))).StatusCode);

        using JsonDocument stored = JsonDocument.Parse(await client.GetStringAsync("v1/vault"));
        Assert.Equal(2, stored.RootElement.GetProperty("version").GetInt64());
        Assert.Equal("second", stored.RootElement.GetProperty("data").GetString());
    }

    [Fact]
    public async Task Vault_IsDeletedOnceAndThenNotFound()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        HttpClient client = server.Client(RunningSyncServer.NewOwnKey());
        await client.PutAsync("v1/vault", Upload(0));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("v1/vault")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("v1/vault")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("v1/vault")).StatusCode);
    }

    [Fact]
    public async Task Accounts_AreKeptApart()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => o.StaticTokens = "alpha-token=alice; beta-token=bob");
        HttpClient alice = server.Client("alpha-token"), bob = server.Client("beta-token"), code = server.Client(RunningSyncServer.NewOwnKey());

        await alice.PutAsync("v1/vault", Upload(0, data: "alice-data"));

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("v1/vault")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await code.GetAsync("v1/vault")).StatusCode);
        Assert.Equal(1, await VersionOf(await bob.PutAsync("v1/vault", Upload(0, data: "bob-data"))));
        Assert.Contains("alice-data", await alice.GetStringAsync("v1/vault"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocaltonetProof_IsCheckedForThisServersAddress_AndAnAnswerIsRemembered()
    {
        // A pretend Localtonet on the loopback address, which knows one good proof.
        var asked = new System.Collections.Concurrent.ConcurrentQueue<(string Proof, string Audience)>();
        WebApplication localtonet = WebApplication.CreateSlimBuilder(["--urls=http://127.0.0.1:0", "--Logging:LogLevel:Default=None"]).Build();
        localtonet.MapPost("/commonapi/elio/identity/verify", async (HttpContext context) =>
        {
            using JsonDocument body = await JsonDocument.ParseAsync(context.Request.Body);
            string proof = body.RootElement.GetProperty("proof").GetString()!;
            asked.Enqueue((proof, body.RootElement.GetProperty("audience").GetString()!));
            return proof switch
            {
                "good-proof" => Results.Json(new { subject = "user-1" }),
                "flaky-proof" => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                _ => Results.Unauthorized(),
            };
        });
        await localtonet.StartAsync();
        try
        {
            await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => (o.Localtonet, o.PublicUrl) = (localtonet.Urls.First(), "HTTPS://Sync.Example.com:8443/ignored/path"));

            Assert.Equal(HttpStatusCode.NotFound, (await server.Client("good-proof").GetAsync("v1/vault")).StatusCode);
            Assert.Equal(1, await VersionOf(await server.Client("good-proof").PutAsync("v1/vault", Upload(0))));
            for (int i = 0; i < 2; i++)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client("bad-proof").GetAsync("v1/vault")).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client("flaky-proof").GetAsync("v1/vault")).StatusCode);
            }

            // Asked about once for this server's address alone; a "no" is remembered, trouble at Localtonet is not.
            Assert.All(asked, question => Assert.Equal("https://sync.example.com:8443", question.Audience));
            Assert.Equal(1, asked.Count(q => q.Proof == "good-proof"));
            Assert.Equal(1, asked.Count(q => q.Proof == "bad-proof"));
            Assert.Equal(2, asked.Count(q => q.Proof == "flaky-proof"));
        }
        finally
        {
            await localtonet.StopAsync();
            await localtonet.DisposeAsync();
        }
    }

    [Fact]
    public async Task SyncCodes_WhenTurnedOff_AreRefusedButStaticTokensStillWork()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => (o.AccountsWithoutSignIn, o.StaticTokens) = (false, "team=ops"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Client(RunningSyncServer.NewOwnKey()).PutAsync("v1/vault", Upload(0))).StatusCode);
        Assert.Equal(1, await VersionOf(await server.Client("team").PutAsync("v1/vault", Upload(0))));

        // Pairing exists only for sync codes.
        Assert.Equal(HttpStatusCode.NotFound, (await server.Client().GetAsync("v1/pair/" + new string('a', 64))).StatusCode);
    }

    [Fact]
    public async Task Upload_LargerThanMaxBytesAllows_IsRefused()
    {
        // The server takes keyring and data together up to twice MaxBytes characters: base64 and JSON make a store larger than what it holds.
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => o.MaxBytes = 1000);
        HttpClient client = server.Client(RunningSyncServer.NewOwnKey());
        string keyring = new('k', 100);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("v1/vault", Upload(0, keyring, new string('d', 1901)))).StatusCode);
        Assert.Equal(1, await VersionOf(await client.PutAsync("v1/vault", Upload(0, keyring, new string('d', 1900)))));
    }

    [Fact]
    public async Task Upload_WhoseBodyIsOverTheRequestLimit_IsRefusedWithoutBeingRead()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => o.MaxBytes = 1000);
        HttpClient client = server.Client(RunningSyncServer.NewOwnKey());

        HttpResponseMessage response = await client.PutAsync("v1/vault", Upload(0, data: new string('d', 200_000)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("v1/vault")).StatusCode);
    }

    [Fact]
    public async Task Upload_ThatIsNotAWellFormedVault_IsRefused()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        HttpClient client = server.Client(RunningSyncServer.NewOwnKey());

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PutAsync("v1/vault", new StringContent("{}", Encoding.UTF8, "text/plain"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("v1/vault", new StringContent("{\"expectedVersion\":", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("v1/vault", new StringContent("null", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("v1/vault", Upload(0, keyring: ""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("v1/vault", Upload(0, data: null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("v1/vault", Upload(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("v1/vault")).StatusCode);
    }

    [Fact]
    public async Task Upload_Garbage_NeverStoresAnythingOrBreaksTheServer()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        HttpClient client = server.Client(RunningSyncServer.NewOwnKey());
        byte[] valid = Encoding.UTF8.GetBytes("""{"expectedVersion":0,"keyring":"{\"kdf\":1}","data":"c2VhbGVk"}""");
        var random = new Random(5);

        int stored = 0;
        for (int round = 0; round < 60; round++)
        {
            var content = new ByteArrayContent(round % 2 == 0 ? Fuzz.Garbage(random, 300) : Fuzz.Mutate(random, valid));
            content.Headers.ContentType = new("application/json");

            HttpResponseMessage response = await client.PutAsync("v1/vault", content);

            Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.OK or HttpStatusCode.Conflict, $"round {round}: {response.StatusCode}");
            if (response.StatusCode == HttpStatusCode.OK) stored++;
        }

        Assert.Equal(HttpStatusCode.OK, (await server.Client().GetAsync("healthz")).StatusCode);
        Assert.True(stored <= 1);
    }

    [Fact]
    public async Task NewAccountsPerDay_LimitsNewStoresFromOneAddressOnly()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => (o.NewAccountsPerDay, o.StaticTokens) = (2, "team=ops"));
        HttpClient first = server.Client(RunningSyncServer.NewOwnKey());

        Assert.Equal(1, await VersionOf(await first.PutAsync("v1/vault", Upload(0))));
        Assert.Equal(1, await VersionOf(await server.Client(RunningSyncServer.NewOwnKey()).PutAsync("v1/vault", Upload(0))));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await server.Client(RunningSyncServer.NewOwnKey()).PutAsync("v1/vault", Upload(0))).StatusCode);

        // A store that exists may still be written, and an account that is not a made-up key is not counted.
        Assert.Equal(2, await VersionOf(await first.PutAsync("v1/vault", Upload(1))));
        Assert.Equal(1, await VersionOf(await server.Client("team").PutAsync("v1/vault", Upload(0))));
    }

    [Fact]
    public async Task MaxVaults_RefusesNewStoresOnceFull()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => o.MaxVaults = 1);
        HttpClient first = server.Client(RunningSyncServer.NewOwnKey());
        await first.PutAsync("v1/vault", Upload(0));

        Assert.Equal(HttpStatusCode.InsufficientStorage, (await server.Client(RunningSyncServer.NewOwnKey()).PutAsync("v1/vault", Upload(0))).StatusCode);
        Assert.Equal(2, await VersionOf(await first.PutAsync("v1/vault", Upload(1))));
    }

    // Two new stores, each from a different address according to X-Forwarded-For. Whether the second is let in says
    // whether the header was believed: with one new store a day per address, only a different address gets another.
    private static async Task<HttpStatusCode> SecondStoreFromAnotherClaimedAddressAsync(Action<SyncServerOptions> configure, string first = "203.0.113.1", string second = "203.0.113.2")
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o =>
        {
            o.NewAccountsPerDay = 1;
            configure(o);
        });

        Assert.Equal(1, await VersionOf(await server.Client(RunningSyncServer.NewOwnKey(), first).PutAsync("v1/vault", Upload(0))));
        return (await server.Client(RunningSyncServer.NewOwnKey(), second).PutAsync("v1/vault", Upload(0))).StatusCode;
    }

    [Fact]
    public async Task ForwardedFor_WithoutBehindProxy_IsIgnored() =>
        Assert.Equal(HttpStatusCode.TooManyRequests, await SecondStoreFromAnotherClaimedAddressAsync(o => o.BehindProxy = false));

    [Fact]
    public async Task ForwardedFor_FromAProxyThatIsNotTrusted_IsIgnored() =>
        Assert.Equal(HttpStatusCode.TooManyRequests, await SecondStoreFromAnotherClaimedAddressAsync(o => (o.BehindProxy, o.TrustedProxies) = (true, "10.0.0.5, 172.16.0.0/12")));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5,127.0.0.0/8")]
    [InlineData("")]
    public async Task ForwardedFor_FromATrustedProxy_IsBelieved(string trusted)
    {
        Assert.Equal(HttpStatusCode.OK, await SecondStoreFromAnotherClaimedAddressAsync(o => (o.BehindProxy, o.TrustedProxies) = (true, trusted)));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SecondStoreFromAnotherClaimedAddressAsync(o => (o.BehindProxy, o.TrustedProxies) = (true, trusted), "203.0.113.7", "203.0.113.7"));
    }

    [Fact]
    public async Task ForwardedFor_IPv6AddressesInOneSlash64_CountAsOneCaller()
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, await SecondStoreFromAnotherClaimedAddressAsync(o => (o.BehindProxy, o.TrustedProxies) = (true, "127.0.0.1"), "2001:db8:1:2::1", "2001:db8:1:2:ffff::9"));
        Assert.Equal(HttpStatusCode.OK, await SecondStoreFromAnotherClaimedAddressAsync(o => (o.BehindProxy, o.TrustedProxies) = (true, "127.0.0.1"), "2001:db8:1:2::1", "2001:db8:1:3::1"));
    }

    [Theory]
    [InlineData("proxy.example.com")]
    [InlineData("10.0.0.0/99")]
    [InlineData("10.0.0.300")]
    public void TrustedProxies_ThatAreNotAddressesOrNetworks_StopTheServerFromStarting(string trusted)
    {
        string folder = Path.Combine(TestData.Root, "sync-server-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Throws<InvalidOperationException>(() => SyncServerApp.Build(
                ["--urls=http://127.0.0.1:0"],
                o => (o.PublicUrl, o.Localtonet, o.Database, o.BehindProxy, o.TrustedProxies) = ("http://sync.test", "", Path.Combine(folder, "sync.db"), true, trusted)));
        }
        finally
        {
            // The database was opened before the proxies were read; let go of it so the folder can go.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TestData.Delete(folder);
        }
    }

    [Fact]
    public void PublicUrl_IsRequired() =>
        Assert.Throws<InvalidOperationException>(() => SyncServerApp.Build(["--urls=http://127.0.0.1:0"], o => (o.PublicUrl, o.Database) = ("", Path.Combine(TestData.Root, "unused.db"))));

    [Fact]
    public async Task Requests_OverTheLimitPerMinute_AreTurnedAway()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        HttpClient client = server.Client();

        HttpStatusCode last = HttpStatusCode.OK;
        for (int i = 0; i < 121 && last == HttpStatusCode.OK; i++) last = (await client.GetAsync("healthz")).StatusCode;

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }

    // ---- Pairing ------------------------------------------------------------------------------

    private static string PairName() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    [Fact]
    public async Task Pairing_IsHandedOverOnceOnly()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        HttpClient client = server.Client();
        string name = PairName();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("v1/pair/" + name, new StringContent("sealed-key"))).StatusCode);

        Assert.Equal("sealed-key", await client.GetStringAsync("v1/pair/" + name));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("v1/pair/" + name)).StatusCode);
    }

    [Theory]
    [InlineData("short", "data")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", "data")]
    [InlineData(null, "")]
    [InlineData(null, "513")]
    public async Task Pairing_WithABadNameOrBody_IsRefused(string? name, string body)
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();

        string content = body == "513" ? new string('x', 513) : body;
        HttpResponseMessage response = await server.Client().PostAsync("v1/pair/" + (name ?? PairName()), new StringContent(content));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pairing_FetchesPerMinute_AreLimitedSoCodesCannotBeTriedOneByOne()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        HttpClient client = server.Client();

        var answers = new List<HttpStatusCode>();
        for (int i = 0; i < 11; i++) answers.Add((await client.GetAsync("v1/pair/" + PairName())).StatusCode);

        Assert.All(answers[..10], answer => Assert.Equal(HttpStatusCode.NotFound, answer));
        Assert.Equal(HttpStatusCode.TooManyRequests, answers[10]);
    }

    [Fact]
    public async Task Pairing_FromAWebPage_IsForbidden()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        HttpClient page = server.Client();
        page.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        string name = PairName();

        Assert.Equal(HttpStatusCode.Forbidden, (await page.PostAsync("v1/pair/" + name, new StringContent("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await page.GetAsync("v1/pair/" + name)).StatusCode);
    }
}

/// <summary>
/// The relay for shared terminals, as a host and its viewers meet it. Each message on the host's connection is four
/// bytes of viewer number, one byte of kind and the payload; a viewer's connection carries only its own bytes.
/// </summary>
public sealed class ShareRelayServerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static Uri WebSocketAddress(RunningSyncServer server, string path) =>
        new UriBuilder(new Uri(server.Address, path)) { Scheme = Uri.UriSchemeWs }.Uri;

    private static async Task<ClientWebSocket> ConnectAsync(RunningSyncServer server, string path, Action<ClientWebSocketOptions>? setUp = null)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        setUp?.Invoke(socket.Options);
        await socket.ConnectAsync(WebSocketAddress(server, path), CancellationToken.None).WaitAsync(Patience);
        return socket;
    }

    // The answer the server gave instead of a WebSocket.
    private static async Task<HttpStatusCode> RefusalAsync(RunningSyncServer server, string path, Action<ClientWebSocketOptions>? setUp = null)
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        setUp?.Invoke(socket.Options);
        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(WebSocketAddress(server, path), CancellationToken.None).WaitAsync(Patience));
        return socket.HttpStatusCode;
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(WebSocket socket)
    {
        using var message = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, CancellationToken.None).WaitAsync(Patience);
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage || result.MessageType == WebSocketMessageType.Close) return (result.MessageType, message.ToArray());
        }
    }

    private static async Task<string> RoomOfAsync(WebSocket host)
    {
        var (type, data) = await ReceiveAsync(host);
        Assert.Equal(WebSocketMessageType.Text, type);
        using JsonDocument created = JsonDocument.Parse(data);
        return created.RootElement.GetProperty("id").GetString()!;
    }

    private static byte[] Frame(uint viewer, byte kind, string payload = "")
    {
        byte[] frame = new byte[5 + Encoding.UTF8.GetByteCount(payload)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame, viewer);
        frame[4] = kind;
        Encoding.UTF8.GetBytes(payload, frame.AsSpan(5));
        return frame;
    }

    private static Task SendAsync(WebSocket socket, byte[] data, WebSocketMessageType type = WebSocketMessageType.Binary) =>
        socket.SendAsync(data, type, true, CancellationToken.None).WaitAsync(Patience);

    [Fact]
    public async Task Host_IsGivenARoomThatViewersJoinAndBytesPassBothWays()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        using ClientWebSocket host = await ConnectAsync(server, "v1/share/host");
        string room = await RoomOfAsync(host);
        Assert.Matches("^[a-hjkmnp-z2-9]{20}$", room);

        using ClientWebSocket viewer = await ConnectAsync(server, "v1/share/join/" + room);
        Assert.Equal(Frame(1, 1), (await ReceiveAsync(host)).Data);

        await SendAsync(viewer, "from the viewer"u8.ToArray());
        Assert.Equal(Frame(1, 0, "from the viewer"), (await ReceiveAsync(host)).Data);

        // Bytes for a viewer number nobody has are dropped; bytes for viewer 1 reach it alone, without the header.
        await SendAsync(host, Frame(7, 0, "for nobody"));
        await SendAsync(host, Frame(1, 0, "from the host"));
        Assert.Equal("from the host"u8.ToArray(), (await ReceiveAsync(viewer)).Data);

        // The host lets the viewer go.
        await SendAsync(host, Frame(1, 2));
        Assert.Equal(WebSocketMessageType.Close, (await ReceiveAsync(viewer)).Type);
    }

    [Fact]
    public async Task Viewer_ThatLeaves_IsReportedToTheHost()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        using ClientWebSocket host = await ConnectAsync(server, "v1/share/host");
        string room = await RoomOfAsync(host);
        ClientWebSocket viewer = await ConnectAsync(server, "v1/share/join/" + room);
        await ReceiveAsync(host);

        await viewer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);

        Assert.Equal(Frame(1, 2), (await ReceiveAsync(host)).Data);
        viewer.Dispose();
    }

    [Fact]
    public async Task Join_OfARoomThatIsNotThere_IsNotFound()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();

        Assert.Equal(HttpStatusCode.NotFound, await RefusalAsync(server, "v1/share/join/abcdefghjkmnpqrstuvw"));
        Assert.Equal(HttpStatusCode.NotFound, await RefusalAsync(server, "v1/share/join/..%2F..%2Fvault"));
    }

    [Fact]
    public async Task Room_WithAllItsViewers_RefusesAnother()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => o.ShareViewersPerRoom = 1);
        using ClientWebSocket host = await ConnectAsync(server, "v1/share/host");
        string room = await RoomOfAsync(host);
        using ClientWebSocket viewer = await ConnectAsync(server, "v1/share/join/" + room);

        Assert.Equal(HttpStatusCode.Forbidden, await RefusalAsync(server, "v1/share/join/" + room));
    }

    [Fact]
    public async Task Rooms_PerAccountAreLimitedAndFreedWhenAHostLeaves()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => o.ShareRoomsPerAccount = 1);
        ClientWebSocket first = await ConnectAsync(server, "v1/share/host");
        await RoomOfAsync(first);

        Assert.Equal(HttpStatusCode.TooManyRequests, await RefusalAsync(server, "v1/share/host"));

        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await ReceiveAsync(first);
        first.Dispose();

        HttpStatusCode? refused = null;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using ClientWebSocket again = await ConnectAsync(server, "v1/share/host");
                refused = null;
                break;
            }
            catch (WebSocketException)
            {
                // The room is let go a moment after the host hangs up.
                refused = HttpStatusCode.TooManyRequests;
                await Task.Delay(100);
            }
        }

        Assert.Null(refused);
    }

    [Fact]
    public async Task Host_WithAProofThatIsNotAccepted_IsRefusedEvenWhereNoAccountIsNeeded()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, await RefusalAsync(server, "v1/share/host", o => o.SetRequestHeader("Authorization", "Bearer expired")));
    }

    [Fact]
    public async Task Host_WithoutAnAccount_IsRefusedWhenTheServerWantsOne()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => (o.ShareWithoutAccount, o.StaticTokens) = (false, "team=ops"));

        Assert.Equal(HttpStatusCode.Unauthorized, await RefusalAsync(server, "v1/share/host"));
        using ClientWebSocket host = await ConnectAsync(server, "v1/share/host", o => o.SetRequestHeader("Authorization", "Bearer team"));
        await RoomOfAsync(host);
    }

    [Fact]
    public async Task Relay_WhenTurnedOff_IsNotThere()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => o.ShareRelay = false);

        Assert.Equal(HttpStatusCode.NotFound, await RefusalAsync(server, "v1/share/host"));
    }

    [Fact]
    public async Task Relay_FromAWebPage_IsForbidden()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();

        Assert.Equal(HttpStatusCode.Forbidden, await RefusalAsync(server, "v1/share/host", o => o.SetRequestHeader("Origin", "https://evil.example")));
    }

    [Fact]
    public async Task Joins_PerMinuteFromOneAddress_AreLimited()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => o.ShareJoinsPerMinute = 3);

        for (int i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.NotFound, await RefusalAsync(server, "v1/share/join/abcdefghjkmnpqrstuv" + "abc"[i]));
        Assert.Equal(HttpStatusCode.TooManyRequests, await RefusalAsync(server, "v1/share/join/abcdefghjkmnpqrstuvw"));
    }

    [Fact]
    public async Task Host_ThatSendsText_IsCutOffAsAProtocolError()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        using ClientWebSocket host = await ConnectAsync(server, "v1/share/host");
        await RoomOfAsync(host);

        await SendAsync(host, "{\"hello\":1}"u8.ToArray(), WebSocketMessageType.Text);

        Assert.Contains(await CutOffAsync(host), new WebSocketCloseStatus?[] { WebSocketCloseStatus.ProtocolError, null });
    }

    [Fact]
    public async Task Host_ThatSendsTooMuchAtOnce_IsCutOff()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        using ClientWebSocket host = await ConnectAsync(server, "v1/share/host");
        await RoomOfAsync(host);

        await SendAsync(host, new byte[5 + (64 * 1024) + 1]);

        Assert.Contains(await CutOffAsync(host), new WebSocketCloseStatus?[] { WebSocketCloseStatus.MessageTooBig, null });
    }

    [Fact]
    public async Task Viewer_ThatSendsTooMuchAtOnce_IsCutOffAndTheHostTold()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync();
        using ClientWebSocket host = await ConnectAsync(server, "v1/share/host");
        string room = await RoomOfAsync(host);
        using ClientWebSocket viewer = await ConnectAsync(server, "v1/share/join/" + room);
        await ReceiveAsync(host);

        await SendAsync(viewer, new byte[(64 * 1024) + 1]);

        Assert.Contains(await CutOffAsync(viewer), new WebSocketCloseStatus?[] { WebSocketCloseStatus.MessageTooBig, null });
        Assert.Equal(Frame(1, 2), (await ReceiveAsync(host)).Data);
    }

    // How the server ended the connection: the reason it gave, or null if it hung up without one. Fails if it
    // goes on sending instead, or neither.
    private static async Task<WebSocketCloseStatus?> CutOffAsync(WebSocket socket)
    {
        try
        {
            var (type, _) = await ReceiveAsync(socket);
            Assert.Equal(WebSocketMessageType.Close, type);
            return socket.CloseStatus;
        }
        catch (WebSocketException)
        {
            return null;
        }
    }

    [Fact]
    public async Task Host_ThatSendsGarbage_NeverTakesTheServerDown()
    {
        await using RunningSyncServer server = await RunningSyncServer.StartAsync(o => o.ShareRoomsPerAccount = 100);
        var random = new Random(11);

        for (int round = 0; round < 10; round++)
        {
            using ClientWebSocket host = await ConnectAsync(server, "v1/share/host");
            string room = await RoomOfAsync(host);
            using ClientWebSocket viewer = await ConnectAsync(server, "v1/share/join/" + room);
            await ReceiveAsync(host);

            try
            {
                for (int i = 0; i < 20; i++) await SendAsync(host, Fuzz.Garbage(random, 64));
            }
            catch (WebSocketException)
            {
                // Cut off for it, which is allowed.
            }
        }

        Assert.Equal(HttpStatusCode.OK, (await server.Client().GetAsync("healthz")).StatusCode);
    }
}

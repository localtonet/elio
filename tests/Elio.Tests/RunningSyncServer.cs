using System.Net.Http.Headers;
using System.Security.Cryptography;
using Elio.SyncServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;

namespace Elio.Tests;

/// <summary>
/// The real sync server, run inside the test process on this computer's loopback address with a port the system
/// picks and a database in the tests' own folder. Localtonet sign-in is off, so it never asks anyone anything.
/// </summary>
internal sealed class RunningSyncServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _folder;
    private readonly List<HttpClient> _clients = [];

    private RunningSyncServer(WebApplication app, string folder, Uri address)
    {
        _app = app;
        _folder = folder;
        Address = address;
    }

    /// <summary>Where it listens, with a slash at the end: http://127.0.0.1:port/.</summary>
    public Uri Address { get; }

    public static async Task<RunningSyncServer> StartAsync(Action<SyncServerOptions>? configure = null)
    {
        string folder = Path.Combine(TestData.Root, "sync-server-" + Guid.NewGuid().ToString("N"));
        WebApplication app = SyncServerApp.Build(
            ["--urls=http://127.0.0.1:0", "--Logging:LogLevel:Default=None"],
            options =>
            {
                options.PublicUrl = "http://sync.test";
                options.Localtonet = string.Empty;
                options.Database = Path.Combine(folder, "sync.db");
                configure?.Invoke(options);
            });
        await app.StartAsync();
        return new RunningSyncServer(app, folder, new Uri(app.Urls.First().TrimEnd('/') + "/"));
    }

    /// <summary>A key an app makes up for itself to sync without an account: "esk_" and 64 hex digits.</summary>
    public static string NewOwnKey() => "esk_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>A client that says who it is with this proof, and claims to be forwarded for this address.</summary>
    public HttpClient Client(string? bearer = null, string? forwardedFor = null)
    {
        var client = new HttpClient { BaseAddress = Address, Timeout = TimeSpan.FromSeconds(30) };
        if (bearer is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (forwardedFor is not null) client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        lock (_clients) _clients.Add(client);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_clients)
        {
            foreach (HttpClient client in _clients) client.Dispose();
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        TestData.Delete(_folder);
    }
}

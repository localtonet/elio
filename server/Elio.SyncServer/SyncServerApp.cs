using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;

namespace Elio.SyncServer;

public sealed class StoredVault
{
    public long Version { get; set; }
    public string Keyring { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
}

public sealed class VaultUpload
{
    public long ExpectedVersion { get; set; }
    public string Keyring { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
}

public sealed class VaultVersion
{
    public long Version { get; set; }
}

internal sealed class VerifyRequest
{
    public string Proof { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
}

internal sealed class VerifyResponse
{
    public string? Subject { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(StoredVault))]
[JsonSerializable(typeof(VaultUpload))]
[JsonSerializable(typeof(VaultVersion))]
[JsonSerializable(typeof(VerifyRequest))]
[JsonSerializable(typeof(VerifyResponse))]
[JsonSerializable(typeof(ShareRoomCreated))]
internal sealed partial class ServerJson : JsonSerializerContext;

/// <summary>How the server is set up. Read from configuration under "Sync".</summary>
public sealed class SyncServerOptions
{
    /// <summary>The address clients use for this server. Proofs of identity are only accepted for this address.</summary>
    public string PublicUrl { get; set; } = string.Empty;

    /// <summary>Where to check proofs of identity. Empty turns Localtonet sign-in off.</summary>
    public string? Localtonet { get; set; } = "https://localtonet.com";

    /// <summary>For running without Localtonet: "token=account" pairs separated by semicolons.</summary>
    public string? StaticTokens { get; set; }

    public string Database { get; set; } = "data/sync.db";

    /// <summary>The most one account may store, in bytes. Encrypted libraries are small.</summary>
    public int MaxBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Whether shared terminals may be passed along through this server. It sees only encrypted bytes.</summary>
    public bool ShareRelay { get; set; } = true;

    /// <summary>
    /// Whether an app may sync with a key it made up itself, with no account anywhere. The key is long and random, so
    /// knowing it is what makes someone the owner of the data kept under it, and that data is unreadable here anyway.
    /// </summary>
    public bool AccountsWithoutSignIn { get; set; } = true;

    /// <summary>How many new stores of data one address may start in a day with such keys, so that the disk cannot be filled for free.</summary>
    public int NewAccountsPerDay { get; set; } = 10;

    /// <summary>
    /// Whether a terminal may be shared without an account. Such rooms are counted by the address they come from
    /// instead, so one computer cannot open rooms without end. Turn it off to keep sharing to people who can sign in.
    /// </summary>
    public bool ShareWithoutAccount { get; set; } = true;

    /// <summary>
    /// Whether the server sits behind a reverse proxy that says, in X-Forwarded-For, who each request really came
    /// from. Without it every client looks like the proxy and they would all share one request limit. Only turn it on
    /// when nothing can reach the server except through the proxy, since the header is otherwise easy to forge.
    /// </summary>
    public bool BehindProxy { get; set; }

    /// <summary>
    /// The proxies whose X-Forwarded-For is believed while <see cref="BehindProxy"/> is on: addresses or networks,
    /// such as "10.0.0.5" or "172.16.0.0/12", separated by commas. Left empty, the header is believed from anyone,
    /// which is only safe when nothing but the proxy can reach the server.
    /// </summary>
    public string? TrustedProxies { get; set; }

    /// <summary>
    /// How many stores of data the server keeps at most, for everyone together. Each may be as large as
    /// <see cref="MaxBytes"/> allows and a key to start one with costs nothing, so this is what bounds the disk.
    /// </summary>
    public int MaxVaults { get; set; } = 100_000;

    /// <summary>
    /// A store that no device read or wrote for this many days is removed. 0 keeps everything for ever. A device
    /// that comes back after that finds nothing stored and has to start sync again from its own copy.
    /// </summary>
    public int VaultIdleDays { get; set; }

    /// <summary>How many terminals one account may share at a time.</summary>
    public int ShareRoomsPerAccount { get; set; } = 4;

    /// <summary>How many people may watch one shared terminal at a time.</summary>
    public int ShareViewersPerRoom { get; set; } = 16;

    /// <summary>How many terminals may be shared through this server at a time, by everyone together.</summary>
    public int ShareMaxRooms { get; set; } = 10_000;

    /// <summary>
    /// What one shared terminal may carry, in bytes a second, each way. The host may send this much; the same
    /// again is split evenly between the viewers, so that none of them can use up another's part. A terminal
    /// needs far less; the limit is there so the server is no use as a free pipe for anything else.
    /// </summary>
    public int ShareBytesPerSecond { get; set; } = 1024 * 1024;

    /// <summary>How far behind, in bytes, a viewer may fall before it is dropped. This much memory may be held for each viewer.</summary>
    public int ShareViewerBacklog { get; set; } = 1024 * 1024;

    /// <summary>A shared terminal that carried nothing for this long is ended, and so is a viewer nothing went to or came from.</summary>
    public TimeSpan ShareIdleTimeout { get; set; } = TimeSpan.FromHours(12);

    /// <summary>How many times a minute one address may try to join a shared terminal, whether or not it exists.</summary>
    public int ShareJoinsPerMinute { get; set; } = 30;
}

/// <summary>Works out which account a request belongs to. Proofs are remembered for a while to spare Localtonet.</summary>
internal sealed class Identities(SyncServerOptions options, HttpClient http)
{
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(10);
    private const int MaxRemembered = 100_000;

    // A proof Localtonet said no to is not asked about again for this long, so that sending the same junk over and
    // over makes this server ask only once. Short, because a proof may become good.
    private static readonly TimeSpan RememberRefused = TimeSpan.FromMinutes(1);
    private const int MaxRefused = 50_000;

    private readonly ConcurrentDictionary<string, (string Subject, DateTime Until)> _known = new();
    private readonly Dictionary<string, string> _static = ParseStatic(options.StaticTokens);

    // By hash, never the proof itself. All are kept equally long, so the order they came in is the order they run out.
    private readonly Dictionary<string, DateTime> _refused = new(StringComparer.Ordinal);
    private readonly Queue<string> _refusedOrder = new();

    private static Dictionary<string, string> ParseStatic(string? text)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in (text ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals > 0) tokens[Hash(pair[..equals])] = pair[(equals + 1)..];
        }

        return tokens;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>What the subject of a self-made key starts with.</summary>
    public const string OwnKeyPrefix = "key:";

    // "esk_" and 64 hex digits: 256 random bits, which nobody guesses.
    private static bool IsOwnKey(string proof) =>
        proof.Length == 68 && proof.StartsWith("esk_", StringComparison.Ordinal) && proof.AsSpan(4).IndexOfAnyExcept("0123456789abcdef") < 0;

    public async Task<string?> SubjectAsync(HttpContext context)
    {
        string? header = context.Request.Headers.Authorization;
        if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;

        string proof = header[7..].Trim();
        string hash = Hash(proof);
        if (_static.TryGetValue(hash, out string? fixedSubject)) return "static:" + fixedSubject;

        // A key the app made up for itself. Whoever holds it owns what is stored under it; only its hash is kept.
        if (IsOwnKey(proof)) return options.AccountsWithoutSignIn ? OwnKeyPrefix + hash : null;
        if (_known.TryGetValue(hash, out var remembered) && remembered.Until > DateTime.UtcNow) return remembered.Subject;
        if (string.IsNullOrEmpty(options.Localtonet) || IsRefused(hash)) return null;

        using HttpResponseMessage response = await http.PostAsJsonAsync(
            new Uri(new Uri(options.Localtonet), "commonapi/elio/identity/verify"),
            new VerifyRequest { Proof = proof, Audience = options.PublicUrl },
            ServerJson.Default.VerifyRequest,
            context.RequestAborted).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Localtonet being in trouble says nothing about the proof, so only a real "no" is remembered.
            if ((int)response.StatusCode < 500) Refuse(hash);
            return null;
        }

        string? subject = (await response.Content.ReadFromJsonAsync(ServerJson.Default.VerifyResponse, context.RequestAborted).ConfigureAwait(false))?.Subject;
        if (subject is null)
        {
            Refuse(hash);
            return null;
        }

        if (_known.Count > MaxRemembered) _known.Clear();
        _known[hash] = ("localtonet:" + subject, DateTime.UtcNow + Remember);
        return "localtonet:" + subject;
    }

    private bool IsRefused(string hash)
    {
        lock (_refused) return _refused.TryGetValue(hash, out DateTime until) && until > DateTime.UtcNow;
    }

    private void Refuse(string hash)
    {
        DateTime now = DateTime.UtcNow;
        lock (_refused)
        {
            // Those that ran out go first, and when it is full the oldest makes room.
            while (_refusedOrder.TryPeek(out string? oldest) && (_refused.Count >= MaxRefused || _refused[oldest] <= now))
                _refused.Remove(_refusedOrder.Dequeue());

            if (_refused.TryAdd(hash, now + RememberRefused)) _refusedOrder.Enqueue(hash);
        }
    }
}

/// <summary>
/// Holds what one device leaves for another to fetch with a pairing code: a few bytes, already encrypted, for ten
/// minutes, handed over once. Kept in memory only, since losing it costs nothing but typing a new code.
/// </summary>
internal sealed class PairingShelf
{
    public const int MaxBytes = 512;
    private const int MaxHeld = 50_000;
    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, (string Data, DateTime Until)> _held = new(StringComparer.Ordinal);

    public static bool IsName(string name) => name.Length == 64 && name.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;

    public bool Put(string name, string data)
    {
        DateTime now = DateTime.UtcNow;
        lock (_held)
        {
            if (_held.Count >= MaxHeld)
            {
                foreach (string old in _held.Where(pair => pair.Value.Until <= now).Select(pair => pair.Key).ToList()) _held.Remove(old);
                if (_held.Count >= MaxHeld) return false;
            }

            _held[name] = (data, now + Kept);
            return true;
        }
    }

    public string? Take(string name)
    {
        lock (_held)
        {
            if (!_held.Remove(name, out (string Data, DateTime Until) found)) return null;
            return found.Until > DateTime.UtcNow ? found.Data : null;
        }
    }
}

/// <summary>Counts something per address and lets it happen only so many times in a day.</summary>
internal sealed class DailyCount(int limit)
{
    private const int MaxSeen = 50_000;

    private readonly Dictionary<string, (DateTime Day, int Count, long Order)> _seen = new(StringComparer.Ordinal);
    private long _order;

    public bool Allow(string address)
    {
        DateTime today = DateTime.UtcNow.Date;
        lock (_seen)
        {
            bool known = _seen.TryGetValue(address, out (DateTime Day, int Count, long Order) record);
            if (record.Day != today) record = (today, 0, ++_order);
            if (record.Count >= limit) return false;

            if (!known && _seen.Count >= MaxSeen) MakeRoom(today);
            _seen[address] = (today, record.Count + 1, record.Order);
            return true;
        }
    }

    // Earlier days' counts go first. If that is not enough, only the address counted longest ago is forgotten:
    // forgetting everyone at once would hand every address a fresh day whenever enough new ones showed up.
    private void MakeRoom(DateTime today)
    {
        foreach (string old in _seen.Where(pair => pair.Value.Day != today).Select(pair => pair.Key).ToList()) _seen.Remove(old);
        if (_seen.Count >= MaxSeen) _seen.Remove(_seen.MinBy(pair => pair.Value.Order).Key);
    }
}

/// <summary>One row per account: a version, the keyring and the sealed library. All of it is unreadable here.</summary>
internal sealed class VaultStore
{
    // When a store was last used is written down at most this often, so that reading does not turn into writing.
    private static readonly TimeSpan AccessNoted = TimeSpan.FromDays(1);

    private readonly string _connection;

    public VaultStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();

        using SqliteConnection db = Open();
        using SqliteCommand create = db.CreateCommand();
        create.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS vaults (
                subject TEXT PRIMARY KEY,
                version INTEGER NOT NULL,
                keyring TEXT NOT NULL,
                data TEXT NOT NULL,
                updated TEXT NOT NULL,
                accessed TEXT NOT NULL
            );
            """;
        create.ExecuteNonQuery();

        // A database from before stores could expire has no record of when each was last used. They all count as
        // used now, so that none is removed for the time nobody was keeping track.
        using SqliteCommand has = db.CreateCommand();
        has.CommandText = "SELECT COUNT(*) FROM pragma_table_info('vaults') WHERE name = 'accessed'";
        if ((long)has.ExecuteScalar()! == 0)
        {
            using SqliteCommand add = db.CreateCommand();
            add.CommandText = $"ALTER TABLE vaults ADD COLUMN accessed TEXT NOT NULL DEFAULT '{DateTime.UtcNow:O}'";
            add.ExecuteNonQuery();
        }

        // What the sweep for unused stores looks through, so that it never has to read the stores themselves.
        using SqliteCommand index = db.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS vaults_accessed ON vaults (accessed)";
        index.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }

    public bool Exists(string subject)
    {
        using SqliteConnection db = Open();
        using SqliteCommand find = db.CreateCommand();
        find.CommandText = "SELECT 1 FROM vaults WHERE subject = $subject";
        find.Parameters.AddWithValue("$subject", subject);
        return find.ExecuteScalar() is not null;
    }

    public long Count()
    {
        using SqliteConnection db = Open();
        using SqliteCommand count = db.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM vaults";
        return (long)count.ExecuteScalar()!;
    }

    /// <summary>Removes the stores nothing read or wrote since a time. Returns how many there were.</summary>
    public int RemoveIdle(DateTime since)
    {
        using SqliteConnection db = Open();
        using SqliteCommand delete = db.CreateCommand();
        delete.CommandText = "DELETE FROM vaults WHERE accessed < $since";
        delete.Parameters.AddWithValue("$since", since.ToString("O"));
        return delete.ExecuteNonQuery();
    }

    public StoredVault? Get(string subject)
    {
        using SqliteConnection db = Open();

        // Reading counts as use too: a device that only ever fetches must not find its store gone.
        DateTime now = DateTime.UtcNow;
        using SqliteCommand touch = db.CreateCommand();
        touch.CommandText = "UPDATE vaults SET accessed = $now WHERE subject = $subject AND accessed < $noted";
        touch.Parameters.AddWithValue("$subject", subject);
        touch.Parameters.AddWithValue("$now", now.ToString("O"));
        touch.Parameters.AddWithValue("$noted", (now - AccessNoted).ToString("O"));
        touch.ExecuteNonQuery();

        using SqliteCommand read = db.CreateCommand();
        read.CommandText = "SELECT version, keyring, data FROM vaults WHERE subject = $subject";
        read.Parameters.AddWithValue("$subject", subject);
        using SqliteDataReader row = read.ExecuteReader();
        return row.Read() ? new StoredVault { Version = row.GetInt64(0), Keyring = row.GetString(1), Data = row.GetString(2) } : null;
    }

    /// <summary>Stores a new version if the expected one is still current. Returns the new version, or null on a conflict.</summary>
    public long? Put(string subject, VaultUpload upload)
    {
        using SqliteConnection db = Open();
        using SqliteCommand write = db.CreateCommand();
        write.Parameters.AddWithValue("$subject", subject);
        write.Parameters.AddWithValue("$keyring", upload.Keyring);
        write.Parameters.AddWithValue("$data", upload.Data);
        write.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        write.Parameters.AddWithValue("$expected", upload.ExpectedVersion);

        write.CommandText = upload.ExpectedVersion == 0
            ? "INSERT OR IGNORE INTO vaults (subject, version, keyring, data, updated, accessed) VALUES ($subject, 1, $keyring, $data, $updated, $updated)"
            : "UPDATE vaults SET version = version + 1, keyring = $keyring, data = $data, updated = $updated, accessed = $updated WHERE subject = $subject AND version = $expected";
        return write.ExecuteNonQuery() == 0 ? null : upload.ExpectedVersion + 1;
    }

    public bool Delete(string subject)
    {
        using SqliteConnection db = Open();
        using SqliteCommand delete = db.CreateCommand();
        delete.CommandText = "DELETE FROM vaults WHERE subject = $subject";
        delete.Parameters.AddWithValue("$subject", subject);
        return delete.ExecuteNonQuery() > 0;
    }
}

/// <summary>
/// The sync server. It keeps one encrypted copy of each user's library and never sees a key: encryption and
/// merging happen on the devices. Anyone can run their own.
/// </summary>
public static class SyncServerApp
{
    private const int RequestsPerMinute = 120;
    private const string JoinLimit = "share-join";
    private const string PairLimit = "pair";
    private const int PairTriesPerMinute = 10;
    private const int PairingOffersPerDay = 30;
    private const string UploadLimit = "upload";

    // An upload may be megabytes, held in memory while it is read. These keep that memory bounded: so many at a
    // time from one address, so many for everyone together, and none that takes for ever to arrive.
    private const int UploadsAtOncePerAddress = 4;
    private const int UploadsAtOnce = 16;
    private static readonly TimeSpan UploadTime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UploadWait = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan IdleSweep = TimeSpan.FromHours(1);

    // Sent often enough that a proxy in front does not take a quiet terminal for a dead connection.
    private static readonly TimeSpan WebSocketKeepAlive = TimeSpan.FromSeconds(30);

    /// <summary>
    /// What a request is counted under: its address, or for IPv6 the /64 the address is in. One subscriber is handed
    /// at least that many IPv6 addresses, so counting each of them by itself would limit nothing.
    /// </summary>
    internal static string AddressKey(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;
        if (address is null) return "unknown";

        // The same IPv4 caller may show up in either form, depending on how the server listens.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    /// <summary>
    /// Turns away requests a web page made. Elio never sends an Origin header and a browser always does for these,
    /// so this keeps a page someone merely visits from using their address, or their place on a private network,
    /// to reach the server.
    /// </summary>
    private static TBuilder NotFromBrowsers<TBuilder>(this TBuilder route)
        where TBuilder : IEndpointConventionBuilder => route.AddEndpointFilter(async (context, next) =>
        context.HttpContext.Request.Headers.ContainsKey("Origin") ? Results.StatusCode(StatusCodes.Status403Forbidden) : await next(context));

    /// <summary>
    /// Lets a device that syncs with a key of its own pass that key to another device by a short code, without the
    /// user copying sixty-eight characters by hand. Nothing here needs, or learns, who anyone is.
    /// </summary>
    private static void MapPairing(WebApplication app)
    {
        var shelf = new PairingShelf();
        var offers = new DailyCount(PairingOffersPerDay);

        app.MapPost("/v1/pair/{name}", async (HttpContext context, string name) =>
        {
            if (!PairingShelf.IsName(name)) return Results.BadRequest();
            if (!offers.Allow(AddressKey(context))) return Results.StatusCode(StatusCodes.Status429TooManyRequests);

            using var reader = new StreamReader(context.Request.Body);
            char[] buffer = new char[PairingShelf.MaxBytes + 1];
            int read = await reader.ReadBlockAsync(buffer, context.RequestAborted);
            if (read is 0 or > PairingShelf.MaxBytes) return Results.BadRequest();

            return shelf.Put(name, new string(buffer, 0, read)) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }).NotFromBrowsers();

        app.MapGet("/v1/pair/{name}", (string name) =>
            PairingShelf.IsName(name) && shelf.Take(name) is string data ? Results.Text(data) : Results.NotFound()).RequireRateLimiting(PairLimit).NotFromBrowsers();
    }

    public static WebApplication Build(string[] args, Action<SyncServerOptions>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
        var options = builder.Configuration.GetSection("Sync").Get<SyncServerOptions>() ?? new SyncServerOptions();
        configure?.Invoke(options);
        if (!Uri.TryCreate(options.PublicUrl, UriKind.Absolute, out Uri? publicUrl))
            throw new InvalidOperationException("Sync:PublicUrl must be set to the address clients use, such as https://sync.example.com.");
        options.PublicUrl = publicUrl.GetLeftPart(UriPartial.Authority).ToLowerInvariant();

        builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ServerJson.Default));
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = (options.MaxBytes * 2L) + 65536);
        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
                AddressKey(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = RequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

            // Fetching by a pairing code is counted the same way, found or not, so that codes cannot be tried one by one.
            limiter.AddPolicy(PairLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                AddressKey(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = PairTriesPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

            // Every try at joining counts, found or not, so that room ids cannot be searched for.
            limiter.AddPolicy(JoinLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                AddressKey(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.ShareJoinsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

            // A few devices behind one address may well sync at the same moment, so the ones over the limit wait their turn.
            limiter.AddPolicy(UploadLimit, context => RateLimitPartition.GetConcurrencyLimiter(
                AddressKey(context),
                _ => new ConcurrencyLimiterOptions { PermitLimit = UploadsAtOncePerAddress, QueueLimit = UploadsAtOncePerAddress * 4 }));
        });

        // The line logged for each request has the whole address in it. The address of a viewer's request ends
        // in the room id, and that of a pairing request in the name made from the code. Neither belongs in a log.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

        var identities = new Identities(options, new HttpClient { Timeout = TimeSpan.FromSeconds(15) });
        var store = new VaultStore(options.Database);
        var newAccounts = new DailyCount(options.NewAccountsPerDay);
        var uploads = new SemaphoreSlim(UploadsAtOnce, UploadsAtOnce);

        WebApplication app = builder.Build();
        if (options.BehindProxy)
        {
            // Before the limiter, which counts by address.
            var forwarded = new Microsoft.AspNetCore.Builder.ForwardedHeadersOptions
            {
                ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
                ForwardLimit = 1,
            };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            foreach (string proxy in (options.TrustedProxies ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (proxy.Contains('/') && IPNetwork.TryParse(proxy, out IPNetwork network)) forwarded.KnownIPNetworks.Add(network);
                else if (!proxy.Contains('/') && IPAddress.TryParse(proxy, out IPAddress? address)) forwarded.KnownProxies.Add(address);
                else throw new InvalidOperationException($"Sync:TrustedProxies has \"{proxy}\" in it, which is neither an address such as 10.0.0.5 nor a network such as 172.16.0.0/12.");
            }

            // With nobody named, the header is believed from whoever sends it.
            if (forwarded.KnownIPNetworks.Count == 0 && forwarded.KnownProxies.Count == 0)
            {
                app.Logger.LogWarning(
                    "Sync:BehindProxy is on and Sync:TrustedProxies is empty, so X-Forwarded-For is believed from anyone. "
                    + "If this server can be reached other than through the proxy, a caller can put any address in that header and get around every limit. "
                    + "Set Sync:TrustedProxies to the proxy's address or network.");
            }

            app.UseForwardedHeaders(forwarded);
        }
        else
        {
            // Said once, by the first request that shows it: with a proxy in front and this setting off, every
            // caller looks like the proxy and they all share one allowance.
            int warned = 0;
            app.Use((context, next) =>
            {
                if (context.Request.Headers.ContainsKey("X-Forwarded-For") && Interlocked.Exchange(ref warned, 1) == 0)
                {
                    app.Logger.LogWarning(
                        "A request arrived with an X-Forwarded-For header, but Sync:BehindProxy is off, so the header is ignored and everyone behind the proxy is counted as one caller. "
                        + "If a reverse proxy is in front of this server, set Sync:BehindProxy=true and Sync:TrustedProxies to the proxy's address.");
                }

                return next(context);
            });
        }

        if (options.VaultIdleDays > 0)
        {
            var sweep = new Timer(
                _ =>
                {
                    try
                    {
                        int removed = store.RemoveIdle(DateTime.UtcNow.AddDays(-options.VaultIdleDays));
                        if (removed > 0) app.Logger.LogInformation("Removed {Count} stores that were not used for {Days} days.", removed, options.VaultIdleDays);
                    }
                    catch (SqliteException ex)
                    {
                        // Tried again in an hour. Nothing is lost by a sweep that did not happen.
                        app.Logger.LogWarning(ex, "Could not remove unused stores.");
                    }
                },
                null,
                TimeSpan.FromMinutes(1),
                IdleSweep);
            app.Lifetime.ApplicationStopping.Register(sweep.Dispose);
        }

        app.UseRateLimiter();

        app.MapGet("/healthz", () => Results.Ok("ok"));

        app.MapGet("/v1/vault", async (HttpContext context) =>
            await identities.SubjectAsync(context) is not string subject ? Results.Unauthorized()
            : store.Get(subject) is StoredVault vault ? Results.Json(vault, ServerJson.Default.StoredVault)
            : Results.NotFound());

        app.MapPut("/v1/vault", async (HttpContext context) =>
        {
            // Who is asking comes first. Nothing of the body, which may be megabytes, is read for a stranger.
            if (await identities.SubjectAsync(context) is not string subject) return Results.Unauthorized();
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            if (!await uploads.WaitAsync(UploadWait, context.RequestAborted)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            try
            {
                VaultUpload? upload;
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                limit.CancelAfter(UploadTime);
                try
                {
                    upload = await context.Request.ReadFromJsonAsync(ServerJson.Default.VaultUpload, limit.Token);
                }
                catch (JsonException)
                {
                    return Results.BadRequest();
                }
                catch (BadHttpRequestException ex)
                {
                    // Too large, mostly.
                    return Results.StatusCode(ex.StatusCode);
                }
                catch (IOException)
                {
                    return Results.BadRequest();
                }
                catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
                {
                    return Results.StatusCode(StatusCodes.Status408RequestTimeout);
                }

                if (upload is null || upload.Keyring is null || upload.Data is null
                    || upload.ExpectedVersion < 0 || upload.Keyring.Length == 0 || upload.Keyring.Length + upload.Data.Length > options.MaxBytes * 2L)
                    return Results.BadRequest();

                if (!store.Exists(subject))
                {
                    if (store.Count() >= options.MaxVaults)
                        return Results.Text("This sync server holds as many stores of data as it is set up to keep (Sync:MaxVaults), so a new one cannot be started here.", statusCode: StatusCodes.Status507InsufficientStorage);

                    // Anyone can make up a key, so starting a new store with one is counted against the address it comes from.
                    if (subject.StartsWith(Identities.OwnKeyPrefix, StringComparison.Ordinal) && !newAccounts.Allow(AddressKey(context)))
                        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
                }

                return store.Put(subject, upload) is long version
                    ? Results.Json(new VaultVersion { Version = version }, ServerJson.Default.VaultVersion)
                    : Results.Conflict();
            }
            finally
            {
                uploads.Release();
            }
        }).RequireRateLimiting(UploadLimit);

        app.MapDelete("/v1/vault", async (HttpContext context) =>
            await identities.SubjectAsync(context) is not string subject ? Results.Unauthorized()
            : store.Delete(subject) ? Results.NoContent()
            : Results.NotFound());

        if (options.AccountsWithoutSignIn) MapPairing(app);
        if (options.ShareRelay) MapShareRelay(app, options, identities);

        return app;
    }

    private static void MapShareRelay(WebApplication app, SyncServerOptions options, Identities identities)
    {
        var relay = new ShareRelay(options, app.Logger);

        // Open connections would otherwise keep the server from stopping until it lost patience.
        app.Lifetime.ApplicationStopping.Register(relay.Dispose);
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = WebSocketKeepAlive });

        // The person sharing. Rooms are counted per account, or per address for someone with no account: what is
        // shared is protected by its password either way, so the account is only there to keep the count fair.
        app.MapGet("/v1/share/host", async (HttpContext context) =>
        {
            string? subject = await identities.SubjectAsync(context);
            if (subject is null && options.ShareWithoutAccount && !context.Request.Headers.ContainsKey("Authorization"))
                subject = "address:" + AddressKey(context);

            // A proof that was sent but is not accepted is still refused, so a signed-in app learns its session ended.
            if (subject is null) context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else if (!context.WebSockets.IsWebSocketRequest) context.Response.StatusCode = StatusCodes.Status400BadRequest;
            else await relay.HostAsync(context, subject);
        }).NotFromBrowsers();

        // Someone watching. No account is asked for: what lets a viewer in is the password, which only
        // the two ends know. The room id just says which host to be put through to.
        app.MapGet("/v1/share/join/{id}", async (HttpContext context, string id) =>
        {
            if (!context.WebSockets.IsWebSocketRequest) context.Response.StatusCode = StatusCodes.Status400BadRequest;
            else await relay.JoinAsync(context, id);
        }).RequireRateLimiting(JoinLimit).NotFromBrowsers();
    }
}

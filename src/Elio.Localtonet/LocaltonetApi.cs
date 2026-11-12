using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Elio.Core;

namespace Elio.Localtonet;

public sealed class LoginStart
{
    public string? RequestId { get; set; }
    public string? PollSecret { get; set; }
    public string? Code { get; set; }
    public string? ApproveUrl { get; set; }
    public int ExpiresInSeconds { get; set; }
    public string? Error { get; set; }
}

public sealed class LoginPoll
{
    public const string Pending = "pending";
    public const string Approved = "approved";

    public string Status { get; set; } = "expired";
    public string? Key { get; set; }
    public string? Account { get; set; }
}

/// <summary>What is sent to ask for access keys for every device at once.</summary>
public sealed class KeysRequest
{
    public int Days { get; set; }
}

public sealed class KeysStart
{
    public string? RequestId { get; set; }
    public string? PollSecret { get; set; }
    public string? Code { get; set; }
    public string? ApproveUrl { get; set; }
    public int ExpiresInSeconds { get; set; }
    public string? Error { get; set; }
}

/// <summary>An access key for one device: what opens a shell there without approving it in the browser.</summary>
public sealed class DeviceKey
{
    public Guid DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public string? Key { get; set; }
}

public sealed class KeysPoll
{
    public const string Pending = "pending";
    public const string Approved = "approved";

    public string Status { get; set; } = "expired";
    public DeviceKey[]? Keys { get; set; }
    public int Days { get; set; }

    /// <summary>How many devices no key could be made for, for example because they have as many as they may.</summary>
    public int Skipped { get; set; }
}

public sealed class DeviceMetrics
{
    public double? CpuPercent { get; set; }
    public long MemoryUsedBytes { get; set; }
    public long MemoryTotalBytes { get; set; }
    public long DiskUsedBytes { get; set; }
    public long DiskTotalBytes { get; set; }
}

/// <summary>A computer on the user's Localtonet account.</summary>
public sealed class LocaltonetDevice
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Os { get; set; }
    public string? Architecture { get; set; }
    public string? RunAsUser { get; set; }
    public string? Fingerprint { get; set; }
    public bool Online { get; set; }
    public DateTime? LastConnectedDate { get; set; }
    public DeviceMetrics? Metrics { get; set; }
}

public sealed class DevicesResponse
{
    public LocaltonetDevice[]? Devices { get; set; }
    public string? Error { get; set; }
}

/// <summary>Where to meet a device for one session, as Localtonet hands it out.</summary>
public sealed class DeviceSession
{
    public string Node { get; set; } = string.Empty;
    public int Port { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Ticket { get; set; } = string.Empty;
    public string HostKeyBlob { get; set; } = string.Empty;
}

public sealed class ConnectResponse
{
    public const string Pending = "pending";
    public const string Approved = "approved";

    /// <summary>The access key given does not open this device.</summary>
    public const string Invalid = "invalid";

    public string Status { get; set; } = "denied";
    public string? RequestId { get; set; }
    public string? PollSecret { get; set; }
    public string? ApproveUrl { get; set; }
    public string? Error { get; set; }
    public DeviceSession? Session { get; set; }
}

public sealed class IdentityResponse
{
    public string? Proof { get; set; }
    public int ExpiresInSeconds { get; set; }
    public string? Error { get; set; }
}

public sealed class IdentityVerifyResponse
{
    public string? Subject { get; set; }
}

/// <summary>The account key stopped working: it was removed on the website, or never was valid.</summary>
public sealed class LocaltonetSignedOutException() : Exception(L.T("Your Localtonet session ended. Sign in again."));

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LoginStart))]
[JsonSerializable(typeof(LoginPoll))]
[JsonSerializable(typeof(DevicesResponse))]
[JsonSerializable(typeof(KeysRequest))]
[JsonSerializable(typeof(KeysStart))]
[JsonSerializable(typeof(KeysPoll))]
[JsonSerializable(typeof(ConnectResponse))]
[JsonSerializable(typeof(IdentityResponse))]
[JsonSerializable(typeof(IdentityVerifyResponse))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
internal sealed partial class LocaltonetJson : JsonSerializerContext;

/// <summary>
/// The website's endpoints for Elio. Opening a shell uses the same public flow as `localtonet ssh`:
/// a device key, or the owner approving in the browser. Nothing here can open a shell by itself.
/// </summary>
public sealed class LocaltonetApi(Uri baseUrl, HttpClient? http = null)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = RequestTimeout };

    /// <summary>
    /// localtonet.com, unless ELIO_LOCALTONET_URL points somewhere else. Somewhere else has to be reached over https,
    /// or be this computer itself, because sign-in keys and access keys travel there; anything else is ignored.
    /// </summary>
    public static Uri DefaultUrl =>
        Uri.TryCreate(Environment.GetEnvironmentVariable("ELIO_LOCALTONET_URL"), UriKind.Absolute, out Uri? custom) && IsSafe(custom)
            ? custom
            : new Uri("https://localtonet.com");

    public Uri BaseUrl { get; } = baseUrl;

    private static bool IsSafe(Uri url) => url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback);

    /// <summary>
    /// The page the website asks to have opened in the browser, or null if it should not be. The address comes from
    /// the server's answer, so it is only opened when it is a web page on the same site Elio is talking to: never
    /// another program's link, a file, or a page somewhere else.
    /// </summary>
    public Uri? ApprovalPage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? page)
        && IsSafe(page)
        && (page.Scheme == Uri.UriSchemeHttps || BaseUrl.IsLoopback)
        && IsSameSite(page.IdnHost, BaseUrl.IdnHost)
            ? page
            : null;

    // The site itself or a name under it, such as app.localtonet.com for localtonet.com.
    private static bool IsSameSite(string host, string site)
    {
        if (site.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) site = site[4..];
        return host.Equals(site, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Asks, and asks again if the network fails, until an answer comes or <paramref name="giveUpAt"/> has passed.
    /// </summary>
    /// <remarks>
    /// For the questions asked over and over while the user approves something in the browser. On a phone that
    /// approval happens in another app, and the system may cut this one off from the network while it is not on
    /// screen; one lost question must not end the wait.
    /// </remarks>
    public static async Task<T> PatientlyAsync<T>(Func<CancellationToken, Task<T>> ask, DateTime giveUpAt, CancellationToken cancellation)
    {
        while (true)
        {
            try
            {
                return await ask(cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when ((ex is HttpRequestException || (ex is OperationCanceledException && !cancellation.IsCancellationRequested)) && DateTime.UtcNow < giveUpAt)
            {
                await Task.Delay(RetryDelay, cancellation).ConfigureAwait(false);
            }
        }
    }

    /// <summary>When to stop waiting for an approval the website gave this many seconds to.</summary>
    public static DateTime Deadline(int expiresInSeconds) => DateTime.UtcNow.AddSeconds(expiresInSeconds > 0 ? expiresInSeconds : 600);

    private async Task<T> PostAsync<T>(string path, Dictionary<string, string?> body, JsonTypeInfo<T> type, string? bearer, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUrl, path))
        {
            Content = JsonContent.Create(body, LocaltonetJson.Default.DictionaryStringString),
        };
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(type, cancellation).ConfigureAwait(false)
            ?? throw new InvalidDataException(L.T("Localtonet returned an empty response."));
    }

    public Task<LoginStart> StartLoginAsync(string clientName, CancellationToken cancellation) =>
        PostAsync("commonapi/elio/login/start", new() { ["clientName"] = clientName }, LocaltonetJson.Default.LoginStart, null, cancellation);

    public Task<LoginPoll> PollLoginAsync(LoginStart start, CancellationToken cancellation) =>
        PostAsync("commonapi/elio/login/poll", new() { ["requestId"] = start.RequestId, ["pollSecret"] = start.PollSecret }, LocaltonetJson.Default.LoginPoll, null, cancellation);

    public async Task<IReadOnlyList<LocaltonetDevice>> ListDevicesAsync(string key, CancellationToken cancellation)
    {
        DevicesResponse response = await PostAsync("commonapi/elio/devices", [], LocaltonetJson.Default.DevicesResponse, key, cancellation).ConfigureAwait(false);
        if (response.Error == "signed-out") throw new LocaltonetSignedOutException();
        if (response.Error is not null) throw new InvalidOperationException(response.Error);
        return response.Devices ?? [];
    }

    /// <summary>
    /// Asks for an access key for each of the account's devices. Nothing is made until the owner approves the request
    /// in the browser, with their second factor.
    /// </summary>
    public async Task<KeysStart> StartKeysAsync(string key, int days, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUrl, "commonapi/elio/keys/start"))
        {
            Content = JsonContent.Create(new KeysRequest { Days = days }, LocaltonetJson.Default.KeysRequest),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        KeysStart start = await response.Content.ReadFromJsonAsync(LocaltonetJson.Default.KeysStart, cancellation).ConfigureAwait(false)
            ?? throw new InvalidDataException(L.T("Localtonet returned an empty response."));
        if (start.Error == "signed-out") throw new LocaltonetSignedOutException();
        return start;
    }

    public Task<KeysPoll> PollKeysAsync(KeysStart pending, CancellationToken cancellation) =>
        PostAsync("commonapi/elio/keys/poll", new() { ["requestId"] = pending.RequestId, ["pollSecret"] = pending.PollSecret }, LocaltonetJson.Default.KeysPoll, null, cancellation);

    public Task<ConnectResponse> ConnectAsync(string deviceName, string? accessKey, CancellationToken cancellation) =>
        PostAsync("commonapi/device/connect", new() { ["deviceName"] = deviceName, ["accessKey"] = accessKey }, LocaltonetJson.Default.ConnectResponse, null, cancellation);

    public Task<ConnectResponse> PollConnectAsync(ConnectResponse pending, CancellationToken cancellation) =>
        PostAsync("commonapi/device/connect/poll", new() { ["requestId"] = pending.RequestId, ["pollSecret"] = pending.PollSecret }, LocaltonetJson.Default.ConnectResponse, null, cancellation);

    /// <summary>A short-lived proof of who the user is, usable only at the given sync server.</summary>
    public async Task<IdentityResponse> IssueIdentityAsync(string key, Uri audience, CancellationToken cancellation)
    {
        IdentityResponse response = await PostAsync("commonapi/elio/identity", new() { ["audience"] = audience.ToString() }, LocaltonetJson.Default.IdentityResponse, key, cancellation).ConfigureAwait(false);
        if (response.Error == "signed-out") throw new LocaltonetSignedOutException();
        if (response.Error is not null || response.Proof is null) throw new InvalidOperationException(response.Error ?? L.T("Could not get an identity."));
        return response;
    }

    /// <summary>For a sync server: which account a proof belongs to, or null if it is not valid here.</summary>
    public async Task<string?> VerifyIdentityAsync(string proof, Uri audience, CancellationToken cancellation) =>
        (await PostAsync("commonapi/elio/identity/verify", new() { ["proof"] = proof, ["audience"] = audience.ToString() }, LocaltonetJson.Default.IdentityVerifyResponse, null, cancellation).ConfigureAwait(false)).Subject;
}

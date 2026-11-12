using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elio.Core;

namespace Elio.Sync;

/// <summary>What a sync server stores for one account: the keyring and the sealed document, both opaque to it.</summary>
public sealed class StoredVault
{
    public long Version { get; set; }
    public string Keyring { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
}

public sealed class VaultUpload
{
    /// <summary>The version this upload replaces, 0 when there is nothing yet.</summary>
    public long ExpectedVersion { get; set; }

    public string Keyring { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
}

public sealed class VaultVersion
{
    public long Version { get; set; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(StoredVault))]
[JsonSerializable(typeof(VaultUpload))]
[JsonSerializable(typeof(VaultVersion))]
public sealed partial class SyncWireJson : JsonSerializerContext;

/// <summary>Someone else uploaded first. Fetch, merge and try again.</summary>
public sealed class SyncConflictException() : Exception(L.T("Another device updated the data during sync."));

/// <summary>Talks to a sync server. Proves who the user is with a short-lived proof from Localtonet.</summary>
public sealed class SyncServerClient(Uri server, Func<CancellationToken, Task<string>> proof, HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

    public Uri Server { get; } = server;

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, CancellationToken cancellation)
    {
        var request = new HttpRequestMessage(method, new Uri(Server, "v1/vault"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await proof(cancellation).ConfigureAwait(false));
        return request;
    }

    /// <summary>The stored vault, or null if this account has none yet.</summary>
    public async Task<StoredVault?> GetAsync(CancellationToken cancellation)
    {
        using HttpRequestMessage request = await RequestAsync(HttpMethod.Get, cancellation).ConfigureAwait(false);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(SyncWireJson.Default.StoredVault, cancellation).ConfigureAwait(false);
    }

    /// <exception cref="SyncConflictException">The stored version is no longer the expected one.</exception>
    public async Task<long> PutAsync(VaultUpload upload, CancellationToken cancellation)
    {
        using HttpRequestMessage request = await RequestAsync(HttpMethod.Put, cancellation).ConfigureAwait(false);
        request.Content = JsonContent.Create(upload, SyncWireJson.Default.VaultUpload);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict) throw new SyncConflictException();
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(SyncWireJson.Default.VaultVersion, cancellation).ConfigureAwait(false))!.Version;
    }

    public async Task DeleteAsync(CancellationToken cancellation)
    {
        using HttpRequestMessage request = await RequestAsync(HttpMethod.Delete, cancellation).ConfigureAwait(false);
        using HttpResponseMessage response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
    }
}

/// <summary>How the engine reads and writes the app's own state.</summary>
public interface ISyncLocal
{
    SyncDocument Capture();

    void Apply(SyncDocument merged);

    byte[]? LoadDataKey();

    void SaveDataKey(byte[]? key);
}

public sealed class SyncResult
{
    public bool Uploaded { get; init; }
    public bool Downloaded { get; init; }
    public long Version { get; init; }
}

/// <summary>Turns sync on, unlocks it on another device, and keeps the copies in step.</summary>
public sealed class SyncEngine(SyncServerClient server, ISyncLocal local)
{
    private const int MaxAttempts = 5;

    public bool IsUnlocked => local.LoadDataKey() is not null;

    /// <summary>Whether this account has anything stored yet.</summary>
    public async Task<bool> ExistsAsync(CancellationToken cancellation) => await server.GetAsync(cancellation).ConfigureAwait(false) is not null;

    /// <summary>Starts sync for an account with nothing stored yet. Returns the recovery key, to be shown once.</summary>
    public async Task<string> CreateAsync(string passphrase, CancellationToken cancellation)
    {
        if (await server.GetAsync(cancellation).ConfigureAwait(false) is not null)
            throw new InvalidOperationException(L.T("This account already has synced data. Open it with your password."));

        byte[] dataKey = SyncCrypto.NewDataKey();
        var (keyring, recovery) = SyncCrypto.CreateKeyring(dataKey, passphrase);
        await server.PutAsync(new VaultUpload
        {
            ExpectedVersion = 0,
            Keyring = JsonSerializer.Serialize(keyring, SyncJson.Default.Keyring),
            Data = Convert.ToBase64String(SyncCrypto.Seal(dataKey, local.Capture())),
        }, cancellation).ConfigureAwait(false);

        local.SaveDataKey(dataKey);
        return recovery;
    }

    /// <summary>Opens the stored copy with the passphrase or the recovery key, and remembers the key on this device.</summary>
    public async Task UnlockAsync(string secret, CancellationToken cancellation)
    {
        StoredVault stored = await server.GetAsync(cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException(L.T("This account has no synced data."));
        Keyring keyring = JsonSerializer.Deserialize(stored.Keyring, SyncJson.Default.Keyring)!;
        local.SaveDataKey(await Task.Run(() => SyncCrypto.Unlock(keyring, secret), cancellation).ConfigureAwait(false));
    }

    public async Task ChangePassphraseAsync(string newPassphrase, CancellationToken cancellation)
    {
        byte[] dataKey = local.LoadDataKey() ?? throw new InvalidOperationException(L.T("Sync is locked."));
        for (int attempt = 1; ; attempt++)
        {
            StoredVault stored = await server.GetAsync(cancellation).ConfigureAwait(false) ?? throw new InvalidOperationException(L.T("This account has no synced data."));
            Keyring keyring = JsonSerializer.Deserialize(stored.Keyring, SyncJson.Default.Keyring)!;
            Keyring changed = await Task.Run(() => SyncCrypto.ChangePassphrase(keyring, dataKey, newPassphrase), cancellation).ConfigureAwait(false);
            try
            {
                await server.PutAsync(new VaultUpload { ExpectedVersion = stored.Version, Keyring = JsonSerializer.Serialize(changed, SyncJson.Default.Keyring), Data = stored.Data }, cancellation).ConfigureAwait(false);
                return;
            }
            catch (SyncConflictException) when (attempt < MaxAttempts)
            {
                // Someone synced in between. Do it again on top of theirs.
            }
        }
    }

    /// <summary>Downloads, merges with what is here, applies the result here, and uploads it if it differs.</summary>
    public async Task<SyncResult> SyncAsync(CancellationToken cancellation)
    {
        byte[] dataKey = local.LoadDataKey() ?? throw new InvalidOperationException(L.T("Sync is locked."));

        for (int attempt = 1; ; attempt++)
        {
            StoredVault stored = await server.GetAsync(cancellation).ConfigureAwait(false)
                ?? throw new InvalidOperationException(L.T("The sync data on the server was deleted."));

            SyncDocument remote = SyncCrypto.Open(dataKey, Convert.FromBase64String(stored.Data));
            SyncDocument merged = SyncMerge.Merge(local.Capture(), remote, DateTime.UtcNow);

            string mergedText = JsonSerializer.Serialize(merged, SyncJson.Default.SyncDocument);
            bool differsFromRemote = mergedText != JsonSerializer.Serialize(SyncMerge.Merge(remote, remote, DateTime.UtcNow), SyncJson.Default.SyncDocument);
            local.Apply(merged);

            if (!differsFromRemote) return new SyncResult { Downloaded = true, Version = stored.Version };

            try
            {
                long version = await server.PutAsync(new VaultUpload
                {
                    ExpectedVersion = stored.Version,
                    Keyring = stored.Keyring,
                    Data = Convert.ToBase64String(SyncCrypto.Seal(dataKey, merged)),
                }, cancellation).ConfigureAwait(false);
                return new SyncResult { Downloaded = true, Uploaded = true, Version = version };
            }
            catch (SyncConflictException) when (attempt < MaxAttempts)
            {
                // Someone uploaded in between. Merge with theirs and try again.
            }
        }
    }

    /// <summary>Stops syncing on this device. What is stored stays for the other devices.</summary>
    public void Forget() => local.SaveDataKey(null);
}

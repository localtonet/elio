using System.Text.Json;
using Avalonia.Threading;
using Elio.Core;
using Elio.Localtonet;
using Elio.Sync;

namespace Elio.UI;

/// <summary>Keeps this device's library in step with the encrypted copy on the sync server.</summary>
public sealed class SyncCoordinator
{
    private const string DataKeyName = "sync-key";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProofMargin = TimeSpan.FromMinutes(2);

    // Where Localtonet runs its copy of the open-source server. Anyone can point Elio at their own.
    public static string DefaultServer => Environment.GetEnvironmentVariable("ELIO_SYNC_URL") is { Length: > 0 } custom ? custom : "https://sync.elio.sh";

    private readonly DispatcherTimer _settle = new() { Interval = Settle };
    private bool _applying;
    private bool _running;
    private bool _again;
    private (string Proof, DateTime Until, Uri Server)? _proof;

    public SyncCoordinator() => _settle.Tick += (_, _) =>
    {
        _settle.Stop();
        _ = SyncNowAsync();
    };

    public Uri? Server => AppServices.Settings.SyncServer is { Length: > 0 } text && Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) ? uri : null;

    public bool HasKey => AppServices.Vault.GetLocal(DataKeyName) is not null;

    /// <summary>The key the synced data is locked with, for handing to a device being added. Null while sync is off or locked.</summary>
    public byte[]? DataKey => AppServices.Vault.GetLocal(DataKeyName) is { } text ? Convert.FromBase64String(text) : null;

    /// <summary>Takes the key from a device that already syncs, which is what unlocking with the password would have produced.</summary>
    public void SetDataKey(byte[] key) => AppServices.Vault.SetLocal(DataKeyName, Convert.ToBase64String(key));

    /// <summary>On, unlocked, and with an account to prove who the user is.</summary>
    private const string AccountKeyName = "sync-account";

    /// <summary>
    /// The key that says which store on the sync server is this user's, when they sync without an account. It is made up
    /// on the first device and typed into the others, where it is called the sync code. Null when a Localtonet account
    /// says who the user is instead.
    /// </summary>
    public string? AccountKey => AppServices.Vault.GetLocal(AccountKeyName);

    public void SetAccountKey(string? key) => AppServices.Vault.SetLocal(AccountKeyName, key);

    /// <summary>A new key of the form the server takes: "esk_" and 256 random bits.</summary>
    public static string NewAccountKey() => "esk_" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    /// <summary>Whether text is a sync code, however it was pasted.</summary>
    public static bool IsAccountKey(string? text) =>
        text is { Length: 68 } && text.StartsWith("esk_", StringComparison.Ordinal) && text.AsSpan(4).IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>On, unlocked, and with something to say whose data it is: a sync code, or an account.</summary>
    public bool IsOn => Server is not null && HasKey && (AccountKey is not null || AppServices.Localtonet.SignedIn);

    public DateTime? LastSync { get; private set; }

    public string Status { get; private set; } = string.Empty;

    public event Action? Changed;

    private void Report(string status)
    {
        Status = status;
        Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    /// <summary>Starts listening for changes. Called once at startup.</summary>
    public void Start()
    {
        AppServices.LibraryChanged += Schedule;
        AppServices.Vault.Changed += Schedule;
        if (IsOn) _ = SyncNowAsync();
    }

    /// <summary>Coming back to the app is a good moment to pick up changes made elsewhere.</summary>
    public void OnActivated()
    {
        if (LastSync is not DateTime last || DateTime.Now - last > ActivationGap) Schedule();
    }

    private static readonly TimeSpan ActivationGap = TimeSpan.FromMinutes(1);

    /// <summary>Syncs a moment after the last of a burst of changes.</summary>
    public void Schedule()
    {
        if (_applying || !IsOn) return;
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>
    /// The server that carries shared terminals: the one chosen for sync, or the usual one when sync was never set up.
    /// Sharing does not need sync to be on, only an account to say who is sharing.
    /// </summary>
    public Uri RelayServer =>
        AppServices.Settings.ShareServer is { Length: > 0 } own && Uri.TryCreate(own, UriKind.Absolute, out Uri? chosen) && chosen.Scheme is "https" or "http" ? chosen
        : Server ?? new Uri(DefaultServer);

    /// <summary>Proves to a server who the user is, without telling it anything else about the account.</summary>
    public async Task<string> ProofForAsync(Uri server, CancellationToken cancellation)
    {
        if (_proof is { } cached && cached.Server == server && cached.Until > DateTime.UtcNow) return cached.Proof;

        string key = AppServices.Localtonet.Key ?? throw new LocaltonetSignedOutException();
        IdentityResponse identity = await AppServices.Localtonet.Api.IssueIdentityAsync(key, server, cancellation);
        _proof = (identity.Proof!, DateTime.UtcNow + TimeSpan.FromSeconds(identity.ExpiresInSeconds) - ProofMargin, server);
        return identity.Proof!;
    }

    public SyncEngine EngineFor(Uri server) => new(new SyncServerClient(server, ProofAsync), new AppLocal(this));

    private async Task<string> ProofAsync(CancellationToken cancellation)
    {
        Uri server = Server ?? throw new InvalidOperationException(L.T("No sync server is set."));

        // A sync code needs nobody's word for it: holding it is the proof.
        return AccountKey ?? await ProofForAsync(server, cancellation);
    }

    public async Task SyncNowAsync()
    {
        if (!IsOn) return;
        if (_running)
        {
            _again = true;
            return;
        }

        _running = true;
        try
        {
            do
            {
                _again = false;
                Report(L.T("Syncing..."));
                await EngineFor(Server!).SyncAsync(CancellationToken.None);
                LastSync = DateTime.Now;
                Report(L.T("Synced {0:HH:mm}", LastSync));
            }
            while (_again);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or InvalidOperationException or LocaltonetSignedOutException or SyncConflictException)
        {
            Report(L.T("Sync failed: {0}", ex.Message));
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>Stops syncing on this device and forgets its key. The stored copy stays for the other devices.</summary>
    public void TurnOff()
    {
        AppServices.Vault.SetLocal(DataKeyName, null);
        AppServices.Settings.SyncServer = null;
        AppServices.SaveSettings();
        Report(string.Empty);
    }

    /// <summary>Reads and writes the app's own state for the engine.</summary>
    private sealed class AppLocal(SyncCoordinator owner) : ISyncLocal
    {
        public SyncDocument Capture() => Dispatcher.UIThread.Invoke(() =>
        {
            HostLibrary library = AppServices.Library;
            var document = new SyncDocument
            {
                Hosts = library.Hosts,
                Groups = library.Groups,
                Snippets = library.Snippets,
                Identities = library.Identities,
                Deleted = library.Deleted,
                KnownHosts = AppServices.KnownHosts.Entries.ToDictionary(p => p.Key, p => p.Value),
            };

            if (AppServices.Settings.SyncSecrets)
            {
                document.Secrets = AppServices.Vault.Entries.ToDictionary(p => p.Key, p => p.Value);
                document.SecretsDeleted = AppServices.Vault.Deleted.ToDictionary(p => p.Key, p => p.Value);
            }

            // Taken on the UI thread, where the library changes, and copied before anything else can touch it.
            return document.Clone();
        });

        public void Apply(SyncDocument merged) => Dispatcher.UIThread.Invoke(() =>
        {
            owner._applying = true;
            try
            {
                HostLibrary library = AppServices.Library;
                library.Hosts.Clear();
                library.Hosts.AddRange(merged.Hosts);
                library.Groups.Clear();
                library.Groups.AddRange(merged.Groups);
                library.Snippets.Clear();
                library.Snippets.AddRange(merged.Snippets);
                library.Identities.Clear();
                library.Identities.AddRange(merged.Identities);
                library.Deleted = merged.Deleted;
                AppServices.SaveLibrary();

                AppServices.KnownHosts.Merge(merged.KnownHosts);
                if (AppServices.Settings.SyncSecrets) AppServices.Vault.Replace(merged.Secrets, merged.SecretsDeleted);
            }
            finally
            {
                owner._applying = false;
            }
        });

        public byte[]? LoadDataKey() => AppServices.Vault.GetLocal(DataKeyName) is { } text ? Convert.FromBase64String(text) : null;

        public void SaveDataKey(byte[]? key) => AppServices.Vault.SetLocal(DataKeyName, key is null ? null : Convert.ToBase64String(key));
    }
}

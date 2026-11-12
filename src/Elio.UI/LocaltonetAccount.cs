using Avalonia.Threading;
using Elio.Core;
using Elio.Localtonet;

namespace Elio.UI;

/// <summary>The Localtonet accounts Elio is signed in to, and the devices on them.</summary>
public sealed class LocaltonetAccount
{
    private const string KeyName = "localtonet-key";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly JsonFileStore<LocaltonetCache> _store = Stores.Localtonet();
    private readonly LocaltonetCache _cache;
    private Dictionary<Guid, LocaltonetDevice> _details = [];

    public LocaltonetAccount() => _cache = _store.Load();

    public LocaltonetApi Api { get; } = new(LocaltonetApi.DefaultUrl);

    private bool _migrated;

    private static string KeyNameFor(string id) => id.Length == 0 ? KeyName : $"{KeyName}:{id}";

    /// <summary>Every account signed in to, the main one first.</summary>
    public IReadOnlyList<LocaltonetLogin> Accounts
    {
        get
        {
            // Before there could be several, the one account was kept as a name in the settings and a key in the vault.
            if (!_migrated)
            {
                _migrated = true;
                if (AppServices.Settings.LocaltonetAccounts.Count == 0 && AppServices.Vault.GetLocal(KeyName) is not null)
                {
                    AppServices.Settings.LocaltonetAccounts.Add(new LocaltonetLogin { Email = AppServices.Settings.LocaltonetAccount });
                    AppServices.SaveSettings();
                }
            }

            return AppServices.Settings.LocaltonetAccounts;
        }
    }

    /// <summary>The key of one account, or null if it has been signed out.</summary>
    public string? KeyOf(LocaltonetLogin account) => AppServices.Vault.GetLocal(KeyNameFor(account.Id));

    /// <summary>The main account's key: the one that says who the user is to the sync server and when sharing.</summary>
    public string? Key => Accounts.Select(KeyOf).FirstOrDefault(key => key is not null);

    public bool SignedIn => Key is not null;

    public string? Account => Accounts.FirstOrDefault(a => KeyOf(a) is not null)?.Email;

    /// <summary>The account most recently signed in to, for what follows a sign-in.</summary>
    public LocaltonetLogin? Latest { get; private set; }

    /// <summary>The devices as connectable entries. Kept on disk, so restored tabs find theirs before the list is refreshed.</summary>
    public IReadOnlyList<HostEntry> Devices => _cache.Devices;

    /// <summary>What Localtonet last said about a device: online or not, its system.</summary>
    public LocaltonetDevice? Details(Guid id) => _details.GetValueOrDefault(id);

    /// <summary>Why the last refresh failed, if it did.</summary>
    public string? Problem { get; private set; }

    public event Action? Changed;

    private void Notify() => Dispatcher.UIThread.Post(() => Changed?.Invoke());

    /// <summary>Opens the approval page and waits there. Returns false if the user gave up or it expired.</summary>
    public async Task<bool> SignInAsync(DialogHost dialogs, Func<Uri, Task> openUrl)
    {
        LoginStart start;
        try
        {
            start = await Api.StartLoginAsync($"Elio ({Environment.MachineName})", CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            await dialogs.ShowMessageAsync(L.T("Could not reach Localtonet"), ex.Message);
            return false;
        }

        if (Api.ApprovalPage(start.ApproveUrl) is not { } url || start.RequestId is null) return false;
        await openUrl(url);

        LoginPoll? result = null;
        DateTime giveUpAt = LocaltonetApi.Deadline(start.ExpiresInSeconds);
        bool finished;
        try
        {
            finished = await dialogs.WaitAsync(
            L.T("Localtonet sign-in"),
            L.T("You should see the same code on the page that opened in your browser:\n\n{0}\n\nOnce you approve, Elio lists your devices. Elio cannot open a shell on the devices with this sign-in; each device needs its own approval."),
            L.T("Reopen page"),
            () => _ = openUrl(url),
            async cancellation =>
            {
                while (true)
                {
                    await Task.Delay(PollInterval, cancellation);
                    result = await LocaltonetApi.PatientlyAsync(c => Api.PollLoginAsync(start, c), giveUpAt, cancellation);
                    if (result.Status != LoginPoll.Pending) return;
                }
            }, code: start.Code);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            await dialogs.ShowMessageAsync(L.T("Could not reach Localtonet"), ex.Message);
            return false;
        }

        if (!finished || result is not { Status: LoginPoll.Approved, Key: { } key })
        {
            if (finished) await dialogs.ShowMessageAsync(L.T("Not signed in"), L.T("The approval expired. Try again."));
            return false;
        }

        // Signing in again to an account already here renews its key rather than listing it twice.
        LocaltonetLogin? account = Accounts.FirstOrDefault(a => string.Equals(a.Email, result.Account, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            account = new LocaltonetLogin { Id = Accounts.Count == 0 ? string.Empty : Guid.NewGuid().ToString("N"), Email = result.Account };
            AppServices.Settings.LocaltonetAccounts.Add(account);
        }

        AppServices.Vault.SetLocal(KeyNameFor(account.Id), key);
        AppServices.Settings.LocaltonetAccount = Account;
        AppServices.SaveSettings();
        Latest = account;
        await RefreshAsync();
        return true;
    }

    /// <summary>
    /// Offers to fetch an access key for every device, so that connecting stops asking for an approval each time.
    /// The user picks how long the keys last and approves the request once in the browser.
    /// </summary>
    /// <returns>How many keys were saved, or null if nothing was done.</returns>
    public async Task<int?> ImportKeysAsync(DialogHost dialogs, Func<Uri, Task> openUrl, LocaltonetLogin? account = null)
    {
        // Each account approves for its own devices, so with several this is done once for each.
        if (account is null && Accounts.Count(a => KeyOf(a) is not null) > 1)
        {
            int total = 0;
            foreach (LocaltonetLogin each in Accounts.Where(a => KeyOf(a) is not null).ToList())
            {
                if (!await dialogs.ConfirmAsync(L.T("Access keys"), L.T("Next: {0}. The browser has to be signed in to that account to approve.", each.Email ?? string.Empty), L.T("Continue"))) continue;
                total += await ImportKeysAsync(dialogs, openUrl, each) ?? 0;
            }

            return total;
        }

        if ((account is null ? Key : KeyOf(account)) is not { } key) return null;
        if (!AppServices.Vault.IsAvailable)
        {
            await dialogs.ShowMessageAsync(L.T("Access keys"), L.T("There is nowhere safe to keep the keys on this device."));
            return null;
        }

        const int Year = 365;
        int days = 0;
        var choices = new List<PaletteItem>
        {
            new(L.T("1 year"), null, () => days = Year),
            new(L.T("2 years"), null, () => days = 2 * Year),
            new(L.T("5 years"), null, () => days = 5 * Year),
            new(L.T("10 years"), null, () => days = 10 * Year),
        };
        if (await dialogs.ChooseAsync(choices, L.T("How long should the access keys last?")) is not PaletteItem chosen) return null;
        chosen.Run();

        KeysStart start;
        try
        {
            start = await Api.StartKeysAsync(key, days, CancellationToken.None);
        }
        catch (LocaltonetSignedOutException ex)
        {
            await dialogs.ShowMessageAsync(L.T("Access keys"), ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            await dialogs.ShowMessageAsync(L.T("Could not reach Localtonet"), ex.Message);
            return null;
        }

        if (Api.ApprovalPage(start.ApproveUrl) is not { } url || start.RequestId is null) return null;
        await openUrl(url);

        KeysPoll? result = null;
        DateTime giveUpAt = LocaltonetApi.Deadline(start.ExpiresInSeconds);
        bool finished;
        try
        {
            finished = await dialogs.WaitAsync(
                L.T("Access keys"),
                L.T("You should see the same code on the page that opened in your browser:\n\n{0}\n\nOnce you approve, Localtonet makes an access key for each of your devices and Elio keeps them in its vault on this computer."),
                L.T("Reopen page"),
                () => _ = openUrl(url),
                async cancellation =>
                {
                    while (true)
                    {
                        await Task.Delay(PollInterval, cancellation);
                        result = await LocaltonetApi.PatientlyAsync(c => Api.PollKeysAsync(start, c), giveUpAt, cancellation);
                        if (result.Status != KeysPoll.Pending) return;
                    }
                }, code: start.Code);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            await dialogs.ShowMessageAsync(L.T("Could not reach Localtonet"), ex.Message);
            return null;
        }

        if (!finished || result is not { Status: KeysPoll.Approved })
        {
            if (finished) await dialogs.ShowMessageAsync(L.T("Access keys"), L.T("The approval expired. Try again."));
            return null;
        }

        int saved = 0;
        foreach (DeviceKey device in result.Keys ?? [])
        {
            if (string.IsNullOrEmpty(device.Key)) continue;
            AppServices.Vault.Set(device.DeviceId, device.Key);
            saved++;
        }

        await RefreshAsync();
        string summary = result.Skipped > 0
            ? L.T("Access keys saved for {0} devices. {1} devices already have as many keys as they may.", saved, result.Skipped)
            : L.T("Access keys saved for {0} devices. Connecting to them no longer asks for approval.", saved);
        await dialogs.ShowMessageAsync(L.T("Access keys"), summary);
        return saved;
    }

    /// <summary>Signs out of every account.</summary>
    public void SignOut()
    {
        foreach (LocaltonetLogin account in Accounts) AppServices.Vault.SetLocal(KeyNameFor(account.Id), null);
        AppServices.Settings.LocaltonetAccounts.Clear();
        AppServices.Settings.LocaltonetAccount = null;
        AppServices.SaveSettings();
        _cache.Devices.Clear();
        _details = [];
        _store.Save(_cache);
        Problem = null;
        Notify();
    }

    /// <summary>Signs out of one account and forgets its devices. The others stay as they are.</summary>
    public void SignOut(LocaltonetLogin account)
    {
        AppServices.Vault.SetLocal(KeyNameFor(account.Id), null);
        AppServices.Settings.LocaltonetAccounts.Remove(account);
        AppServices.Settings.LocaltonetAccount = Account;
        AppServices.SaveSettings();

        foreach (HostEntry device in _cache.Devices.Where(d => (d.LocaltonetAccountId ?? string.Empty) == account.Id).ToList())
        {
            _cache.Devices.Remove(device);
            _details.Remove(device.Id);
        }

        _store.Save(_cache);
        Notify();
    }

    /// <summary>The devices of one account.</summary>
    public IEnumerable<HostEntry> DevicesOf(LocaltonetLogin account) => _cache.Devices.Where(d => (d.LocaltonetAccountId ?? string.Empty) == account.Id);

    /// <summary>Asks Localtonet for each account's current device list.</summary>
    public async Task RefreshAsync()
    {
        var entries = new List<HostEntry>();
        var details = new Dictionary<Guid, LocaltonetDevice>();
        string? problem = null;

        foreach (LocaltonetLogin account in Accounts.ToList())
        {
            if (KeyOf(account) is not { } key) continue;

            try
            {
                IReadOnlyList<LocaltonetDevice> devices = await Api.ListDevicesAsync(key, CancellationToken.None);

                // The entries are rebuilt from the list, but keep their ids so saved access keys and tabs still match.
                foreach (LocaltonetDevice device in devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    details[device.Id] = device;
                    entries.Add(new HostEntry
                    {
                        Id = device.Id,
                        Name = device.Name,
                        Address = device.Name,
                        User = device.RunAsUser ?? "device",
                        Localtonet = true,
                        LocaltonetAccountId = account.Id,

                        // Without an access key every new connection is approved in the browser, which shouldn't pop up by itself.
                        AutoReconnect = AppServices.Vault.Get(device.Id) is not null,
                    });
                }
            }
            catch (LocaltonetSignedOutException ex)
            {
                AppServices.Vault.SetLocal(KeyNameFor(account.Id), null);
                AppServices.Settings.LocaltonetAccounts.Remove(account);
                AppServices.Settings.LocaltonetAccount = Account;
                AppServices.SaveSettings();
                problem = ex.Message;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or InvalidOperationException)
            {
                // What was known about this account's devices is kept until it can be asked again.
                foreach (HostEntry known in DevicesOf(account).ToList())
                {
                    entries.Add(known);
                    if (_details.TryGetValue(known.Id, out LocaltonetDevice? old)) details[known.Id] = old;
                }

                problem = L.T("Could not get the list: {0}", ex.Message);
            }
        }

        _cache.Devices.Clear();
        _cache.Devices.AddRange(entries);
        _details = details;
        _store.Save(_cache);
        Problem = problem;
        Notify();
    }
}

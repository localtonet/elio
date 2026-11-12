using Avalonia.Controls;
using Avalonia.Input.Platform;
using Elio.Connections;
using Elio.Core;

namespace Elio.UI;

/// <content>The keychain: the identities servers share, making a key, and putting one on servers.</content>
public partial class MainView
{
    /// <summary>Opens the keychain and keeps it open until the user closes it, doing what they pick in between.</summary>
    public async Task ShowKeychainAsync()
    {
        HostLibrary library = AppServices.Library;

        // Only one dialog shows at a time, so the list steps aside for each form and comes back after it.
        while (true)
        {
            KeychainChoice choice = await Dialogs.ShowKeychainAsync(library.Identities, UsedBy);
            switch (choice)
            {
                case { Action: KeychainAction.Add }:
                    await EditIdentityAsync(null);
                    break;
                case { Action: KeychainAction.Generate }:
                    await GenerateKeyAsync();
                    break;
                case { Action: KeychainAction.Edit, Identity: Identity identity }:
                    await EditIdentityAsync(identity);
                    break;
                case { Action: KeychainAction.Delete, Identity: Identity identity }:
                    await DeleteIdentityAsync(identity);
                    break;
                case { Action: KeychainAction.ShowPublicKey, Identity: Identity identity }:
                    if (await Dialogs.ShowPublicKeyAsync(identity, CopyTextAsync)) await InstallKeyAsync(identity);
                    break;
                case { Action: KeychainAction.Install, Identity: Identity identity }:
                    await InstallKeyAsync(identity);
                    break;
                default:
                    return;
            }
        }
    }

    private static int UsedBy(Identity identity) =>
        AppServices.Library.Hosts.Count(h => h.IdentityId == identity.Id) + AppServices.Library.Groups.Count(g => g.IdentityId == identity.Id);

    private async Task CopyTextAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    // An identity is nothing without its secret, and a vault the user declined to open takes nothing in. So the
    // vault is opened first, and the keychain says so if it stays shut. Windows Hello shows its own prompt and
    // waits for it, which is why this is kept off the thread the window lives on.
    private async Task<bool> VaultOpenAsync()
    {
        if (!AppServices.Vault.IsLocked || await Task.Run(AppServices.Vault.TryUnlock)) return true;

        await Dialogs.ShowMessageAsync(L.T("Keychain"), L.T("The vault is locked, so the keychain cannot be read or changed."));
        return false;
    }

    private async Task EditIdentityAsync(Identity? existing)
    {
        if (!await VaultOpenAsync()) return;

        IdentitySecret? secret = existing is null ? null : IdentitySecret.Read(AppServices.Vault.Get(existing.Id));
        if (await Dialogs.EditIdentityAsync(existing, secret) is IdentityEdit edit) SaveIdentity(edit.Identity, edit.Secret);
    }

    private static void SaveIdentity(Identity identity, IdentitySecret secret)
    {
        List<Identity> identities = AppServices.Library.Identities;
        identity.ModifiedUtc = DateTime.UtcNow;

        int at = identities.FindIndex(i => i.Id == identity.Id);
        if (at >= 0) identities[at] = identity;
        else identities.Add(identity);

        if (secret.IsEmpty) AppServices.Vault.Remove(identity.Id);
        else AppServices.Vault.Set(identity.Id, secret.Write());
        AppServices.SaveLibrary();
    }

    private async Task DeleteIdentityAsync(Identity identity)
    {
        HostLibrary library = AppServices.Library;
        int users = UsedBy(identity);
        string message = users == 0
            ? L.T("\"{0}\" and its saved password or key will be deleted.", identity.Name)
            : L.T("\"{0}\" is used by {1} servers and groups. They go back to their own user name, key and password.", identity.Name, users);
        if (!await Dialogs.ConfirmAsync(L.T("Delete identity"), message, L.T("Delete"), danger: true)) return;

        DateTime now = DateTime.UtcNow;
        foreach (HostEntry host in library.Hosts.Where(h => h.IdentityId == identity.Id))
        {
            // The user name is not a secret, and a server that had it from the identity alone would be left
            // with nobody to log in as.
            if (string.IsNullOrWhiteSpace(host.User)) host.User = identity.User;
            host.IdentityId = null;
            host.ModifiedUtc = now;
        }

        foreach (HostGroup group in library.Groups.Where(g => g.IdentityId == identity.Id))
        {
            if (string.IsNullOrWhiteSpace(group.User) && identity.User.Length > 0) group.User = identity.User;
            group.IdentityId = null;
            group.ModifiedUtc = now;
        }

        // By id: a sync that ran while the question was up has put other objects in the list.
        library.Identities.RemoveAll(i => i.Id == identity.Id);
        library.Deleted[identity.Id] = now;
        AppServices.Vault.Remove(identity.Id);
        AppServices.SaveLibrary();
    }

    private async Task GenerateKeyAsync()
    {
        if (!await VaultOpenAsync()) return;
        if (await Dialogs.AskNewKeyAsync() is not KeyRequest request) return;

        // An RSA key of this size takes a few seconds to find, so it is made off the window's thread, behind a
        // dialog that can be cancelled.
        GeneratedKey? made = null;
        bool finished = await Dialogs.WaitAsync(L.T("Generate key"), L.T("Making the key..."), null, null, _ =>
        {
            made = SshKeys.Generate(request.Type, request.Name, request.Passphrase);
            return Task.CompletedTask;
        });
        if (!finished || made is null) return;

        var identity = new Identity { Name = request.Name, User = request.User, Kind = IdentityKind.Key, PublicKey = made.PublicKey };
        SaveIdentity(identity, new IdentitySecret { PrivateKey = made.PrivateKey, Passphrase = request.Passphrase });

        if (await Dialogs.ShowPublicKeyAsync(identity, CopyTextAsync)) await InstallKeyAsync(identity);
    }

    private Task InstallKeyAsync(Identity identity)
    {
        // A Localtonet device is reached with an access key and has no account of the user's to put a key into.
        List<HostEntry> servers = HostList.Hosts.Where(h => IsSsh(h) && !h.Localtonet).OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return Dialogs.InstallKeyAsync(identity, servers, (host, cancellation) => InstallKeyOnAsync(identity, host, cancellation), switched => UseIdentityOn(identity, switched));
    }

    /// <summary>
    /// Adds an identity's public key to one server's authorized_keys. The login is the server's own as it is today,
    /// through the same path a command run on many servers takes: a host key that is not known is never accepted here.
    /// </summary>
    private async Task<KeyInstallResult> InstallKeyOnAsync(Identity identity, HostEntry host, CancellationToken cancellation)
    {
        string command;
        try
        {
            command = SshKeys.InstallCommand(identity.PublicKey ?? string.Empty);
        }
        catch (ArgumentException)
        {
            // A public key line that came in by sync or from a file and is not a plain key is not sent anywhere.
            return new KeyInstallResult(false, L.T("This identity's public key is not one that can be installed."), false);
        }

        HostLibrary library = AppServices.Library;
        string account = ConnectionResolver.Resolve(host, library).User;
        bool fits = ConnectionResolver.CanUseIdentity(host, identity, library);

        CommandResult result = await Workspace.RunOnHostAsync(host, command, cancellation);
        if (result.Failure is not null) return new KeyInstallResult(false, result.Failure, false);

        bool added = result.Output.Contains(SshKeys.InstallAdded, StringComparison.Ordinal);
        if (!added && !result.Output.Contains(SshKeys.InstallPresent, StringComparison.Ordinal))
        {
            string said = (result.Error.Trim() is { Length: > 0 } error ? error : result.Output.Trim()) is { Length: > 0 } text ? text : $"exit {result.ExitCode}";
            return new KeyInstallResult(false, L.T("The server did not add the key: {0}", said), false);
        }

        string message = added ? L.T("Key added for {0}.", account) : L.T("The key was already there for {0}.", account);
        if (ConnectionResolver.Resolve(host, library).Identity?.Id == identity.Id) return new KeyInstallResult(true, message, false);
        if (!fits) message += " " + L.T("This identity logs in as {0}, so the server keeps its own login.", identity.User);
        return new KeyInstallResult(true, message, fits);
    }

    private static void UseIdentityOn(Identity identity, IReadOnlyList<HostEntry> servers)
    {
        DateTime now = DateTime.UtcNow;
        foreach (HostEntry asked in servers)
        {
            // Found again by id: installing takes a while, and a sync in between replaces the list's entries.
            if (AppServices.Library.Hosts.FirstOrDefault(h => h.Id == asked.Id) is HostEntry host && ConnectionResolver.UseIdentity(host, identity, AppServices.Library)) host.ModifiedUtc = now;
        }

        AppServices.SaveLibrary();
    }
}

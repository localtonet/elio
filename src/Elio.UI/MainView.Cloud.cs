using Elio.Connections;
using Elio.Core;

namespace Elio.UI;

/// <content>
/// The imports that go through a provider's own command-line tool (Azure, Google Cloud) or a server's own API
/// (Proxmox VE), and the Docker containers of a saved server.
/// </content>
public partial class MainView
{
    // Where the vault keeps a Proxmox server's address, token and the certificate the user accepted, for those who
    // asked it to. They are this device's own and never synced.
    private const string ProxmoxVaultName = "import.proxmox";

    // Listing the containers is one quick command. A server that takes longer has stopped answering.
    private static readonly TimeSpan RemoteDockerTimeout = TimeSpan.FromSeconds(30);

    // The command-line tools of the cloud providers only run on a computer.
    private static bool CanRunCloudTools => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>
    /// Opens an unsaved copy of a saved server in a new tab, given the original and then the copy, so that the copy
    /// can log in with what is saved for the original: its password and its proxy's are filed under the original's id.
    /// Without it the copy opens as any server does, and asks for a password the original has saved.
    /// </summary>
    public Func<HostEntry, HostEntry, Task>? OpenServerCopy { get; set; }

    // These two hold no credentials of their own: the login is the tool's, so nothing goes into the vault.
    private Task ImportFromAzureAsync() =>
        RunCloudImportAsync(AzureImport.Provider, string.Empty, null, null, cancellation => AzureImport.ListAsync(null, cancellation));

    private Task ImportFromGoogleCloudAsync() =>
        RunCloudImportAsync(GoogleCloudImport.Provider, string.Empty, null, null, cancellation => GoogleCloudImport.ListAsync(null, cancellation));

    /// <summary>
    /// Asks for a Proxmox VE server and an API token, and imports the machines it knows an address for. A certificate
    /// the system does not trust is shown to the user first, and only on their yes is that one certificate accepted.
    /// </summary>
    private async Task ImportFromProxmoxAsync()
    {
        const string Title = ProxmoxImport.Provider;
        Vault vault = AppServices.Vault;
        string? saved = vault.IsAvailable ? vault.GetLocal(ProxmoxVaultName) : null;

        // Kept as one secret of four lines: address, token ID, secret, and the fingerprint of the certificate the
        // user accepted, empty where the system trusts the server's certificate by itself.
        string[] known = (saved ?? string.Empty).Split('\n');
        string? Known(int index) => index < known.Length && known[index].Length > 0 ? known[index] : null;

        CredentialAnswer? answer = await Dialogs.AskCredentialsAsync(
            Title,
            L.T("An API token that may read the machines is enough. It is sent to this server and nowhere else, only once its certificate is accepted, and is only kept if you tick the box."),
            [
                new CredentialField(L.T("Address"), Secret: false, Known(0), Placeholder: "https://pve.example.com:8006"),
                new CredentialField(L.T("API token ID"), Secret: false, Known(1), Placeholder: "root@pam!elio"),
                new CredentialField(L.T("Secret"), Secret: true, Known(2)),
            ],
            vault.IsAvailable,
            remembered: saved is not null);
        if (answer is null) return;

        ProxmoxCredentials credentials;
        try
        {
            credentials = ProxmoxImport.Credentials(answer.Values[0], answer.Values[1], answer.Values[2]);
        }
        catch (CloudImportException ex)
        {
            await Dialogs.ShowMessageAsync(Title, ex.Message);
            return;
        }

        // Unticking the box is how saved credentials are forgotten, the accepted certificate with them, whether or
        // not the import then works. So the certificate is asked about again.
        if (!answer.Remember && saved is not null) vault.SetLocal(ProxmoxVaultName, null);

        // A certificate accepted before counts for the server it was accepted for, and for no other.
        string? pin = answer.Remember && Known(0) is string before && ProxmoxImport.ServerAddress(before) is Uri server && server == credentials.Server ? Known(3) : null;

        ProxmoxListing? found = null;
        bool asked = false;
        while (true)
        {
            string? trusted = pin;
            try
            {
                if (!await Dialogs.WaitAsync(Title, L.T("Asking {0} for its servers…", Title), null, null, async cancellation => found = await ProxmoxImport.ListAsync(credentials, trusted, null, cancellation))) return;
                break;
            }
            catch (UntrustedCertificateException ex) when (!asked)
            {
                // Nothing was sent: the connection stopped at the certificate. On a yes it is asked again, with this
                // one certificate accepted and every other refused.
                asked = true;
                string where = L.T("Proxmox VE shows its own under the node, System, Certificates, and pvenode cert info prints it on the node.");
                if (!await Dialogs.AskTrustCertificateAsync(Title, ex.Host, ex.Fingerprint, where)) return;
                pin = ex.Fingerprint;
            }
            catch (Exception ex) when (ex is CloudImportException or UntrustedCertificateException)
            {
                await Dialogs.ShowMessageAsync(Title, ex.Message);
                return;
            }
        }

        if (found is null) return;

        // Kept only now that it has worked, the certificate that was accepted along with it.
        if (answer.Remember)
        {
            string remember = string.Join('\n', credentials.Origin, credentials.TokenId, answer.Values[2].Trim(), pin ?? string.Empty);
            if (remember != saved) vault.SetLocal(ProxmoxVaultName, remember);
        }

        // An empty list must not leave an empty group behind.
        var notes = new List<string>();
        if (found.Library.Hosts.Count > 0) notes.Add(L.T("{0} new servers added.", HostList.Merge(found.Library)));
        else if (found.LeftOut == 0) notes.Add(L.T("{0} has no servers to import.", Title));
        if (found.LeftOut > 0) notes.Add(L.T("{0} machines were left out because Proxmox knows no address for them: they are stopped, or have no QEMU guest agent running.", found.LeftOut));
        await Dialogs.ShowMessageAsync(Title, string.Join("\n\n", notes));
    }

    /// <summary>
    /// Lists the containers Docker is running on a saved SSH server, and opens a shell in the chosen one as a new
    /// tab: a copy of the server that runs docker exec in place of its login shell. Without a server, asks which one.
    /// </summary>
    public async Task ShowRemoteDockerAsync(HostEntry? server)
    {
        const string Title = "Docker";

        if (server is null)
        {
            // Saved servers only: a Localtonet device is reached with an access key filed under its own id.
            HostEntry? picked = null;
            List<PaletteItem> servers = HostList.Hosts.Where(h => IsSsh(h) && !h.Localtonet)
                .OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(h => new PaletteItem(h.Name, HostListView.Describe(h), () => picked = h))
                .ToList();
            if (servers.Count == 0)
            {
                await Dialogs.ShowMessageAsync(Title, L.T("There is no saved SSH server to look on."));
                return;
            }

            if (await Dialogs.ChooseAsync(servers, L.T("Server to list the containers of")) is not PaletteItem chosen) return;
            chosen.Run();
            if (picked is null) return;
            server = picked;
        }

        if (!IsSsh(server) || server.Localtonet)
        {
            await Dialogs.ShowMessageAsync(Title, L.T("Containers can only be listed on saved SSH servers."));
            return;
        }

        // Started here, on the window's thread like every other command run on a server, and waited for behind a
        // dialog that can be cancelled.
        using var stop = new CancellationTokenSource(RemoteDockerTimeout);
        Task<CommandResult> listing = Workspace.RunOnHostAsync(server, RemoteDockerContainers.ListCommand, stop.Token);
        bool finished = await Dialogs.WaitAsync(Title, L.T("Asking {0} for its containers…", server.Name), null, null, async cancellation =>
        {
            using (cancellation.Register(stop.Cancel)) await listing;
        });
        if (!finished)
        {
            stop.Cancel();
            return;
        }

        CommandResult result = await listing;
        if (stop.IsCancellationRequested) result = result with { Failure = L.T("{0} did not answer in time.", server.Name) };

        if (RemoteDockerContainers.Problem(result) is string problem)
        {
            await Dialogs.ShowMessageAsync(Title, L.T("{0} could not list its containers: {1}", server.Name, problem));
            return;
        }

        List<RemoteContainer> containers = RemoteDockerContainers.Parse(result.Output);
        if (containers.Count == 0)
        {
            await Dialogs.ShowMessageAsync(Title, L.T("No containers are running on {0}.", server.Name));
            return;
        }

        RemoteContainer? container = null;
        List<PaletteItem> items = containers
            .Select(c => new PaletteItem(c.Name, string.Join("  ·  ", new[] { c.Image, c.Status }.Where(t => t.Length > 0)), () => container = c))
            .ToList();
        if (await Dialogs.ChooseAsync(items, L.T("Container to open a shell in")) is not PaletteItem item) return;
        item.Run();
        if (container is null) return;

        HostEntry copy = RemoteDockerContainers.ShellIn(server, container);
        if (OpenServerCopy is { } open) await open(server, copy);
        else await Workspace.OpenAsync(copy);
    }
}

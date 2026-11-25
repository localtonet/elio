using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Elio.Core;

namespace Elio.UI;

/// <content>
/// Bringing servers in from elsewhere: other tools' files, and the accounts of cloud providers. Whatever the
/// source, what it yields goes through the same merge, so importing twice adds nothing twice.
/// </content>
public partial class MainView
{
    // Names of products, the same in every language. They are what someone types into the palette to find the import.
    private const string ImportSources = "OpenSSH · PuTTY · Ansible · CSV · DigitalOcean · AWS · Azure · Google Cloud · Hetzner · Linode · Vultr · Tailscale · Proxmox";

    // Where the vault keeps cloud credentials, for those who asked it to. They are this device's own and never synced.
    private const string DigitalOceanVaultName = "import.digitalocean";
    private const string AwsVaultName = "import.aws";
    private const string HetznerVaultName = "import.hetzner";
    private const string LinodeVaultName = "import.linode";
    private const string VultrVaultName = "import.vultr";
    private const string TailscaleVaultName = "import.tailscale";

    // An inventory or a table of servers is a few kilobytes. Something far larger was picked by mistake.
    private const long LargestImportFile = 8 * 1024 * 1024;

    /// <summary>Asks where the servers are to come from, and imports from there.</summary>
    public async Task ShowImportAsync()
    {
        var items = new List<PaletteItem>
        {
            new(L.T("Import OpenSSH config"), "~/.ssh/config", () => ShowImportResult(L.T("OpenSSH import"), HostList.ImportSshConfig)),
        };
        if (OperatingSystem.IsWindows()) items.Add(new(L.T("Import PuTTY sessions"), null, () => ShowImportResult(L.T("PuTTY import"), HostList.ImportPutty)));
        items.Add(new(L.T("Import an Ansible inventory"), "INI · YAML", () => _ = ImportTextFileAsync(L.T("Import an Ansible inventory"), HostImport.FromAnsibleInventory)));
        items.Add(new(L.T("Import a CSV or TSV file"), L.T("Columns: name, host, port, user, group, tags"), () => _ = ImportTextFileAsync(L.T("Import a CSV or TSV file"), (text, _) => HostImport.FromCsv(text))));
        items.Add(new(L.T("Import droplets from DigitalOcean"), L.T("With an API token"), () => _ = ImportFromDigitalOceanAsync()));
        items.Add(new(L.T("Import instances from AWS EC2"), L.T("With an access key"), () => _ = ImportFromAwsAsync()));
        if (CanRunCloudTools)
        {
            items.Add(new(L.T("Import virtual machines from Azure"), L.T("With the Azure CLI you are signed in to (az)"), () => _ = ImportFromAzureAsync()));
            items.Add(new(L.T("Import instances from Google Cloud"), L.T("With the gcloud CLI you are signed in to"), () => _ = ImportFromGoogleCloudAsync()));
        }
        items.Add(new(L.T("Import servers from Hetzner Cloud"), L.T("With an API token"), () => _ = ImportFromHetznerAsync()));
        items.Add(new(L.T("Import instances from Linode"), L.T("With an API token"), () => _ = ImportFromLinodeAsync()));
        items.Add(new(L.T("Import instances from Vultr"), L.T("With an API key"), () => _ = ImportFromVultrAsync()));
        items.Add(new(L.T("Import devices from Tailscale"), L.T("With an API access token"), () => _ = ImportFromTailscaleAsync()));
        items.Add(new(L.T("Import virtual machines and containers from Proxmox VE"), L.T("With an API token"), () => _ = ImportFromProxmoxAsync()));
        items.Add(new(L.T("Import servers from a file"), "Elio", () => _ = ImportServersAsync()));

        if (await Dialogs.ChooseAsync(items, L.T("Where to import servers from")) is PaletteItem chosen) chosen.Run();
    }

    /// <summary>Asks for a file, reads servers out of its text, and adds the ones that are not here yet.</summary>
    /// <param name="read">Turns the file's text into servers and groups. It is given the file's name as well.</param>
    private async Task ImportTextFileAsync(string title, Func<string, string, HostLibrary> read)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;

        // No filter by type: an inventory is as often called "hosts" as anything with an extension.
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        if (files.Count == 0) return;

        string note;
        try
        {
            await using Stream stream = await files[0].OpenReadAsync();
            if (stream.CanSeek && stream.Length > LargestImportFile) throw new IOException(L.T("That file is too large to be a list of servers."));

            using var reader = new StreamReader(stream);
            HostLibrary found = read(await reader.ReadToEndAsync(), files[0].Name);
            note = L.T("{0} new servers added.", HostList.Merge(found));
        }
        catch (Exception ex) when (ex is ImportFormatException or IOException or UnauthorizedAccessException)
        {
            // A file that is not understood adds nothing at all, and the message says which line stopped it.
            note = ex.Message;
        }

        await Dialogs.ShowMessageAsync(title, note);
    }

    private Task ImportFromDigitalOceanAsync() =>
        ImportWithTokenAsync(DigitalOceanImport.GroupName, DigitalOceanVaultName, TokenNote(DigitalOceanImport.GroupName), L.T("API token"), null, DigitalOceanImport.ListAsync);

    private Task ImportFromHetznerAsync() =>
        ImportWithTokenAsync(HetznerImport.GroupName, HetznerVaultName, TokenNote(HetznerImport.GroupName), L.T("API token"), null, HetznerImport.ListAsync);

    private Task ImportFromLinodeAsync() =>
        ImportWithTokenAsync(LinodeImport.GroupName, LinodeVaultName, TokenNote(LinodeImport.GroupName), L.T("API token"), null, LinodeImport.ListAsync);

    // Vultr calls its token an API key.
    private Task ImportFromVultrAsync() =>
        ImportWithTokenAsync(VultrImport.GroupName, VultrVaultName, TokenNote(VultrImport.GroupName), L.T("API key"), null, VultrImport.ListAsync);

    private Task ImportFromTailscaleAsync() =>
        ImportWithTokenAsync(TailscaleImport.GroupName, TailscaleVaultName, TokenNote(TailscaleImport.GroupName), L.T("API access token"), "tskey-api-…", TailscaleImport.ListAsync);

    private static string TokenNote(string provider) =>
        L.T("An API token that may read is enough. It is sent to {0} and nowhere else, and is only kept if you tick the box.", provider);

    /// <summary>Asks for the one secret a provider lists its servers with, and imports what it lists.</summary>
    /// <param name="list">The provider's ListAsync. It is given the secret, no handler (that is for tests), and a way to stop.</param>
    private async Task ImportWithTokenAsync(string title, string vaultName, string note, string label, string? placeholder, Func<string, HttpMessageHandler?, CancellationToken, Task<HostLibrary>> list)
    {
        Vault vault = AppServices.Vault;
        string? saved = vault.IsAvailable ? vault.GetLocal(vaultName) : null;

        CredentialAnswer? answer = await Dialogs.AskCredentialsAsync(
            title,
            note,
            [new CredentialField(label, Secret: true, saved, Placeholder: placeholder)],
            vault.IsAvailable,
            remembered: saved is not null);
        if (answer is null) return;

        string token = answer.Values[0];
        await RunCloudImportAsync(title, vaultName, answer.Remember ? token : null, saved, cancellation => list(token, null, cancellation));
    }

    private async Task ImportFromAwsAsync()
    {
        const string Title = "AWS EC2";
        Vault vault = AppServices.Vault;
        string? saved = vault.IsAvailable ? vault.GetLocal(AwsVaultName) : null;

        // Kept as one secret of four lines: key id, secret key, session token, region.
        string[] known = (saved ?? string.Empty).Split('\n');
        string? Known(int index) => index < known.Length && known[index].Length > 0 ? known[index] : null;

        CredentialAnswer? answer = await Dialogs.AskCredentialsAsync(
            Title,
            L.T("A key that may only read EC2 is enough. The secret key signs the requests on this device and is never sent. Nothing is kept unless you tick the box."),
            [
                new CredentialField(L.T("Access key ID"), Secret: false, Known(0)),
                new CredentialField(L.T("Secret access key"), Secret: true, Known(1)),
                new CredentialField(L.T("Session token (only for temporary credentials)"), Secret: true, Known(2), Optional: true),
                new CredentialField(L.T("Region"), Secret: false, Known(3), Placeholder: "eu-west-1"),
            ],
            vault.IsAvailable,
            remembered: saved is not null);
        if (answer is null) return;

        var credentials = new AwsCredentials(answer.Values[0], answer.Values[1], answer.Values[2].Length > 0 ? answer.Values[2] : null);
        string region = answer.Values[3];
        await RunCloudImportAsync(Title, AwsVaultName, answer.Remember ? string.Join('\n', answer.Values) : null, saved, cancellation => AwsEc2Import.ListAsync(credentials, region, null, cancellation));
    }

    /// <summary>Asks a provider for its servers while a dialog waits, adds the new ones, and says how many there were.</summary>
    /// <param name="remember">What to keep in the vault once the provider has accepted it, or null to keep nothing.</param>
    /// <param name="saved">What the vault holds now.</param>
    private async Task RunCloudImportAsync(string title, string vaultName, string? remember, string? saved, Func<CancellationToken, Task<HostLibrary>> list)
    {
        // Unticking the box is how saved credentials are forgotten, whether or not the import then works.
        if (remember is null && saved is not null) AppServices.Vault.SetLocal(vaultName, null);

        HostLibrary? found = null;
        try
        {
            if (!await Dialogs.WaitAsync(title, L.T("Asking {0} for its servers…", title), null, null, async cancellation => found = await list(cancellation))) return;
        }
        catch (CloudImportException ex)
        {
            await Dialogs.ShowMessageAsync(title, ex.Message);
            return;
        }

        if (found is null) return;

        // Kept only now that they have worked: credentials with a typing mistake in them are not worth remembering.
        if (remember is not null && remember != saved) AppServices.Vault.SetLocal(vaultName, remember);

        // An account with nothing in it must not leave an empty group behind.
        string note = found.Hosts.Count == 0 ? L.T("{0} has no servers to import.", title) : L.T("{0} new servers added.", HostList.Merge(found));
        await Dialogs.ShowMessageAsync(title, note);
    }
}

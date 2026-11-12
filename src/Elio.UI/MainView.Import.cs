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
    private const string ImportSources = "OpenSSH · PuTTY · Ansible · CSV · DigitalOcean · AWS";

    // Where the vault keeps cloud credentials, for those who asked it to. They are this device's own and never synced.
    private const string DigitalOceanVaultName = "import.digitalocean";
    private const string AwsVaultName = "import.aws";

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

    private async Task ImportFromDigitalOceanAsync()
    {
        const string Title = "DigitalOcean";
        Vault vault = AppServices.Vault;
        string? saved = vault.IsAvailable ? vault.GetLocal(DigitalOceanVaultName) : null;

        CredentialAnswer? answer = await Dialogs.AskCredentialsAsync(
            Title,
            L.T("An API token that may read is enough. It is sent to {0} and nowhere else, and is only kept if you tick the box.", Title),
            [new CredentialField(L.T("API token"), Secret: true, saved)],
            vault.IsAvailable,
            remembered: saved is not null);
        if (answer is null) return;

        string token = answer.Values[0];
        await RunCloudImportAsync(Title, DigitalOceanVaultName, answer.Remember ? token : null, saved, cancellation => DigitalOceanImport.ListAsync(token, null, cancellation));
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

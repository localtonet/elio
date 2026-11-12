using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Elio.Core;

namespace Elio.UI;

/// <content>Saving the server list to a file and reading one back, for carrying it somewhere sync does not reach.</content>
public partial class MainView
{
    private static readonly FilePickerFileType LibraryFiles = new("Elio") { Patterns = ["*" + LibraryFile.Extension, "*.json"] };

    /// <summary>Asks where to, and writes the servers, groups and commands there. Passwords stay behind.</summary>
    public async Task ExportServersAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage) return;

        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L.T("Export servers to a file"),
            SuggestedFileName = "servers" + LibraryFile.Extension,
            FileTypeChoices = [LibraryFiles],
        });
        if (file is null) return;

        try
        {
            await using Stream stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(LibraryFile.Write(AppServices.Library));
            await Dialogs.ShowMessageAsync(L.T("Export servers to a file"), L.T("Saved {0} servers. Passwords are not in the file.", AppServices.Library.Hosts.Count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(L.T("Export servers to a file"), ex.Message);
        }
    }

    /// <summary>Asks for such a file and adds what is in it that is not here already.</summary>
    public async Task ImportServersAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L.T("Import servers from a file"),
            AllowMultiple = false,
            FileTypeFilter = [LibraryFiles],
        });
        if (files.Count == 0) return;

        try
        {
            await using Stream stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            HostLibrary? library = LibraryFile.Read(await reader.ReadToEndAsync());

            // The file may come from someone else, and a server in it can be set to do things as soon as it is
            // opened. Those are not taken in without the user having been told.
            if (library is not null && LibraryFile.CountActing(library) is > 0 and var acting)
            {
                int choice = await Dialogs.AskChoiceAsync(
                    L.T("Import servers from a file"),
                    L.T("{0} entries in this file do more than connect: they run commands on this computer or on the server, let the server use your SSH agent, let AI tools run commands, or open ports to other computers. Keep that only if you trust where the file came from.", acting)
                        + (library.Groups.Any(LibraryFile.ActsOnItsOwn) ? " " + L.T("Some groups in it also send every server placed in them through jump hosts or a proxy.") : string.Empty),
                    L.T("Import without those settings"),
                    L.T("Import as they are"));
                if (choice < 0) return;
                if (choice == 0) LibraryFile.MakePlain(library);
            }

            string note = library is not null
                ? L.T("{0} new servers added.", HostList.Merge(library))
                : L.T("That file does not hold a list of servers from Elio.");
            await Dialogs.ShowMessageAsync(L.T("Import servers from a file"), note);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(L.T("Import servers from a file"), ex.Message);
        }
    }
}

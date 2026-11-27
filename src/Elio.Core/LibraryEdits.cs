namespace Elio.Core;

/// <summary>
/// Puts what an edit dialog gave back into the library. A sync replaces every item in the library with a fresh copy,
/// and it may run while a dialog is open, so the item is found again by its id. Looked for as the object the dialog
/// was opened with, it would not be there any more, and the edit would be lost without a word.
/// </summary>
public static class LibraryEdits
{
    /// <summary>Puts the edited server in place of the one with its id.</summary>
    public static void Replace(HostLibrary library, HostEntry edited) => Replace(library.Hosts, edited, h => h.Id, library.Deleted);

    /// <summary>Puts the edited snippet in place of the one with its id.</summary>
    public static void Replace(HostLibrary library, Snippet edited) => Replace(library.Snippets, edited, s => s.Id, library.Deleted);

    // One that was removed meanwhile, on another device, comes back: saving the dialog is a newer change than the
    // removal, which is how sync settles the two as well.
    private static void Replace<T>(List<T> items, T edited, Func<T, Guid> idOf, Dictionary<Guid, DateTime> deleted)
    {
        Guid id = idOf(edited);
        int at = items.FindIndex(item => idOf(item) == id);
        if (at >= 0)
        {
            items[at] = edited;
            return;
        }

        deleted.Remove(id);
        items.Add(edited);
    }
}

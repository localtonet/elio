namespace Elio.Core;

/// <summary>Which terminals a snippet is for: every one, or the servers and groups it names.</summary>
public static class SnippetScope
{
    /// <summary>
    /// Whether a snippet is for this server: it names no server at all, names this one, or names a group the server is
    /// in, however deep. A terminal that is not a saved server (null) only gets the snippets that are for everything.
    /// </summary>
    public static bool AppliesTo(Snippet snippet, HostEntry? host, HostLibrary library)
    {
        if (snippet.For.Count == 0) return true;
        if (host is null) return false;
        if (snippet.For.Contains(host.Id)) return true;

        // Up the groups, with a limit: a loop in the groups must not hang the palette.
        var seen = new HashSet<Guid>();
        for (Guid? group = host.GroupId; group is Guid id && seen.Add(id); group = library.Groups.FirstOrDefault(g => g.Id == id)?.ParentId)
        {
            if (snippet.For.Contains(id)) return true;
        }

        return false;
    }

    /// <summary>The names of what a snippet is for, servers and groups, for showing. Ids that no longer exist are left out.</summary>
    public static List<string> Names(Snippet snippet, HostLibrary library) =>
        snippet.For
            .Select(id => library.Hosts.FirstOrDefault(h => h.Id == id)?.Name ?? library.Groups.FirstOrDefault(g => g.Id == id)?.Name)
            .OfType<string>()
            .ToList();

    /// <summary>
    /// Reads names written by the user, separated by commas, as servers or groups. A server is looked for first.
    /// Returns the ids, and the names that matched nothing.
    /// </summary>
    public static (List<Guid> Ids, List<string> Unknown) Parse(string? text, HostLibrary library)
    {
        var ids = new List<Guid>();
        var unknown = new List<string>();
        foreach (string name in (text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            Guid? id = library.Hosts.FirstOrDefault(h => h.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))?.Id
                ?? library.Groups.FirstOrDefault(g => g.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))?.Id;
            if (id is Guid found)
            {
                if (!ids.Contains(found)) ids.Add(found);
            }
            else
            {
                unknown.Add(name);
            }
        }

        return (ids, unknown);
    }
}

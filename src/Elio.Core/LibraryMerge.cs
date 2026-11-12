namespace Elio.Core;

/// <summary>
/// Takes servers from outside into the library: from a file of Elio's, an OpenSSH config, another program.
/// Nothing that comes in keeps the id it came with. Saved passwords are found by id, so an id from outside that
/// matched one already here would point that password at an address of the file's choosing.
/// </summary>
public static class LibraryMerge
{
    /// <summary>Adds what a file of servers holds that is not here already: its groups, commands and identities too.</summary>
    /// <param name="hasSecret">Whether a password or key is saved on this device for an identity that is already here.</param>
    /// <returns>How many servers were added.</returns>
    public static int Merge(HostLibrary library, HostLibrary file, Func<Identity, bool> hasSecret)
    {
        var groupIds = new Dictionary<Guid, Guid>();
        var placing = new HashSet<Guid>();
        var newGroups = new List<(HostGroup Here, HostGroup InFile)>();

        // Whether a server put into a group already here would log in with a secret saved on this device. A file
        // must not get its servers into such a group: opening one would send that secret to the file's address.
        bool LendsSecret(HostGroup group) =>
            ConnectionResolver.Resolve(new HostEntry { Kind = HostKind.Ssh, Port = 0, GroupId = group.Id }, library).Identity is Identity lent && hasSecret(lent);

        // Finds the group here that matches one in the file, by name and place, or adds it. Parents go first.
        Guid? Place(Guid? fromFile)
        {
            if (fromFile is not Guid id) return null;
            if (groupIds.TryGetValue(id, out Guid placed)) return placed;

            // What was in a group that did not come along, or that is inside itself, goes to the top level rather than nowhere.
            if (file.Groups.FirstOrDefault(g => g.Id == id) is not HostGroup group || !placing.Add(id)) return null;

            Guid? parent = Place(group.ParentId);
            HostGroup? same = library.Groups.FirstOrDefault(g => g.Name == group.Name && g.ParentId == parent && !LendsSecret(g));
            if (same is null)
            {
                same = new HostGroup { Name = group.Name, ParentId = parent, Collapsed = group.Collapsed, ModifiedUtc = DateTime.UtcNow };
                library.Groups.Add(same);
                newGroups.Add((same, group));
            }

            return groupIds[id] = same.Id;
        }

        foreach (HostGroup group in file.Groups) Place(group.Id);

        foreach (Snippet snippet in file.Snippets)
        {
            if (library.Snippets.Any(s => s.Name == snippet.Name && s.Command == snippet.Command)) continue;
            library.Snippets.Add(new Snippet { Name = snippet.Name, Command = snippet.Command, ModifiedUtc = DateTime.UtcNow });
        }

        // The identity each server named in the file, taken off it until it is known which servers are new.
        var wanted = new Dictionary<HostEntry, Guid>();
        foreach (HostEntry host in file.Hosts)
        {
            host.GroupId = Place(host.GroupId);
            if (host.IdentityId is Guid named) wanted[host] = named;
        }

        var hostIds = new Dictionary<Guid, Guid>();
        int added = Merge(library, file.Hosts, hostIds);

        // An identity from the file always arrives as a new one of its own, with no password or key: a file never
        // carries those. It is never matched to an identity already here, however alike the two look, because
        // that would send the password saved here to whatever address the file gave its server. Only identities
        // that something new points to are taken, so importing the same file twice adds nothing.
        var identityIds = new Dictionary<Guid, Guid>();
        Guid? PlaceIdentity(Guid? fromFile)
        {
            if (fromFile is not Guid id) return null;
            if (identityIds.TryGetValue(id, out Guid placed)) return placed;
            if (file.Identities.FirstOrDefault(i => i.Id == id) is not Identity identity) return null;

            var copy = new Identity { Name = identity.Name, User = identity.User, Kind = identity.Kind, PublicKey = identity.PublicKey, ModifiedUtc = DateTime.UtcNow };
            library.Identities.Add(copy);
            return identityIds[id] = copy.Id;
        }

        foreach (var (host, named) in wanted)
        {
            if (library.Hosts.Contains(host)) host.IdentityId = PlaceIdentity(named);
        }

        // A group's defaults come along only with a group that is new here. One that was matched to a group
        // already here keeps what it had, so a file cannot reroute the servers the user keeps in it.
        foreach (var (group, inFile) in newGroups)
        {
            group.IdentityId = PlaceIdentity(inFile.IdentityId);
            group.User = inFile.User;
            group.Port = inFile.Port;
            group.UseAgent = inFile.UseAgent;
            group.UseMosh = inFile.UseMosh;
            group.Proxy = inFile.Proxy;

            // As for a server's own: a jump host that did not come along is dropped.
            group.JumpHosts = inFile.JumpHosts.Where(hostIds.ContainsKey).Select(id => hostIds[id]).ToList();
        }

        return added;
    }

    /// <summary>Adds the servers that aren't there yet, so importing twice changes nothing.</summary>
    /// <param name="placed">Filled with the id each server came with and the id it has here, for a caller that has more to remap.</param>
    /// <returns>How many servers were added.</returns>
    public static int Merge(HostLibrary library, IReadOnlyList<HostEntry> imported, Dictionary<Guid, Guid>? placed = null)
    {
        // The id each one came with, to the id it has here: that of the same server if it is here already, or
        // else a new one. An id from outside is never kept, because the vault finds a server's password by its
        // id, and a file that reused the id of a server already here would get that password sent to its own address.
        Dictionary<Guid, Guid> ids = placed ?? [];
        var fresh = new List<HostEntry>();

        foreach (HostEntry host in imported)
        {
            HostEntry? same = library.Hosts.FirstOrDefault(h => h.Name == host.Name && h.Address == host.Address && h.Port == host.Port && h.User == host.User);
            Guid id = same?.Id ?? Guid.NewGuid();
            ids.TryAdd(host.Id, id);
            if (same is not null) continue;

            host.Id = id;

            // The same goes for the identity it names: an id from outside could only be a guess at one saved here.
            // A file's own identities are given back, under new ids, by the caller that read them.
            host.IdentityId = null;
            host.ModifiedUtc = DateTime.UtcNow;
            library.Hosts.Add(host);
            fresh.Add(host);
        }

        // A jump host that did not come along is dropped: its id could only be a guess at a server already here.
        foreach (HostEntry host in fresh) host.JumpHosts = host.JumpHosts.Where(ids.ContainsKey).Select(id => ids[id]).ToList();
        return fresh.Count;
    }
}

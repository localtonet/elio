using System.Text.Json;

namespace Elio.Core;

/// <summary>
/// The server list as a file to carry to another computer or keep as a copy: servers, groups, commands and the
/// identities of the keychain. Passwords and private keys are not in it; they live in the vault and never leave it
/// this way, so an identity arrives with its name, user name and public key only.
/// </summary>
public static class LibraryFile
{
    public const string Extension = ".elio.json";

    // Written list by list, so that nothing added to the library later ends up in a file without anyone deciding it should.
    public static string Write(HostLibrary library) => JsonSerializer.Serialize(
        new HostLibrary { Hosts = library.Hosts, Groups = library.Groups, Snippets = library.Snippets, Identities = library.Identities },
        CoreJsonContext.Default.HostLibrary);

    /// <summary>Reads such a file. Null if the text is not one.</summary>
    public static HostLibrary? Read(string text)
    {
        try
        {
            HostLibrary? library = JsonSerializer.Deserialize(text, CoreJsonContext.Default.HostLibrary);
            if (library is not { Hosts: not null, Groups: not null, Snippets: not null }) return null;

            // The file may have been written by anyone, and JSON can say null where the code expects a list.
            library.Hosts.RemoveAll(host => host is null);
            library.Groups.RemoveAll(group => group?.Name is null);
            library.Snippets.RemoveAll(snippet => snippet?.Name is null || snippet.Command is null);

            // A file from before there were identities has none, which is no reason to turn it away.
            library.Identities ??= [];
            library.Identities.RemoveAll(identity => identity?.Name is null);
            foreach (Identity identity in library.Identities) identity.User ??= string.Empty;

            foreach (HostGroup group in library.Groups)
            {
                group.JumpHosts ??= [];
                group.Proxy = Usable(group.Proxy);
            }

            foreach (HostEntry host in library.Hosts)
            {
                host.Proxy = Usable(host.Proxy);
                host.Name ??= string.Empty;
                host.Address ??= string.Empty;
                host.User ??= string.Empty;
                host.Tags ??= [];
                host.JumpHosts ??= [];
                host.Environment ??= [];
                host.Forwards ??= [];
                host.Forwards.RemoveAll(forward => forward?.BindAddress is null || forward.TargetHost is null);
            }

            return library;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether an entry does more than connect: it starts a program on this computer, runs something on the
    /// server, hands the server the SSH agent, lets AI tools run commands, or opens a port to other computers.
    /// A file from someone else can carry these, so they are asked about before it is taken in.
    /// </summary>
    public static bool ActsOnItsOwn(HostEntry host) =>
        host.Kind == HostKind.Local
        || !string.IsNullOrWhiteSpace(host.LoginScript)
        || !string.IsNullOrWhiteSpace(host.RemoteCommand)
        || host.ForwardAgent
        || host.AllowMcpCommands
        || host.Forwards.Any(OpensToOthers);

    /// <summary>
    /// Whether a group does more than hold servers: it sends every server put into it through jump hosts or a proxy
    /// of the file's choosing. On one server that is plain to see when it is opened; on a group it also takes hold
    /// of servers moved into it later, so it is asked about like the rest.
    /// </summary>
    public static bool ActsOnItsOwn(HostGroup group) =>
        group.JumpHosts.Count > 0 || group.Proxy is { Kind: not ProxyKind.None };

    /// <summary>How many entries of a file, servers and groups, <see cref="ActsOnItsOwn(HostEntry)"/> is true for.</summary>
    public static int CountActing(HostLibrary library) =>
        library.Hosts.Count(ActsOnItsOwn) + library.Groups.Count(ActsOnItsOwn);

    /// <summary>Leaves out the local shells and takes off everything <see cref="ActsOnItsOwn(HostEntry)"/> looks for.</summary>
    public static void MakePlain(HostLibrary library)
    {
        foreach (HostGroup group in library.Groups)
        {
            group.JumpHosts = [];
            group.Proxy = null;
        }

        library.Hosts.RemoveAll(host => host.Kind == HostKind.Local);
        foreach (HostEntry host in library.Hosts)
        {
            host.LoginScript = null;
            host.RemoteCommand = null;
            host.ForwardAgent = false;
            host.AllowMcpCommands = false;
            host.Forwards.RemoveAll(OpensToOthers);
        }
    }

    // A proxy that JSON left without an address or a user name is put right, so nothing later trips on a null.
    private static ProxySettings? Usable(ProxySettings? proxy)
    {
        if (proxy is null) return null;

        proxy.Host ??= string.Empty;
        proxy.User ??= string.Empty;
        return proxy;
    }

    // A remote forward lets the server reach into this computer's network wherever it listens, so only a
    // local or dynamic one that listens on this computer alone is harmless.
    private static bool OpensToOthers(PortForward forward) =>
        forward.Kind == ForwardKind.Remote
        || !(forward.BindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(forward.BindAddress.Trim('[', ']'), out System.Net.IPAddress? address) && System.Net.IPAddress.IsLoopback(address)));
}

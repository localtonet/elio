namespace Elio.Core;

/// <summary>
/// What a server really connects with, once its own settings, those of the groups it is in and the identity it
/// points to have been put together. Nothing secret is in here; <see cref="ConnectionResolver.Login"/> adds that.
/// </summary>
public sealed class ResolvedHost
{
    public required HostEntry Host { get; init; }

    public string User { get; init; } = string.Empty;
    public int Port { get; init; }

    /// <summary>The identity that supplies the login, the server's own or its group's. Null when the server logs in with its own fields.</summary>
    public Identity? Identity { get; init; }

    public bool UseAgent { get; init; }
    public bool UseMosh { get; init; }

    /// <summary>Servers to pass through on the way, outermost first.</summary>
    public IReadOnlyList<Guid> JumpHosts { get; init; } = [];

    /// <summary>The proxy to connect through. Null for a direct connection.</summary>
    public ProxySettings? Proxy { get; init; }

    /// <summary>The server or group the proxy was set on, which is what its password is filed under.</summary>
    public Guid? ProxyOwner { get; init; }

    // The group each inherited value came from, for telling the user where it applies from. Null for a value
    // that is the server's own, or that nobody set.

    public HostGroup? UserFrom { get; init; }
    public HostGroup? PortFrom { get; init; }
    public HostGroup? IdentityFrom { get; init; }
    public HostGroup? JumpHostsFrom { get; init; }
    public HostGroup? ProxyFrom { get; init; }
    public HostGroup? AgentFrom { get; init; }
    public HostGroup? MoshFrom { get; init; }
}

/// <summary>What to log in with. A class and not a record, so that printing one can never print a password.</summary>
public sealed class ResolvedLogin
{
    public string User { get; init; } = string.Empty;
    public string? Password { get; init; }

    /// <summary>A private key in a file on this computer.</summary>
    public string? KeyPath { get; init; }

    /// <summary>A private key as text, from an identity.</summary>
    public string? KeyText { get; init; }
    public string? KeyPassphrase { get; init; }
    public bool UseAgent { get; init; }

    /// <summary>The identity whose secret is used. Null when the server's own password or key file is.</summary>
    public Identity? Identity { get; init; }

    public bool HasKey => KeyPath is not null || KeyText is not null;

    /// <summary>Whether there is nothing at all to log in with yet, so a password has to be asked for.</summary>
    public bool NeedsSecret => !HasKey && !UseAgent && Password is null;

    /// <summary>
    /// Whether a password typed by the user would be used. Not when an identity supplies the login: what was
    /// typed would be filed under the server, where nothing would look for it.
    /// </summary>
    public bool TakesTypedPassword => Identity is null && KeyPath is null;
}

/// <summary>
/// The one place where a server, the groups above it and an identity are combined. Everything that connects asks
/// here, so that a terminal, the file panel, a jump host and a command run on many servers all agree.
/// </summary>
public static class ConnectionResolver
{
    public const int DefaultSshPort = 22;

    /// <summary>
    /// Works out what a server connects with. The server's own settings come first, then those of its group, then
    /// of the group that one is in, and so on: the nearest that says anything wins.
    /// </summary>
    public static ResolvedHost Resolve(HostEntry host, HostLibrary library)
    {
        // Groups hold defaults for SSH only. A serial port or a Telnet server has no login to inherit.
        if (host.Kind != HostKind.Ssh)
            return new ResolvedHost { Host = host, User = host.User, Port = host.Port, UseAgent = host.UseAgent, UseMosh = host.UseMosh, JumpHosts = host.JumpHosts };

        Identity? identity = Find(library, host.IdentityId);
        HostGroup? identityFrom = null;

        // A server with a key file of its own has said how it logs in, the same as one that names an identity.
        bool ownLogin = identity is not null || host.KeyPath is not null;

        string? user = FirstSet(identity?.User, host.User);
        HostGroup? userFrom = null;
        int? port = host.Port > 0 ? host.Port : null;
        HostGroup? portFrom = null;
        IReadOnlyList<Guid>? jumps = host.JumpHosts.Count > 0 ? host.JumpHosts : null;
        HostGroup? jumpsFrom = null;
        ProxySettings? proxy = host.Proxy;
        Guid? proxyOwner = proxy is null ? null : host.Id;
        HostGroup? proxyFrom = null;

        // On a server these two are plain yes or no, and no is what every server saved before groups had
        // defaults says. So no means "nothing of my own", and a group can only turn them on.
        bool? agent = host.UseAgent ? true : null;
        HostGroup? agentFrom = null;
        bool? mosh = host.UseMosh ? true : null;
        HostGroup? moshFrom = null;

        foreach (HostGroup group in GroupChain(host.GroupId, library))
        {
            bool identityFromHere = false;
            if (!ownLogin && identity is null && Find(library, group.IdentityId) is Identity inherited)
            {
                (identity, identityFrom, identityFromHere) = (inherited, group, true);
            }

            // Within one group its identity's user name comes before the group's plain one, as on a server.
            if (user is null && FirstSet(identityFromHere ? identity!.User : null, group.User) is string named)
            {
                (user, userFrom) = (named, group);
            }

            if (port is null && group.Port is > 0 and <= 65535) (port, portFrom) = (group.Port, group);

            if (jumps is null && group.JumpHosts is { Count: > 0 })
            {
                // A server that is itself one of the group's jump hosts is reached through the ones before it
                // only. Otherwise the gateway of a group could never be in that group.
                int self = group.JumpHosts.IndexOf(host.Id);
                (jumps, jumpsFrom) = (self < 0 ? group.JumpHosts : group.JumpHosts.Take(self).ToList(), group);
            }

            if (proxy is null && group.Proxy is not null) (proxy, proxyOwner, proxyFrom) = (group.Proxy, group.Id, group);
            if (agent is null && group.UseAgent is bool groupAgent) (agent, agentFrom) = (groupAgent, group);
            if (mosh is null && group.UseMosh is bool groupMosh) (mosh, moshFrom) = (groupMosh, group);
        }

        bool direct = proxy is null || proxy.Kind == ProxyKind.None || string.IsNullOrWhiteSpace(proxy.Host);
        return new ResolvedHost
        {
            Host = host,
            User = user ?? string.Empty,
            Port = port ?? DefaultSshPort,
            Identity = identity,
            UseAgent = agent ?? false,
            UseMosh = mosh ?? false,
            JumpHosts = jumps ?? [],
            Proxy = direct ? null : proxy,
            ProxyOwner = direct ? null : proxyOwner,
            UserFrom = userFrom,
            PortFrom = portFrom,
            IdentityFrom = identityFrom,
            JumpHostsFrom = jumpsFrom,
            ProxyFrom = direct ? null : proxyFrom,
            AgentFrom = agent == true ? agentFrom : null,
            MoshFrom = mosh == true ? moshFrom : null,
        };
    }

    /// <summary>
    /// Adds the secrets to what <see cref="Resolve"/> found.
    /// </summary>
    /// <param name="secretOf">Looks an id up in the vault. Asked for the identity's id, or for the server's own.</param>
    public static ResolvedLogin Login(ResolvedHost resolved, Func<Guid, string?> secretOf)
    {
        HostEntry host = resolved.Host;

        if (resolved.Identity is Identity identity && IdentitySecret.Read(secretOf(identity.Id)) is { IsEmpty: false } secret)
        {
            bool key = !string.IsNullOrEmpty(secret.PrivateKey);
            return new ResolvedLogin
            {
                User = resolved.User,
                Password = key ? null : secret.Password,
                KeyText = key ? secret.PrivateKey : null,
                KeyPassphrase = key && !string.IsNullOrEmpty(secret.Passphrase) ? secret.Passphrase : null,
                UseAgent = resolved.UseAgent,
                Identity = identity,
            };
        }

        // No identity, or one whose secret is not on this device: it was synced without passwords, or came in a
        // file, which never carries them. The server's own key file and saved password are what is left, and the
        // identity still supplies the user name.
        string? own = secretOf(host.Id);
        return new ResolvedLogin
        {
            User = resolved.User,
            Password = host.KeyPath is null ? own : null,
            KeyPath = host.KeyPath,
            KeyPassphrase = host.KeyPath is null ? null : own,
            UseAgent = resolved.UseAgent,
        };
    }

    /// <summary>
    /// Whether a server can be pointed at an identity and still log in to the account it logs in to now. That is
    /// what matters after a key was put into that account's authorized_keys: an identity that names another
    /// user would knock on a door the key was never hung behind.
    /// </summary>
    public static bool CanUseIdentity(HostEntry host, Identity identity, HostLibrary library) =>
        string.IsNullOrWhiteSpace(identity.User) || identity.User.Trim() == Resolve(host, library).User;

    /// <summary>
    /// Points a server at an identity, keeping the account it logs in to. Nothing is changed, and false comes
    /// back, when <see cref="CanUseIdentity"/> says no.
    /// </summary>
    public static bool UseIdentity(HostEntry host, Identity identity, HostLibrary library)
    {
        if (!CanUseIdentity(host, identity, library)) return false;

        string user = Resolve(host, library).User;
        host.IdentityId = identity.Id;

        // The user name may have come from the identity the server pointed at until now. The new one has none
        // to give in that case, so the server takes the name as its own.
        if (Resolve(host, library).User != user) host.User = user;
        return true;
    }

    /// <summary>The groups a server is in, nearest first. Stops at a group that is missing, or that it has already passed.</summary>
    public static List<HostGroup> GroupChain(Guid? groupId, HostLibrary library)
    {
        var chain = new List<HostGroup>();
        var seen = new HashSet<Guid>();

        // Sync can bring in a group whose parent is inside it, when two devices moved groups at once. That must
        // not hang a connection, so each group is passed once.
        while (groupId is Guid id && seen.Add(id) && library.Groups.FirstOrDefault(g => g.Id == id) is HostGroup group)
        {
            chain.Add(group);
            groupId = group.ParentId;
        }

        return chain;
    }

    private static Identity? Find(HostLibrary library, Guid? id) =>
        id is Guid wanted ? library.Identities.FirstOrDefault(i => i.Id == wanted) : null;

    private static string? FirstSet(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : !string.IsNullOrWhiteSpace(second) ? second.Trim() : null;
}

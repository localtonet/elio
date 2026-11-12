using Elio.Core;

namespace Elio.Connections;

/// <summary>
/// Turns what <see cref="ConnectionResolver"/> worked out into what a connection is made with. The app's window and
/// the server for AI tools both build their paths here, so a server logs in the same way wherever it is opened from.
/// </summary>
public static class SshEndpoints
{
    /// <summary>One server, with its login.</summary>
    /// <param name="secretOf">Looks an id up in the vault.</param>
    public static SshEndpoint For(ResolvedHost resolved, Func<Guid, string?> secretOf)
    {
        ResolvedLogin login = ConnectionResolver.Login(resolved, secretOf);
        return new SshEndpoint(resolved.Host.Address, resolved.Port, login.User, login.Password, login.KeyPath, login.KeyPassphrase, login.UseAgent, login.KeyText);
    }

    /// <summary>
    /// A whole path, jump hosts first and the server itself last, with the proxy that leads to its first server.
    /// </summary>
    public static List<SshEndpoint> ForPath(IReadOnlyList<ResolvedHost> path, Func<Guid, string?> secretOf)
    {
        List<SshEndpoint> endpoints = path.Select(step => For(step, secretOf)).ToList();
        if (endpoints.Count > 0 && ProxyFor(path, secretOf) is SshProxy proxy) endpoints[0] = endpoints[0] with { Proxy = proxy };
        return endpoints;
    }

    /// <summary>
    /// The proxy a path starts through. The server being connected to says so first, since it is the one the user
    /// opened; if it names none, the first jump host's own proxy is used, which is how that host is reached when it
    /// is opened by itself.
    /// </summary>
    public static SshProxy? ProxyFor(IReadOnlyList<ResolvedHost> path, Func<Guid, string?> secretOf)
    {
        if (path.Count == 0) return null;

        ResolvedHost from = path[^1].Proxy is not null ? path[^1] : path[0];
        if (from.Proxy is not ProxySettings proxy || from.ProxyOwner is not Guid owner) return null;

        string? user = string.IsNullOrWhiteSpace(proxy.User) ? null : proxy.User.Trim();
        return new SshProxy(proxy.Kind, proxy.Host.Trim(), proxy.Port, user, user is null ? null : secretOf(ProxySettings.SecretId(owner)));
    }
}

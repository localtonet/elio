using System.Globalization;
using System.Text;

namespace Elio.Core;

/// <summary>
/// Writes the SSH servers of a library as an OpenSSH config file, so that ssh, scp, rsync, git and editors that read
/// ~/.ssh/config reach them the same way Elio does. Passwords and keys kept in the vault never go into it: a server
/// that logs in with an identity's key gets a comment saying so instead of an IdentityFile.
/// </summary>
public static class SshConfigExport
{
    /// <summary>The file, with one Host block per SSH server, in the order of their names.</summary>
    /// <param name="keyFileOf">Where an identity's key is kept as a file on this computer, if the caller knows; the vault keeps keys as text, which ssh cannot read.</param>
    public static string Write(HostLibrary library, Func<Identity, string?>? keyFileOf = null)
    {
        var text = new StringBuilder();
        text.Append("# Written by Elio on ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(".\n");
        text.Append("# Passwords and keys kept in Elio's vault are not in this file.\n");

        List<HostEntry> servers = library.Hosts
            .Where(h => h.Kind == HostKind.Ssh && !h.Localtonet && h.Address.Length > 0)
            .OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Dictionary<Guid, string> aliases = Aliases(servers);

        foreach (HostEntry host in servers)
        {
            ResolvedHost resolved = ConnectionResolver.Resolve(host, library);
            text.Append('\n');
            if (!string.Equals(aliases[host.Id], host.Name, StringComparison.Ordinal)) text.Append("# ").Append(OneLine(host.Name)).Append('\n');
            text.Append("Host ").Append(aliases[host.Id]).Append('\n');
            Line(text, "HostName", host.Address);
            if (resolved.User.Length > 0) Line(text, "User", resolved.User);
            if (resolved.Port is > 0 and not ConnectionResolver.DefaultSshPort) Line(text, "Port", resolved.Port.ToString(CultureInfo.InvariantCulture));

            if (host.KeyPath is { Length: > 0 } keyPath)
            {
                Line(text, "IdentityFile", keyPath);
                Line(text, "IdentitiesOnly", "yes");
            }
            else if (resolved.Identity is { Kind: IdentityKind.Key } identity)
            {
                if (keyFileOf?.Invoke(identity) is { Length: > 0 } file)
                {
                    Line(text, "IdentityFile", file);
                    Line(text, "IdentitiesOnly", "yes");
                }
                else
                {
                    text.Append("    # Logs in with the key of the identity \"").Append(OneLine(identity.Name)).Append("\", which is kept in Elio's vault.\n");
                }
            }

            List<string> jumps = resolved.JumpHosts.Select(id => aliases.GetValueOrDefault(id)).OfType<string>().ToList();
            if (jumps.Count > 0) Line(text, "ProxyJump", string.Join(',', jumps));
            else if (resolved.Proxy is { Kind: ProxyKind.Command, Command: { Length: > 0 } command }) Line(text, "ProxyCommand", command);
            else if (resolved.Proxy is { Kind: ProxyKind.Socks5 or ProxyKind.Http } proxy && proxy.Host.Length > 0)
                text.Append("    # Connects through the ").Append(proxy.Kind == ProxyKind.Http ? "HTTP CONNECT" : "SOCKS5").Append(" proxy ").Append(proxy.Host).Append(':').Append(proxy.Port.ToString(CultureInfo.InvariantCulture)).Append(", which ssh needs a ProxyCommand for.\n");

            if (resolved.UseAgent && host.KeyPath is null && resolved.Identity is null) Line(text, "IdentityAgent", "SSH_AUTH_SOCK");
            if (host.ForwardAgent) Line(text, "ForwardAgent", "yes");
            if (host.ForwardX11) Line(text, "ForwardX11", "yes");

            foreach (PortForward forward in host.Forwards)
            {
                string listen = Endpoint(forward.BindAddress, forward.BindPort);
                switch (forward.Kind)
                {
                    case ForwardKind.Local:
                        Line(text, "LocalForward", $"{listen} {Endpoint(forward.TargetHost, forward.TargetPort)}");
                        break;
                    case ForwardKind.Remote:
                        Line(text, "RemoteForward", $"{listen} {Endpoint(forward.TargetHost, forward.TargetPort)}");
                        break;
                    case ForwardKind.Dynamic:
                        Line(text, "DynamicForward", listen);
                        break;
                }
            }

            if (host.Environment.Count > 0)
                Line(text, "SetEnv", string.Join(' ', host.Environment.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={Quote(v.Value)}")));

            if (host.RemoteCommand is { Length: > 0 } remote)
            {
                Line(text, "RemoteCommand", remote);
                Line(text, "RequestTTY", "yes");
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// A name ssh can be given for each server: the server's own name where it is one word, else its words joined by
    /// hyphens, made unique with a number where two come out the same.
    /// </summary>
    public static Dictionary<Guid, string> Aliases(IEnumerable<HostEntry> servers)
    {
        var aliases = new Dictionary<Guid, string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HostEntry host in servers)
        {
            string alias = Alias(host.Name.Length > 0 ? host.Name : host.Address);
            string unique = alias;
            for (int n = 2; !taken.Add(unique); n++) unique = $"{alias}-{n}";
            aliases[host.Id] = unique;
        }

        return aliases;
    }

    // Letters, digits, dots, hyphens and underscores, as a Host pattern can hold without being read as a pattern.
    private static string Alias(string name)
    {
        var alias = new StringBuilder();
        foreach (char c in name.Trim())
        {
            if (char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '@') alias.Append(c);
            else if (alias.Length > 0 && alias[^1] != '-') alias.Append('-');
        }

        string result = alias.ToString().Trim('-');
        return result.Length > 0 ? result : "server";
    }

    private static string Endpoint(string address, int port) =>
        (address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address) + ":" + port.ToString(CultureInfo.InvariantCulture);

    // A ProxyCommand, RemoteCommand or SetEnv list is a line of words on purpose, and is written as it is.
    private static readonly HashSet<string> WordLines = new(StringComparer.Ordinal) { "ProxyCommand", "RemoteCommand", "SetEnv", "LocalForward", "RemoteForward" };

    private static void Line(StringBuilder text, string keyword, string value) =>
        text.Append("    ").Append(keyword).Append(' ').Append(WordLines.Contains(keyword) ? OneLine(value) : Quote(value)).Append('\n');

    // ssh_config splits a value at spaces unless it is in double quotes, and has no way to write a double quote
    // inside them. A line break would end the setting, so none ever gets through.
    private static string Quote(string value)
    {
        string line = OneLine(value);
        return line.Contains(' ', StringComparison.Ordinal) ? $"\"{line.Replace("\"", string.Empty, StringComparison.Ordinal)}\"" : line;
    }

    private static string OneLine(string value) => string.Concat(value.Select(c => char.IsControl(c) ? ' ' : c)).Trim();
}

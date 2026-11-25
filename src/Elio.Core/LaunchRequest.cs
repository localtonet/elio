using System.Globalization;

namespace Elio.Core;

/// <summary>What a launch of Elio, or a link handed to it, asks for.</summary>
public enum LaunchKind
{
    /// <summary>Nothing in particular: a new tab with the usual local shell when Elio is already running.</summary>
    NewTab,

    /// <summary>An SSH connection to an address, matched to a saved server where there is one.</summary>
    Ssh,

    /// <summary>A saved server, by its name.</summary>
    Connect,

    /// <summary>A saved workspace, by its name.</summary>
    Workspace,

    /// <summary>Another elio:// link, such as the one that adds a device to sync.</summary>
    Link,

    Help,
    Version,

    /// <summary>The arguments could not be understood. <see cref="LaunchRequest.Error"/> says why.</summary>
    Invalid,
}

/// <summary>Where an SSH connection asked for on the command line or in a link goes. An empty user leaves it to the saved server, or asks.</summary>
public sealed record SshTarget(string Host, int Port, string User, string? KeyPath = null);

/// <summary>
/// A launch of Elio as the command line or a link describes it. Reading it here, apart from the window, keeps what
/// is accepted in one place that tests can reach.
/// </summary>
/// <param name="FromLink">
/// Whether it came from a link opened somewhere else, such as a web page, rather than from a command the user
/// typed. A link is only acted on after the user agrees, because anyone can put one in front of them.
/// </param>
public sealed record LaunchRequest(LaunchKind Kind, string? Name = null, SshTarget? Target = null, string? Link = null, bool FromLink = false, string? Error = null)
{
    public const string SshScheme = "ssh";
    public const string ElioScheme = "elio";

    private const string ConnectPath = "connect";
    private const string WorkspacePath = "workspace";

    // A name in DNS is at most 253 characters. Nothing longer is an address.
    private const int LongestHost = 253;
    private const int LongestUser = 256;
    private const int LongestName = 512;

    public static LaunchRequest NewTab { get; } = new(LaunchKind.NewTab);

    /// <summary>What <c>elio --help</c> prints.</summary>
    public static string Usage =>
        "Usage:\n" +
        "  elio                              Open Elio, or a new tab in the Elio that is running\n" +
        "  elio ssh [-p port] [-l user] [-i key] [user@]host[:port]\n" +
        "                                    Connect over SSH; a saved server with that address is used if there is one\n" +
        "  elio ssh://user@host:port         The same, as a link\n" +
        "  elio connect <server name>        Connect to a saved server\n" +
        "  elio workspace <workspace name>   Open a saved workspace\n" +
        "  elio --mcp                        Run as a Model Context Protocol server for AI tools\n" +
        "  elio --version                    Print the version\n";

    /// <summary>Reads the arguments Elio was started with, the program's own name not included.</summary>
    public static LaunchRequest Parse(IReadOnlyList<string> args)
    {
        // Arguments that belong to Elio's own machinery, such as the one an update passes, are not requests.
        List<string> words = args.Where(a => !a.StartsWith("--wait-for-exit=", StringComparison.Ordinal)).ToList();
        if (words.Count == 0) return NewTab;

        string first = words[0];
        switch (first)
        {
            case "-h" or "--help" or "help" or "/?":
                return new(LaunchKind.Help);
            case "-v" or "--version" or "version":
                return new(LaunchKind.Version);
            case "ssh":
                return ParseSsh(words.Skip(1).ToList());
            case "connect":
                return NamedRequest(LaunchKind.Connect, words.Skip(1), "connect", fromLink: false);
            case "workspace":
                return NamedRequest(LaunchKind.Workspace, words.Skip(1), "workspace", fromLink: false);
        }

        // A link is how the operating system hands one over: the only argument, as it was clicked.
        if (words.Count == 1 && first.Contains("://", StringComparison.Ordinal)) return FromLinkText(first);

        return Invalid(string.Format(CultureInfo.InvariantCulture, "Unknown command \"{0}\". Run elio --help to see what Elio understands.", first));
    }

    /// <summary>Reads a link opened from outside: ssh://, elio://connect/…, elio://workspace/…, or another elio:// link.</summary>
    public static LaunchRequest FromLinkText(string link)
    {
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out Uri? uri)) return Invalid("That is not a link Elio can open.");

        if (uri.Scheme.Equals(SshScheme, StringComparison.OrdinalIgnoreCase))
        {
            return ParseSshUri(uri) is { } target
                ? new(LaunchKind.Ssh, Target: target, FromLink: true)
                : Invalid("The ssh:// link does not name a server Elio can connect to.");
        }

        if (!uri.Scheme.Equals(ElioScheme, StringComparison.OrdinalIgnoreCase)) return Invalid("That is not a link Elio can open.");

        // elio://connect/<name>: the host part of the link is the word, and the path the name.
        string what = uri.Host;
        string name = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        if (what.Equals(ConnectPath, StringComparison.OrdinalIgnoreCase)) return NamedRequest(LaunchKind.Connect, [name], "elio://connect/", fromLink: true);
        if (what.Equals(WorkspacePath, StringComparison.OrdinalIgnoreCase)) return NamedRequest(LaunchKind.Workspace, [name], "elio://workspace/", fromLink: true);

        // Any other elio:// link, such as a sync link, goes on as it came.
        return new(LaunchKind.Link, Link: link.Trim(), FromLink: true);
    }

    /// <summary>The arguments to hand this request to a copy that is already running, so that it reads them the same way.</summary>
    public IReadOnlyList<string> ToArguments() => Kind switch
    {
        LaunchKind.Ssh when Target is { } t && FromLink => [ToSshLink(t)],
        LaunchKind.Ssh when Target is { } t => SshArguments(t),
        LaunchKind.Connect when FromLink => [$"{ElioScheme}://{ConnectPath}/{Uri.EscapeDataString(Name ?? string.Empty)}"],
        LaunchKind.Connect => ["connect", Name ?? string.Empty],
        LaunchKind.Workspace when FromLink => [$"{ElioScheme}://{WorkspacePath}/{Uri.EscapeDataString(Name ?? string.Empty)}"],
        LaunchKind.Workspace => ["workspace", Name ?? string.Empty],
        LaunchKind.Link => [Link ?? string.Empty],
        _ => [],
    };

    /// <summary>The ssh:// link for a target, as another program would write it.</summary>
    public static string ToSshLink(SshTarget target)
    {
        string host = target.Host.Contains(':', StringComparison.Ordinal) ? $"[{target.Host}]" : target.Host;
        string user = target.User.Length > 0 ? Uri.EscapeDataString(target.User) + "@" : string.Empty;
        string port = target.Port == 22 ? string.Empty : ":" + target.Port.ToString(CultureInfo.InvariantCulture);
        return $"{SshScheme}://{user}{host}{port}";
    }

    private static List<string> SshArguments(SshTarget target)
    {
        var words = new List<string> { "ssh" };
        if (target.Port != 22) words.AddRange(["-p", target.Port.ToString(CultureInfo.InvariantCulture)]);
        if (target.User.Length > 0) words.AddRange(["-l", target.User]);
        if (target.KeyPath is { Length: > 0 } key) words.AddRange(["-i", key]);
        words.Add(target.Host.Contains(':', StringComparison.Ordinal) ? $"[{target.Host}]" : target.Host);
        return words;
    }

    private static LaunchRequest NamedRequest(LaunchKind kind, IEnumerable<string> words, string command, bool fromLink)
    {
        // A name with spaces may come as one quoted argument or as several; either way it is the same name.
        string name = string.Join(' ', words).Trim();
        if (name.Length == 0) return Invalid(string.Format(CultureInfo.InvariantCulture, "{0} needs a name.", command));
        if (name.Length > LongestName || name.Any(char.IsControl)) return Invalid("That name cannot be a name in Elio.");
        return new(kind, Name: name, FromLink: fromLink);
    }

    private static LaunchRequest ParseSsh(List<string> words)
    {
        int? port = null;
        string? user = null;
        string? key = null;
        string? destination = null;

        for (int i = 0; i < words.Count; i++)
        {
            string word = words[i];
            if (word is "-p" or "-l" or "-i")
            {
                if (i + 1 >= words.Count) return Invalid(string.Format(CultureInfo.InvariantCulture, "{0} needs a value.", word));
                string value = words[++i];
                switch (word)
                {
                    case "-p":
                        if (!TryPort(value, out int p)) return Invalid(string.Format(CultureInfo.InvariantCulture, "\"{0}\" is not a port.", value));
                        port = p;
                        break;
                    case "-l":
                        user = value;
                        break;
                    default:
                        key = value;
                        break;
                }
            }
            else if (word.StartsWith('-'))
            {
                return Invalid(string.Format(CultureInfo.InvariantCulture, "elio ssh does not know the option {0}. It understands -p, -l and -i.", word));
            }
            else if (destination is null)
            {
                destination = word;
            }
            else
            {
                // A command after the address is what ssh would run there instead of a shell. Elio opens a terminal.
                return Invalid("elio ssh opens a terminal on the server; it does not take a command to run there.");
            }
        }

        if (destination is null) return Invalid("elio ssh needs the server to connect to, such as user@example.com.");

        SshTarget? target = destination.Contains("://", StringComparison.Ordinal)
            ? Uri.TryCreate(destination, UriKind.Absolute, out Uri? uri) && uri.Scheme.Equals(SshScheme, StringComparison.OrdinalIgnoreCase) ? ParseSshUri(uri) : null
            : ParseDestination(destination);
        if (target is null) return Invalid(string.Format(CultureInfo.InvariantCulture, "\"{0}\" is not a server Elio can connect to.", destination));

        // Options given apart win over what the destination says, as they do for ssh.
        if (port is int givenPort) target = target with { Port = givenPort };
        if (user is not null)
        {
            if (!IsUser(user)) return Invalid(string.Format(CultureInfo.InvariantCulture, "\"{0}\" is not a user name.", user));
            target = target with { User = user };
        }

        if (key is not null) target = target with { KeyPath = key };
        return new(LaunchKind.Ssh, Target: target);
    }

    /// <summary>Reads [user@]host[:port], with an IPv6 address in brackets when it has a port.</summary>
    public static SshTarget? ParseDestination(string destination)
    {
        string user = string.Empty;
        string rest = destination;

        int at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            user = rest[..at];
            rest = rest[(at + 1)..];
            if (!IsUser(user)) return null;
        }

        int port = 22;
        string host;
        if (rest.StartsWith('['))
        {
            int close = rest.IndexOf(']', StringComparison.Ordinal);
            if (close < 0) return null;
            host = rest[1..close];
            string after = rest[(close + 1)..];
            if (after.Length > 0)
            {
                if (!after.StartsWith(':') || !TryPort(after[1..], out port)) return null;
            }
        }
        else if (rest.Count(c => c == ':') == 1)
        {
            int colon = rest.IndexOf(':', StringComparison.Ordinal);
            host = rest[..colon];
            if (!TryPort(rest[(colon + 1)..], out port)) return null;
        }
        else
        {
            // No colon, or an IPv6 address without brackets, which then cannot carry a port.
            host = rest;
        }

        return IsHost(host) ? new SshTarget(host, port, user) : null;
    }

    private static SshTarget? ParseSshUri(Uri uri)
    {
        // ssh://user;fingerprint=...@host: what follows the semicolon describes the connection, it is not the name.
        string user = Uri.UnescapeDataString(uri.UserInfo);
        int semicolon = user.IndexOf(';', StringComparison.Ordinal);
        if (semicolon >= 0) user = user[..semicolon];
        int colon = user.IndexOf(':', StringComparison.Ordinal);

        // A password in a link is never used: it would have been readable by everything the link passed through.
        if (colon >= 0) user = user[..colon];
        if (user.Length > 0 && !IsUser(user)) return null;

        string host = uri.IdnHost.Trim('[', ']');
        int port = uri.IsDefaultPort || uri.Port <= 0 ? 22 : uri.Port;
        return IsHost(host) && port is > 0 and <= 65535 ? new SshTarget(host, port, user) : null;
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    // Letters, digits, dots, hyphens and underscores, or an IPv6 address. Never something that starts with a hyphen,
    // which a program further along could take for an option.
    private static bool IsHost(string host) =>
        host.Length is > 0 and <= LongestHost
        && !host.StartsWith('-')
        && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '%');

    // A user may be "me@corp.example", or "CORP\me" on a Windows server: only spaces and control characters are never part of one.
    private static bool IsUser(string user) =>
        user.Length is > 0 and <= LongestUser
        && !user.StartsWith('-')
        && !user.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));

    private static LaunchRequest Invalid(string error) => new(LaunchKind.Invalid, Error: error);

    /// <summary>
    /// The saved SSH server a target means: same address, same port and, where the target names one, the same user.
    /// <paramref name="effective"/> gives a server's user and port as it connects, group defaults included.
    /// </summary>
    public static HostEntry? FindSaved(IEnumerable<HostEntry> hosts, SshTarget target, Func<HostEntry, (string User, int Port)> effective) =>
        hosts
            .Where(h => h.Kind == HostKind.Ssh && h.Address.Equals(target.Host, StringComparison.OrdinalIgnoreCase))
            .Select(h => (Host: h, Settings: effective(h)))
            .Where(x => x.Settings.Port == target.Port && (target.User.Length == 0 || x.Settings.User == target.User))

            // A server with the user spelled out is a closer match than one that leaves it open.
            .OrderByDescending(x => x.Settings.User == target.User)
            .ThenBy(x => x.Host.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Host)
            .FirstOrDefault();
}

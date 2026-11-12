using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Elio.Core;

/// <summary>Reads servers out of other tools' settings. Secrets are never imported.</summary>
public static partial class HostImport
{
    // The same limit ssh applies, which also stops a file that includes itself.
    private const int MaxIncludeDepth = 16;

    private sealed record ConfigBlock(string[] Patterns, List<(string Key, string Value)> Options);

    /// <summary>The servers named in an OpenSSH client config. Wildcard entries only contribute defaults.</summary>
    /// <param name="text">The config file's contents.</param>
    /// <param name="readInclude">Returns the contents of the files an Include line refers to.</param>
    public static List<HostEntry> FromSshConfig(string text, Func<string, IEnumerable<string>> readInclude)
    {
        List<ConfigBlock> blocks = ParseBlocks(text, readInclude);
        List<string> aliases = blocks
            .SelectMany(b => b.Patterns)
            .Where(p => !p.StartsWith('!') && p.IndexOfAny(['*', '?']) < 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var hosts = new List<HostEntry>();
        var jumpsByHost = new Dictionary<HostEntry, string>();

        foreach (string alias in aliases)
        {
            Dictionary<string, string> first = [];
            var forwards = new List<PortForward>();

            // ssh takes the first value it meets for each option, while forwards add up.
            foreach (ConfigBlock block in blocks.Where(b => Matches(b.Patterns, alias)))
            {
                foreach (var (key, value) in block.Options)
                {
                    if (key == "localforward") AddForward(forwards, ForwardKind.Local, value);
                    else if (key == "remoteforward") AddForward(forwards, ForwardKind.Remote, value);
                    else if (key == "dynamicforward") AddForward(forwards, ForwardKind.Dynamic, value);
                    else first.TryAdd(key, value);
                }
            }

            string address = first.GetValueOrDefault("hostname", alias).Replace("%h", alias, StringComparison.Ordinal);
            string user = first.GetValueOrDefault("user", Environment.UserName);
            string? identity = first.GetValueOrDefault("identityfile") is { } file and not "none" ? ExpandTokens(file, address, user) : null;

            var host = new HostEntry
            {
                Name = alias,
                Address = address,
                Port = int.TryParse(first.GetValueOrDefault("port"), out int port) ? port : 22,
                User = user,
                KeyPath = identity,

                // ssh tries the agent whenever one is running, and a host with no key of its own depends on that.
                UseAgent = identity is null && first.GetValueOrDefault("identityagent") != "none",
                ForwardAgent = Yes(first.GetValueOrDefault("forwardagent")),
                ForwardX11 = Yes(first.GetValueOrDefault("forwardx11")),
                RemoteCommand = first.GetValueOrDefault("remotecommand"),
                Forwards = forwards,
            };
            hosts.Add(host);
            if (first.TryGetValue("proxyjump", out string? jump) && !jump.Equals("none", StringComparison.OrdinalIgnoreCase)) jumpsByHost[host] = jump;
        }

        foreach (var (host, jump) in jumpsByHost) ResolveJumps(host, jump, hosts);
        return hosts;
    }

    private static bool Yes(string? value) => string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private static List<ConfigBlock> ParseBlocks(string text, Func<string, IEnumerable<string>> readInclude)
    {
        var blocks = new List<ConfigBlock>();
        ConfigBlock current = new(["*"], []);
        blocks.Add(current);

        void Consume(string content, int depth)
        {
            foreach (string raw in content.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                Match match = Regex.Match(line, @"^([A-Za-z]+)(?:\s*=\s*|\s+)(.*)$");
                if (!match.Success) continue;

                string key = match.Groups[1].Value.ToLowerInvariant();
                string value = match.Groups[2].Value.Trim().Trim('"');

                if (key == "include")
                {
                    if (depth >= MaxIncludeDepth) continue;

                    // Settings after an Include still belong to the block it appeared in.
                    ConfigBlock enclosing = current;
                    foreach (string included in value.Split(' ', StringSplitOptions.RemoveEmptyEntries).SelectMany(readInclude)) Consume(included, depth + 1);
                    current = enclosing;
                }
                else if (key == "host")
                {
                    current = new ConfigBlock(value.Split(' ', StringSplitOptions.RemoveEmptyEntries), []);
                    blocks.Add(current);
                }
                else if (key == "match")
                {
                    // Conditions depend on things only known at connection time, so these settings are left out.
                    current = new ConfigBlock([], []);
                    blocks.Add(current);
                }
                else
                {
                    current.Options.Add((key, value));
                }
            }
        }

        Consume(text, 0);
        return blocks;
    }

    private static bool Matches(string[] patterns, string alias)
    {
        bool matched = false;
        foreach (string pattern in patterns)
        {
            bool negated = pattern.StartsWith('!');
            string glob = "^" + Regex.Escape(negated ? pattern[1..] : pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            if (!Regex.IsMatch(alias, glob)) continue;
            if (negated) return false;
            matched = true;
        }
        return matched;
    }

    private static void AddForward(List<PortForward> forwards, ForwardKind kind, string value)
    {
        // The config separates the two halves with a space where the command line uses a colon.
        string spec = string.Join(':', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (PortForward.FromSpec(kind, spec) is PortForward forward) forwards.Add(forward);
    }

    private static string ExpandTokens(string path, string address, string user)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith('~')) path = home + path[1..];
        return path.Replace("%d", home, StringComparison.Ordinal).Replace("%h", address, StringComparison.Ordinal).Replace("%r", user, StringComparison.Ordinal);
    }

    private static void ResolveJumps(HostEntry host, string jump, List<HostEntry> hosts)
    {
        foreach (string hop in jump.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            HostEntry? target = hosts.FirstOrDefault(h => h.Name == hop);
            if (target is null)
            {
                // A hop written out as user@host:port has no entry of its own yet.
                Match parts = Regex.Match(hop, @"^(?:(?<user>[^@]+)@)?(?<host>[^:]+)(?::(?<port>\d+))?$");
                if (!parts.Success) continue;

                target = new HostEntry
                {
                    Name = hop,
                    Address = parts.Groups["host"].Value,
                    User = parts.Groups["user"].Success ? parts.Groups["user"].Value : host.User,
                    Port = parts.Groups["port"].Success ? int.Parse(parts.Groups["port"].Value) : 22,
                };
                hosts.Add(target);
            }

            host.JumpHosts.Add(target.Id);
        }
    }

    /// <summary>Saved SSH sessions from PuTTY, on Windows.</summary>
    public static List<HostEntry> FromPutty()
    {
        var hosts = new List<HostEntry>();
        if (!OperatingSystem.IsWindows()) return hosts;

        using RegistryKey? sessions = Registry.CurrentUser.OpenSubKey(@"Software\SimonTatham\PuTTY\Sessions");
        if (sessions is null) return hosts;

        foreach (string name in sessions.GetSubKeyNames())
        {
            using RegistryKey? session = sessions.OpenSubKey(name);
            if (session?.GetValue("Protocol") as string != "ssh" || session.GetValue("HostName") is not string { Length: > 0 } address) continue;

            string? key = session.GetValue("PublicKeyFile") as string;
            hosts.Add(new HostEntry
            {
                Name = Uri.UnescapeDataString(name),
                Address = address,
                Port = session.GetValue("PortNumber") is int port ? port : 22,
                User = session.GetValue("UserName") as string ?? Environment.UserName,
                KeyPath = string.IsNullOrEmpty(key) ? null : key,
            });
        }

        return hosts;
    }
}

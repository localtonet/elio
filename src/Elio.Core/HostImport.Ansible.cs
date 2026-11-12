using System.Text;
using System.Text.RegularExpressions;

namespace Elio.Core;

/// <summary>A file to import servers from could not be understood. It says which line, so that line can be found and fixed.</summary>
public sealed class ImportFormatException(int line, string message) : FormatException(message)
{
    /// <summary>The line the import stopped at, counting from one. Zero when it is the file as a whole.</summary>
    public int Line => line;
}

/// <content>Ansible inventories, in the INI form and in the usual YAML layout. Groups become groups here, nested as they are there.</content>
public static partial class HostImport
{
    // The two groups every inventory has without naming them. They hold everything, so they say nothing about a server.
    private const string AllGroup = "all";
    private const string UngroupedGroup = "ungrouped";

    // A pattern such as a[1:100000] must not be able to fill the list, or the memory.
    private const int MaxHostsPerPattern = 4096;

    private sealed class InventoryGroup
    {
        public List<string> Hosts { get; } = [];
        public List<string> Children { get; } = [];
        public Dictionary<string, string> Vars { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>What both forms of inventory are read into, before any of it becomes a server.</summary>
    private sealed class Inventory
    {
        private readonly Dictionary<string, InventoryGroup> _groups = new(StringComparer.Ordinal);

        /// <summary>Group names in the order the file first mentions them.</summary>
        public List<string> GroupOrder { get; } = [];

        /// <summary>Host names in the order the file first mentions them.</summary>
        public List<string> HostOrder { get; } = [];

        public Dictionary<string, Dictionary<string, string>> HostVars { get; } = new(StringComparer.Ordinal);

        public InventoryGroup Group(string name)
        {
            if (_groups.TryGetValue(name, out InventoryGroup? group)) return group;

            GroupOrder.Add(name);
            return _groups[name] = new InventoryGroup();
        }

        public InventoryGroup? Find(string name) => _groups.GetValueOrDefault(name);

        public void AddHost(string group, string name, IEnumerable<KeyValuePair<string, string>> vars)
        {
            // "host:2222" is the short way of giving a port. An address with several colons is IPv6, and is left whole.
            Match port = Regex.Match(name, @"^([^:]+):(\d{1,5})$");
            if (port.Success) name = port.Groups[1].Value;

            if (!HostVars.TryGetValue(name, out Dictionary<string, string>? known))
            {
                HostOrder.Add(name);
                HostVars[name] = known = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            if (port.Success) known["ansible_port"] = port.Groups[2].Value;
            foreach (var (key, value) in vars) known[key] = value;

            InventoryGroup into = Group(group);
            if (!into.Hosts.Contains(name)) into.Hosts.Add(name);
        }
    }

    private static ImportFormatException NotUnderstood(int line) =>
        new(line, L.T("Line {0}: the import does not understand this line.", line));

    private static ImportFormatException NotSupported(int line) =>
        new(line, L.T("Line {0}: lists, values in brackets or braces, anchors, tags and text that runs over several lines are not supported.", line));

    private static ImportFormatException BadIndentation(int line) =>
        new(line, L.T("Line {0}: the indentation does not match the lines above.", line));

    /// <summary>
    /// The servers and groups of an Ansible inventory. The file's name decides between the two forms where it
    /// says which it is, and otherwise the first line does.
    /// </summary>
    /// <exception cref="ImportFormatException">A line is not something an inventory holds. Nothing is guessed at.</exception>
    public static HostLibrary FromAnsibleInventory(string text, string? fileName = null)
    {
        string extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        bool yaml = extension switch
        {
            ".yml" or ".yaml" => true,
            ".ini" or ".cfg" => false,
            _ => LooksLikeYaml(text),
        };
        return yaml ? FromAnsibleYaml(text) : FromAnsibleIni(text);
    }

    // An INI inventory begins with a section or a host line. A YAML one begins with "---" or a key that has nothing after its colon.
    private static bool LooksLikeYaml(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            return line == "---" || (line.EndsWith(':') && !line.StartsWith('['));
        }

        return false;
    }

    /// <summary>The servers and groups of an inventory in the INI form.</summary>
    /// <exception cref="ImportFormatException">A line is not a section, a host or a variable.</exception>
    public static HostLibrary FromAnsibleIni(string text)
    {
        var inventory = new Inventory();
        string section = UngroupedGroup;
        string kind = "hosts";
        int number = 0;

        foreach (string raw in text.Split('\n'))
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;

            if (line.StartsWith('['))
            {
                Match header = Regex.Match(line, @"^\[([^\[\]:]+)(?::(children|vars))?\]\s*(?:#.*)?$");
                if (!header.Success) throw NotUnderstood(number);

                section = header.Groups[1].Value.Trim();
                kind = header.Groups[2].Success ? header.Groups[2].Value : "hosts";
                inventory.Group(section);
                continue;
            }

            if (kind == "vars")
            {
                // The value is everything after the first "=", so it may itself hold spaces and "=".
                int equals = line.IndexOf('=');
                if (equals <= 0) throw NotUnderstood(number);
                inventory.Group(section).Vars[line[..equals].Trim()] = Unquote(line[(equals + 1)..].Trim());
                continue;
            }

            List<string> words = SplitWords(line, number);
            if (words.Count == 0) continue;

            if (kind == "children")
            {
                if (words.Count != 1) throw NotUnderstood(number);
                InventoryGroup parent = inventory.Group(section);
                if (!parent.Children.Contains(words[0])) parent.Children.Add(words[0]);
                inventory.Group(words[0]);
                continue;
            }

            var vars = new List<KeyValuePair<string, string>>();
            foreach (string word in words.Skip(1))
            {
                int equals = word.IndexOf('=');
                if (equals <= 0) throw NotUnderstood(number);
                vars.Add(new(word[..equals], word[(equals + 1)..]));
            }

            foreach (string name in ExpandRanges(words[0], number)) inventory.AddHost(section, name, vars);
        }

        return ToLibrary(inventory);
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0] ? value[1..^1] : value;

    /// <summary>
    /// Splits a host line the way a shell would: at spaces, except inside quotes, which are then dropped. What
    /// follows a "#" that starts a word is a comment.
    /// </summary>
    private static List<string> SplitWords(string line, int number)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        bool inWord = false;
        char quote = '\0';

        foreach (char c in line)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else word.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inWord = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord) words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
            else if (c == '#' && !inWord)
            {
                break;
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }

        if (quote != '\0') throw NotUnderstood(number);
        if (inWord) words.Add(word.ToString());
        return words;
    }

    /// <summary>
    /// The host names a pattern stands for: "web[01:03].example.com" is three of them, and keeps the leading zero.
    /// Letters work as numbers do, and a third part is the step.
    /// </summary>
    internal static List<string> ExpandRanges(string pattern, int line)
    {
        var names = new List<string>();
        Expand(pattern);
        return names;

        void Expand(string text)
        {
            Match range = Regex.Match(text, @"\[([^\[\]:]+):([^\[\]:]+)(?::(\d+))?\]");
            if (!range.Success)
            {
                // A bracket that opens the name is an IPv6 address written out. One further in can only be a range gone wrong.
                if (text.IndexOf('[') > 0 || names.Count >= MaxHostsPerPattern) throw NotUnderstood(line);
                names.Add(text);
                return;
            }

            string head = text[..range.Index], tail = text[(range.Index + range.Length)..];
            string from = range.Groups[1].Value, to = range.Groups[2].Value;
            int step = range.Groups[3].Success ? int.Parse(range.Groups[3].Value) : 1;
            if (step < 1) throw NotUnderstood(line);

            if (IsNumber(from) && IsNumber(to))
            {
                int first = int.Parse(from), last = int.Parse(to);
                if (last < first || (last - first) / step >= MaxHostsPerPattern) throw NotUnderstood(line);

                // Written as 01, the numbers keep that width: 01, 02 … 10.
                int width = from.Length > 1 && from[0] == '0' ? from.Length : 0;
                for (int i = first; i <= last; i += step) Expand(head + i.ToString().PadLeft(width, '0') + tail);
            }
            else if (from.Length == 1 && to.Length == 1 && char.IsAsciiLetter(from[0]) && char.IsAsciiLetter(to[0]) && from[0] <= to[0])
            {
                for (int c = from[0]; c <= to[0]; c += step) Expand(head + (char)c + tail);
            }
            else
            {
                throw NotUnderstood(line);
            }
        }

        static bool IsNumber(string text) => text.Length is > 0 and <= 9 && text.All(char.IsAsciiDigit);
    }

    /// <summary>One value in a YAML file: text, a mapping, or nothing at all.</summary>
    internal sealed class YamlNode(int line)
    {
        public int Line => line;
        public string? Scalar { get; init; }
        public List<(string Key, int Line, YamlNode Value)>? Map { get; init; }
    }

    private readonly record struct YamlLine(int Number, int Indent, string Content);

    /// <summary>
    /// Reads the part of YAML an inventory is written in: mappings nested by indentation, with plain or quoted
    /// text as values. Everything else YAML can do stops the import at the line it appears on, because reading
    /// it half right would add servers with the wrong address.
    /// </summary>
    internal static YamlNode ParseYaml(string text)
    {
        var lines = new List<YamlLine>();
        int number = 0;
        foreach (string raw in text.Split('\n'))
        {
            number++;
            string line = raw.TrimEnd('\r', ' ', '\t');
            string content = line.TrimStart(' ');
            if (content.Length == 0 || content.StartsWith('#')) continue;

            // One "---" may open the file. A second one starts another document, which an inventory does not have.
            if (content == "---" && lines.Count == 0 && line.Length == 3) continue;
            if (content is "---" or "...") throw NotSupported(number);
            if (content[0] == '\t') throw BadIndentation(number);

            lines.Add(new YamlLine(number, line.Length - content.Length, content));
        }

        if (lines.Count == 0) throw NotUnderstood(0);

        int index = 0;
        YamlNode root = ParseYamlMap(lines, ref index, lines[0].Indent);
        if (index < lines.Count) throw BadIndentation(lines[index].Number);
        return root;
    }

    private static YamlNode ParseYamlMap(List<YamlLine> lines, ref int index, int indent)
    {
        var map = new List<(string Key, int Line, YamlNode Value)>();
        int first = lines[index].Number;

        while (index < lines.Count && lines[index].Indent >= indent)
        {
            YamlLine line = lines[index];
            if (line.Indent > indent) throw BadIndentation(line.Number);

            var (key, rest) = SplitYamlKey(line.Content, line.Number);
            if (map.Any(entry => entry.Key == key)) throw NotUnderstood(line.Number);
            index++;

            YamlNode value;
            if (YamlScalar(rest, line.Number, out string? scalar)) value = new YamlNode(line.Number) { Scalar = scalar };
            else if (index < lines.Count && lines[index].Indent > indent) value = ParseYamlMap(lines, ref index, lines[index].Indent);
            else value = new YamlNode(line.Number);

            map.Add((key, line.Number, value));
        }

        return new YamlNode(first) { Map = map };
    }

    // What YAML would read as something other than plain text, when a key or a value starts with it.
    private const string YamlIndicators = "[]{}&*!|>%@`,?";

    private static (string Key, string After) SplitYamlKey(string content, int line)
    {
        if (content == "-" || content.StartsWith("- ", StringComparison.Ordinal)) throw NotSupported(line);
        if (YamlIndicators.Contains(content[0])) throw NotSupported(line);

        if (content[0] is '"' or '\'')
        {
            string key = YamlQuoted(content, line, out int end);
            string after = content[end..].TrimStart();
            if (!after.StartsWith(':') || (after.Length > 1 && after[1] != ' ')) throw NotUnderstood(line);
            return (key, after[1..].Trim());
        }

        // The key ends at the first colon that has a space or the end of the line after it, so "fe80::1" stays whole.
        for (int i = 0; i < content.Length; i++)
        {
            if (content[i] != ':' || (i + 1 < content.Length && content[i + 1] != ' ')) continue;
            if (i == 0) throw NotUnderstood(line);
            return (content[..i].TrimEnd(), content[(i + 1)..].Trim());
        }

        // Text on a line of its own, where a "key: value" belongs.
        throw NotUnderstood(line);
    }

    /// <summary>Reads what follows a key's colon. False when nothing does, or only a comment or a null.</summary>
    private static bool YamlScalar(string rest, int line, out string? scalar)
    {
        scalar = null;
        if (rest.Length == 0 || rest[0] == '#') return false;

        if (rest[0] is '"' or '\'')
        {
            scalar = YamlQuoted(rest, line, out int end);
            string after = rest[end..].Trim();
            if (after.Length > 0 && after[0] != '#') throw NotUnderstood(line);
            return true;
        }

        if (YamlIndicators.Contains(rest[0]) || rest == "-" || rest.StartsWith("- ", StringComparison.Ordinal)) throw NotSupported(line);

        int comment = rest.IndexOf(" #", StringComparison.Ordinal);
        string plain = (comment < 0 ? rest : rest[..comment]).TrimEnd();

        // "a: b: c" is not valid, and guessing which colon was meant is not this parser's place.
        if (plain.Contains(": ", StringComparison.Ordinal) || plain.EndsWith(':')) throw NotUnderstood(line);
        if (plain is "~" or "null" or "Null" or "NULL") return false;

        scalar = plain;
        return true;
    }

    /// <summary>Reads quoted text that starts the given string, and says where it ended.</summary>
    private static string YamlQuoted(string text, int line, out int end)
    {
        char quote = text[0];
        var value = new StringBuilder();

        for (int i = 1; i < text.Length; i++)
        {
            char c = text[i];
            if (quote == '\'' && c == '\'')
            {
                // Inside single quotes, two of them are one that belongs to the text.
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    value.Append('\'');
                    i++;
                    continue;
                }

                end = i + 1;
                return value.ToString();
            }

            if (quote == '"' && c == '"')
            {
                end = i + 1;
                return value.ToString();
            }

            if (quote == '"' && c == '\\')
            {
                if (++i >= text.Length) break;
                value.Append(text[i] switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    'n' => '\n',
                    't' => '\t',
                    _ => throw NotSupported(line),
                });
                continue;
            }

            value.Append(c);
        }

        // The quote is closed on a later line, if at all.
        throw NotSupported(line);
    }

    /// <summary>The servers and groups of an inventory in the YAML layout: groups holding "hosts", "vars" and "children".</summary>
    /// <exception cref="ImportFormatException">A line uses YAML this import does not read, or is not part of an inventory.</exception>
    public static HostLibrary FromAnsibleYaml(string text)
    {
        var inventory = new Inventory();
        foreach (var (name, _, node) in ParseYaml(text).Map!) ReadYamlGroup(inventory, name, node, depth: 0);
        return ToLibrary(inventory);
    }

    private static void ReadYamlGroup(Inventory inventory, string name, YamlNode node, int depth)
    {
        // Indentation cannot nest without end, but a file made to try should not take the stack with it.
        if (depth > 64 || node.Scalar is not null) throw NotUnderstood(node.Line);

        InventoryGroup group = inventory.Group(name);
        foreach (var (key, line, value) in node.Map ?? [])
        {
            if (value.Scalar is not null) throw NotUnderstood(line);

            switch (key)
            {
                case "hosts":
                    foreach (var (pattern, hostLine, hostVars) in value.Map ?? [])
                    {
                        if (hostVars.Scalar is not null) throw NotUnderstood(hostLine);
                        foreach (string host in ExpandRanges(pattern, hostLine)) inventory.AddHost(name, host, Scalars(hostVars));
                    }

                    break;

                case "vars":
                    foreach (var (variable, text) in Scalars(value)) group.Vars[variable] = text;
                    break;

                case "children":
                    foreach (var (child, _, childNode) in value.Map ?? [])
                    {
                        if (!group.Children.Contains(child)) group.Children.Add(child);
                        ReadYamlGroup(inventory, child, childNode, depth + 1);
                    }

                    break;

                default:
                    throw NotUnderstood(line);
            }
        }
    }

    // Variables that hold a mapping are somebody's settings for a playbook. None of the ones read here do.
    private static IEnumerable<KeyValuePair<string, string>> Scalars(YamlNode node) =>
        (node.Map ?? []).Where(entry => entry.Value.Scalar is not null).Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value.Scalar!));

    private static HostLibrary ToLibrary(Inventory inventory)
    {
        var library = new HostLibrary();
        var placed = new Dictionary<string, HostGroup>(StringComparer.Ordinal);
        var parents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string name in inventory.GroupOrder)
            foreach (string child in inventory.Find(name)!.Children)
                (parents.TryGetValue(child, out List<string>? of) ? of : (parents[child] = [])).Add(name);

        bool Real(string group) => group is not (AllGroup or UngroupedGroup);

        // The group here that stands for an inventory group, made on first use with its parents before it. A
        // group that is its own ancestor ends up at the top level instead of going round for ever.
        Guid? Place(string name, HashSet<string> placing)
        {
            if (!Real(name) || !placing.Add(name)) return null;
            if (placed.TryGetValue(name, out HostGroup? known)) return known.Id;

            string? parent = parents.GetValueOrDefault(name)?.FirstOrDefault(Real);
            var group = new HostGroup { Name = name, ParentId = parent is null ? null : Place(parent, placing) };
            placed[name] = group;
            library.Groups.Add(group);
            return group.Id;
        }

        // A group's variables, with those of the groups above it underneath, so the nearer one wins.
        void Collect(string name, Dictionary<string, string> into, HashSet<string> seen)
        {
            if (!seen.Add(name)) return;
            foreach (string parent in parents.GetValueOrDefault(name) ?? []) Collect(parent, into, seen);
            foreach (var (key, value) in inventory.Find(name)?.Vars ?? []) into[key] = value;
        }

        foreach (string name in inventory.HostOrder)
        {
            List<string> memberships = inventory.GroupOrder.Where(g => Real(g) && inventory.Find(g)!.Hosts.Contains(name)).ToList();
            string? home = memberships.FirstOrDefault();

            var vars = new Dictionary<string, string>(StringComparer.Ordinal);
            Collect(AllGroup, vars, []);

            // The group the server is put in is read last of the groups, so its settings are the ones that hold.
            foreach (string group in memberships.Skip(1)) Collect(group, vars, [AllGroup]);
            if (home is not null) Collect(home, vars, [AllGroup]);
            foreach (var (variable, value) in inventory.HostVars[name]) vars[variable] = value;

            // A value built from other variables can only be worked out by Ansible itself, so it counts as not given.
            string? Var(params string[] keys) =>
                keys.Select(vars.GetValueOrDefault).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !value.Contains("{{", StringComparison.Ordinal));

            // The machine Ansible runs on is in many inventories, and is not a server to connect to.
            if (Var("ansible_connection") == "local") continue;

            string address = Var("ansible_host", "ansible_ssh_host") ?? name;
            string user = Var("ansible_user", "ansible_ssh_user") ?? Environment.UserName;
            string? key = Var("ansible_ssh_private_key_file", "ansible_private_key_file") is { } file ? ExpandTokens(file, address, user) : null;

            library.Hosts.Add(new HostEntry
            {
                Name = name,
                Address = address,
                Port = int.TryParse(Var("ansible_port", "ansible_ssh_port"), out int port) && port is > 0 and <= 65535 ? port : 22,
                User = user,
                KeyPath = key,

                // Ansible goes through ssh, which tries the agent whenever no key is named.
                UseAgent = key is null,
                GroupId = home is null ? null : Place(home, []),

                // One server may be in several groups there and only one here, so the others are kept as tags.
                Tags = memberships.Skip(1).ToList(),
            });
        }

        return library;
    }
}

using System.Text;
using System.Text.Json;
using Elio.Connections;
using Elio.Core;

namespace Elio.Desktop;

/// <summary>
/// Elio as a Model Context Protocol server, for AI tools that can use one. Started with `elio --mcp`, it
/// speaks JSON-RPC over its input and output and shows no window.
/// </summary>
/// <remarks>
/// It can always list the saved servers, without anything secret. Running commands on them is off until
/// the user turns it on in Settings and again for each server it is to reach, and even then it only reaches
/// servers Elio has connected to before: the password or key must already be saved, and the server's key
/// already trusted. It never asks anyone anything, because there is nobody at this end to ask, so every
/// command it runs is written to a log in the data folder instead.
/// </remarks>
internal static class McpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private const string ListTool = "list_servers";
    private const string RunTool = "run_command";
    private const int MaxOutputCharacters = 64 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    private const int ParseError = -32700;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;

    public static async Task<int> RunAsync(Stream input, Stream output)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        while (await reader.ReadLineAsync().ConfigureAwait(false) is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            byte[]? reply = await HandleAsync(line).ConfigureAwait(false);
            if (reply is null) continue;

            await output.WriteAsync(reply).ConfigureAwait(false);
            output.WriteByte((byte)'\n');
            await output.FlushAsync().ConfigureAwait(false);
        }

        return 0;
    }

    /// <returns>The reply as JSON, or null for a notification, which gets none.</returns>
    private static async Task<byte[]?> HandleAsync(string line)
    {
        JsonDocument request;
        try
        {
            request = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return Reply(null, writer => WriteError(writer, ParseError, "The message is not valid JSON."));
        }

        using (request)
        {
            JsonElement root = request.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Reply(null, writer => WriteError(writer, ParseError, "The message is not a JSON object."));

            JsonElement? id = root.TryGetProperty("id", out JsonElement given) ? given.Clone() : null;
            string method = root.TryGetProperty("method", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString()! : string.Empty;
            JsonElement parameters = root.TryGetProperty("params", out JsonElement supplied) ? supplied : default;

            // A message without an id expects no answer, whatever it says.
            if (id is null) return null;

            switch (method)
            {
                case "initialize":
                    string version = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("protocolVersion", out JsonElement asked) && asked.ValueKind == JsonValueKind.String
                        ? asked.GetString()!
                        : ProtocolVersion;
                    return Reply(id, writer => WriteInitialize(writer, version));

                case "ping":
                    return Reply(id, writer =>
                    {
                        writer.WriteStartObject("result");
                        writer.WriteEndObject();
                    });

                case "tools/list":
                    return Reply(id, WriteTools);

                case "tools/call":
                    (string text, bool failed)? outcome = await CallAsync(parameters).ConfigureAwait(false);
                    return outcome is var (text, failed)
                        ? Reply(id, writer => WriteToolResult(writer, text, failed))
                        : Reply(id, writer => WriteError(writer, InvalidParams, "There is no such tool."));

                default:
                    return Reply(id, writer => WriteError(writer, MethodNotFound, $"Unknown method: {method}"));
            }
        }
    }

    private static byte[] Reply(JsonElement? id, Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (id is JsonElement value) value.WriteTo(writer);
            else writer.WriteNullValue();
            body(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteError(Utf8JsonWriter writer, int code, string message)
    {
        writer.WriteStartObject("error");
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
    }

    private static void WriteInitialize(Utf8JsonWriter writer, string version)
    {
        writer.WriteStartObject("result");
        writer.WriteString("protocolVersion", version);
        writer.WriteStartObject("capabilities");
        writer.WriteStartObject("tools");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartObject("serverInfo");
        writer.WriteString("name", "elio");
        writer.WriteString("version", typeof(McpServer).Assembly.GetName().Version?.ToString() ?? "0");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteTools(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("result");
        writer.WriteStartArray("tools");

        writer.WriteStartObject();
        writer.WriteString("name", ListTool);
        writer.WriteString("description", "Lists the servers saved in Elio: name, kind, address, user, group and tags. Never includes passwords or keys.");
        writer.WriteStartObject("inputSchema");
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteStartObject();
        writer.WriteString("name", RunTool);
        writer.WriteString("description", "Runs one command over SSH on a saved server and returns its exit code and output. Works only if the user has allowed it in Elio's settings and for that server, and has connected to that server from Elio before. Every command is written to a log the user can read.");
        writer.WriteStartObject("inputSchema");
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        writer.WriteStartObject("server");
        writer.WriteString("type", "string");
        writer.WriteString("description", "The server's name as list_servers gives it.");
        writer.WriteEndObject();
        writer.WriteStartObject("command");
        writer.WriteString("type", "string");
        writer.WriteString("description", "The command line to run.");
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartArray("required");
        writer.WriteStringValue("server");
        writer.WriteStringValue("command");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteToolResult(Utf8JsonWriter writer, string text, bool failed)
    {
        writer.WriteStartObject("result");
        writer.WriteStartArray("content");
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", text);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteBoolean("isError", failed);
        writer.WriteEndObject();
    }

    /// <returns>What the tool said and whether it failed, or null if there is no tool by that name.</returns>
    private static async Task<(string Text, bool Failed)?> CallAsync(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || !parameters.TryGetProperty("name", out JsonElement name)) return null;

        JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement given) && given.ValueKind == JsonValueKind.Object ? given : default;
        string Argument(string key) =>
            arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;

        // Read fresh each time: the user may have added a server or changed the setting since the last call.
        HostLibrary library = Stores.Hosts().Load();
        return name.GetString() switch
        {
            ListTool => (ListServers(library), false),
            RunTool => await RunCommandAsync(library, Argument("server"), Argument("command")).ConfigureAwait(false),
            _ => null,
        };
    }

    private static string ListServers(HostLibrary library)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (HostEntry host in library.Hosts.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteStartObject();
                writer.WriteString("name", host.Name);
                writer.WriteString("kind", host.Kind.ToString().ToLowerInvariant());
                writer.WriteString("address", host.Address);
                // What the server really connects with, which its group or its identity may have supplied.
                ResolvedHost resolved = ConnectionResolver.Resolve(host, library);
                if (host.Kind is HostKind.Ssh or HostKind.Telnet) writer.WriteNumber("port", resolved.Port);
                if (resolved.User.Length > 0) writer.WriteString("user", resolved.User);
                if (library.Groups.FirstOrDefault(g => g.Id == host.GroupId) is HostGroup group) writer.WriteString("group", group.Name);
                if (host.Tags.Count > 0)
                {
                    writer.WriteStartArray("tags");
                    foreach (string tag in host.Tags) writer.WriteStringValue(tag);
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task<(string Text, bool Failed)?> RunCommandAsync(HostLibrary library, string serverName, string command)
    {
        if (!Stores.Settings().Load().McpRunCommands)
            return ("Running commands is turned off. The user can allow it in Elio under Settings.", true);
        if (serverName.Length == 0 || command.Length == 0) return ("Both a server and a command are needed.", true);

        List<HostEntry> named = library.Hosts.Where(h => h.Name.Equals(serverName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count != 1) return (named.Count == 0 ? $"There is no server named \"{serverName}\"." : $"More than one server is named \"{serverName}\".", true);

        // The setting lets AI tools in at all; which servers they may touch is chosen one by one.
        if (!named[0].AllowMcpCommands)
            return ($"AI tools are not allowed to run commands on {named[0].Name}. The user can allow it in Elio: edit the server, open Advanced and tick \"AI tools may run commands on this server\".", true);

        var vault = new Vault();
        var known = new KnownHosts();
        var steps = new List<ResolvedHost>();
        string? problem = AddToPath(named[0], library, vault, steps, []);
        if (problem is not null) return (problem, true);

        // The same resolver the app's window uses: identities, group defaults and the proxy apply here too.
        List<SshEndpoint> path = SshEndpoints.ForPath(steps, vault.Get);

        // Nothing is run that cannot be written down afterwards.
        if (!CanRecord()) return ($"The command was not run, because it could not be recorded in {LogPath}.", true);

        string run = Guid.NewGuid().ToString("N");
        Activity.Started(run, named[0].Name, command);

        using var timeout = new CancellationTokenSource(CommandTimeout);
        CommandResult result = await SshCommandRunner.RunAsync(
            path,
            command,
            (endpoint, key) => known.Check(endpoint.Host, endpoint.Port, key.Fingerprint) == HostKeyStatus.Trusted,
            timeout.Token).ConfigureAwait(false);

        Record(named[0].Name, command, result.Failure is null ? $"exit {result.ExitCode}" : "failed");
        Activity.Ended(run, result);
        if (result.Failure is not null) return (result.Failure, true);

        var text = new StringBuilder($"exit code: {result.ExitCode}\n");
        if (result.Output.Length > 0) text.Append("\nstdout:\n").Append(Shorten(result.Output));
        if (result.Error.Length > 0) text.Append("\nstderr:\n").Append(Shorten(result.Error));
        return (text.ToString(), result.ExitCode != 0);
    }

    /// <summary>
    /// What the Elio window shows of this process while it works: each command as it starts, and what came back
    /// when it ended. The log below is the record; this is the view, with the output in it, kept to a size.
    /// </summary>
    private static class Activity
    {
        private const int MostOutput = 32 * 1024;
        private const long TrimAbove = 1024 * 1024;
        private const int KeepAfterTrim = 256 * 1024;

        private static string FilePath => Path.Combine(ElioPaths.DataDirectory, "mcp-activity.jsonl");

        public static void Started(string run, string server, string command) => Write(writer =>
        {
            writer.WriteString("id", run);
            writer.WriteString("t", "start");
            writer.WriteString("utc", DateTime.UtcNow);
            writer.WriteString("server", server);
            writer.WriteString("command", command);
        });

        public static void Ended(string run, CommandResult result) => Write(writer =>
        {
            writer.WriteString("id", run);
            writer.WriteString("t", "end");
            writer.WriteString("utc", DateTime.UtcNow);
            writer.WriteString("outcome", result.Failure is null ? $"exit {result.ExitCode}" : "failed");
            writer.WriteNumber("ms", (long)result.Duration.TotalMilliseconds);
            writer.WriteString("output", Cut(result.Output));
            writer.WriteString("error", Cut(result.Failure ?? result.Error));
        });

        private static string Cut(string text) => text.Length <= MostOutput ? text : text[..MostOutput] + $"\n[{text.Length - MostOutput} more characters not shown]";

        // One JSON object to a line. A failure to write it costs the view a line and nothing else: the command
        // and its record in the log do not depend on it.
        private static void Write(Action<Utf8JsonWriter> fill)
        {
            try
            {
                Directory.CreateDirectory(ElioPaths.DataDirectory);
                Trim();

                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    fill(writer);
                    writer.WriteEndObject();
                }

                buffer.WriteByte((byte)'\n');
                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite };

                // Output from a server can hold anything, so nobody else on the computer gets to read it.
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var file = new FileStream(FilePath, options);
                file.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // Keeps the newest part once the file has grown large, starting at a line's beginning.
        private static void Trim()
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length <= TrimAbove) return;

            byte[] all = File.ReadAllBytes(FilePath);
            int from = Array.IndexOf(all, (byte)'\n', all.Length - KeepAfterTrim) + 1;
            File.WriteAllBytes(FilePath, all[from..]);
        }
    }

    /// <summary>Where every command an AI tool ran is written down, one to a line, for the user to read.</summary>
    private static string LogPath => Path.Combine(ElioPaths.DataDirectory, "mcp-commands.log");

    private static FileStream OpenLog()
    {
        Directory.CreateDirectory(ElioPaths.DataDirectory);
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite };

        // The commands may say a lot about the servers, so nobody else on the computer gets to read them.
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(LogPath, options);
    }

    private static bool CanRecord()
    {
        try
        {
            OpenLog().Dispose();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Adds one line: the time in UTC, the server, how it ended and the command.</summary>
    private static void Record(string server, string command, string outcome)
    {
        // A line break inside a name or a command is written as \n, so nothing in them can pass for a line of its own.
        static string OneLine(string text) => string.Concat(text.Select(c => c == '\\' ? "\\\\" : c == '\n' ? "\\n" : c == '\r' ? "\\r" : char.IsControl(c) ? $"\\u{(int)c:x4}" : c.ToString()));

        try
        {
            using FileStream log = OpenLog();
            log.Write(Encoding.UTF8.GetBytes($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\t{OneLine(server)}\t{outcome}\t{OneLine(command)}\n"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It could be written a moment ago. The command has run by now, and its answer is still worth giving.
        }
    }

    private static string Shorten(string output) =>
        output.Length <= MaxOutputCharacters ? output : output[..MaxOutputCharacters] + $"\n[{output.Length - MaxOutputCharacters} more characters not shown]";

    /// <summary>Adds the server and the jump hosts before it. Returns what is missing, or null if the path is complete.</summary>
    private static string? AddToPath(HostEntry host, HostLibrary library, Vault vault, List<ResolvedHost> path, HashSet<Guid> visiting)
    {
        if (host.Kind != HostKind.Ssh) return $"{host.Name} is not an SSH server.";
        if (host.Localtonet) return $"{host.Name} is a Localtonet device, which this cannot reach.";
        if (!visiting.Add(host.Id)) return $"{host.Name} is in its own chain of jump hosts.";

        ResolvedHost resolved = ConnectionResolver.Resolve(host, library);
        foreach (Guid jump in resolved.JumpHosts)
        {
            if (library.Hosts.FirstOrDefault(h => h.Id == jump) is not HostEntry hop) return $"A jump host of {host.Name} no longer exists.";
            if (AddToPath(hop, library, vault, path, visiting) is string problem) return problem;
        }

        if (ConnectionResolver.Login(resolved, vault.Get).NeedsSecret) return $"No password is saved for {host.Name}. The user has to connect to it from Elio once.";

        path.Add(resolved);
        return null;
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Elio.Core;

/// <summary>A plugin found in the plugins folder, whether or not it can be used.</summary>
/// <param name="Name">The plugin's name: what its manifest says, or its folder's name.</param>
/// <param name="Folder">The folder it lives in.</param>
/// <param name="Error">What is wrong with it, in the user's language, or null if nothing is.</param>
/// <param name="CommandLine">The program it starts and what it passes to it, for showing to the user.</param>
/// <param name="Approval">What stands for this plugin as it is now, when asking whether the user has allowed it: the
/// SHA-256 of its folder's name and its plugin.json. Empty for one that cannot be used.</param>
public sealed record PluginInfo(string Name, string Folder, string Description, string Version, string? Error, string CommandLine = "", string Approval = "");

/// <summary>
/// Which plugins the user has allowed to run. A plugin is a program, and the plugins folder is only a folder:
/// whoever can put files there, on a shared computer or a drive carried around, would otherwise have their
/// program started the next time Elio opens.
/// </summary>
/// <remarks>
/// The list is kept in the vault, because that is the one place a person who can merely write files into the
/// data folder cannot write to. While the vault is locked nothing counts as allowed.
/// </remarks>
public sealed class PluginApprovals(Vault vault)
{
    private const string VaultName = "plugin-approvals";

    /// <summary>Whether the user allowed the plugin as it is now. Never raises the vault's prompt.</summary>
    public bool Contains(string approval) => approval.Length > 0 && !vault.IsLocked && Stored().Contains(approval);

    public void Add(string approval)
    {
        if (approval.Length == 0 || vault.IsLocked) return;
        vault.SetLocal(VaultName, string.Join('\n', Stored().Append(approval).Distinct()));
    }

    private string[] Stored() => (vault.GetLocal(VaultName) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Something a plugin offers to do, for a command palette.</summary>
/// <param name="Plugin">The plugin's name.</param>
/// <param name="Id">The plugin's own name for the command.</param>
/// <param name="Title">What to show the user.</param>
public sealed record PluginCommand(string Plugin, string Id, string Title, string? Description = null);

/// <summary>The server a terminal is connected to, as much of it as a plugin is told. Never a password or a key.</summary>
/// <param name="Kind">"ssh", "telnet", "serial" or "local".</param>
public sealed record PluginServer(string Name, string Address, string User, string Kind);

/// <summary>What the user was looking at when a plugin command was run. Anything unknown is left null.</summary>
/// <param name="Directory">The terminal's current folder.</param>
/// <param name="Selection">The text selected in the terminal.</param>
public sealed record PluginContext(PluginServer? Server = null, string? Directory = null, string? Selection = null);

/// <summary>A plugin could not be started, did not answer, or said it failed. The message is for the user.</summary>
public sealed class PluginException(string message) : Exception(message);

/// <summary>
/// What a plugin may ask Elio to do while one of its commands runs. The UI implements this; the calls
/// arrive on background threads, one at a time for each plugin.
/// </summary>
/// <remarks>
/// This list is everything a plugin can reach. It is short on purpose, and nothing in it gives out
/// passwords, keys or anything else from the vault.
/// </remarks>
public interface IPluginActions
{
    /// <summary>Types text into the active terminal, without pressing Enter.</summary>
    Task SendTextAsync(string plugin, string text);

    /// <summary>Types a command into the active terminal and presses Enter.</summary>
    Task RunCommandAsync(string plugin, string command);

    /// <summary>Shows a short message.</summary>
    Task NotifyAsync(string plugin, string message);

    Task WriteClipboardAsync(string plugin, string text);

    /// <summary>Opens a web page. Only ever called with an http or https address.</summary>
    Task OpenUrlAsync(string plugin, Uri url);

    /// <summary>Asks the user for a line of text.</summary>
    /// <returns>What was typed, or null if the user backed out.</returns>
    Task<string?> PromptAsync(string plugin, string title, string? placeholder);
}

internal sealed record PluginManifest(
    string Name,
    string Folder,
    string Description,
    string Version,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string Approval);

/// <summary>
/// Finds the plugins in the data folder, runs each as a program of its own and talks to it in JSON-RPC
/// over its input and output, one message a line. See docs/PLUGINS.md for what is said.
/// </summary>
/// <remarks>
/// A plugin is a separate process so that it can be written in anything, and so that one that hangs or
/// crashes takes only itself down: every failure here comes back as a <see cref="PluginException"/> or
/// as text in <see cref="PluginInfo.Error"/>.
/// </remarks>
public sealed class PluginHost : IDisposable
{
    public const int ProtocolVersion = 1;

    private const string ManifestFile = "plugin.json";

    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(5);

    private readonly IPluginActions _actions;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PluginManifest> _manifests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<PluginProcess>> _started = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<PluginCommand>> _offered = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PluginLog> _logs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PluginProcess> _processes = [];
    private bool _disposed;

    /// <param name="actions">What plugins may ask for.</param>
    /// <param name="folder">Where plugins are kept, or null for "plugins" in Elio's data folder.</param>
    public PluginHost(IPluginActions actions, string? folder = null)
    {
        _actions = actions;
        Folder = folder ?? Path.Combine(ElioPaths.DataDirectory, "plugins");
    }

    /// <summary>The folder that holds one folder for each plugin.</summary>
    public string Folder { get; }

    /// <summary>
    /// Says whether the user has allowed a plugin, given its <see cref="PluginInfo.Approval"/>. One that is not
    /// allowed is found and listed but never started. Until this is set, none is.
    /// </summary>
    public Func<string, bool> IsApproved { get; set; } = _ => false;

    /// <summary>How long a running command may go without a word before the plugin is taken to be stuck.
    /// Time the user spends answering a prompt is not counted.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Every command the plugins started so far have offered, in order of plugin name.</summary>
    public IReadOnlyList<PluginCommand> Commands { get; private set; } = [];

    /// <summary><see cref="Commands"/> is a new list. Raised on a background thread.</summary>
    public event Action? CommandsChanged;

    /// <summary>Reads the manifests. Starts nothing, so it is quick and safe to call often.</summary>
    /// <returns>One entry for each folder with a plugin.json in it, broken ones included.</returns>
    public IReadOnlyList<PluginInfo> Discover()
    {
        var found = new List<PluginInfo>();
        var manifests = new Dictionary<string, PluginManifest>(StringComparer.OrdinalIgnoreCase);

        string[] folders;
        try
        {
            folders = Directory.Exists(Folder) ? Directory.GetDirectories(Folder) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            folders = [];
        }

        foreach (string folder in folders.Order(StringComparer.OrdinalIgnoreCase))
        {
            string file = Path.Combine(folder, ManifestFile);
            if (!File.Exists(file)) continue;

            string fallback = Path.GetFileName(folder);
            try
            {
                PluginManifest manifest = ReadManifest(file, folder, fallback);
                string? error = manifests.TryAdd(manifest.Name, manifest) ? null : L.T("Another plugin is already called {0}.", manifest.Name);
                string commandLine = string.Join(' ', manifest.Args.Prepend(PluginProcess.ResolveCommand(manifest)).Select(word => word.Contains(' ') ? $"\"{word}\"" : word));
                found.Add(new PluginInfo(manifest.Name, folder, manifest.Description, manifest.Version, error, commandLine, error is null ? manifest.Approval : string.Empty));
            }
            catch (PluginException ex)
            {
                found.Add(new PluginInfo(fallback, folder, string.Empty, string.Empty, ex.Message));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                found.Add(new PluginInfo(fallback, folder, string.Empty, string.Empty, L.T("plugin.json could not be read: {0}", ex.Message)));
            }
        }

        lock (_gate)
        {
            _manifests.Clear();

            // Only what the user allowed is kept, so nothing further on can start one that was not: to the
            // rest of this class it is as if the plugin were not there.
            foreach (var (name, manifest) in manifests)
                if (IsApproved(manifest.Approval)) _manifests[name] = manifest;
        }

        return found;
    }

    private static PluginManifest ReadManifest(string file, string folder, string fallbackName)
    {
        // People write these by hand, so comments and a comma too many are let through.
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        byte[] bytes = File.ReadAllBytes(file);

        // Taken from the very bytes that are read below, so what was allowed is what runs. The folder's name
        // is part of it: the same file copied into another folder would start another folder's program.
        string approval = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([.. Encoding.UTF8.GetBytes(fallbackName), 0, .. bytes]));
        using JsonDocument document = JsonDocument.Parse(bytes, options);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("The file does not hold a JSON object.");

        string command = PluginJson.Text(root, "command") ?? string.Empty;
        if (command.Trim().Length == 0) throw new PluginException(L.T("plugin.json does not say which command to run."));

        var args = new List<string>();
        if (root.TryGetProperty("args", out JsonElement list))
        {
            if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new PluginException(L.T("In plugin.json, \"{0}\" is not written the way it should be.", "args"));
            args.AddRange(list.EnumerateArray().Select(item => item.GetString()!));
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("env", out JsonElement variables))
        {
            if (variables.ValueKind != JsonValueKind.Object || variables.EnumerateObject().Any(item => item.Value.ValueKind != JsonValueKind.String))
                throw new PluginException(L.T("In plugin.json, \"{0}\" is not written the way it should be.", "env"));
            foreach (JsonProperty variable in variables.EnumerateObject()) env[variable.Name] = variable.Value.GetString()!;
        }

        string name = PluginJson.Text(root, "name") is { Length: > 0 } given ? given.Trim() : fallbackName;
        return new PluginManifest(name, folder, PluginJson.Text(root, "description") ?? string.Empty, PluginJson.Text(root, "version") ?? string.Empty, command.Trim(), args, env, approval);
    }

    /// <summary>
    /// Reads the manifests, starts every plugin the user has allowed that is not running yet and asks each
    /// what it offers. One that fails to start or to answer is reported in the result and otherwise left out.
    /// </summary>
    public async Task<IReadOnlyList<PluginInfo>> LoadAsync(CancellationToken cancellation = default)
    {
        IReadOnlyList<PluginInfo> found = Discover();

        // All at once: a plugin that never answers then costs one wait, not one for each plugin behind it.
        Task<string?>[] starting = found.Select(async info =>
        {
            if (info.Error is not null) return info.Error;

            // Not allowed is not an error: it waits for the user to say.
            bool allowed;
            lock (_gate) allowed = _manifests.TryGetValue(info.Name, out PluginManifest? kept) && kept.Approval == info.Approval;
            if (!allowed) return null;
            try
            {
                await StartedAsync(info.Name).WaitAsync(cancellation).ConfigureAwait(false);
                return null;
            }
            catch (PluginException ex)
            {
                return ex.Message;
            }
        }).ToArray();

        string?[] errors = await Task.WhenAll(starting).ConfigureAwait(false);

        // A plugin whose folder was deleted since last time must not leave its commands behind.
        bool pruned = false;
        lock (_gate)
        {
            foreach (string gone in _offered.Keys.Where(name => !_manifests.ContainsKey(name)).ToList())
            {
                _offered.Remove(gone);
                pruned = true;
            }

            if (pruned) Commands = _offered.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).SelectMany(pair => pair.Value).ToList();
        }

        if (pruned) CommandsChanged?.Invoke();
        return found.Select((info, index) => info with { Error = errors[index] }).ToList();
    }

    /// <summary>Whether the plugin's process is running now.</summary>
    public bool IsRunning(string plugin)
    {
        lock (_gate) return _started.TryGetValue(plugin, out Task<PluginProcess>? task) && task.IsCompletedSuccessfully && !task.Result.Exited;
    }

    /// <summary>The last things the plugin wrote that were not protocol messages: its error output, mostly.</summary>
    public IReadOnlyList<string> Log(string plugin)
    {
        PluginLog? log;
        lock (_gate) _logs.TryGetValue(plugin, out log);
        return log?.Lines() ?? [];
    }

    /// <summary>Runs a command, starting its plugin first if it is not running.</summary>
    /// <exception cref="PluginException">The plugin could not be started, stopped, did not answer, or reported a failure.</exception>
    public async Task RunAsync(PluginCommand command, PluginContext context, CancellationToken cancellation = default)
    {
        PluginProcess process = await StartedAsync(command.Plugin).WaitAsync(cancellation).ConfigureAwait(false);
        await process.RunAsync(command.Id, context, CommandTimeout, cancellation).ConfigureAwait(false);
    }

    /// <summary>Stops one plugin's process. It is started again the next time it is needed.</summary>
    public void Stop(string plugin)
    {
        Task<PluginProcess>? task;
        lock (_gate) _started.Remove(plugin, out task);
        task?.ContinueWith(static started => started.Result.Dispose(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private Task<PluginProcess> StartedAsync(string plugin)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // A start under way is shared. One that failed, or whose process has since ended, is tried again.
            if (_started.TryGetValue(plugin, out Task<PluginProcess>? existing) && (!existing.IsCompleted || (existing.IsCompletedSuccessfully && !existing.Result.Exited)))
                return existing;

            if (!_manifests.TryGetValue(plugin, out PluginManifest? manifest))
                return Task.FromException<PluginProcess>(new PluginException(L.T("There is no plugin called {0}.", plugin)));

            if (!_logs.TryGetValue(manifest.Name, out PluginLog? log)) _logs[manifest.Name] = log = new PluginLog();

            Task<PluginProcess> starting = Task.Run(() => StartAsync(manifest, log));
            _started[plugin] = starting;
            return starting;
        }
    }

    private async Task<PluginProcess> StartAsync(PluginManifest manifest, PluginLog log)
    {
        var process = PluginProcess.Start(manifest, _actions, log);
        lock (_gate)
        {
            if (_disposed)
            {
                process.Dispose();
                throw new ObjectDisposedException(nameof(PluginHost));
            }

            _processes.RemoveAll(old => old.Exited);
            _processes.Add(process);
        }

        try
        {
            JsonElement answer = await process.RequestAsync(
                "initialize",
                writer =>
                {
                    writer.WriteString("elioVersion", typeof(PluginHost).Assembly.GetName().Version?.ToString() ?? "0");
                    writer.WriteNumber("protocolVersion", ProtocolVersion);
                },
                InitializeTimeout,
                CancellationToken.None).ConfigureAwait(false);

            var commands = new List<PluginCommand>();
            if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("commands", out JsonElement offered) || offered.ValueKind != JsonValueKind.Array)
                throw new PluginException(L.T("The plugin {0} gave an answer that could not be understood.", manifest.Name));

            foreach (JsonElement item in offered.EnumerateArray())
            {
                // One badly written command is skipped; the rest of the plugin still works.
                if (item.ValueKind != JsonValueKind.Object || PluginJson.Text(item, "id") is not { Length: > 0 } id || PluginJson.Text(item, "title") is not { Length: > 0 } title) continue;
                commands.Add(new PluginCommand(manifest.Name, id, title, PluginJson.Text(item, "description")));
            }

            lock (_gate)
            {
                _offered[manifest.Name] = commands;
                Commands = _offered.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).SelectMany(pair => pair.Value).ToList();
            }

            CommandsChanged?.Invoke();
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <summary>Stops every plugin, along with anything a plugin started.</summary>
    public void Dispose()
    {
        List<PluginProcess> processes;
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            processes = [.. _processes];
            _processes.Clear();
            _started.Clear();
        }

        foreach (PluginProcess process in processes) process.Dispose();
    }
}

/// <summary>The last lines a plugin wrote outside the protocol. Old lines fall off, so it cannot grow.</summary>
internal sealed class PluginLog
{
    private const int MaxLines = 200;
    private const int MaxLineLength = 2000;

    private readonly Queue<string> _lines = new();

    public void Add(string line)
    {
        if (line.Length > MaxLineLength) line = line[..MaxLineLength];
        lock (_lines)
        {
            if (_lines.Count == MaxLines) _lines.Dequeue();
            _lines.Enqueue(line);
        }
    }

    public IReadOnlyList<string> Lines()
    {
        lock (_lines) return [.. _lines];
    }
}

internal static class PluginJson
{
    // A line longer than this is not a message anyone meant to send. It is dropped, not kept in memory.
    private const int MaxLineBytes = 1024 * 1024;

    /// <summary>The text under a name, or null if it is missing or not text.</summary>
    public static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Reads lines of UTF-8 from a pipe until it closes, skipping empty ones and any that are far too long.</summary>
    public static async IAsyncEnumerable<string> LinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellation = default)
    {
        byte[] buffer = new byte[16 * 1024];
        var line = new MemoryStream();
        bool tooLong = false;
        while (true)
        {
            int count;
            try
            {
                count = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                count = 0;
            }

            if (count == 0) break;

            int start = 0;
            while (start < count)
            {
                int end = Array.IndexOf(buffer, (byte)'\n', start, count - start);
                int length = (end < 0 ? count : end) - start;
                if (line.Length + length > MaxLineBytes) tooLong = true;
                else line.Write(buffer, start, length);

                if (end < 0) break;

                if (!tooLong && Decode(line) is string text) yield return text;
                line.SetLength(0);
                tooLong = false;
                start = end + 1;
            }
        }

        // A last line without a line break still counts.
        if (!tooLong && Decode(line) is string last) yield return last;
    }

    private static string? Decode(MemoryStream line)
    {
        string text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).Trim();
        return text.Length == 0 ? null : text;
    }
}

/// <summary>One running plugin: its process, and the conversation with it.</summary>
internal sealed class PluginProcess : IDisposable
{
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int ActionFailed = -32000;
    private const int NotRunning = -32001;

    private readonly PluginManifest _manifest;
    private readonly IPluginActions _actions;
    private readonly PluginLog _log;
    private readonly Process _process;
    private readonly Stream _input;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _waiting = [];
    private readonly List<Patience> _patience = [];

    // What the plugin asks for is done in the order it asked, so "type this" then "press Enter" stay that way.
    // Bounded, so a plugin that asks faster than it can be served is made to wait rather than fill memory.
    private readonly Channel<JsonElement> _calls = Channel.CreateBounded<JsonElement>(64);
    private int _nextId;
    private int _commandsRunning;
    private int _hostBusy;
    private volatile bool _exited;
    private int _disposed;

    private PluginProcess(PluginManifest manifest, IPluginActions actions, PluginLog log, Process process)
    {
        _manifest = manifest;
        _actions = actions;
        _log = log;
        _process = process;
        _input = process.StandardInput.BaseStream;

        _ = ReadAsync();
        _ = ReadErrorsAsync();
        _ = ServeAsync();
    }

    /// <summary>The process has ended, or its output has closed, which for this purpose is the same.</summary>
    public bool Exited => _exited;

    // How long one request will wait. The clock is stopped while the plugin is waiting on Elio instead.
    private sealed class Patience(TimeSpan time)
    {
        public TimeSpan Time { get; } = time;

        public CancellationTokenSource Over { get; } = new();
    }

    public static PluginProcess Start(PluginManifest manifest, IPluginActions actions, PluginLog log)
    {
        var start = new ProcessStartInfo
        {
            FileName = ResolveCommand(manifest),
            WorkingDirectory = manifest.Folder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in manifest.Args) start.ArgumentList.Add(argument);

        // Python is the likely language, and left alone it holds its output back and writes it in the
        // system's code page. Both would look like a plugin that never answers. Harmless to anything else.
        start.Environment.TryAdd("PYTHONUNBUFFERED", "1");
        start.Environment.TryAdd("PYTHONIOENCODING", "utf-8");
        start.Environment["ELIO_PLUGIN_DIR"] = manifest.Folder;
        foreach (var (name, value) in manifest.Env) start.Environment[name] = value;

        try
        {
            Process process = Process.Start(start) ?? throw new PluginException(L.T("The plugin {0} could not be started: {1}", manifest.Name, start.FileName));
            return new PluginProcess(manifest, actions, log, process);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            throw new PluginException(L.T("The plugin {0} could not be started: {1}", manifest.Name, ex.Message));
        }
    }

    // A program that comes with the plugin is found in its folder. Anything else, like "python", is left
    // for the system to find.
    internal static string ResolveCommand(PluginManifest manifest)
    {
        string command = manifest.Command;
        if (Path.IsPathRooted(command)) return command;

        string local = Path.GetFullPath(Path.Combine(manifest.Folder, command));
        bool namesAPath = command.Contains('/') || command.Contains('\\');
        if (namesAPath || File.Exists(local)) return local;
        return OperatingSystem.IsWindows() && File.Exists(local + ".exe") ? local + ".exe" : command;
    }

    public async Task RunAsync(string id, PluginContext context, TimeSpan timeout, CancellationToken cancellation)
    {
        // While this is above zero the plugin may ask for things; see ServeAsync.
        Interlocked.Increment(ref _commandsRunning);
        try
        {
            await RequestAsync(
                "command/run",
                writer =>
                {
                    writer.WriteString("id", id);
                    writer.WriteStartObject("context");
                    if (context.Server is PluginServer server)
                    {
                        writer.WriteStartObject("server");
                        writer.WriteString("name", server.Name);
                        writer.WriteString("address", server.Address);
                        writer.WriteString("user", server.User);
                        writer.WriteString("kind", server.Kind);
                        writer.WriteEndObject();
                    }

                    if (context.Directory is not null) writer.WriteString("directory", context.Directory);
                    if (context.Selection is not null) writer.WriteString("selection", context.Selection);
                    writer.WriteEndObject();
                },
                timeout,
                cancellation).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _commandsRunning);
        }
    }

    /// <summary>Asks the plugin something and waits for its answer.</summary>
    /// <exception cref="PluginException">It stopped, took too long, or answered with an error.</exception>
    public async Task<JsonElement> RequestAsync(string method, Action<Utf8JsonWriter> parameters, TimeSpan timeout, CancellationToken cancellation)
    {
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var patience = new Patience(timeout);
        int id;
        lock (_gate)
        {
            if (_exited) throw Stopped();

            id = ++_nextId;
            _waiting[id] = answer;
            _patience.Add(patience);
        }

        try
        {
            await WriteAsync(writer =>
            {
                writer.WriteNumber("id", id);
                writer.WriteString("method", method);
                writer.WriteStartObject("params");
                parameters(writer);
                writer.WriteEndObject();
            }).ConfigureAwait(false);

            lock (_gate)
            {
                if (_hostBusy == 0) patience.Over.CancelAfter(timeout);
            }

            using var either = CancellationTokenSource.CreateLinkedTokenSource(patience.Over.Token, cancellation);
            try
            {
                return await answer.Task.WaitAsync(either.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                // It is stuck, and nothing more can be asked of a stuck plugin. The next use starts a fresh one.
                Dispose();
                throw new PluginException(L.T("The plugin {0} did not answer in {1:0} seconds.", _manifest.Name, timeout.TotalSeconds));
            }
        }
        finally
        {
            lock (_gate)
            {
                _waiting.Remove(id);
                _patience.Remove(patience);
            }

            patience.Over.Dispose();
        }
    }

    private PluginException Stopped() => new(L.T("The plugin {0} stopped unexpectedly.", _manifest.Name));

    private async Task WriteAsync(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            body(writer);
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');

        await _writing.WaitAsync().ConfigureAwait(false);
        try
        {
            await _input.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length)).ConfigureAwait(false);
            await _input.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw Stopped();
        }
        finally
        {
            _writing.Release();
        }
    }

    private async Task ReadAsync()
    {
        try
        {
            await foreach (string line in PluginJson.LinesAsync(_process.StandardOutput.BaseStream).ConfigureAwait(false))
            {
                JsonElement message;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    message = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // A stray print. Kept where the plugin's author can find it, and otherwise ignored.
                    _log.Add("stdout: " + line);
                    continue;
                }

                if (message.ValueKind != JsonValueKind.Object)
                {
                    _log.Add("stdout: " + line);
                }
                else if (PluginJson.Text(message, "method") is not null)
                {
                    await _calls.Writer.WriteAsync(message).ConfigureAwait(false);
                }
                else if (message.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out int number))
                {
                    TaskCompletionSource<JsonElement>? waiting;
                    lock (_gate) _waiting.TryGetValue(number, out waiting);
                    if (waiting is null) continue;

                    if (message.TryGetProperty("error", out JsonElement error))
                        waiting.TrySetException(new PluginException(L.T("The plugin {0} reported an error: {1}", _manifest.Name, PluginJson.Text(error, "message") ?? error.ToString())));
                    else
                        waiting.TrySetResult(message.TryGetProperty("result", out JsonElement result) ? result : default);
                }
            }
        }
        catch (ChannelClosedException)
        {
            // Being shut down.
        }
        finally
        {
            Ended();
        }
    }

    private async Task ReadErrorsAsync()
    {
        await foreach (string line in PluginJson.LinesAsync(_process.StandardError.BaseStream).ConfigureAwait(false)) _log.Add(line);
    }

    // The output closed: nobody is going to answer anything now.
    private void Ended()
    {
        List<TaskCompletionSource<JsonElement>> waiting;
        lock (_gate)
        {
            _exited = true;
            waiting = [.. _waiting.Values];
            _waiting.Clear();
        }

        _calls.Writer.TryComplete();
        foreach (TaskCompletionSource<JsonElement> answer in waiting) answer.TrySetException(Stopped());
    }

    private async Task ServeAsync()
    {
        await foreach (JsonElement call in _calls.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            JsonElement? id = call.TryGetProperty("id", out JsonElement given) && given.ValueKind is JsonValueKind.Number or JsonValueKind.String ? given : null;
            string method = PluginJson.Text(call, "method")!;
            JsonElement parameters = call.TryGetProperty("params", out JsonElement supplied) ? supplied : default;

            Busy(true);
            Action<Utf8JsonWriter> reply;
            try
            {
                reply = await CallAsync(method, parameters).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The UI could not do it. The plugin is told, and decides for itself what that means.
                reply = Error(ActionFailed, ex.Message);
            }
            finally
            {
                Busy(false);
            }

            // Without an id it was a notification, which gets no answer whatever happened.
            if (id is not JsonElement answerTo) continue;

            try
            {
                await WriteAsync(writer =>
                {
                    writer.WritePropertyName("id");
                    answerTo.WriteTo(writer);
                    reply(writer);
                }).ConfigureAwait(false);
            }
            catch (PluginException)
            {
                return;
            }
        }
    }

    // While Elio is doing something for the plugin, perhaps waiting for the user to type, the plugin is
    // not the one being slow, so its time is not running.
    private void Busy(bool busy)
    {
        lock (_gate)
        {
            _hostBusy += busy ? 1 : -1;
            foreach (Patience patience in _patience)
            {
                try
                {
                    patience.Over.CancelAfter(busy ? Timeout.InfiniteTimeSpan : patience.Time);
                }
                catch (ObjectDisposedException)
                {
                    // That request finished this instant.
                }
            }
        }
    }

    private async Task<Action<Utf8JsonWriter>> CallAsync(string method, JsonElement parameters)
    {
        if (method is not ("terminal/send" or "terminal/run" or "notify" or "clipboard/write" or "openUrl" or "prompt"))
            return Error(MethodNotFound, $"Unknown method: {method}");

        // A plugin acts only when the user has asked it to. One that could type into a terminal whenever
        // it liked would be a very different thing to install.
        if (Volatile.Read(ref _commandsRunning) == 0) return Error(NotRunning, "Host methods can only be called while a command is running.");

        string name = _manifest.Name;
        switch (method)
        {
            case "terminal/send":
                if (PluginJson.Text(parameters, "text") is not string text) return Missing("text");
                await _actions.SendTextAsync(name, text).ConfigureAwait(false);
                return Done;

            case "terminal/run":
                if (PluginJson.Text(parameters, "command") is not string command) return Missing("command");
                await _actions.RunCommandAsync(name, command).ConfigureAwait(false);
                return Done;

            case "notify":
                if (PluginJson.Text(parameters, "message") is not string message) return Missing("message");
                await _actions.NotifyAsync(name, message).ConfigureAwait(false);
                return Done;

            case "clipboard/write":
                if (PluginJson.Text(parameters, "text") is not string copied) return Missing("text");
                await _actions.WriteClipboardAsync(name, copied).ConfigureAwait(false);
                return Done;

            case "openUrl":
                if (PluginJson.Text(parameters, "url") is not string address) return Missing("url");

                // Anything else could start a program or open a file, which is more than a plugin was promised.
                if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                    return Error(InvalidParams, "Only http and https addresses can be opened.");
                await _actions.OpenUrlAsync(name, url).ConfigureAwait(false);
                return Done;

            default:
                if (PluginJson.Text(parameters, "title") is not string title) return Missing("title");
                string? typed = await _actions.PromptAsync(name, title, PluginJson.Text(parameters, "placeholder")).ConfigureAwait(false);
                return writer =>
                {
                    writer.WriteStartObject("result");
                    if (typed is null) writer.WriteNull("text");
                    else writer.WriteString("text", typed);
                    writer.WriteEndObject();
                };
        }
    }

    private static void Done(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("result");
        writer.WriteEndObject();
    }

    private static Action<Utf8JsonWriter> Missing(string name) => Error(InvalidParams, $"\"{name}\" is missing or is not text.");

    private static Action<Utf8JsonWriter> Error(int code, string message) => writer =>
    {
        writer.WriteStartObject("error");
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
    };

    /// <summary>Ends the process and whatever it started.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // Already gone, or not ours to end. Either way there is nothing more to do about it.
        }

        Ended();
        _process.Dispose();
    }
}

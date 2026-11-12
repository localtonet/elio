using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Elio.Core;

namespace Elio.UI;

/// <summary>One command an AI tool ran through <c>elio --mcp</c>, as the log has it.</summary>
/// <param name="Outcome">"exit 0", "exit 1", "failed": how the log says it ended.</param>
public sealed record AiCommand(DateTime Utc, string Server, string Outcome, string Command);

/// <summary>
/// Shows in the window what AI tools do through Elio. They run in a process of their own, started by the tool
/// with <c>elio --mcp</c>, and write every command to a log; without this the window the user is looking at
/// would say nothing while commands run on their servers.
/// </summary>
public static class AiActivity
{
    private const string FileName = "mcp-commands.log";

    /// <summary>Where the commands are written down, one to a line.</summary>
    public static string LogPath => Path.Combine(ElioPaths.DataDirectory, FileName);

    /// <summary>One line of the log, or null for a line that is not one.</summary>
    public static AiCommand? Parse(string line)
    {
        string[] parts = line.Split('\t', 4);
        if (parts.Length != 4) return null;
        if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime utc)) return null;

        return new AiCommand(utc, parts[1], parts[2], parts[3]);
    }

    /// <summary>The newest commands in the log, newest first.</summary>
    public static IReadOnlyList<AiCommand> Recent(int most = 200)
    {
        try
        {
            if (!File.Exists(LogPath)) return [];

            // Read while the other process may be writing to it.
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var all = new List<AiCommand>();
            while (reader.ReadLine() is { } line)
                if (Parse(line) is { } command) all.Add(command);

            all.Reverse();
            return all.Count > most ? all.GetRange(0, most) : all;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static FileSystemWatcher? _watcher;
    private static long _seen;

    /// <summary>
    /// Starts telling the window about commands as they are logged. Only what is added from now on is
    /// announced: what was there before the window opened is history, to be looked up, not news.
    /// </summary>
    public static void Watch(Action<AiCommand> ran)
    {
        if (_watcher is not null) return;

        try
        {
            Directory.CreateDirectory(ElioPaths.DataDirectory);
            _seen = File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0;
            _watcher = new FileSystemWatcher(ElioPaths.DataDirectory, FileName) { NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.FileName };
            _watcher.Changed += (_, _) => Announce(ran);
            _watcher.Created += (_, _) => Announce(ran);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            // No watching on this system or folder. The log can still be opened and read.
            _watcher = null;
        }
    }

    private static readonly Lock Gate = new();

    private static void Announce(Action<AiCommand> ran)
    {
        var added = new List<AiCommand>();
        lock (Gate)
        {
            try
            {
                using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < _seen) _seen = 0; // The log was cleared or replaced.
                if (stream.Length == _seen) return;

                stream.Seek(_seen, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string text = reader.ReadToEnd();

                // Only whole lines: one still being written is left for the next time.
                int end = text.LastIndexOf('\n');
                if (end < 0) return;
                _seen += Encoding.UTF8.GetByteCount(text[..(end + 1)]);
                foreach (string line in text[..end].Split('\n'))
                    if (Parse(line.TrimEnd('\r')) is { } command) added.Add(command);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }

        foreach (AiCommand command in added) Dispatcher.UIThread.Post(() => ran(command));
    }
}

/// <summary>A command an AI tool started through <c>elio --mcp</c>, and, once it has ended, what came of it.</summary>
public sealed class AiRun(string id, DateTime startedUtc, string server, string command)
{
    public string Id { get; } = id;

    public DateTime StartedUtc { get; } = startedUtc;

    public string Server { get; } = server;

    public string Command { get; } = command;

    /// <summary>"exit 0", "exit 1" or "failed"; null while it is still running.</summary>
    public string? Outcome { get; set; }

    public TimeSpan Duration { get; set; }

    public string Output { get; set; } = string.Empty;

    /// <summary>What the command wrote as errors, or why it could not be run.</summary>
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Follows what the AI process writes for the window: each command as it starts and as it ends, with its output.
/// </summary>
public static class AiRuns
{
    private const string FileName = "mcp-activity.jsonl";
    private const int MostKept = 200;

    private static readonly List<AiRun> Kept = [];
    private static readonly Lock Gate = new();
    private static FileSystemWatcher? _watcher;
    private static long _seen;

    private static string FilePath => Path.Combine(ElioPaths.DataDirectory, FileName);

    /// <summary>The commands known so far, oldest first. Read on the UI thread.</summary>
    public static IReadOnlyList<AiRun> All => Kept;

    /// <summary>Raised on the UI thread when a command starts.</summary>
    public static event Action<AiRun>? Started;

    /// <summary>Raised on the UI thread when a command has ended.</summary>
    public static event Action<AiRun>? Ended;

    /// <summary>Raised on the UI thread when the list was emptied.</summary>
    public static event Action? Cleared;

    /// <summary>Takes one line of the file into the list. Returns the run it was about and whether it ended it.</summary>
    public static (AiRun Run, bool Ended)? Take(string line, List<AiRun> runs)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? Text(string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            if (Text("id") is not { Length: > 0 } id || Text("t") is not { } kind) return null;
            DateTime utc = root.TryGetProperty("utc", out JsonElement when) && when.ValueKind == JsonValueKind.String && when.TryGetDateTime(out DateTime parsed) ? parsed.ToUniversalTime() : DateTime.UtcNow;

            if (kind == "start")
            {
                var run = new AiRun(id, utc, Text("server") ?? string.Empty, Text("command") ?? string.Empty);
                runs.Add(run);
                if (runs.Count > MostKept) runs.RemoveAt(0);
                return (run, false);
            }

            if (kind != "end" || runs.FindLast(r => r.Id == id) is not { } ended) return null;

            ended.Outcome = Text("outcome") ?? "failed";
            ended.Duration = root.TryGetProperty("ms", out JsonElement ms) && ms.ValueKind == JsonValueKind.Number && ms.TryGetInt64(out long took) ? TimeSpan.FromMilliseconds(took) : utc - ended.StartedUtc;
            ended.Output = Text("output") ?? string.Empty;
            ended.Error = Text("error") ?? string.Empty;
            return (ended, true);
        }
        catch (JsonException)
        {
            // A line cut short, or not one of these.
            return null;
        }
    }

    /// <summary>Reads what is there already, then keeps following. Called once, on the UI thread.</summary>
    public static void Watch()
    {
        if (_watcher is not null) return;

        Read(announce: false);
        try
        {
            Directory.CreateDirectory(ElioPaths.DataDirectory);
            _watcher = new FileSystemWatcher(ElioPaths.DataDirectory, FileName) { NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.FileName };
            _watcher.Changed += (_, _) => Dispatcher.UIThread.Post(() => Read(announce: true));
            _watcher.Created += (_, _) => Dispatcher.UIThread.Post(() => Read(announce: true));
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            // No watching on this system or folder; what was read stays readable.
            _watcher = null;
        }
    }

    /// <summary>Forgets the commands and removes the file their output was kept in.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            try
            {
                File.Delete(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use at this very moment; the list is emptied all the same.
            }

            _seen = 0;
            Kept.Clear();
        }

        Cleared?.Invoke();
    }

    private static void Read(bool announce)
    {
        var news = new List<(AiRun Run, bool Ended)>();
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return;

                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < _seen)
                {
                    // Trimmed or replaced by the other process: start over from what is there now.
                    _seen = 0;
                    Kept.Clear();
                }

                if (stream.Length == _seen) return;

                stream.Seek(_seen, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string text = reader.ReadToEnd();

                // Only whole lines: one still being written is left for the next time.
                int end = text.LastIndexOf('\n');
                if (end < 0) return;
                _seen += Encoding.UTF8.GetByteCount(text[..(end + 1)]);
                foreach (string line in text[..end].Split('\n'))
                    if (Take(line, Kept) is { } taken) news.Add(taken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }

        if (!announce) return;
        foreach ((AiRun run, bool ended) in news)
        {
            if (ended) Ended?.Invoke(run);
            else Started?.Invoke(run);
        }
    }
}

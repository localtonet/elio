using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia.Threading;
using Elio.Connections;
using Elio.Core;

namespace Elio.UI.Files;

/// <summary>Opens a downloaded copy in an editor, chosen so that a server can't make this computer run something.</summary>
public static class FileEditors
{
    // The only kinds handed to whatever program the system has for them: plain data, settings and source that no
    // system runs or shows in a browser when opened. A list of what is dangerous can never be complete, and the name
    // is the server's choice, so everything else, a file with no extension included, goes to a text editor by name.
    private static readonly HashSet<string> PlainKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".md", ".json", ".yaml", ".yml", ".toml", ".ini", ".conf", ".cfg", ".cnf", ".env", ".csv", ".tsv",
        ".sql", ".c", ".h", ".cpp", ".hpp", ".cs", ".go", ".rs", ".java", ".kt", ".ts", ".tsx", ".css", ".scss",
        ".properties", ".gradle", ".tf", ".dockerfile", ".gitignore",
    };

    private static readonly TimeSpan HandlerLookupLimit = TimeSpan.FromSeconds(5);

    /// <summary>Opens a file for editing. Returns false if it was refused. Replaced in tests.</summary>
    public static Func<string, bool> Open { get; set; } = OpenWithSystem;

    private static bool OpenWithSystem(string path)
    {
        // An editor the user named gets every file, whatever its kind: it is their program, started with the file as
        // an argument, so nothing the server chose is run. Only if it cannot be started does the system decide.
        if (OpenWithChosenEditor(AppServices.Settings.EditorCommand, path)) return true;

        bool plain = PlainKinds.Contains(Path.GetExtension(path));

        if (OperatingSystem.IsWindows())
        {
            // Notepad by its full path, so that nothing called notepad.exe elsewhere on the search path is started instead.
            Process.Start(plain
                ? new ProcessStartInfo(path) { UseShellExecute = true }
                : new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe")) { ArgumentList = { path }, UseShellExecute = false })?.Dispose();
            return true;
        }

        if (OperatingSystem.IsMacOS())
        {
            // -t opens it in the text editor whatever kind of file it is.
            var open = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            if (!plain) open.ArgumentList.Add("-t");
            open.ArgumentList.Add(path);
            Process.Start(open)?.Dispose();
            return true;
        }

        if (!plain) return OpenInTextEditor(path);

        Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { path }, UseShellExecute = false })?.Dispose();
        return true;
    }

    // What cmd.exe gives a meaning of its own, even inside quotes in the case of the percent sign.
    private static readonly char[] BatchSpecials = ['%', '^', '&', '|', '<', '>', '!', '"', '(', ')', '\r', '\n'];

    // The kinds of program Windows starts by name, in the order it tries them.
    private static readonly string[] WindowsProgramKinds = [".exe", ".com", ".cmd", ".bat"];

    private static bool OpenWithChosenEditor(string? setting, string path)
    {
        IReadOnlyList<string> command = SplitCommand(setting);
        if (command.Count == 0) return false;

        try
        {
            string program = OperatingSystem.IsWindows() ? FindOnWindows(command[0]) : command[0];

            // Windows hands a .cmd or .bat file to cmd.exe, which reads the whole line again by rules of its own, and
            // the file's name is the server's choice. Such an editor (Visual Studio Code's "code" is one) only gets
            // names in which nothing means anything to cmd.exe.
            if (OperatingSystem.IsWindows() && Path.GetExtension(program).ToUpperInvariant() is ".CMD" or ".BAT" && path.IndexOfAny(BatchSpecials) >= 0) return false;

            // Started directly, never through a shell, and the file is one argument of its own however it is named.
            var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in command.Skip(1)) start.ArgumentList.Add(argument);
            start.ArgumentList.Add(path);

            using Process? started = Process.Start(start);
            return started is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or ArgumentException or IOException)
        {
            // Not installed, misspelled or not a program. The system's own choice is better than nothing.
            return false;
        }
    }

    /// <summary>
    /// Splits the editor setting into the program and the arguments that go before the file: at spaces, except inside
    /// double quotes. A path to a program that exists is taken whole, so one with spaces in it needs no quotes.
    /// </summary>
    internal static IReadOnlyList<string> SplitCommand(string? setting)
    {
        string text = (setting ?? string.Empty).Trim();
        if (text.Length == 0) return [];

        string whole = text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
        if (!whole.Contains('"') && Path.IsPathRooted(whole) && File.Exists(whole)) return [whole];

        var parts = new List<string>();
        var part = new System.Text.StringBuilder();
        bool quoted = false;
        bool any = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                // A pair of quotes with nothing between them is still an argument, an empty one.
                (quoted, any) = (!quoted, true);
            }
            else if (c == ' ' && !quoted)
            {
                if (any) parts.Add(part.ToString());
                part.Clear();
                any = false;
            }
            else
            {
                part.Append(c);
                any = true;
            }
        }

        if (any) parts.Add(part.ToString());
        return parts.Count > 0 && parts[0].Length == 0 ? [] : parts;
    }

    // Windows itself only finds .exe files by a bare name, and many editors are started by a script of the same name.
    // A name with a folder in it is used as it is.
    private static string FindOnWindows(string program)
    {
        if (program.IndexOfAny(['\\', '/', ':']) >= 0) return program;

        string[] kinds = Path.HasExtension(program) ? [string.Empty] : WindowsProgramKinds;
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string kind in kinds)
            {
                string candidate = Path.Combine(folder, program + kind);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return program;
    }

    // Linux has no one text editor, so the desktop is asked which program it uses for plain text and that program is
    // started with the file. Where that cannot be found out the file is refused: xdg-open would go by the file's kind.
    private static bool OpenInTextEditor(string path)
    {
        try
        {
            string editor;
            using (Process? query = Process.Start(new ProcessStartInfo("xdg-mime") { ArgumentList = { "query", "default", "text/plain" }, UseShellExecute = false, RedirectStandardOutput = true }))
            {
                if (query is null) return false;

                Task<string> answer = query.StandardOutput.ReadToEndAsync();
                if (!query.WaitForExit(HandlerLookupLimit) || !answer.Wait(HandlerLookupLimit))
                {
                    query.Kill();
                    return false;
                }

                editor = answer.Result.Trim();
            }

            if (!editor.EndsWith(".desktop", StringComparison.Ordinal) || editor.StartsWith('-')) return false;

            using Process? started = Process.Start(new ProcessStartInfo("gtk-launch") { ArgumentList = { editor, path }, UseShellExecute = false });
            return started is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or AggregateException)
        {
            // One of the two tools is not installed.
            return false;
        }
    }
}

/// <summary>A server file being edited in a local program. Each save goes back to the server.</summary>
public sealed class RemoteEdit : IDisposable
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(500);
    private const int ReadAttempts = 6;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(150);

    private readonly FileRow _row;
    private readonly string _folder;
    private readonly Func<CancellationToken, Task<SftpConnection>> _connection;
    private readonly DialogHost _dialogs;
    private readonly FileSystemWatcher _watcher;
    private readonly DispatcherTimer _settle;
    private byte[] _uploaded = [];
    private (long Size, DateTime Modified) _serverStamp;
    private bool _pushing;
    private bool _pushAgain;

    private RemoteEdit(FileRow row, string folder, string localPath, Func<CancellationToken, Task<SftpConnection>> connection, DialogHost dialogs)
    {
        _row = row;
        _folder = folder;
        LocalPath = localPath;
        _connection = connection;
        _dialogs = dialogs;
        Status = L.T("Editing");

        _settle = new DispatcherTimer { Interval = SettleDelay };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _ = PushAsync();
        };

        // Many editors save by writing a new file and renaming it over the old one, so the whole folder is watched.
        _watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
        _watcher.Changed += (_, _) => Dispatcher.UIThread.Post(Restart);
        _watcher.Created += (_, _) => Dispatcher.UIThread.Post(Restart);
        _watcher.Renamed += (_, _) => Dispatcher.UIThread.Post(Restart);
    }

    public string Name => _row.Name;

    public string RemotePath => _row.Path;

    public string LocalPath { get; }

    public string Status { get; private set; }

    public event Action? Changed;

    /// <summary>Downloads a copy and opens it.</summary>
    public static async Task<RemoteEdit> StartAsync(FileRow row, Func<CancellationToken, Task<SftpConnection>> connection, DialogHost dialogs)
    {
        string folder = CreateFolder();

        string local = Path.Combine(folder, LocalNames.Safe(row.Name));
        SftpConnection c = await connection(CancellationToken.None);
        await c.DownloadFileAsync(row.Path, local, row.ModifiedUtc, CancellationToken.None);

        var edit = new RemoteEdit(row, folder, local, connection, dialogs)
        {
            _uploaded = SHA256.HashData(await File.ReadAllBytesAsync(local)),
        };

        var stamp = await c.StatAsync(row.Path, CancellationToken.None);
        edit._serverStamp = stamp is { } s ? (s.Size, s.ModifiedUtc) : (0, DateTime.MinValue);
        edit._watcher.EnableRaisingEvents = true;

        if (!FileEditors.Open(local))
        {
            edit.Dispose();
            throw new InvalidOperationException(L.T("This file type was not opened for security reasons. Download it and inspect it yourself."));
        }

        return edit;
    }

    /// <summary>
    /// Makes the folder a copy is edited in. On Windows that is under the user's own temp folder. Elsewhere the temp
    /// folder is shared by everyone on the computer, where someone else could make "Elio" first and then read the copy
    /// or swap it for a link, so it goes where only this user can reach: the session's runtime folder, or failing that
    /// beside Elio's own files.
    /// </summary>
    private static string CreateFolder()
    {
        string name = Guid.NewGuid().ToString("N");
        if (OperatingSystem.IsWindows())
        {
            string temporary = Path.Combine(Path.GetTempPath(), "Elio", "edit", name);
            Directory.CreateDirectory(temporary);
            return temporary;
        }

        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        string app = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Directory.Exists(runtime)
            ? Path.Combine(runtime, "elio")
            : ElioPaths.DataDirectory;
        string folder = Path.Combine(app, "edit", name);
        Directory.CreateDirectory(folder, OwnerOnly);

        // A folder that was already there keeps the permissions it had. One that nobody but its owner can enter, and
        // that a folder was just made inside of, can only be this user's own, so that is what each level has to be.
        foreach (string level in new[] { app, Path.Combine(app, "edit"), folder })
        {
            if ((File.GetUnixFileMode(level) & ~OwnerOnly) == 0) continue;

            Directory.Delete(folder);
            throw new UnauthorizedAccessException(L.T("{0} can be opened by other users of this computer, so no copy was put there for editing.", level));
        }

        return folder;
    }

    private void Restart()
    {
        _settle.Stop();
        _settle.Start();
    }

    private async Task<byte[]?> ReadWhenFreeAsync()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await File.ReadAllBytesAsync(LocalPath);
            }
            catch (IOException) when (attempt < ReadAttempts)
            {
                // The editor is still holding the file.
                await Task.Delay(ReadRetryDelay);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }
    }

    private void SetStatus(string text)
    {
        Status = text;
        Changed?.Invoke();
    }

    private async Task PushAsync()
    {
        if (_pushing)
        {
            _pushAgain = true;
            return;
        }

        _pushing = true;
        try
        {
            if (await ReadWhenFreeAsync() is not byte[] content) return;

            byte[] hash = SHA256.HashData(content);
            if (hash.AsSpan().SequenceEqual(_uploaded)) return;

            SetStatus(L.T("Uploading..."));
            SftpConnection c = await _connection(CancellationToken.None);

            var now = await c.StatAsync(_row.Path, CancellationToken.None);
            if (now is { } server && (server.Size, server.ModifiedUtc) != _serverStamp)
            {
                bool overwrite = await _dialogs.ConfirmAsync(
                    L.T("The file changed on the server"),
                    L.T("{0} changed on the server while you were editing. If you save, the changes there will be lost.", _row.Name),
                    L.T("Overwrite"),
                    danger: true);

                if (!overwrite)
                {
                    _uploaded = hash;
                    SetStatus(L.T("Not saved: the version on the server changed"));
                    return;
                }
            }

            await c.UploadFileAsync(LocalPath, _row.Path, CancellationToken.None);
            _uploaded = hash;

            var after = await c.StatAsync(_row.Path, CancellationToken.None);
            if (after is { } stamp) _serverStamp = (stamp.Size, stamp.ModifiedUtc);
            SetStatus(L.T("Saved {0:HH:mm:ss}", DateTime.Now));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetStatus(L.T("Upload failed: {0}", ex.Message));
        }
        finally
        {
            _pushing = false;
            if (_pushAgain)
            {
                _pushAgain = false;
                _ = PushAsync();
            }
        }
    }

    public void Dispose()
    {
        _settle.Stop();
        _watcher.Dispose();

        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The editor still has it open, which only stops the delete on Windows. There it's in the temp folder, so the system cleans it up eventually.
        }
    }
}

using System.Text;

namespace Elio.Terminal;

/// <summary>
/// Commands the user has entered, newest first, for suggesting the rest of one as it is typed again.
/// Safe to use from any thread.
/// </summary>
public sealed class CommandHistory(int capacity = CommandHistory.DefaultCapacity)
{
    public const int DefaultCapacity = 2000;

    // Longer than anyone types, and short enough that a pasted file does not end up in here.
    private const int MaxCommandLength = 1024;

    // Between the scope and the command on each line of the file. Neither can contain one.
    private const char Separator = '\t';

    private readonly Lock _gate = new();
    private readonly List<(string Scope, string Command)> _entries = [];
    private readonly int _capacity = Math.Max(1, capacity);

    /// <summary>While set, nothing is remembered. For when the caller knows something secret is being typed.</summary>
    public bool Paused { get; set; }

    /// <summary>Something was added since the last <see cref="Save"/> or <see cref="Load"/>.</summary>
    public bool Modified { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>Remembers a command as the most recent one.</summary>
    /// <param name="scope">Where it was entered, such as a host's id. A suggestion from the same place is preferred.</param>
    /// <returns>False if it was not something to remember.</returns>
    public bool Add(string command, string? scope = null)
    {
        if (Paused || !Worth(command)) return false;

        string text = command.TrimEnd();
        string key = Scope(scope);
        lock (_gate)
        {
            // A command entered again moves to the front rather than being there twice.
            _entries.Remove((key, text));
            _entries.Insert(0, (key, text));
            if (_entries.Count > _capacity) _entries.RemoveRange(_capacity, _entries.Count - _capacity);
            Modified = true;
        }

        return true;
    }

    private static bool Worth(string command)
    {
        // A leading space is how shells are told to keep a command out of their own history. A trailing
        // backslash is half a command.
        if (command.Length is 0 or > MaxCommandLength || command[0] == ' ' || string.IsNullOrWhiteSpace(command)) return false;
        if (command.TrimEnd().EndsWith('\\')) return false;

        foreach (char c in command)
            if (char.IsControl(c)) return false;
        return true;
    }

    private static string Scope(string? scope) => scope is null ? string.Empty : scope.Replace(Separator, ' ').ReplaceLineEndings(" ");

    /// <summary>
    /// The most recent command that starts with what was typed and goes on beyond it, or null. Letters
    /// have to match in case, as they do for the shell.
    /// </summary>
    /// <param name="scope">Commands entered in this place are looked at first, then all the others.</param>
    public string? Suggest(string prefix, string? scope = null)
    {
        if (prefix.Length == 0) return null;

        string key = Scope(scope);
        lock (_gate)
        {
            if (scope is not null)
            {
                foreach (var (entryScope, command) in _entries)
                    if (entryScope == key && Continues(command, prefix)) return command;
            }

            foreach (var (_, command) in _entries)
                if (Continues(command, prefix)) return command;
        }

        return null;
    }

    /// <summary>
    /// What to offer for what has been typed. First the commands that go on from it: the ones entered in this place,
    /// the ones entered elsewhere, then well-known commands. After those, the user's commands that merely have it
    /// somewhere inside, which is how a long command is found again by a word from its middle.
    /// </summary>
    /// <param name="max">The most to give back.</param>
    public IReadOnlyList<string> Matches(string prefix, string? scope = null, int max = 8)
    {
        var found = new List<string>();
        if (prefix.Length == 0 || max <= 0) return found;

        string key = Scope(scope);
        lock (_gate)
        {
            if (scope is not null)
            {
                foreach (var (entryScope, command) in _entries)
                {
                    if (entryScope == key && Continues(command, prefix) && !found.Contains(command)) found.Add(command);
                    if (found.Count == max) return found;
                }
            }

            foreach (var (_, command) in _entries)
            {
                if (Continues(command, prefix) && !found.Contains(command)) found.Add(command);
                if (found.Count == max) return found;
            }

            foreach (string command in CommonCommands.All)
            {
                if (Continues(command, prefix) && !found.Contains(command)) found.Add(command);
                if (found.Count == max) return found;
            }

            // Two letters match too much to be worth a list.
            if (prefix.Length >= 3)
            {
                foreach (var (_, command) in _entries)
                {
                    if (command.Contains(prefix, StringComparison.OrdinalIgnoreCase) && !found.Contains(command)) found.Add(command);
                    if (found.Count == max) return found;
                }
            }
        }

        return found;
    }

    private static bool Continues(string command, string prefix) => command.Length > prefix.Length && command.StartsWith(prefix, StringComparison.Ordinal);

    /// <summary>The commands, newest first. With a scope, only the ones entered there.</summary>
    public IReadOnlyList<string> Commands(string? scope = null)
    {
        string key = Scope(scope);
        lock (_gate) return [.. _entries.Where(e => scope is null || e.Scope == key).Select(e => e.Command)];
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Modified = true;
        }
    }

    /// <summary>
    /// Replaces what is remembered with what a file holds. A file that is missing or cannot be read
    /// leaves nothing remembered, which is how a first run starts.
    /// </summary>
    public void Load(string path)
    {
        var loaded = new List<(string, string)>();
        try
        {
            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                int split = line.IndexOf(Separator);
                if (split < 0 || !Worth(line[(split + 1)..])) continue;

                loaded.Add((line[..split], line[(split + 1)..]));
                if (loaded.Count >= _capacity) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            loaded.Clear();
        }

        lock (_gate)
        {
            _entries.Clear();
            _entries.AddRange(loaded.Distinct());
            Modified = false;
        }
    }

    /// <summary>
    /// Writes what is remembered to a file as plain text: a line for each command, newest first, each
    /// with its scope and a tab in front. The file is written whole under another name and then moved
    /// into place, so a crash halfway leaves the old one.
    /// </summary>
    /// <returns>False if the file could not be written.</returns>
    public bool Save(string path)
    {
        var text = new StringBuilder();
        lock (_gate)
        {
            foreach (var (scope, command) in _entries) text.Append(scope).Append(Separator).Append(command).Append('\n');
        }

        string temporary = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder)
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
                else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            // Commands can have passwords and server names in them, so on Linux and macOS only the user may read the file.
            using (FileStream file = Elio.Core.PrivateFiles.Open(temporary, FileMode.CreateNew))
                file.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ToString()));

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // The leftover is harmless, and there is nothing more to try.
            }

            return false;
        }

        lock (_gate) Modified = false;
        return true;
    }
}

/// <summary>
/// Keeps track of the line being typed at a prompt, going only by the keys pressed. The terminal cannot
/// see the shell's line, so as soon as something happens that could change it in a way that cannot be
/// followed, the line counts as unknown until Enter starts a new one.
/// </summary>
public sealed class TypedLine
{
    private readonly StringBuilder _text = new();

    /// <summary>False once the line may no longer be what the keys alone would make it.</summary>
    public bool Known { get; private set; } = true;

    /// <summary>What has been typed since the last Enter, or null if that is not known.</summary>
    public string? Text => Known ? _text.ToString() : null;

    /// <summary>
    /// Text was sent: typed, or pasted. Anything in it that is not a character to show, such as a new
    /// line, a tab or a control code, makes the line unknown.
    /// </summary>
    public void Type(string text)
    {
        if (!Known) return;

        foreach (char c in text)
        {
            if (!char.IsControl(c)) continue;
            Invalidate();
            return;
        }

        _text.Append(text);
    }

    /// <summary>Backspace was pressed, which removes the last character while the cursor is at the end.</summary>
    public void Backspace()
    {
        if (!Known || _text.Length == 0) return;

        int remove = _text.Length >= 2 && char.IsLowSurrogate(_text[^1]) && char.IsHighSurrogate(_text[^2]) ? 2 : 1;
        _text.Length -= remove;
    }

    /// <summary>
    /// Something was sent that moves the cursor or changes the line in a way that cannot be followed:
    /// an arrow key, Tab, a control key, a click. Unknown until the next Enter.
    /// </summary>
    public void Invalidate()
    {
        Known = false;
        _text.Clear();
    }

    /// <summary>The line was thrown away and an empty one started, as Ctrl+C and Ctrl+U do in a shell.</summary>
    public void Reset()
    {
        Known = true;
        _text.Clear();
    }

    /// <summary>Enter was pressed. Returns the line that was entered if it is known and not empty, and starts a new one.</summary>
    public string? Enter()
    {
        string? entered = Known && _text.Length > 0 ? _text.ToString() : null;
        Reset();
        return entered;
    }

    /// <summary>
    /// Takes the rest of a suggestion. Returns what has to be sent to the shell to get there, which is
    /// empty if the suggestion does not continue the line.
    /// </summary>
    public string Accept(string suggestion) => Take(suggestion, wordOnly: false);

    /// <summary>Says the line is now this text, after what was typed has been rubbed out and written again.</summary>
    public void Replace(string line)
    {
        Known = true;
        _text.Clear();
        _text.Append(line);
    }

    /// <summary>Takes a suggestion up to the end of its next word.</summary>
    public string AcceptWord(string suggestion) => Take(suggestion, wordOnly: true);

    private string Take(string suggestion, bool wordOnly)
    {
        if (!Known || suggestion.Length <= _text.Length || !suggestion.StartsWith(_text.ToString(), StringComparison.Ordinal)) return string.Empty;

        int end = suggestion.Length;
        if (wordOnly)
        {
            // The blanks before the word go with it, so that taking word after word never stalls on them.
            end = _text.Length;
            while (end < suggestion.Length && char.IsWhiteSpace(suggestion[end])) end++;
            while (end < suggestion.Length && !char.IsWhiteSpace(suggestion[end])) end++;
        }

        string rest = suggestion[_text.Length..end];
        _text.Append(rest);
        return rest;
    }

    /// <summary>
    /// Whether what the shell shows to the left of the cursor ends with the line as typed. It does not
    /// while a password is being asked for, which is the one line that must never be remembered or
    /// completed, nor when the line on screen is not the one the keys suggest.
    /// </summary>
    /// <param name="beforeCursor">The cursor's row up to the cursor.</param>
    /// <param name="settled">
    /// True to require the whole line, false to accept its first characters alone: the last ones typed
    /// may not have come back from a slow server yet when Enter is pressed.
    /// </param>
    public static bool Echoed(string typed, string? beforeCursor, bool settled)
    {
        if (typed.Length == 0 || string.IsNullOrEmpty(beforeCursor)) return false;
        if (beforeCursor.EndsWith(typed, StringComparison.Ordinal)) return true;

        // A long line wraps, and then the cursor's row holds only its end.
        if (typed.Length > beforeCursor.Length && beforeCursor.Length >= 2 && typed.EndsWith(beforeCursor, StringComparison.Ordinal)) return true;
        if (settled) return false;

        for (int length = typed.Length - 1; length >= Math.Min(2, typed.Length); length--)
            if (beforeCursor.EndsWith(typed.AsSpan(0, length), StringComparison.Ordinal)) return true;
        return false;
    }
}

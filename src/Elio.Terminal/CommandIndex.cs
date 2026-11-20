namespace Elio.Terminal;

/// <summary>
/// The names that can be typed as a command: everything in the folders of <c>$PATH</c>, as read on this computer or as
/// listed by another one. It is what tells a command that is there from one that is not, for coloring a line as it is typed.
/// </summary>
/// <remarks>
/// A name that is not in the list is only called missing where the list is the whole story. A shell has more than what
/// is on <c>$PATH</c>: aliases, functions and, in PowerShell, cmdlets. Where the list cannot be trusted to be complete,
/// the answer for such a name is that it is not known.
/// </remarks>
public sealed class CommandIndex
{
    // What Windows runs by name when the system does not say. .ps1 is not in PATHEXT, but PowerShell runs it all the same.
    private static readonly string[] DefaultExtensions = [".exe", ".cmd", ".bat", ".com", ".ps1"];

    // Where commands are that a login may not have on its $PATH although the shell a person types in does, such as one that a
    // shell's own startup file adds to it. Better too many folders than a good command called missing.
    private const string UsualFolders = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:$HOME/bin:$HOME/.local/bin:/snap/bin:/opt/homebrew/bin";

    private readonly bool _authoritative;
    private readonly bool _ignoreCase;
    private readonly bool _thisComputer;
    private readonly object _gate = new();
    private HashSet<string> _listed;
    private readonly HashSet<string> _added;
    private int _scanning;
    private volatile bool _loaded;

    /// <param name="authoritative">
    /// Whether a name that is not listed is really missing: true for the <c>$PATH</c> of a POSIX shell. False where a shell
    /// has commands that no folder holds, as Windows shells do, and nothing is then ever called missing. Names are told
    /// apart by case where it is true, which is how a POSIX shell reads them, and not where it is false.
    /// </param>
    public CommandIndex(bool authoritative)
        : this(authoritative, ignoreCase: !authoritative, thisComputer: false)
    {
    }

    private CommandIndex(bool authoritative, bool ignoreCase, bool thisComputer)
    {
        _authoritative = authoritative;
        _ignoreCase = ignoreCase;
        _thisComputer = thisComputer;
        StringComparer comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _listed = new HashSet<string>(comparer);
        _added = new HashSet<string>(comparer);
    }

    /// <summary>
    /// The commands of this computer. They are read in the background, and until that is done every answer is "not known".
    /// A list is authoritative on Linux and macOS, where a command is a file in a folder of <c>$PATH</c>, and not on Windows.
    /// </summary>
    public static CommandIndex ForThisComputer()
    {
        bool windows = OperatingSystem.IsWindows();
        var index = new CommandIndex(authoritative: !windows && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()), ignoreCase: windows, thisComputer: true);
        index.Rescan();
        return index;
    }

    /// <summary>
    /// The commands another computer reported, one to a line (see <see cref="ListCommand"/>). Blank lines, the carriage returns
    /// of a server that ends lines the Windows way, and anything that cannot be a command's name are left out.
    /// </summary>
    public static CommandIndex FromListing(string listing, bool authoritative)
    {
        var index = new CommandIndex(authoritative);
        foreach (string line in listing.Split('\n'))
        {
            string name = line.Trim();
            if (IsName(name)) index._listed.Add(name);
        }

        index._loaded = true;
        return index;
    }

    /// <summary>
    /// A command line that makes a POSIX login print the name of every command on its <c>$PATH</c>, one to a line, sorted, each
    /// only once. It goes in an SSH exec channel, where the shell that runs it may be fish or csh, so everything is in one
    /// <c>sh -lc</c> with the script in single quotes, using nothing either of them would read differently.
    /// </summary>
    /// <remarks>
    /// <c>sh -l</c> reads the login files, so that what a profile adds to <c>$PATH</c> counts. What only a shell's own
    /// startup file adds does not, which is why the usual places are added: a command that a login does not see is called
    /// missing, and the caller may well prefer not to trust the list that far. Only what is an executable file is listed.
    /// </remarks>
    public static string ListCommand { get; } =
        "sh -lc 'PATH=\"$PATH:" + UsualFolders + "\"; IFS=:; for d in $PATH; do [ -d \"$d\" ] || continue; "
        + "for f in \"$d\"/*; do [ -f \"$f\" ] && [ -x \"$f\" ] && printf \"%s\\n\" \"${f##*/}\"; done; done 2>/dev/null | sort -u'";

    /// <summary>Whether the commands have been read. Until then nothing is called missing.</summary>
    public bool IsLoaded => _loaded;

    /// <summary>Counts a name as a command, such as the first word of one that was run before.</summary>
    public void Add(string name)
    {
        name = name.Trim();
        if (!IsName(name)) return;

        lock (_gate) _added.Add(name);
    }

    /// <summary>Whether a word is a command: listed, or not. A word that is a path and not a name to look for on <c>$PATH</c> is not known.</summary>
    public CommandState Check(string word)
    {
        if (string.IsNullOrEmpty(word) || word.AsSpan().ContainsAny('/', '\\') || word[0] is '~' or '$' or '.') return CommandState.Unknown;

        lock (_gate)
        {
            if (_listed.Contains(word) || _added.Contains(word)) return CommandState.Exists;
        }

        return _authoritative && _loaded ? CommandState.Missing : CommandState.Unknown;
    }

    /// <summary>
    /// Reads the folders of <c>$PATH</c> again, for a command that was installed since. The names read before stay in use until
    /// the new ones are ready. Nothing happens for a list that was not made from this computer's own folders.
    /// </summary>
    public void Rescan()
    {
        if (!_thisComputer || Interlocked.Exchange(ref _scanning, 1) == 1) return;

        // Taken now and not on the thread that reads, so that what is asked for is the $PATH of the moment of the call.
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string? pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        bool windows = OperatingSystem.IsWindows();

        _ = Task.Run(() =>
        {
            try
            {
                HashSet<string> names = Read(path, windows, ExtensionsFrom(pathExt), _ignoreCase);
                lock (_gate) _listed = names;
                _loaded = true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A computer whose folders cannot be read has no list, and says nothing is missing.
            }
            finally
            {
                Volatile.Write(ref _scanning, 0);
            }
        });
    }

    private static bool IsName(string name) =>
        name.Length > 0 && name.Length <= 255 && !name.AsSpan().ContainsAny(" \t/\\\0");

    /// <summary>The kinds of file Windows runs by name: what the system says, and the ones it is always taken to say.</summary>
    internal static string[] ExtensionsFrom(string? pathExt)
    {
        var all = new List<string>(DefaultExtensions);
        if (!string.IsNullOrWhiteSpace(pathExt))
        {
            foreach (string part in pathExt.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                string extension = part.StartsWith('.') ? part : "." + part;
                if (!all.Contains(extension, StringComparer.OrdinalIgnoreCase)) all.Add(extension);
            }
        }

        return [.. all];
    }

    /// <summary>
    /// The names a file in a folder of <c>$PATH</c> can be run by. On Windows a file runs only if its kind is one that
    /// the system runs, and by its whole name or without the kind, as in git and git.exe. Elsewhere by its name.
    /// </summary>
    internal static IReadOnlyList<string> NamesFor(string fileName, bool windowsStyle, IReadOnlyList<string> extensions)
    {
        if (!windowsStyle) return [fileName];

        string extension = Path.GetExtension(fileName);
        if (extension.Length == 0 || !extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return [];

        string bare = fileName[..^extension.Length];
        return bare.Length == 0 ? [fileName] : [fileName, bare];
    }

    private static HashSet<string> Read(string path, bool windows, IReadOnlyList<string> extensions, bool ignoreCase)
    {
        var names = new HashSet<string>(ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string folder in path.Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (!Directory.Exists(folder)) continue;

                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    string name = Path.GetFileName(file);
                    if (!windows && !IsExecutable(file)) continue;

                    foreach (string listed in NamesFor(name, windows, extensions))
                        if (IsName(listed)) names.Add(listed);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A folder that is not there or not readable has no commands in it, and the next is still read.
            }
        }

        return names;
    }

    private static bool IsExecutable(string file)
    {
        // Windows has no execute permission: what runs there is told by its kind, which is not asked here.
        if (OperatingSystem.IsWindows()) return true;

        const UnixFileMode Execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        try
        {
            return (File.GetUnixFileMode(file) & Execute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // A link that leads nowhere, or a file that cannot be looked at, is not a command.
            return false;
        }
    }
}

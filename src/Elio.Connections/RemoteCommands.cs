using System.Text;
using Elio.Core;

namespace Elio.Connections;

/// <summary>
/// Makes text safe to put in a command line that a POSIX shell on a server is going to read. Whatever the text holds,
/// the shell sees it back as one word, exactly as it was.
/// </summary>
public static class PosixQuote
{
    /// <summary>Inside single quotes nothing means anything, so only the quote itself needs a way out: close, say it, open again.</summary>
    /// <exception cref="ArgumentException">The text has a NUL in it, which no command line can hold.</exception>
    public static string Single(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0')) throw new ArgumentException(L.T("A name cannot hold a null character."), nameof(text));

        return "'" + text.Replace("'", "'\\''") + "'";
    }

    /// <summary>
    /// A path as an argument. One that starts with a dash would be read as an option, so it is made to start with the
    /// folder it is in: not every program a server has takes <c>--</c> to say where the options end.
    /// </summary>
    internal static string Path(string path) => Single(path.StartsWith('-') ? "./" + path : path);
}

public enum ArchiveKind
{
    None,
    Zip,
    Tar,
    TarGz,
    TarBz2,
    TarXz,

    /// <summary>One file packed on its own.</summary>
    Gz,
}

/// <summary>Knows the archives a server can open and make with the tools it is likely to have, and the commands that do it.</summary>
public static class Archives
{
    // Longest first where one ends the same way as another: ".tar.gz" before ".gz".
    private static readonly (string Suffix, ArchiveKind Kind)[] Suffixes =
    [
        (".tar.gz", ArchiveKind.TarGz),
        (".tgz", ArchiveKind.TarGz),
        (".tar.bz2", ArchiveKind.TarBz2),
        (".tbz2", ArchiveKind.TarBz2),
        (".tbz", ArchiveKind.TarBz2),
        (".tar.xz", ArchiveKind.TarXz),
        (".txz", ArchiveKind.TarXz),
        (".tar", ArchiveKind.Tar),
        (".zip", ArchiveKind.Zip),
        (".gz", ArchiveKind.Gz),
    ];

    /// <summary>What a file is by its name, in either case. A name that is nothing but the suffix is not an archive.</summary>
    public static ArchiveKind Detect(string fileName) => Match(fileName).Kind;

    private static (ArchiveKind Kind, int SuffixLength) Match(string fileName)
    {
        foreach ((string suffix, ArchiveKind kind) in Suffixes)
        {
            if (fileName.Length > suffix.Length && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return (kind, suffix.Length);
        }

        return (ArchiveKind.None, 0);
    }

    /// <summary>What a name made for this kind of archive ends with.</summary>
    public static string Extension(ArchiveKind kind) => kind switch
    {
        ArchiveKind.Zip => ".zip",
        ArchiveKind.Tar => ".tar",
        ArchiveKind.TarGz => ".tar.gz",
        ArchiveKind.TarBz2 => ".tar.bz2",
        ArchiveKind.TarXz => ".tar.xz",
        ArchiveKind.Gz => ".gz",
        _ => string.Empty,
    };

    /// <summary>The name of the folder an archive is unpacked into: its own name without the suffix.</summary>
    public static string FolderNameFor(string archiveFileName)
    {
        (ArchiveKind kind, int length) = Match(archiveFileName);
        if (kind != ArchiveKind.None) return archiveFileName[..^length];

        int dot = archiveFileName.LastIndexOf('.');
        return dot > 0 ? archiveFileName[..dot] : archiveFileName;
    }

    /// <summary>
    /// Unpacks an archive into a folder, made if it is not there. Paths should be complete ones. <c>tar</c> and
    /// <c>unzip</c> do not unpack to places above the folder, and cut a leading slash off the names in the archive. A zip never
    /// replaces a file that is already there and a .gz neither; <c>tar</c> does what it always does, since the option that
    /// would stop it is missing from the smaller ones.
    /// </summary>
    public static string ExtractCommand(string archivePath, string targetFolder, ArchiveKind kind)
    {
        string archive = PosixQuote.Path(archivePath);
        string target = PosixQuote.Path(targetFolder);
        string make = $"mkdir -p {target}";

        switch (kind)
        {
            case ArchiveKind.Zip:
                return $"{make} && unzip -n -q {archive} -d {target}";
            case ArchiveKind.Tar:
                return $"{make} && tar -xf {archive} -C {target}";

            // The kind is said out loud and not left to tar to find out: a smaller tar, such as BusyBox's, may not.
            case ArchiveKind.TarGz:
                return $"{make} && tar -xzf {archive} -C {target}";
            case ArchiveKind.TarBz2:
                return $"{make} && tar -xjf {archive} -C {target}";
            case ArchiveKind.TarXz:
                return $"{make} && tar -xJf {archive} -C {target}";

            case ArchiveKind.Gz:
            {
                // Never over a file that is there: what was in the folder is not the archive's to replace.
                string unpacked = PosixQuote.Path(UnpackedPath(archivePath, targetFolder));
                return $"{make} && [ ! -e {unpacked} ] && gunzip -c {archive} > {unpacked}";
            }

            default:
                return $"echo {PosixQuote.Single(L.T("This is not an archive that can be opened here."))} >&2; exit 2";
        }
    }

    // A file packed on its own comes out under the name it had before it was packed.
    private static string UnpackedPath(string archivePath, string targetFolder)
    {
        string name = archivePath[(archivePath.LastIndexOf('/') + 1)..];
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)) name = name[..^".gz".Length];
        if (name.Length == 0) name = "unpacked";

        return targetFolder.EndsWith('/') ? targetFolder + name : $"{targetFolder}/{name}";
    }

    /// <summary>
    /// Packs the named files and folders of a folder into an archive. The names are the ones in that folder, not
    /// paths, so that the archive holds them without the folders above. The archive is best written somewhere other
    /// than inside what it packs, and a half-written one is for the caller to remove if this fails.
    /// </summary>
    /// <exception cref="ArgumentException">There is nothing to pack, or the kind is not one to make.</exception>
    public static string CreateCommand(string folder, IReadOnlyList<string> names, string archivePath, ArchiveKind kind)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0) throw new ArgumentException(L.T("There is nothing to pack."), nameof(names));

        string tool = kind switch
        {
            ArchiveKind.Zip => "zip -r -q",
            ArchiveKind.Tar => "tar -cf",
            ArchiveKind.TarGz => "tar -czf",
            ArchiveKind.TarBz2 => "tar -cjf",
            ArchiveKind.TarXz => "tar -cJf",
            _ => throw new ArgumentException(L.T("This kind of archive cannot be made."), nameof(kind)),
        };

        var command = new StringBuilder($"cd {PosixQuote.Path(folder)} && {tool} {PosixQuote.Path(archivePath)}");
        foreach (string name in names) command.Append(' ').Append(PosixQuote.Path(name));
        return command.ToString();
    }
}

/// <summary>A line of a file that has what was searched for.</summary>
public sealed record ContentMatch(string Path, int Line, string Snippet);

/// <summary>Looks through the files of a folder on a server for some text, and reads what comes back.</summary>
public static class ContentSearchCommand
{
    private const int MaxSnippet = 200;

    /// <summary>
    /// The command line for a search. It is handed to <c>sh</c> whole, since the shell a user logs in with is not always
    /// one that reads <c>if</c> the same way. <c>rg</c> is used where there is one; otherwise <c>grep</c>, and if that
    /// is a small one that does not know the options for skipping binary files and ending each path with a NUL,
    /// a plainer run of it. Each file gives its first match only, no link is followed, and what is printed is cut
    /// off after <paramref name="limit"/> lines.
    /// </summary>
    /// <param name="regex">The query is a regular expression. Otherwise it is looked for as it is.</param>
    public static string Build(string folder, string query, bool ignoreCase, bool regex, int limit)
    {
        // Without the slash at the end, so that no path that comes back has two in a row.
        string trimmed = folder.TrimEnd('/');
        string root = PosixQuote.Path(trimmed.Length > 0 ? trimmed : folder.Length > 0 ? "/" : ".");
        string text = PosixQuote.Single(query);
        string caseFlag = ignoreCase ? " -i" : string.Empty;
        string kindFlag = regex ? string.Empty : " -F";
        string grepKind = regex ? " -E" : " -F";

        // --null and not -Z, which on a Mac's grep means something else. The probe is run on a line that does match, so
        // that only an option the program does not know can make it fail.
        const string Probe = "printf 'x\\n' | grep -I --null -n -m 1 --exclude-dir=.git -e x >/dev/null 2>&1";

        string script =
            "if command -v rg >/dev/null 2>&1; then " +
            $"rg --null --line-number --max-count 1 --no-heading --color never --hidden --no-ignore --glob '!.git'{caseFlag}{kindFlag} -e {text} -- {root}; " +
            $"elif {Probe}; then " +
            $"grep -rIn --null -m 1 --exclude-dir=.git{caseFlag}{grepKind} -e {text} -- {root}; " +
            "else " +
            $"grep -rnH -m 1{caseFlag}{grepKind} -e {text} -- {root}; " +
            $"fi | head -n {Math.Max(1, limit)}";

        return "sh -c " + PosixQuote.Single(script);
    }

    /// <summary>
    /// Reads what a search printed: a path, a NUL where <c>grep</c> and <c>rg</c> were asked for one, the line number, a
    /// colon and the line. Where there is no NUL the path ends at the first <c>:12:</c>, so a path that has such a thing in
    /// it is cut short. Lines that are not matches, like a complaint about a file that could not be read, are left out.
    /// </summary>
    public static IReadOnlyList<ContentMatch> Parse(string output)
    {
        var matches = new List<ContentMatch>();
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("Binary file ", StringComparison.Ordinal)) continue;

            ContentMatch? match = line.Contains('\0') ? ParseNull(line) : ParsePlain(line);
            if (match is not null) matches.Add(match);
        }

        return matches;
    }

    private static ContentMatch? ParseNull(string line)
    {
        int nul = line.IndexOf('\0');
        if (nul == 0) return null;

        ReadOnlySpan<char> rest = line.AsSpan(nul + 1);
        int colon = rest.IndexOf(':');
        if (colon <= 0 || !int.TryParse(rest[..colon], out int number)) return null;

        return new ContentMatch(line[..nul], number, Snippet(rest[(colon + 1)..]));
    }

    private static ContentMatch? ParsePlain(string line)
    {
        // The first ":<digits>:" after at least one character of path.
        for (int colon = line.IndexOf(':', 1); colon > 0; colon = line.IndexOf(':', colon + 1))
        {
            int end = colon + 1;
            while (end < line.Length && line[end] is >= '0' and <= '9') end++;
            if (end == colon + 1 || end >= line.Length || line[end] != ':') continue;
            if (!int.TryParse(line.AsSpan(colon + 1, end - colon - 1), out int number)) continue;

            return new ContentMatch(line[..colon], number, Snippet(line.AsSpan(end + 1)));
        }

        return null;
    }

    // A minified file is one line of a megabyte, and the list shows a glimpse of it.
    private static string Snippet(ReadOnlySpan<char> text)
    {
        ReadOnlySpan<char> trimmed = text.Trim();
        return trimmed.Length <= MaxSnippet ? trimmed.ToString() : string.Concat(trimmed[..MaxSnippet], "…");
    }
}

/// <summary>Changes the permissions of a folder and of everything in it on the server itself, in one command.</summary>
public static class PermissionCommand
{
    /// <summary>
    /// <c>chmod -R</c> with the mode as three octal digits. GNU's, BusyBox's and the BSDs' all leave alone the links they
    /// find below the folder rather than follow them, go on past what they cannot change and say so for each, and GNU's
    /// keeps a folder's set-user and set-group bits, as the one-at-a-time way does too.
    /// </summary>
    /// <param name="mode">Nine bits, such as 0b111_101_101 for 755. Anything above them is left out.</param>
    public static string Build(string folder, int mode) =>
        $"chmod -R {(mode >> 6) & 7}{(mode >> 3) & 7}{mode & 7} {PosixQuote.Path(folder)}";
}

/// <summary>Copies on the server itself, which is much quicker than carrying the data there and back.</summary>
public static class CopyCommand
{
    /// <summary>
    /// A copy that keeps permissions, times and links, and takes a folder with what is in it. It would put the copy
    /// inside a folder that is already there, so the caller makes sure the target is not.
    /// </summary>
    public static string Build(string sourcePath, string targetPath) =>
        $"cp -a -- {PosixQuote.Single(sourcePath)} {PosixQuote.Single(targetPath)}";
}

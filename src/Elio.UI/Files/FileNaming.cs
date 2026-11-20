namespace Elio.UI.Files;

/// <summary>Names for what the panel makes without being told a name: copies, and folders that archives are unpacked into.</summary>
public static class FileNaming
{
    /// <summary>
    /// A name for a copy that nothing in the folder has yet: "report.txt" becomes "report copy.txt", then
    /// "report copy 2.txt" and so on. The extension stays last, and a folder or a name that only starts with a dot has none.
    /// </summary>
    /// <param name="taken">The names in the folder. Compared without regard to case, since a copy that differs only by case would
    /// replace the original on the file systems that do not tell them apart.</param>
    public static string CopyName(string name, bool isDirectory, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        (string stem, string extension) = Split(name, isDirectory);

        string candidate = $"{stem} copy{extension}";
        for (int number = 2; used.Contains(candidate); number++) candidate = $"{stem} copy {number}{extension}";
        return candidate;
    }

    /// <summary>The first name of the form "name", "name 2", "name 3" that the folder does not have.</summary>
    public static string Unused(string name, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        string candidate = name;
        for (int number = 2; used.Contains(candidate); number++) candidate = $"{name} {number}";
        return candidate;
    }

    /// <summary>Whether a name typed by the user can be one file's name: not empty, no folder separators, not "." or "..".</summary>
    public static bool IsPlainName(string name) =>
        name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\', '\0']) < 0 && name.Trim().Length > 0;

    private static (string Stem, string Extension) Split(string name, bool isDirectory)
    {
        int dot = name.LastIndexOf('.');
        if (isDirectory || dot <= 0) return (name, string.Empty);

        string stem = name[..dot];
        string extension = name[dot..];

        // "a.tar.gz" is one kind of file, and its copy is "a copy.tar.gz".
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase) && extension.ToLowerInvariant() is ".gz" or ".bz2" or ".xz")
            return (stem[..^4], stem[^4..] + extension);

        return (stem, extension);
    }
}

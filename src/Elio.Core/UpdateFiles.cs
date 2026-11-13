using System.Formats.Tar;
using System.IO.Compression;

namespace Elio.Core;

/// <summary>
/// The file work of installing an update: taking the one program out of a release's archive, and putting it where the
/// running one is. Nothing here knows about the network or about who signed what; that is settled before it is called.
/// </summary>
public static class UpdateFiles
{
    /// <summary>The most one file taken out of an archive may take, unpacked.</summary>
    public const long LargestProgram = 600L * 1024 * 1024;

    /// <summary>The ending of the name a replaced program or bundle is kept under until the next start can delete it.</summary>
    public const string AsideSuffix = ".old";

    /// <summary>The ending of the name a new program has while it is being put in place.</summary>
    public const string NewSuffix = ".new";

    /// <summary>
    /// Writes the one file called <paramref name="fileName"/> out of a zip. The archive's own paths are never used: the
    /// file is looked for at the top of the archive or one folder down, which is where a release keeps it, and is
    /// written to <paramref name="destination"/> and nowhere else.
    /// </summary>
    /// <returns>False if the archive holds no such file.</returns>
    public static bool ExtractFromZip(string archive, string fileName, string destination)
    {
        using ZipArchive zip = ZipFile.OpenRead(archive);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!IsPlainEntry(entry.FullName, fileName) || entry.Length is <= 0 or > LargestProgram) continue;

            using Stream source = entry.Open();
            CopyNew(source, destination);
            return true;
        }

        return false;
    }

    /// <summary>The same for a gzipped tar. Links and folders are never taken, only a regular file.</summary>
    public static bool ExtractFromTarGz(string archive, string fileName, string destination)
    {
        using FileStream file = File.OpenRead(archive);
        using var unzipped = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(unzipped);
        while (tar.GetNextEntry(copyData: false) is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            if (!IsPlainEntry(entry.Name, fileName) || entry.DataStream is null || entry.Length is <= 0 or > LargestProgram) continue;

            CopyNew(entry.DataStream, destination);
            return true;
        }

        return false;
    }

    /// <summary>Whether a path in an archive is the file, at the top or one folder down, with nothing that leaves the folder.</summary>
    internal static bool IsPlainEntry(string path, string fileName)
    {
        string[] parts = path.Replace('\\', '/').Split('/');
        return parts.Length is 1 or 2 && parts[^1] == fileName && parts.All(part => part.Length > 0 && part is not ("." or ".."));
    }

    private static void CopyNew(Stream source, string destination)
    {
        using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        // The size an archive claims is not taken on trust: a few kilobytes can unpack to gigabytes.
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > LargestProgram) throw new InvalidDataException("The file in the archive is larger than any Elio program.");
            target.Write(buffer, 0, read);
        }
    }

    /// <summary>
    /// Puts <paramref name="replacement"/> where <paramref name="target"/> is. The old file is renamed aside first
    /// rather than overwritten, which is what lets it be replaced while it runs, on Windows too, and if the new one
    /// can not be moved in, the old one goes back.
    /// </summary>
    /// <returns>Where the old file was kept.</returns>
    public static string Swap(string target, string replacement)
    {
        string aside = FreeName(target + AsideSuffix);
        File.Move(target, aside);
        try
        {
            File.Move(replacement, target);
        }
        catch
        {
            File.Move(aside, target);
            throw;
        }

        return aside;
    }

    /// <summary>The same for a folder, such as a macOS application bundle.</summary>
    public static string SwapDirectory(string target, string replacement)
    {
        string aside = FreeName(target + AsideSuffix);
        Directory.Move(target, aside);
        try
        {
            Directory.Move(replacement, target);
        }
        catch
        {
            Directory.Move(aside, target);
            throw;
        }

        return aside;
    }

    // The old copy of an earlier update may still be there, held open by a shell that outlived its window.
    private static string FreeName(string wanted)
    {
        for (int number = 0; ; number++)
        {
            string name = number == 0 ? wanted : wanted + number;
            if (!File.Exists(name) && !Directory.Exists(name)) return name;
            if (Delete(name)) return name;
        }
    }

    /// <summary>
    /// Deletes what an update left beside <paramref name="target"/>: the program it replaced and a new one that never
    /// went in. A file that is still in use stays and is tried again at the next start. Never throws.
    /// </summary>
    public static void RemoveLeftovers(string target)
    {
        try
        {
            string? folder = Path.GetDirectoryName(target);
            if (folder is null || !Directory.Exists(folder)) return;

            string name = Path.GetFileName(target);
            foreach (string pattern in new[] { name + AsideSuffix + "*", name + NewSuffix })
            {
                foreach (string leftover in Directory.EnumerateFileSystemEntries(folder, pattern)) Delete(leftover);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next start.
        }
    }

    /// <summary>Whether files can be made in a folder, which is what replacing a program there takes.</summary>
    public static bool CanWriteTo(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, ".elio-write-" + Guid.NewGuid().ToString("N")[..8]);
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Delete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            return !File.Exists(path) && !Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Elio.Connections;
using Elio.Core;

namespace Elio.UI.Files;

/// <summary>One line in a file list.</summary>
/// <param name="IsHidden">A name that starts with a dot, or a file marked hidden on Windows.</param>
/// <param name="Where">Set for a result found in a folder below the one shown: that folder, written from the one shown.</param>
/// <param name="Snippet">Set for a result of a search through what files say: the line that matched.</param>
/// <param name="Line">The number of that line, counted from 1.</param>
public sealed record FileRow(string Name, string Path, long Size, DateTime ModifiedUtc, bool IsDirectory, bool IsLink, string Permissions, RemoteEntry? Remote, bool IsHidden = false, string? Where = null, string? Snippet = null, int Line = 0)
{
    /// <summary>Whether this is where a search of what files say found something. Such a row is only a way to get to the file.</summary>
    public bool IsContentHit => Snippet is not null;
}

/// <summary>What a search through the contents of files found.</summary>
/// <param name="Note">Something the user should be told, such as why a search could not run on a server.</param>
/// <param name="Truncated">The search stopped at its limit, with more still to be found.</param>
public sealed record ContentSearchResult(IReadOnlyList<FileRow> Rows, string? Note = null, bool Truncated = false);

/// <summary>Where a panel's files come from, so both sides of the screen can be the same control.</summary>
/// <remarks>
/// What goes beyond listing, renaming and deleting has a default that refuses, so that a source made for a test, or one
/// that cannot do something, only has to say what it can.
/// </remarks>
public interface IFileSource
{
    bool IsRemote { get; }

    Task<string> GetStartPathAsync(CancellationToken cancellation);

    Task<List<FileRow>> ListAsync(string path, CancellationToken cancellation);

    /// <summary>The folder above, or null at the top.</summary>
    string? Parent(string path);

    string Combine(string folder, string name);

    Task CreateFolderAsync(string path, CancellationToken cancellation);

    Task RenameAsync(FileRow row, string newName, CancellationToken cancellation);

    Task DeleteAsync(FileRow row, CancellationToken cancellation);

    /// <summary>Makes an empty file. Fails if the name is taken.</summary>
    Task CreateFileAsync(string path, CancellationToken cancellation) => throw new NotSupportedException();

    /// <summary>Copies a file or a folder to a path that must not exist yet.</summary>
    /// <returns>How many links were left out, which are not followed.</returns>
    Task<int> CopyAsync(FileRow row, string targetPath, CancellationToken cancellation) => throw new NotSupportedException();

    /// <summary>Moves a file or a folder to a path that must not exist yet.</summary>
    Task MoveAsync(FileRow row, string targetPath, CancellationToken cancellation) => throw new NotSupportedException();

    /// <summary>The first bytes of a file, or all of it if it is shorter.</summary>
    Task<byte[]> ReadAsync(string path, int limit, CancellationToken cancellation) => throw new NotSupportedException();

    /// <summary>Replaces a file with these bytes, all or nothing.</summary>
    Task WriteAsync(string path, byte[] data, CancellationToken cancellation) => throw new NotSupportedException();

    /// <summary>The size and time of a file, or null if it is not there.</summary>
    Task<(long Size, DateTime ModifiedUtc)?> StatAsync(string path, CancellationToken cancellation) => throw new NotSupportedException();

    /// <summary>Whether this source can unpack and pack archives and search inside files. A server may not allow commands.</summary>
    bool CanRunCommands => false;

    Task ExtractAsync(FileRow archive, string targetFolder, CancellationToken cancellation) => throw new NotSupportedException();

    Task CompressAsync(string folder, IReadOnlyList<string> names, string archiveName, ArchiveKind kind, CancellationToken cancellation) => throw new NotSupportedException();

    Task<ContentSearchResult> SearchContentAsync(string folder, string query, bool ignoreCase, bool regex, int limit, CancellationToken cancellation) => throw new NotSupportedException();

    /// <summary>Whether permissions can be changed here. Windows has none of this kind, so this computer cannot there.</summary>
    bool CanChangeMode => false;

    /// <summary>
    /// Sets the permissions of one file or folder to nine bits, such as 0b110_100_100 for 644. A folder keeps its set-user
    /// and set-group bits, as <c>chmod</c> leaves them. A link is changed through, as <c>chmod</c> does: what it points at changes.
    /// </summary>
    Task ChangeModeAsync(FileRow row, int mode, CancellationToken cancellation) => throw new NotSupportedException();

    /// <summary>
    /// Sets the permissions of a folder and of everything in it in one go, where that is far quicker than one at a time.
    /// No link inside is followed.
    /// </summary>
    /// <returns>What could not be changed, one line each; or null when it cannot be done in one go here, and nothing was changed.</returns>
    Task<IReadOnlyList<string>?> ChangeModeTreeAsync(FileRow folder, int mode, CancellationToken cancellation) => Task.FromResult<IReadOnlyList<string>?>(null);
}

/// <summary>Files on this computer.</summary>
public sealed class LocalFileSource : IFileSource
{
    /// <summary>The list of drives on Windows, which has no single root folder.</summary>
    private const string TopLevel = "";

    // A file bigger than this is not read through when looking for words in it: it is not a text anyone searches.
    private const long SearchableBytes = 8 * 1024 * 1024;
    private const int BinaryProbeBytes = 4096;
    private const int SnippetLength = 200;
    private const string PartSuffix = ".eliopart";
    private const int MaxSearchDepth = 40;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public bool IsRemote => false;

    public bool CanRunCommands => true;

    public Task<string> GetStartPathAsync(CancellationToken cancellation) =>
        Task.FromResult(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public Task<List<FileRow>> ListAsync(string path, CancellationToken cancellation) => Task.Run(() =>
    {
        if (path == TopLevel && OperatingSystem.IsWindows())
            return DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => new FileRow(d.Name, d.Name, 0, DateTime.MinValue, true, false, string.Empty, null)).ToList();

        var rows = new List<FileRow>();
        foreach (FileSystemInfo info in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            cancellation.ThrowIfCancellationRequested();
            bool isDirectory = info is DirectoryInfo;
            bool hidden = info.Name.StartsWith('.') || info.Attributes.HasFlag(FileAttributes.Hidden);
            rows.Add(new FileRow(info.Name, info.FullName, isDirectory ? 0 : ((FileInfo)info).Length, info.LastWriteTimeUtc, isDirectory, info.LinkTarget is not null, PermissionText(info), null, hidden));
        }

        return rows;
    }, cancellation);

    private static string PermissionText(FileSystemInfo info)
    {
        if (OperatingSystem.IsWindows()) return string.Empty;

        UnixFileMode mode = info.UnixFileMode;
        char Flag(UnixFileMode bit, char letter) => mode.HasFlag(bit) ? letter : '-';
        return string.Concat(
            Flag(UnixFileMode.UserRead, 'r'), Flag(UnixFileMode.UserWrite, 'w'), Flag(UnixFileMode.UserExecute, 'x'),
            Flag(UnixFileMode.GroupRead, 'r'), Flag(UnixFileMode.GroupWrite, 'w'), Flag(UnixFileMode.GroupExecute, 'x'),
            Flag(UnixFileMode.OtherRead, 'r'), Flag(UnixFileMode.OtherWrite, 'w'), Flag(UnixFileMode.OtherExecute, 'x'));
    }

    public string? Parent(string path)
    {
        if (path == TopLevel) return null;
        string? parent = Directory.GetParent(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is { Length: > 0 } trimmed && !IsDriveRoot(path) ? trimmed : path)?.FullName;
        if (parent is not null && !IsDriveRoot(path)) return parent;
        return OperatingSystem.IsWindows() ? TopLevel : null;
    }

    private static bool IsDriveRoot(string path) => Path.GetPathRoot(path) == path;

    public string Combine(string folder, string name) => Path.Combine(folder, name);

    public Task CreateFolderAsync(string path, CancellationToken cancellation) => Task.Run(() => Directory.CreateDirectory(path), cancellation);

    public Task RenameAsync(FileRow row, string newName, CancellationToken cancellation) => Task.Run(() =>
    {
        string target = Path.Combine(Path.GetDirectoryName(row.Path)!, newName);
        if (row.IsDirectory) Directory.Move(row.Path, target);
        else File.Move(row.Path, target);
    }, cancellation);

    public Task DeleteAsync(FileRow row, CancellationToken cancellation) => Task.Run(() =>
    {
        // A link to a folder goes as a link, without touching what it points at.
        if (row.IsDirectory) Directory.Delete(row.Path, recursive: !row.IsLink);
        else File.Delete(row.Path);
    }, cancellation);

    public Task CreateFileAsync(string path, CancellationToken cancellation) => Task.Run(() =>
    {
        using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
    }, cancellation);

    public Task<int> CopyAsync(FileRow row, string targetPath, CancellationToken cancellation) => Task.Run(() => CopyTree(row.Path, row.IsDirectory, row.IsLink, targetPath, cancellation), cancellation);

    // Links are left out: a link to a folder could lead back into the folder being copied, and the copy of a link
    // that is followed is not a link any more, which nobody asked for.
    private static int CopyTree(string source, bool isDirectory, bool isLink, string target, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (isLink) return 1;

        if (!isDirectory)
        {
            File.Copy(source, target, overwrite: false);
            return 0;
        }

        string full = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(target).StartsWith(full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException(L.T("A folder cannot be copied into itself."));

        Directory.CreateDirectory(target);
        int skipped = 0;
        foreach (FileSystemInfo child in new DirectoryInfo(source).EnumerateFileSystemInfos())
            skipped += CopyTree(child.FullName, child is DirectoryInfo, child.LinkTarget is not null, Path.Combine(target, child.Name), cancellation);

        return skipped;
    }

    public Task MoveAsync(FileRow row, string targetPath, CancellationToken cancellation) => Task.Run(() =>
    {
        try
        {
            if (row.IsDirectory) Directory.Move(row.Path, targetPath);
            else File.Move(row.Path, targetPath);
        }
        catch (IOException) when (!File.Exists(targetPath) && !Directory.Exists(targetPath) && (File.Exists(row.Path) || Directory.Exists(row.Path)))
        {
            // Another drive: a move there is a copy followed by deleting the original, and only once the copy is whole.
            if (CopyTree(row.Path, row.IsDirectory, row.IsLink, targetPath, cancellation) > 0)
                throw new IOException(L.T("Links inside cannot be moved to another drive."));

            if (row.IsDirectory) Directory.Delete(row.Path, recursive: !row.IsLink);
            else File.Delete(row.Path);
        }
    }, cancellation);

    public async Task<byte[]> ReadAsync(string path, int limit, CancellationToken cancellation)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        var buffer = new byte[(int)Math.Min(limit, file.Length)];
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = await file.ReadAsync(buffer.AsMemory(filled), cancellation).ConfigureAwait(false);
            if (read == 0) break;
            filled += read;
        }

        return filled == buffer.Length ? buffer : buffer[..filled];
    }

    public async Task WriteAsync(string path, byte[] data, CancellationToken cancellation)
    {
        // Beside the file first and swapped in at the end, so that a disk that fills up leaves the old file as it was.
        string part = path + PartSuffix;
        await File.WriteAllBytesAsync(part, data, cancellation).ConfigureAwait(false);

        if (!OperatingSystem.IsWindows() && File.Exists(path)) File.SetUnixFileMode(part, File.GetUnixFileMode(path));
        File.Move(part, path, overwrite: true);
    }

    public Task<(long Size, DateTime ModifiedUtc)?> StatAsync(string path, CancellationToken cancellation)
    {
        var info = new FileInfo(path);
        return Task.FromResult<(long, DateTime)?>(info.Exists ? (info.Length, info.LastWriteTimeUtc) : null);
    }

    public bool CanChangeMode => !OperatingSystem.IsWindows();

    public Task ChangeModeAsync(FileRow row, int mode, CancellationToken cancellation) => Task.Run(() =>
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(L.T("Windows has no permissions of this kind."));

        // A folder keeps its set-user and set-group bits, which say who owns what is made in it, as chmod leaves them.
        UnixFileMode kept = row.IsDirectory ? File.GetUnixFileMode(row.Path) & (UnixFileMode.SetUser | UnixFileMode.SetGroup) : UnixFileMode.None;
        File.SetUnixFileMode(row.Path, (UnixFileMode)(mode & 0b111_111_111) | kept);
    }, cancellation);

    public Task ExtractAsync(FileRow archive, string targetFolder, CancellationToken cancellation) => Task.Run(() =>
    {
        ArchiveKind kind = Archives.Detect(archive.Name);
        Directory.CreateDirectory(targetFolder);

        switch (kind)
        {
            case ArchiveKind.Zip:
                // Entries that would land outside the folder are refused by the library.
                ZipFile.ExtractToDirectory(archive.Path, targetFolder, overwriteFiles: false);
                break;
            case ArchiveKind.Tar:
                using (FileStream plain = File.OpenRead(archive.Path)) ExtractTar(plain, targetFolder, cancellation);
                break;
            case ArchiveKind.TarGz:
                using (FileStream packed = File.OpenRead(archive.Path))
                using (var unpacked = new GZipStream(packed, CompressionMode.Decompress)) ExtractTar(unpacked, targetFolder, cancellation);
                break;
            case ArchiveKind.Gz:
                using (FileStream packed = File.OpenRead(archive.Path))
                using (var unpacked = new GZipStream(packed, CompressionMode.Decompress))
                using (var output = new FileStream(Path.Combine(targetFolder, archive.Name[..^3]), FileMode.CreateNew, FileAccess.Write)) unpacked.CopyTo(output);
                break;
            default:
                throw new NotSupportedException(L.T("This computer unpacks .zip, .tar, .tar.gz and .gz. Other kinds can be unpacked on a server."));
        }
    }, cancellation);

    // One entry at a time, and only files and folders: a link in an archive could point out of the folder, and what is
    // unpacked after it would then be written wherever it points.
    private static void ExtractTar(Stream stream, string targetFolder, CancellationToken cancellation)
    {
        string root = Path.GetFullPath(targetFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is TarEntry entry)
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory)) continue;

            string path = Path.GetFullPath(Path.Combine(targetFolder, entry.Name));
            if (!IsInside(root, path))
                throw new IOException(L.T("The archive holds a path that leads out of the folder: {0}", entry.Name));

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
    }

    private static bool IsInside(string root, string path)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.StartsWith(root, comparison) || string.Equals(path + Path.DirectorySeparatorChar, root, comparison);
    }

    public Task CompressAsync(string folder, IReadOnlyList<string> names, string archiveName, ArchiveKind kind, CancellationToken cancellation) => Task.Run(() =>
    {
        string archivePath = Path.Combine(folder, archiveName);
        using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write);

        try
        {
            switch (kind)
            {
                case ArchiveKind.Zip:
                    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                        foreach (string name in names) AddToZip(zip, Path.Combine(folder, name), name, cancellation);
                    break;
                case ArchiveKind.Tar:
                    WriteTar(output, folder, names, cancellation);
                    break;
                case ArchiveKind.TarGz:
                    using (var packed = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) WriteTar(packed, folder, names, cancellation);
                    break;
                default:
                    throw new NotSupportedException(L.T("This computer makes .zip, .tar and .tar.gz archives."));
            }
        }
        catch
        {
            // Half an archive is worse than none.
            output.Dispose();
            try
            {
                File.Delete(archivePath);
            }
            catch (IOException)
            {
                // Still held open: the leftover is named like an archive and can be deleted by hand.
            }

            throw;
        }
    }, cancellation);

    private static void AddToZip(ZipArchive zip, string path, string entryName, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        if (info.LinkTarget is not null) return;

        if (Directory.Exists(path))
        {
            zip.CreateEntry(entryName.TrimEnd('/') + "/");
            foreach (string child in Directory.EnumerateFileSystemEntries(path)) AddToZip(zip, child, entryName.TrimEnd('/') + "/" + Path.GetFileName(child), cancellation);
            return;
        }

        zip.CreateEntryFromFile(path, entryName, CompressionLevel.Optimal);
    }

    private static void WriteTar(Stream output, string folder, IReadOnlyList<string> names, CancellationToken cancellation)
    {
        using var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true);
        foreach (string name in names) AddToTar(writer, Path.Combine(folder, name), name, cancellation);
    }

    private static void AddToTar(TarWriter writer, string path, string entryName, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (new FileInfo(path).LinkTarget is not null) return;

        writer.WriteEntry(path, entryName);
        if (!Directory.Exists(path)) return;

        foreach (string child in Directory.EnumerateFileSystemEntries(path)) AddToTar(writer, child, entryName.TrimEnd('/') + "/" + Path.GetFileName(child), cancellation);
    }

    public Task<ContentSearchResult> SearchContentAsync(string folder, string query, bool ignoreCase, bool regex, int limit, CancellationToken cancellation) => Task.Run(() =>
    {
        Regex? pattern = regex ? new Regex(query, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, RegexTimeout) : null;
        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var found = new List<FileRow>();
        bool truncated = false;

        bool Matches(string line)
        {
            try
            {
                return pattern is null ? line.Contains(query, comparison) : pattern.IsMatch(line);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((folder, 0));
        while (pending.Count > 0 && !truncated)
        {
            (string current, int depth) = pending.Pop();
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(current).EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (FileSystemInfo child in children)
            {
                cancellation.ThrowIfCancellationRequested();
                if (child.LinkTarget is not null) continue;

                if (child is DirectoryInfo)
                {
                    if (depth < MaxSearchDepth) pending.Push((child.FullName, depth + 1));
                    continue;
                }

                var file = (FileInfo)child;
                if (file.Length is 0 or > SearchableBytes) continue;
                if (FirstMatch(file.FullName, Matches) is not (int line, string text)) continue;

                string relative = Path.GetRelativePath(folder, file.DirectoryName ?? folder);
                found.Add(new FileRow(file.Name, file.FullName, file.Length, file.LastWriteTimeUtc, false, false, PermissionText(file), null, file.Name.StartsWith('.'), relative == "." ? null : relative, Trim(text), line));
                if (found.Count >= limit)
                {
                    truncated = true;
                    break;
                }
            }
        }

        return new ContentSearchResult(found, null, truncated);
    }, cancellation);

    private static (int Line, string Text)? FirstMatch(string path, Func<string, bool> matches)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            // A file with a zero byte in its start is not text, whatever its name says.
            var probe = new byte[BinaryProbeBytes];
            int probed = stream.Read(probe, 0, probe.Length);
            if (Array.IndexOf(probe, (byte)0, 0, probed) >= 0) return null;
            stream.Position = 0;

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            int number = 0;
            while (reader.ReadLine() is string line)
            {
                number++;
                if (matches(line)) return (number, line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read has nothing to show.
        }

        return null;
    }

    internal static string Trim(string line)
    {
        string text = line.Trim();
        return text.Length <= SnippetLength ? text : text[..SnippetLength] + "…";
    }
}

/// <summary>Files on a server.</summary>
/// <param name="run">Runs a command on the server, for what SFTP cannot do. Null when the server may not be given commands.</param>
public sealed class RemoteFileSource(Func<CancellationToken, Task<SftpConnection>> connection, Func<string, CancellationToken, Task<CommandResult>>? run = null) : IFileSource
{
    // A line of a match is shown at most this long.
    private const int MaxLineLength = 200;

    public bool IsRemote => true;

    public bool CanRunCommands => run is not null;

    public async Task<string> GetStartPathAsync(CancellationToken cancellation) =>
        (await connection(cancellation)).HomeDirectory;

    public async Task<List<FileRow>> ListAsync(string path, CancellationToken cancellation)
    {
        SftpConnection c = await connection(cancellation);
        return (await c.ListAsync(path, cancellation)).Select(e => new FileRow(e.Name, e.FullPath, e.Size, e.ModifiedUtc, e.IsDirectory, e.IsSymbolicLink, e.Permissions, e, e.Name.StartsWith('.'))).ToList();
    }

    public string? Parent(string path) => path is "/" or "" ? null : RemotePath.Parent(path);

    public string Combine(string folder, string name) => RemotePath.Combine(folder, name);

    public async Task CreateFolderAsync(string path, CancellationToken cancellation) =>
        await (await connection(cancellation)).CreateDirectoryAsync(path, cancellation);

    public async Task RenameAsync(FileRow row, string newName, CancellationToken cancellation) =>
        await (await connection(cancellation)).RenameAsync(row.Remote!, RemotePath.Combine(RemotePath.Parent(row.Path), newName), cancellation);

    public async Task DeleteAsync(FileRow row, CancellationToken cancellation) =>
        await (await connection(cancellation)).DeleteAsync(row.Remote!, cancellation);

    public bool CanChangeMode => true;

    // A row found by a search through what files say has no listing behind it, and is changed by its path.
    public async Task ChangeModeAsync(FileRow row, int mode, CancellationToken cancellation) =>
        await (await connection(cancellation)).ChangeModeAsync(row.Remote ?? new RemoteEntry(row.Name, row.Path, row.Size, row.ModifiedUtc, row.IsDirectory, row.IsLink, row.Permissions), mode, cancellation);

    public async Task<IReadOnlyList<string>?> ChangeModeTreeAsync(FileRow folder, int mode, CancellationToken cancellation)
    {
        if (run is null || folder.IsLink) return null;

        CommandResult result = await run(PermissionCommand.Build(folder.Path, mode), cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();

        // A server that would not run it, or has no chmod, is gone through over SFTP instead. Nothing was changed by this.
        if (result.Failure is not null || result.ExitCode is null or 126 or 127) return null;
        if (result.ExitCode == 0) return [];

        // chmod goes on past what it cannot change, and says so for each on a line of its own.
        string[] lines = result.Error.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines : [Complaint(result)];
    }

    public async Task CreateFileAsync(string path, CancellationToken cancellation) =>
        await (await connection(cancellation)).CreateFileAsync(path, cancellation);

    public async Task<int> CopyAsync(FileRow row, string targetPath, CancellationToken cancellation)
    {
        // The server copies on its own where it can be asked to, which is far quicker than bringing every byte here and
        // sending it back. Only a server that cannot be given commands has the copy go through this computer.
        if (run is not null && !row.IsLink)
        {
            CommandResult result = await run(CopyCommand.Build(row.Path, targetPath), cancellation).ConfigureAwait(false);
            if (result.Succeeded) return 0;
            if (result.Failure is null && result.ExitCode is not (126 or 127)) throw new IOException(Complaint(result));
        }

        return await (await connection(cancellation)).CopyAsync(row.Remote!, targetPath, cancellation);
    }

    public async Task MoveAsync(FileRow row, string targetPath, CancellationToken cancellation) =>
        await (await connection(cancellation)).MoveAsync(row.Remote!, targetPath, cancellation);

    public async Task<byte[]> ReadAsync(string path, int limit, CancellationToken cancellation) =>
        await (await connection(cancellation)).ReadAsync(path, limit, cancellation);

    public async Task WriteAsync(string path, byte[] data, CancellationToken cancellation) =>
        await (await connection(cancellation)).WriteAsync(path, data, cancellation);

    public async Task<(long Size, DateTime ModifiedUtc)?> StatAsync(string path, CancellationToken cancellation) =>
        (await (await connection(cancellation)).StatAsync(path, cancellation)) is { } stamp ? (stamp.Size, stamp.ModifiedUtc) : null;

    // What a command said was wrong. A command often says more than one thing, and the first is not always the one that
    // matters: tar says what it did about a name before it says that it would not unpack it. So up to two lines are shown.
    private static string Complaint(CommandResult result)
    {
        string text = (result.Error.Length > 0 ? result.Error : result.Output).Trim();
        string[] lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string shown = string.Join("; ", lines.Take(2));
        if (shown.Length > 300) shown = shown[..300] + "…";
        return shown.Length > 0 ? shown : L.T("The command failed (exit code {0}).", result.ExitCode?.ToString() ?? "?");
    }

    private async Task<CommandResult> RunAsync(string command, CancellationToken cancellation)
    {
        if (run is null) throw new NotSupportedException(L.T("This server cannot be given commands."));

        CommandResult result = await run(command, cancellation).ConfigureAwait(false);
        if (result.Failure is not null) throw new IOException(result.Failure);
        return result;
    }

    public async Task ExtractAsync(FileRow archive, string targetFolder, CancellationToken cancellation)
    {
        ArchiveKind kind = Archives.Detect(archive.Name);
        if (kind == ArchiveKind.None) throw new NotSupportedException(L.T("This is not an archive that can be unpacked."));

        CommandResult result = await RunAsync(Archives.ExtractCommand(archive.Path, targetFolder, kind), cancellation).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException(Complaint(result));
    }

    public async Task CompressAsync(string folder, IReadOnlyList<string> names, string archiveName, ArchiveKind kind, CancellationToken cancellation)
    {
        CommandResult result = await RunAsync(Archives.CreateCommand(folder, names, RemotePath.Combine(folder, archiveName), kind), cancellation).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException(Complaint(result));
    }

    public async Task<ContentSearchResult> SearchContentAsync(string folder, string query, bool ignoreCase, bool regex, int limit, CancellationToken cancellation)
    {
        CommandResult result = await RunAsync(ContentSearchCommand.Build(folder, query, ignoreCase, regex, limit), cancellation).ConfigureAwait(false);
        IReadOnlyList<ContentMatch> matches = ContentSearchCommand.Parse(result.Output);

        // A search that found nothing and said something is more likely to have failed than to have found nothing.
        string? note = matches.Count == 0 && result.Error.Trim().Length > 0 ? Complaint(result) : null;

        string prefix = folder.TrimEnd('/') + "/";
        var rows = new List<FileRow>(matches.Count);
        foreach (ContentMatch match in matches)
        {
            string name = RemotePath.NameOf(match.Path);
            string parent = RemotePath.Parent(match.Path);
            string? where = parent.StartsWith(prefix, StringComparison.Ordinal) ? parent[prefix.Length..] : null;
            rows.Add(new FileRow(name, match.Path, 0, DateTime.MinValue, false, false, string.Empty, null, name.StartsWith('.'), where, match.Snippet.Length > MaxLineLength ? match.Snippet[..MaxLineLength] + "…" : match.Snippet, match.Line));
        }

        return new ContentSearchResult(rows, note, matches.Count >= limit);
    }
}

/// <summary>Turns numbers into what a person expects to read.</summary>
public static class FileText
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Size(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {Units[unit]}";
    }

    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return string.Empty;
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1 ? L.T("{0} h {1} min", (int)span.TotalHours, span.Minutes) : span.TotalMinutes >= 1 ? L.T("{0} min {1} s", span.Minutes, span.Seconds) : L.T("{0} s", Math.Max(1, span.Seconds));
    }
}

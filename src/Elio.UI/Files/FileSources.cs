using Elio.Connections;
using Elio.Core;

namespace Elio.UI.Files;

/// <summary>One line in a file list.</summary>
/// <param name="IsHidden">A name that starts with a dot, or a file marked hidden on Windows.</param>
/// <param name="Where">Set for a result found in a folder below the one shown: that folder, written from the one shown.</param>
public sealed record FileRow(string Name, string Path, long Size, DateTime ModifiedUtc, bool IsDirectory, bool IsLink, string Permissions, RemoteEntry? Remote, bool IsHidden = false, string? Where = null);

/// <summary>Where a panel's files come from, so both sides of the screen can be the same control.</summary>
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
}

/// <summary>Files on this computer.</summary>
public sealed class LocalFileSource : IFileSource
{
    /// <summary>The list of drives on Windows, which has no single root folder.</summary>
    private const string TopLevel = "";

    public bool IsRemote => false;

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
}

/// <summary>Files on a server.</summary>
public sealed class RemoteFileSource(Func<CancellationToken, Task<SftpConnection>> connection) : IFileSource
{
    public bool IsRemote => true;

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

    public async Task ChangeModeAsync(FileRow row, short mode, CancellationToken cancellation) =>
        await (await connection(cancellation)).ChangeModeAsync(row.Path, mode, cancellation);
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

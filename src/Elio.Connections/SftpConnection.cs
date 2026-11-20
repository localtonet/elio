using Elio.Core;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Elio.Connections;

/// <summary>A file or folder on the server.</summary>
public sealed record RemoteEntry(string Name, string FullPath, long Size, DateTime ModifiedUtc, bool IsDirectory, bool IsSymbolicLink, string Permissions)
{
    // The client resolves links in every path it is given, so deleting or renaming a link by name would act on
    // whatever it points at. The object from the listing still refers to the link itself.
    internal ISftpFile? Source { get; init; }
}

/// <summary>Paths on the server, which always use forward slashes whatever this machine does.</summary>
public static class RemotePath
{
    public static string Combine(string folder, string name) => folder.EndsWith('/') ? folder + name : $"{folder}/{name}";

    public static string Parent(string path)
    {
        string trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        int slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    public static string NameOf(string path) => path.TrimEnd('/')[(path.TrimEnd('/').LastIndexOf('/') + 1)..];
}

/// <summary>
/// A connection used only for files. It is never shared with a shell, so a large transfer cannot
/// make typing lag or take a terminal down with it.
/// </summary>
public sealed class SftpConnection : IDisposable
{
    private const int ParallelLinkLookups = 16;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    private readonly SshChain _chain;
    private readonly CancellationTokenSource _lost = new();
    private int _disposed;

    private SftpConnection(SshChain chain)
    {
        _chain = chain;
        chain.Files.OperationTimeout = OperationTimeout;
        chain.Files.ErrorOccurred += OnError;
    }

    /// <summary>Gives up on a connection that stopped responding, releasing everything waiting on it.</summary>
    internal void Abandon()
    {
        _ = _lost.CancelAsync();
        _ = Task.Run(Dispose);
    }

    // Replies stop arriving when the link dies, so anything waiting for one has to be told. Cancelling
    // asynchronously keeps those continuations off the thread that delivers messages.
    private void OnError(object? sender, ExceptionEventArgs e) => _ = _lost.CancelAsync();

    internal SftpClient Client => _chain.Files;

    /// <summary>Signalled when the connection breaks.</summary>
    public CancellationToken Lost => _lost.Token;

    public bool IsConnected
    {
        get
        {
            try
            {
                return !_lost.IsCancellationRequested && Volatile.Read(ref _disposed) == 0 && Client.IsConnected;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <summary>The folder the server starts a session in.</summary>
    public string HomeDirectory => Client.WorkingDirectory;

    /// <summary>Opens a file connection at the end of a path whose earlier servers are jump hosts.</summary>
    public static async Task<SftpConnection> ConnectAsync(IReadOnlyList<SshEndpoint> path, HostKeyPolicy policy, CancellationToken cancellation) =>
        new(await SshChain.OpenForFilesAsync(path, policy, cancellation).ConfigureAwait(false));

    /// <summary>The contents of a folder, without "." and "..".</summary>
    public async Task<List<RemoteEntry>> ListAsync(string path, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, Lost);
        var entries = new List<ISftpFile>();
        await foreach (ISftpFile file in Client.ListDirectoryAsync(path, linked.Token).ConfigureAwait(false))
            if (file.Name is not ("." or "..")) entries.Add(file);

        // A listing describes a link, not what it points at, and a link to a folder has to behave like a folder.
        var linkTargets = new Dictionary<string, SftpFileAttributes>();
        await Parallel.ForEachAsync(
            entries.Where(e => e.IsSymbolicLink),
            new ParallelOptions { MaxDegreeOfParallelism = ParallelLinkLookups, CancellationToken = linked.Token },
            async (link, token) =>
            {
                try
                {
                    SftpFileAttributes target = await Client.GetAttributesAsync(link.FullName, token).ConfigureAwait(false);
                    lock (linkTargets) linkTargets[link.FullName] = target;
                }
                catch (SshException)
                {
                    // A link to nothing stays listed as a plain entry.
                }
            }).ConfigureAwait(false);

        return entries.Select(e => Describe(e, path, linkTargets.GetValueOrDefault(e.FullName))).ToList();
    }

    private static RemoteEntry Describe(ISftpFile file, string folder, SftpFileAttributes? linkTarget) => new(
        file.Name,
        RemotePath.Combine(folder, file.Name),
        linkTarget?.Size ?? file.Length,
        linkTarget?.LastWriteTimeUtc ?? file.LastWriteTimeUtc,
        file.IsDirectory || linkTarget?.IsDirectory == true,
        file.IsSymbolicLink,
        PermissionText(file))
    {
        Source = file,
    };

    private static string PermissionText(ISftpFile f) => string.Concat(
        f.OwnerCanRead ? 'r' : '-', f.OwnerCanWrite ? 'w' : '-', f.OwnerCanExecute ? 'x' : '-',
        f.GroupCanRead ? 'r' : '-', f.GroupCanWrite ? 'w' : '-', f.GroupCanExecute ? 'x' : '-',
        f.OthersCanRead ? 'r' : '-', f.OthersCanWrite ? 'w' : '-', f.OthersCanExecute ? 'x' : '-');

    /// <summary>Size and time of a path, or null if nothing is there.</summary>
    public async Task<(long Size, DateTime ModifiedUtc, bool IsDirectory)?> StatAsync(string path, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, Lost);
        try
        {
            SftpFileAttributes a = await Client.GetAttributesAsync(path, linked.Token).ConfigureAwait(false);
            return (a.Size, a.LastWriteTimeUtc, a.IsDirectory);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, Lost);
        await Client.CreateDirectoryAsync(path, linked.Token).ConfigureAwait(false);
    }

    /// <summary>Renames or moves, refusing to replace something already there.</summary>
    public Task RenameAsync(RemoteEntry entry, string to, CancellationToken cancellation) =>
        Task.Run(() => entry.Source!.MoveTo(to), cancellation);

    /// <summary>Sets permissions, given as digits such as 644.</summary>
    public Task ChangeModeAsync(string path, short mode, CancellationToken cancellation) =>
        Task.Run(() => Client.ChangePermissions(path, mode), cancellation);

    /// <summary>Deletes a file or link, or a folder with everything inside it.</summary>
    public async Task DeleteAsync(RemoteEntry entry, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, Lost);

        // A link is removed as a link. What it points at is left alone, and a folder's contents are not walked.
        if (entry.IsDirectory && !entry.IsSymbolicLink)
        {
            foreach (RemoteEntry child in await ListAsync(entry.FullPath, linked.Token).ConfigureAwait(false))
                await DeleteAsync(child, linked.Token).ConfigureAwait(false);
        }

        await entry.Source!.DeleteAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>Creates an empty file. Fails if something with that name is already there.</summary>
    public async Task CreateFileAsync(string path, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, Lost);
        await using SftpFileStream created = await Client.OpenAsync(path, FileMode.CreateNew, FileAccess.Write, linked.Token).ConfigureAwait(false);
    }

    /// <summary>Moves something into another folder of the same server, refusing to replace what is there.</summary>
    public Task MoveAsync(RemoteEntry entry, string to, CancellationToken cancellation) => RenameAsync(entry, to, cancellation);

    /// <summary>The first bytes of a file, or all of it if it is shorter.</summary>
    public async Task<byte[]> ReadAsync(string path, int limit, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, Lost);
        return await SftpCopy.ReadBytesAsync(Client, path, limit, linked.Token).ConfigureAwait(false);
    }

    /// <summary>Replaces a file with these bytes, only once all of them have arrived, keeping its permissions.</summary>
    public async Task WriteAsync(string path, byte[] data, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, Lost);
        await SftpCopy.WriteBytesAsync(Client, path, data, linked.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a file, or a folder with everything in it, to another place on this server. A link is left out: one that
    /// points at a folder could lead back into the folder being copied.
    /// </summary>
    /// <returns>How many links were left out.</returns>
    public async Task<int> CopyAsync(RemoteEntry entry, string to, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, Lost);
        if (entry.IsSymbolicLink) return 1;

        if (!entry.IsDirectory)
        {
            await SftpCopy.CopyWithinAsync(Client, entry.FullPath, to, linked.Token).ConfigureAwait(false);
            return 0;
        }

        // Copying a folder into itself would never end: what is made inside it is found by the next listing.
        if (to == entry.FullPath || to.StartsWith(entry.FullPath.TrimEnd('/') + "/", StringComparison.Ordinal))
            throw new IOException(L.T("A folder cannot be copied into itself."));

        await Client.CreateDirectoryAsync(to, linked.Token).ConfigureAwait(false);
        int skipped = 0;
        foreach (RemoteEntry child in await ListAsync(entry.FullPath, linked.Token).ConfigureAwait(false))
            skipped += await CopyAsync(child, RemotePath.Combine(to, child.Name), linked.Token).ConfigureAwait(false);

        return skipped;
    }

    /// <summary>Copies one file here, for short jobs that don't belong in the transfer queue.</summary>
    public Task DownloadFileAsync(string remotePath, string localPath, DateTime modifiedUtc, CancellationToken cancellation) =>
        SftpCopy.DownloadAsync(Client, remotePath, localPath, modifiedUtc, static _ => { }, static _ => { }, cancellation);

    /// <summary>Copies one file to the server, replacing what is there only once the whole file has arrived.</summary>
    public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellation) =>
        SftpCopy.UploadAsync(Client, localPath, remotePath, static _ => { }, static _ => { }, cancellation);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _chain.Dispose();
    }
}

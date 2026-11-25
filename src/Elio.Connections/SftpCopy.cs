using System.Buffers;
using Elio.Core;
using Microsoft.Win32.SafeHandles;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Elio.Connections;

/// <summary>Copies one file, picking up from what an earlier attempt left behind.</summary>
internal static class SftpCopy
{
    internal const string PartSuffix = ".eliopart";

    // The most file data one SFTP write may carry: a 32 KB packet less its header and a short handle.
    private const int MaxPiece = (32 * 1024) - 29;

    // How many writes may be on their way before the oldest has to be confirmed. The server answers them in order,
    // so the link is kept full without the file ever having a hole further back than this.
    private const int MaxPendingWrites = 128;

    // How much of the end of an unfinished upload is sent again: everything that might not have been confirmed.
    private const long MaxGap = (long)MaxPendingWrites * MaxPiece;
    private const int ReadBufferSize = 1024 * 1024;
    private const int CompareBytes = 64 * 1024;

    // A write that gets no answer for this long means the connection is gone without having said so.
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Writes to a temporary name and swaps it in at the end, so a failed upload never damages what was there.</summary>
    /// <param name="limits">The speed limits the upload goes by, or null for none.</param>
    public static async Task UploadAsync(SftpClient client, string localPath, string remotePath, Action<long> setPosition, Action<long> add, CancellationToken cancellation, IReadOnlyList<TransferThrottle>? limits = null)
    {
        string part = remotePath + PartSuffix;
        using SafeFileHandle source = File.OpenHandle(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
        long size = RandomAccess.GetLength(source);

        ReadAt read = (buffer, offset, token) => ReadExactlyAsync(source, buffer, offset, token);

        long start = await FindUploadStartAsync(client, read, size, part, cancellation).ConfigureAwait(false);
        setPosition(start);
        await WritePartAsync(client, read, part, start, size, add, limits, cancellation).ConfigureAwait(false);

        // A new file takes its permissions from the local one where that means anything.
        await Task.Run(() => Publish(client, part, remotePath, File.GetLastWriteTimeUtc(localPath), OperatingSystem.IsWindows() ? null : DigitsOf(File.GetUnixFileMode(localPath))), cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a file from one server to another as it arrives, without keeping any of it on this computer. It uses the
    /// same temporary name, resuming and final swap as an upload.
    /// </summary>
    /// <param name="limits">The speed limits the copy goes by, or null for none.</param>
    public static async Task CopyAsync(SftpClient from, SftpClient to, string sourcePath, string targetPath, Action<long> setPosition, Action<long> add, CancellationToken cancellation, IReadOnlyList<TransferThrottle>? limits = null)
    {
        string part = targetPath + PartSuffix;
        SftpFileAttributes original = await from.GetAttributesAsync(sourcePath, cancellation).ConfigureAwait(false);
        long size = original.Size;

        // The stream asks for the blocks ahead of the one being read, which keeps the source as busy as the target.
        await using SftpFileStream source = await from.OpenAsync(sourcePath, FileMode.Open, FileAccess.Read, cancellation).ConfigureAwait(false);
        ReadAt read = (buffer, offset, token) => ReadExactlyAsync(source, buffer, offset, token);

        long start = await FindUploadStartAsync(to, read, size, part, cancellation).ConfigureAwait(false);
        setPosition(start);
        await WritePartAsync(to, read, part, start, size, add, limits, cancellation).ConfigureAwait(false);

        await Task.Run(() => Publish(to, part, targetPath, original.LastWriteTimeUtc, ModeOf(original)), cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces a file with these bytes the way an upload does: written beside it first and swapped in at the end, so a
    /// failure leaves what was there alone, and the file keeps the permissions it had.
    /// </summary>
    public static async Task WriteBytesAsync(SftpClient client, string remotePath, byte[] data, CancellationToken cancellation)
    {
        string part = remotePath + PartSuffix;
        long start = 0;

        await using (SftpFileStream target = await client.OpenAsync(part, FileMode.Create, FileAccess.Write, cancellation).ConfigureAwait(false))
        {
            ReadAt read = (buffer, offset, _) =>
            {
                data.AsMemory((int)offset, buffer.Length).CopyTo(buffer);
                return ValueTask.CompletedTask;
            };

            await Task.Run(() => SendAsync(read, target, start, data.Length, static _ => { }, null, cancellation), cancellation).ConfigureAwait(false);
        }

        SftpFileAttributes written = await client.GetAttributesAsync(part, cancellation).ConfigureAwait(false);
        if (written.Size != data.Length) throw new IOException(L.T("The file on the server is {0} bytes, it should be {1}.", written.Size, data.Length));

        await Task.Run(() => Publish(client, part, remotePath, DateTime.UtcNow, null), cancellation).ConfigureAwait(false);
    }

    /// <summary>The first bytes of a file, or all of it if it is shorter.</summary>
    public static async Task<byte[]> ReadBytesAsync(SftpClient client, string remotePath, int limit, CancellationToken cancellation)
    {
        await using SftpFileStream source = await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellation).ConfigureAwait(false);

        var result = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (result.Length < limit)
            {
                int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - result.Length)), cancellation).ConfigureAwait(false);
                if (read == 0) break;
                result.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return result.ToArray();
    }

    /// <summary>Copies a file to another place on the same server, through this connection.</summary>
    public static Task CopyWithinAsync(SftpClient client, string sourcePath, string targetPath, CancellationToken cancellation) =>
        CopyAsync(client, client, sourcePath, targetPath, static _ => { }, static _ => { }, cancellation);

    /// <summary>Fills a buffer with the source's bytes from a place in it, wherever the source is.</summary>
    private delegate ValueTask ReadAt(Memory<byte> buffer, long offset, CancellationToken cancellation);

    /// <summary>Writes the rest of the source to the temporary file and checks that all of it is there.</summary>
    private static async Task WritePartAsync(SftpClient client, ReadAt read, string part, long start, long size, Action<long> add, IReadOnlyList<TransferThrottle>? limits, CancellationToken cancellation)
    {
        // Creating empties any leftover, and makes sure even a zero-length file exists.
        await using (SftpFileStream target = await client.OpenAsync(part, start == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write, cancellation).ConfigureAwait(false))
        {
            // Sending waits for room in the connection's window without giving the thread back, so it gets its own.
            await Task.Run(() => SendAsync(read, target, start, size, add, limits, cancellation), cancellation).ConfigureAwait(false);
        }

        SftpFileAttributes written = await client.GetAttributesAsync(part, cancellation).ConfigureAwait(false);
        if (written.Size != size) throw new IOException(L.T("The file on the server is {0} bytes, it should be {1}.", written.Size, size));
    }

    /// <summary>Sends the file as a stream of writes on one handle, without waiting for each to be confirmed before the next.</summary>
    /// <param name="limits">Speed limits to keep to, or null for none. Each write waits for its turn under them before it goes.</param>
    private static async Task SendAsync(ReadAt read, SftpFileStream target, long start, long size, Action<long> add, IReadOnlyList<TransferThrottle>? limits, CancellationToken cancellation)
    {
        int largest = Math.Min(target.MaxWriteLength, MaxPiece);
        var pending = new Queue<(Task Write, int Length)>(MaxPendingWrites);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);

        async ValueTask ConfirmOldestAsync()
        {
            (Task write, int length) = pending.Dequeue();
            if (!write.IsCompleted) await write.WaitAsync(WriteTimeout, cancellation).ConfigureAwait(false);
            else await write.ConfigureAwait(false);
            add(length);
        }

        try
        {
            long offset = start;
            while (offset < size)
            {
                int filled = (int)Math.Min(buffer.Length, size - offset);
                await read(buffer.AsMemory(0, filled), offset, cancellation).ConfigureAwait(false);

                for (int at = 0; at < filled;)
                {
                    while (pending.Count >= MaxPendingWrites || (pending.Count > 0 && pending.Peek().Write.IsCompleted)) await ConfirmOldestAsync().ConfigureAwait(false);

                    // Asked again for every piece, so that a limit changed during the transfer takes hold at once. Smaller
                    // pieces only make less unconfirmed when a connection breaks, so resuming stays as safe as before.
                    int length = Math.Min(TransferThrottle.PieceFor(limits, largest), filled - at);
                    await TransferThrottle.WaitAsync(limits, length, cancellation).ConfigureAwait(false);
                    pending.Enqueue((target.WriteAtAsync(offset + at, buffer, at, length, cancellation), length));
                    at += length;
                }

                offset += filled;
            }

            while (pending.Count > 0) await ConfirmOldestAsync().ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new IOException(L.T("The server stopped answering."));
        }
        finally
        {
            // Whatever is still on its way after a failure has nobody left to hear how it ended.
            foreach ((Task write, _) in pending) _ = write.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async ValueTask ReadExactlyAsync(SafeFileHandle file, Memory<byte> buffer, long offset, CancellationToken cancellation)
    {
        while (buffer.Length > 0)
        {
            int read = await RandomAccess.ReadAsync(file, buffer, offset, cancellation).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException(L.T("The file got shorter during the transfer."));
            buffer = buffer[read..];
            offset += read;
        }
    }

    private static async ValueTask ReadExactlyAsync(SftpFileStream file, Memory<byte> buffer, long offset, CancellationToken cancellation)
    {
        // Reading on uses the blocks already asked for. Only a jump throws them away.
        if (file.Position != offset) file.Seek(offset, SeekOrigin.Begin);

        while (buffer.Length > 0)
        {
            int read = await file.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException(L.T("The file got shorter during the transfer."));
            buffer = buffer[read..];
        }
    }

    private static async Task<long> FindUploadStartAsync(SftpClient client, ReadAt read, long size, string part, CancellationToken cancellation)
    {
        long existing;
        try
        {
            existing = (await client.GetAttributesAsync(part, cancellation).ConfigureAwait(false)).Size;
        }
        catch (SftpPathNotFoundException)
        {
            return 0;
        }

        if (existing > size) return 0;

        // The last writes of a broken upload may not all have landed. Sending that stretch again is cheap.
        long start = existing - MaxGap;
        if (start <= 0) return 0;

        byte[] local = new byte[Math.Min(CompareBytes, start)];
        await read(local, start - local.Length, cancellation).ConfigureAwait(false);
        return await RemoteBytesMatchAsync(client, part, start - local.Length, local, cancellation).ConfigureAwait(false) ? start : 0;
    }

    private static async Task<bool> RemoteBytesMatchAsync(SftpClient client, string path, long offset, byte[] expected, CancellationToken cancellation)
    {
        await using SftpFileStream remote = await client.OpenAsync(path, FileMode.Open, FileAccess.Read, cancellation).ConfigureAwait(false);
        remote.Seek(offset, SeekOrigin.Begin);

        byte[] actual = new byte[expected.Length];
        int filled = 0;
        while (filled < actual.Length)
        {
            int read = await remote.ReadAsync(actual.AsMemory(filled), cancellation).ConfigureAwait(false);
            if (read == 0) return false;
            filled += read;
        }

        return actual.AsSpan().SequenceEqual(expected);
    }

    private static void Publish(SftpClient client, string part, string remotePath, DateTime modifiedUtc, short? modeWhenNew)
    {
        SftpFileAttributes attributes = client.GetAttributes(part);
        attributes.LastWriteTimeUtc = modifiedUtc;

        // A replaced file keeps its permissions. A new one takes them from the file it is a copy of.
        if ((ModeOf(client, remotePath) ?? modeWhenNew) is short mode) attributes.SetPermissions(mode);
        client.SetAttributes(part, attributes);

        try
        {
            client.RenameFile(part, remotePath, isPosix: true);
        }
        catch (NotSupportedException)
        {
            // Older servers can't replace in one step.
            if (client.Exists(remotePath)) client.DeleteFile(remotePath);
            client.RenameFile(part, remotePath);
        }
    }

    private static short? ModeOf(SftpClient client, string path)
    {
        try
        {
            return ModeOf(client.GetAttributes(path));
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    private static short ModeOf(SftpFileAttributes a)
    {
        int Digit(bool read, bool write, bool execute) => (read ? 4 : 0) + (write ? 2 : 0) + (execute ? 1 : 0);
        return (short)((Digit(a.OwnerCanRead, a.OwnerCanWrite, a.OwnerCanExecute) * 100) + (Digit(a.GroupCanRead, a.GroupCanWrite, a.GroupCanExecute) * 10) + Digit(a.OthersCanRead, a.OthersCanWrite, a.OthersCanExecute));
    }

    private static short DigitsOf(UnixFileMode mode)
    {
        int bits = (int)mode;
        return (short)((((bits >> 6) & 7) * 100) + (((bits >> 3) & 7) * 10) + (bits & 7));
    }

    /// <summary>Saves to a temporary name and moves it into place at the end.</summary>
    /// <param name="limits">
    /// The speed limits the download goes by, or null for none. The data is paid for as it is taken from the stream, which
    /// asks the server for some of what follows before it is wanted: so a file can start out above the limit, and over the
    /// whole of it keeps to it.
    /// </param>
    public static async Task DownloadAsync(SftpClient client, string remotePath, string localPath, DateTime modifiedUtc, Action<long> setPosition, Action<long> add, CancellationToken cancellation, IReadOnlyList<TransferThrottle>? limits = null)
    {
        string part = localPath + PartSuffix;
        long start = await FindDownloadStartAsync(client, remotePath, part, cancellation).ConfigureAwait(false);
        setPosition(start);

        await using (var target = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, ReadBufferSize, FileOptions.Asynchronous))
        {
            target.SetLength(start);
            target.Position = start;

            await using SftpFileStream source = await client.OpenAsync(remotePath, FileMode.Open, FileAccess.Read, cancellation).ConfigureAwait(false);
            source.Seek(start, SeekOrigin.Begin);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
            try
            {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, TransferThrottle.PieceFor(limits, buffer.Length)), cancellation).ConfigureAwait(false)) > 0)
                {
                    await TransferThrottle.WaitAsync(limits, read, cancellation).ConfigureAwait(false);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                    add(read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        File.Move(part, localPath, overwrite: true);
        File.SetLastWriteTimeUtc(localPath, modifiedUtc);
    }

    private static async Task<long> FindDownloadStartAsync(SftpClient client, string remotePath, string part, CancellationToken cancellation)
    {
        if (!File.Exists(part)) return 0;

        long existing = new FileInfo(part).Length;
        long remoteSize = (await client.GetAttributesAsync(remotePath, cancellation).ConfigureAwait(false)).Size;
        if (existing == 0 || existing > remoteSize) return 0;

        byte[] local = new byte[Math.Min(CompareBytes, existing)];
        await using (var file = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            file.Position = existing - local.Length;
            await file.ReadExactlyAsync(local, cancellation).ConfigureAwait(false);
        }

        return await RemoteBytesMatchAsync(client, remotePath, existing - local.Length, local, cancellation).ConfigureAwait(false) ? existing : 0;
    }

    /// <summary>Removes the temporary file of a transfer the user cancelled.</summary>
    public static void DeleteLocalPart(string localPath)
    {
        try
        {
            File.Delete(localPath + PartSuffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover is harmless, the next attempt checks it before using it.
        }
    }

    /// <summary>Removes the temporary file of a transfer the user cancelled.</summary>
    public static async Task DeleteRemotePartAsync(SftpClient client, string remotePath, CancellationToken cancellation)
    {
        try
        {
            await client.DeleteFileAsync(remotePath + PartSuffix, cancellation).ConfigureAwait(false);
        }
        catch (SshException)
        {
            // Same as above.
        }
    }
}

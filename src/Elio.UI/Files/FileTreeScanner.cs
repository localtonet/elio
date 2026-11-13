namespace Elio.UI.Files;

/// <summary>What a walk through the folders below one came to.</summary>
/// <param name="Folders">How many folders were listed.</param>
/// <param name="Items">How many files and folders were found.</param>
/// <param name="Unreadable">How many folders could not be listed (no permission, gone meanwhile) and were left out.</param>
/// <param name="Truncated">The walk stopped at the limit, with more still to be found.</param>
public readonly record struct FileScanSummary(int Folders, int Items, int Unreadable, bool Truncated);

/// <summary>
/// Lists the folders below a folder, a level at a time, so that a name can be looked for in all of them. A server has to
/// be asked once for each folder, and that is what makes this slow there: it can be stopped at any time, never follows a
/// link (which could lead back to where it started) and stops after a number of items.
/// </summary>
public static class FileTreeScanner
{
    /// <summary>Items found before the walk gives up. More than any list is useful for, and little enough to hold in memory.</summary>
    public const int DefaultLimit = 200_000;

    /// <summary>A path this deep is a loop that links did not give away, such as a folder mounted inside itself.</summary>
    public const int MaxDepth = 40;

    /// <param name="source">Where the folders are.</param>
    /// <param name="folders">The folders to start in. Their own contents are found; they are not themselves part of the result.</param>
    /// <param name="separator">What to put between the folders of a result's <see cref="FileRow.Where"/>.</param>
    /// <param name="found">Called with what each folder held, from any thread, as soon as it was listed.</param>
    /// <param name="parallelism">How many folders to ask for at once. A server answers fast to a few and slowly to many.</param>
    /// <exception cref="OperationCanceledException">The walk was cancelled. What was found before was already passed on.</exception>
    public static async Task<FileScanSummary> ScanAsync(
        IFileSource source,
        IEnumerable<FileRow> folders,
        char separator,
        Action<IReadOnlyList<FileRow>> found,
        int parallelism,
        int limit,
        CancellationToken cancellation)
    {
        int listed = 0, items = 0, unreadable = 0;
        bool truncated = false;

        // (the folder, written from where the walk began)
        List<(FileRow Folder, string Relative)> level = folders.Where(Descends).Select(f => (f, f.Name)).ToList();

        for (int depth = 0; level.Count > 0 && depth < MaxDepth && !truncated; depth++)
        {
            var next = new System.Collections.Concurrent.ConcurrentBag<(FileRow Folder, string Relative)>();

            await Parallel.ForEachAsync(
                level,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellation },
                async (entry, token) =>
                {
                    if (Volatile.Read(ref truncated)) return;

                    List<FileRow> children;
                    try
                    {
                        children = await source.ListAsync(entry.Folder.Path, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // One folder that cannot be read is not the end of the search.
                        Interlocked.Increment(ref unreadable);
                        return;
                    }

                    Interlocked.Increment(ref listed);
                    var rows = new List<FileRow>(children.Count);
                    foreach (FileRow child in children)
                    {
                        // The limit is shared by all the folders being listed at once, so it is claimed one item at a time.
                        if (Interlocked.Increment(ref items) > limit)
                        {
                            Volatile.Write(ref truncated, true);
                            break;
                        }

                        rows.Add(child with { Where = entry.Relative });
                        if (Descends(child)) next.Add((child, entry.Relative + separator + child.Name));
                    }

                    if (rows.Count > 0) found(rows);
                }).ConfigureAwait(false);

            level = [.. next];
        }

        // A walk that was cut short by depth, and not by the limit, still had things below.
        if (level.Count > 0 && !truncated) truncated = true;

        return new FileScanSummary(listed, Math.Min(items, limit), unreadable, truncated);
    }

    private static bool Descends(FileRow row) => row.IsDirectory && !row.IsLink;
}

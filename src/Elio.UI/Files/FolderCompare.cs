using System.Collections.Concurrent;
using System.Diagnostics;

namespace Elio.UI.Files;

/// <summary>What one side's folder holds, and what the folders below it hold when they were gone through too.</summary>
public sealed class FolderSnapshot
{
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);

    /// <summary>Each folder's contents under its path from the top folder, with / between the names. The top folder is "".</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<FileRow>> Folders { get; init; }

    /// <summary>Whether the folders below the top one were gone through.</summary>
    public bool Subfolders { get; init; }

    /// <summary>
    /// Whether "a.txt" and "A.txt" are two different files here. They are on a server and on Linux. Windows and macOS
    /// usually take them for one.
    /// </summary>
    public bool CaseSensitive { get; init; } = true;

    /// <summary>The folders below that could not be listed, by their path from the top.</summary>
    public IReadOnlySet<string> Unreadable { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The walk stopped at its limit, or too deep, with more still below.</summary>
    public bool Truncated { get; init; }

    /// <summary>
    /// Lists a folder, and the folders below it if asked, a level at a time and a few folders at once, the way the search of
    /// the folders below does. No link is followed: one could lead back to where the walk began.
    /// </summary>
    /// <param name="progress">Told now and then how many items have been found so far.</param>
    /// <exception cref="OperationCanceledException">Stopped.</exception>
    /// <exception cref="Exception">The top folder could not be listed: there is nothing to compare.</exception>
    public static async Task<FolderSnapshot> TakeAsync(IFileSource source, string path, bool subfolders, bool caseSensitive, IProgress<int>? progress, CancellationToken cancellation, int limit = FileTreeScanner.DefaultLimit)
    {
        List<FileRow> top = await source.ListAsync(path, cancellation).ConfigureAwait(false);
        var folders = new ConcurrentDictionary<string, IReadOnlyList<FileRow>>(StringComparer.Ordinal) { [string.Empty] = top };
        var unreadable = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        int items = top.Count;
        bool truncated = items > limit;
        long lastReport = Stopwatch.GetTimestamp();

        List<(FileRow Folder, string Relative)> level = subfolders ? top.Where(Descends).Select(f => (f, f.Name)).ToList() : [];
        for (int depth = 0; level.Count > 0 && !truncated; depth++)
        {
            // A loop that links did not give away, such as a folder mounted inside itself.
            if (depth >= FileTreeScanner.MaxDepth)
            {
                truncated = true;
                break;
            }

            var next = new ConcurrentBag<(FileRow Folder, string Relative)>();
            await Parallel.ForEachAsync(
                level,
                new ParallelOptions { MaxDegreeOfParallelism = source.IsRemote ? 4 : 2, CancellationToken = cancellation },
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
                        // Not the end of the comparison, but nothing in it can be said to be missing from either side.
                        unreadable[entry.Relative] = true;
                        return;
                    }

                    folders[entry.Relative] = children;
                    if (Interlocked.Add(ref items, children.Count) > limit) Volatile.Write(ref truncated, true);
                    foreach (FileRow child in children.Where(Descends)) next.Add((child, entry.Relative + "/" + child.Name));

                    long last = Interlocked.Read(ref lastReport);
                    if (progress is not null && Stopwatch.GetElapsedTime(last) >= ReportEvery && Interlocked.CompareExchange(ref lastReport, Stopwatch.GetTimestamp(), last) == last)
                        progress.Report(Volatile.Read(ref items));
                }).ConfigureAwait(false);

            level = [.. next];
        }

        return new FolderSnapshot
        {
            Folders = folders,
            Subfolders = subfolders,
            CaseSensitive = caseSensitive,
            Unreadable = unreadable.Keys.ToHashSet(StringComparer.Ordinal),
            Truncated = truncated,
        };
    }

    private static bool Descends(FileRow row) => row.IsDirectory && !row.IsLink;
}

/// <summary>How a name on both sides, or on one, differs from the other side.</summary>
public enum CompareState
{
    /// <summary>Only the left side has it.</summary>
    LeftOnly,

    /// <summary>Only the right side has it.</summary>
    RightOnly,

    /// <summary>Both have the file, and the left one was changed later.</summary>
    LeftNewer,

    /// <summary>Both have the file, and the right one was changed later.</summary>
    RightNewer,

    /// <summary>Both have the file, changed at the same time give or take the tolerance, but not the same size. Which is newer cannot be told.</summary>
    SizeDiffers,

    /// <summary>Each side has the name in another case. The side that does not tell case apart takes the two for one, the other for two.</summary>
    CaseDiffers,

    /// <summary>One of several names that differ only in case, on a side that tells them apart, which the other side could hold only one of.</summary>
    CaseClash,

    /// <summary>A file on one side and a folder on the other.</summary>
    KindDiffers,

    /// <summary>A folder on both sides that one of them could not read, so what is in it was not compared.</summary>
    Unreadable,
}

/// <summary>One difference between the two sides.</summary>
/// <param name="Folder">The folder it is in, from the top: "" for the top folder, "a/b" further down.</param>
/// <param name="Name">Its name, as the left side has it if both do.</param>
/// <param name="Size">For a folder only one side has, the bytes of all the files in it. Otherwise 0.</param>
/// <param name="Files">For a folder only one side has, how many files are in it. Otherwise 0.</param>
public sealed record CompareItem(string Folder, string Name, CompareState State, FileRow? Left, FileRow? Right, long Size = 0, int Files = 0)
{
    public bool IsDirectory => (Left ?? Right)?.IsDirectory == true;

    /// <summary>Where it is from the top folder, such as "a/b/c.txt".</summary>
    public string RelativePath => Folder.Length == 0 ? Name : Folder + "/" + Name;
}

/// <summary>How a name in a list is marked after a comparison, from the point of view of the side the list is on.</summary>
public enum FileMark
{
    OnlyHere,
    Newer,
    Older,
    SizeDiffers,

    /// <summary>The name clashes with the other side's, or with another name here, by upper and lower case.</summary>
    CaseConflict,
    KindDiffers,
    Unreadable,

    /// <summary>A folder both sides have, with a difference somewhere inside it.</summary>
    DiffersInside,
}

/// <summary>How many differences a side has, as that side sees them.</summary>
public readonly record struct CompareTally(int OnlyHere, int Newer, int Older, int Other)
{
    public int Total => OnlyHere + Newer + Older + Other;
}

/// <summary>
/// The differences between two folders: what only one side has, and the files both have that are not the same, by size or
/// by a time of change further apart than <see cref="Tolerance"/>. Links on either side are left out, and the same name on
/// the other side with them, since a copy over a link would land wherever it points.
/// </summary>
public sealed class FolderComparison
{
    /// <summary>
    /// Times closer than this are the same time. FAT keeps times to two seconds, SFTP sends whole seconds, and a copy
    /// that keeps its time can come out a second off.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    private FolderComparison(IReadOnlyList<CompareItem> items, int same, int linksLeftOut, bool subfolders, bool incomplete)
    {
        Items = items;
        Same = same;
        LinksLeftOut = linksLeftOut;
        Subfolders = subfolders;
        Incomplete = incomplete;
    }

    /// <summary>Every difference, folder by folder. What is the same is only counted.</summary>
    public IReadOnlyList<CompareItem> Items { get; }

    /// <summary>How many files both sides have alike.</summary>
    public int Same { get; }

    /// <summary>How many links were left out, with whatever had the same name on the other side.</summary>
    public int LinksLeftOut { get; }

    /// <summary>Whether the folders below were compared too.</summary>
    public bool Subfolders { get; }

    /// <summary>A side was not gone through to the end, so what looks missing may only not have been seen.</summary>
    public bool Incomplete { get; }

    public static FolderComparison Compare(FolderSnapshot left, FolderSnapshot right)
    {
        // When either side takes "A" and "a" for one name, the names are matched the way that side sees them.
        StringComparer names = left.CaseSensitive && right.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        bool subfolders = left.Subfolders && right.Subfolders;
        Dictionary<string, (long Bytes, int Files)> leftTotals = TotalsOf(left);
        Dictionary<string, (long Bytes, int Files)> rightTotals = TotalsOf(right);
        var items = new List<CompareItem>();
        int same = 0;
        int links = 0;

        CompareItem OneSided(string folder, FileRow row, bool onLeft)
        {
            (long bytes, int files) = row.IsDirectory
                ? (onLeft ? leftTotals : rightTotals).GetValueOrDefault(Join(folder, row.Name))
                : (row.Size, 1);
            return new CompareItem(folder, row.Name, onLeft ? CompareState.LeftOnly : CompareState.RightOnly, onLeft ? row : null, onLeft ? null : row, row.IsDirectory ? bytes : 0, row.IsDirectory ? files : 0);
        }

        void CompareFolder(string folder)
        {
            var groups = new Dictionary<string, (List<FileRow> Left, List<FileRow> Right)>(names);
            (List<FileRow> Left, List<FileRow> Right) GroupOf(string name)
            {
                if (!groups.TryGetValue(name, out var group)) groups[name] = group = ([], []);
                return group;
            }

            foreach (FileRow row in left.Folders.GetValueOrDefault(folder) ?? []) GroupOf(row.Name).Left.Add(row);
            foreach (FileRow row in right.Folders.GetValueOrDefault(folder) ?? []) GroupOf(row.Name).Right.Add(row);

            foreach ((List<FileRow> ls, List<FileRow> rs) in groups.Values)
            {
                int linked = ls.Count(r => r.IsLink) + rs.Count(r => r.IsLink);
                if (linked > 0)
                {
                    links += linked;
                    continue;
                }

                if (ls.Count > 1 || rs.Count > 1)
                {
                    foreach (FileRow row in ls) items.Add(new CompareItem(folder, row.Name, CompareState.CaseClash, row, null));
                    foreach (FileRow row in rs) items.Add(new CompareItem(folder, row.Name, CompareState.CaseClash, null, row));
                    continue;
                }

                FileRow? a = ls.FirstOrDefault();
                FileRow? b = rs.FirstOrDefault();
                if (b is null)
                {
                    items.Add(OneSided(folder, a!, onLeft: true));
                }
                else if (a is null)
                {
                    items.Add(OneSided(folder, b, onLeft: false));
                }
                else if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal))
                {
                    items.Add(new CompareItem(folder, a.Name, CompareState.CaseDiffers, a, b));
                }
                else if (a.IsDirectory != b.IsDirectory)
                {
                    items.Add(new CompareItem(folder, a.Name, CompareState.KindDiffers, a, b));
                }
                else if (a.IsDirectory)
                {
                    // A folder both sides have is the same as far as it goes. Only going in says more.
                    if (!subfolders) continue;

                    string inner = Join(folder, a.Name);
                    if (left.Unreadable.Contains(inner) || right.Unreadable.Contains(inner)) items.Add(new CompareItem(folder, a.Name, CompareState.Unreadable, a, b));
                    else CompareFolder(inner);
                }
                else if (CompareFiles(a, b) is CompareState state)
                {
                    items.Add(new CompareItem(folder, a.Name, state, a, b));
                }
                else
                {
                    same++;
                }
            }
        }

        CompareFolder(string.Empty);
        items.Sort((x, y) =>
        {
            int byFolder = StringComparer.OrdinalIgnoreCase.Compare(x.Folder, y.Folder);
            return byFolder != 0 ? byFolder : StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);
        });

        return new FolderComparison(items, same, links, subfolders, left.Truncated || right.Truncated);
    }

    /// <summary>How two files that both sides have differ, or null if they do not.</summary>
    public static CompareState? CompareFiles(FileRow left, FileRow right)
    {
        // A time that is not known, such as on a listing that left it out, cannot say which is newer.
        bool known = left.ModifiedUtc != DateTime.MinValue && right.ModifiedUtc != DateTime.MinValue;
        TimeSpan apart = known ? left.ModifiedUtc - right.ModifiedUtc : TimeSpan.Zero;
        if (apart.Duration() <= Tolerance) return left.Size == right.Size ? null : CompareState.SizeDiffers;
        return apart > TimeSpan.Zero ? CompareState.LeftNewer : CompareState.RightNewer;
    }

    /// <summary>
    /// The marks for the names a side's list shows, by name as that side has it. A difference further down marks the folder
    /// at the top that holds it, which both sides have.
    /// </summary>
    public IReadOnlyDictionary<string, FileMark> MarksFor(bool left)
    {
        var marks = new Dictionary<string, FileMark>(StringComparer.Ordinal);
        foreach (CompareItem item in Items)
        {
            if (item.Folder.Length > 0)
            {
                marks.TryAdd(item.Folder.Split('/')[0], FileMark.DiffersInside);
                continue;
            }

            if ((left ? item.Left : item.Right) is not FileRow row) continue;
            marks[row.Name] = item.State switch
            {
                CompareState.LeftOnly or CompareState.RightOnly => FileMark.OnlyHere,
                CompareState.LeftNewer => left ? FileMark.Newer : FileMark.Older,
                CompareState.RightNewer => left ? FileMark.Older : FileMark.Newer,
                CompareState.SizeDiffers => FileMark.SizeDiffers,
                CompareState.CaseDiffers or CompareState.CaseClash => FileMark.CaseConflict,
                CompareState.KindDiffers => FileMark.KindDiffers,
                _ => FileMark.Unreadable,
            };
        }

        return marks;
    }

    /// <summary>The differences counted as a side sees them, all the way down.</summary>
    public CompareTally Tally(bool left)
    {
        int onlyHere = 0, newer = 0, older = 0, other = 0;
        foreach (CompareItem item in Items)
        {
            switch (item.State)
            {
                case CompareState.LeftOnly or CompareState.RightOnly:
                    if ((left ? item.Left : item.Right) is not null) onlyHere++;
                    break;
                case CompareState.LeftNewer:
                    if (left) newer++;
                    else older++;
                    break;
                case CompareState.RightNewer:
                    if (left) older++;
                    else newer++;
                    break;
                case CompareState.CaseClash:
                    if ((left ? item.Left : item.Right) is not null) other++;
                    break;
                default:
                    other++;
                    break;
            }
        }

        return new CompareTally(onlyHere, newer, older, other);
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    // The bytes and files in each folder, the folders below it included, leaving links out as a copy does.
    private static Dictionary<string, (long Bytes, int Files)> TotalsOf(FolderSnapshot snapshot)
    {
        var totals = new Dictionary<string, (long Bytes, int Files)>(StringComparer.Ordinal);
        foreach ((string folder, IReadOnlyList<FileRow> rows) in snapshot.Folders)
        {
            long bytes = 0;
            int files = 0;
            foreach (FileRow row in rows.Where(r => !r.IsDirectory && !r.IsLink))
            {
                bytes += row.Size;
                files++;
            }

            if (files == 0) continue;
            for (string at = folder; ; at = at.Contains('/') ? at[..at.LastIndexOf('/')] : string.Empty)
            {
                (long b, int f) = totals.GetValueOrDefault(at);
                totals[at] = (b + bytes, f + files);
                if (at.Length == 0) break;
            }
        }

        return totals;
    }
}

/// <summary>Which way a synchronisation copies.</summary>
public enum SyncDirection
{
    LeftToRight,
    RightToLeft,

    /// <summary>Each way, whichever side has the newer file.</summary>
    BothWays,
}

/// <summary>Why something that differs is not copied.</summary>
public enum SkipReason
{
    /// <summary>The side being copied to has the newer file, and replacing newer files was not asked for.</summary>
    NewerOnTheOtherSide,

    /// <summary>Both ways were asked for, and the two files have the same time but not the same size.</summary>
    CannotTellWhichIsNewer,
    NamesDifferInCase,
    NamesClashByCase,
    FileAndFolder,
    Unreadable,

    /// <summary>A folder only one side has, from a comparison that did not go into folders: what is in it was never looked at.</summary>
    FolderNeedsSubfolders,
}

/// <summary>Something to copy to the other side, into the folder it is in on this one.</summary>
/// <param name="Bytes">What copying it moves: a file's size, or all the files in a folder.</param>
/// <param name="Files">How many files copying it moves.</param>
public sealed record SyncCopy(CompareItem Item, bool ToRight, long Bytes, int Files)
{
    /// <summary>What is copied: the left side's file or folder when it goes to the right.</summary>
    public FileRow Source => (ToRight ? Item.Left : Item.Right)!;
}

/// <summary>A difference that is left as it is, and why.</summary>
public sealed record SyncSkip(CompareItem Item, SkipReason Reason);

/// <summary>
/// What a synchronisation in one direction would copy, worked out from a comparison before anything is done. Nothing in it
/// deletes: what only the side being copied to has stays there.
/// </summary>
public sealed class SyncPlan
{
    private SyncPlan(IReadOnlyList<SyncCopy> copies, IReadOnlyList<SyncSkip> skipped, int untouched)
    {
        Copies = copies;
        Skipped = skipped;
        Untouched = untouched;
        foreach (SyncCopy copy in copies)
        {
            if (copy.ToRight)
            {
                FilesToRight += copy.Files;
                BytesToRight += copy.Bytes;
            }
            else
            {
                FilesToLeft += copy.Files;
                BytesToLeft += copy.Bytes;
            }
        }
    }

    public IReadOnlyList<SyncCopy> Copies { get; }

    public IReadOnlyList<SyncSkip> Skipped { get; }

    /// <summary>What only the side being copied to has, which stays as it is.</summary>
    public int Untouched { get; }

    public int FilesToRight { get; }

    public long BytesToRight { get; }

    public int FilesToLeft { get; }

    public long BytesToLeft { get; }

    /// <param name="replaceNewer">For one way: also copy over a file that is newer on the side being copied to.</param>
    /// <exception cref="InvalidOperationException">The comparison is incomplete, and what looks missing may only not have been seen.</exception>
    public static SyncPlan Build(FolderComparison comparison, SyncDirection direction, bool replaceNewer)
    {
        if (comparison.Incomplete) throw new InvalidOperationException("An incomplete comparison cannot be synchronised.");

        bool toRight = direction is SyncDirection.LeftToRight or SyncDirection.BothWays;
        bool toLeft = direction is SyncDirection.RightToLeft or SyncDirection.BothWays;
        var copies = new List<SyncCopy>();
        var skipped = new List<SyncSkip>();
        int untouched = 0;

        void Copy(CompareItem item, bool right)
        {
            FileRow source = (right ? item.Left : item.Right)!;
            copies.Add(source.IsDirectory ? new SyncCopy(item, right, item.Size, item.Files) : new SyncCopy(item, right, source.Size, 1));
        }

        void Skip(CompareItem item, SkipReason reason) => skipped.Add(new SyncSkip(item, reason));

        foreach (CompareItem item in comparison.Items)
        {
            switch (item.State)
            {
                case CompareState.LeftOnly or CompareState.RightOnly:
                    bool right = item.State == CompareState.LeftOnly;
                    if (!(right ? toRight : toLeft)) untouched++;
                    else if (item.IsDirectory && !comparison.Subfolders) Skip(item, SkipReason.FolderNeedsSubfolders);
                    else Copy(item, right);
                    break;

                case CompareState.LeftNewer or CompareState.RightNewer:
                    bool newerOnLeft = item.State == CompareState.LeftNewer;
                    if (newerOnLeft ? toRight : toLeft) Copy(item, newerOnLeft);
                    else if (replaceNewer && direction != SyncDirection.BothWays) Copy(item, !newerOnLeft);
                    else Skip(item, SkipReason.NewerOnTheOtherSide);
                    break;

                case CompareState.SizeDiffers:
                    if (direction == SyncDirection.BothWays) Skip(item, SkipReason.CannotTellWhichIsNewer);
                    else Copy(item, toRight);
                    break;

                case CompareState.CaseClash:
                    // Only a matter for the way that would bring the name over.
                    if ((item.Left is not null && toRight) || (item.Right is not null && toLeft)) Skip(item, SkipReason.NamesClashByCase);
                    else untouched++;
                    break;

                case CompareState.CaseDiffers:
                    Skip(item, SkipReason.NamesDifferInCase);
                    break;

                case CompareState.KindDiffers:
                    Skip(item, SkipReason.FileAndFolder);
                    break;

                default:
                    Skip(item, SkipReason.Unreadable);
                    break;
            }
        }

        return new SyncPlan(copies, skipped, untouched);
    }
}

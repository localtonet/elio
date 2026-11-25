using System.Diagnostics;
using Elio.Core;

namespace Elio.UI.Files;

/// <summary>What a change of permissions reaches beyond what was chosen: FileZilla's three choices, and the plain one.</summary>
public enum PermissionScope
{
    /// <summary>What was chosen and nothing more.</summary>
    OnlyThis,

    /// <summary>The folder and everything in it, all the way down.</summary>
    Everything,

    /// <summary>The files in the folder, all the way down. The folders keep what they have, the chosen one too.</summary>
    OnlyFiles,

    /// <summary>The folder and the folders in it, all the way down. The files keep what they have.</summary>
    OnlyFolders,
}

/// <summary>What the user chose in the permissions dialog.</summary>
/// <param name="Mode">The nine bits, such as 0b110_100_100 for 644.</param>
public sealed record PermissionChoice(int Mode, PermissionScope Scope);

/// <summary>How a change of permissions went.</summary>
/// <param name="Changed">How many files and folders were changed one at a time. A folder the server changed in one go is not counted in it.</param>
/// <param name="Failures">What could not be changed or read, each with the reason. Only the first of them are kept.</param>
/// <param name="FailureCount">How many failed, the ones not kept too.</param>
/// <param name="LinksLeftOut">Links that were neither changed nor followed.</param>
/// <param name="GaveUp">So many failed one after another that the rest was not tried.</param>
public sealed record PermissionReport(int Changed, IReadOnlyList<string> Failures, int FailureCount, int LinksLeftOut, bool GaveUp);

/// <summary>Permissions as the three digits people type and the nine letters a listing shows.</summary>
public static class PermissionBits
{
    /// <summary>"644" as bits, or null for anything that is not three digits from 0 to 7.</summary>
    public static int? Parse(string? digits)
    {
        string text = digits?.Trim() ?? string.Empty;
        if (text.Length != 3 || text.Any(c => c is < '0' or > '7')) return null;
        return ((text[0] - '0') << 6) | ((text[1] - '0') << 3) | (text[2] - '0');
    }

    /// <summary>Bits as the three digits, such as "644".</summary>
    public static string Digits(int mode) => $"{(mode >> 6) & 7}{(mode >> 3) & 7}{mode & 7}";

    /// <summary>Bits as a listing shows them, such as "rw-r--r--".</summary>
    public static string Letters(int mode)
    {
        char Flag(int bit, char letter) => (mode & bit) != 0 ? letter : '-';
        return string.Concat(
            Flag(0b100_000_000, 'r'), Flag(0b010_000_000, 'w'), Flag(0b001_000_000, 'x'),
            Flag(0b000_100_000, 'r'), Flag(0b000_010_000, 'w'), Flag(0b000_001_000, 'x'),
            Flag(0b000_000_100, 'r'), Flag(0b000_000_010, 'w'), Flag(0b000_000_001, 'x'));
    }

    /// <summary>What a listing shows, such as "rw-r--r--", as the three digits. Empty for anything else, such as the nothing Windows shows.</summary>
    public static string DigitsOfLetters(string letters)
    {
        if (letters.Length != 9) return string.Empty;
        int Digit(int start) => (letters[start] == 'r' ? 4 : 0) + (letters[start + 1] == 'w' ? 2 : 0) + (letters[start + 2] is 'x' or 's' or 't' ? 1 : 0);
        return $"{Digit(0)}{Digit(3)}{Digit(6)}";
    }
}

/// <summary>
/// Changes the permissions of what was chosen and, if asked, of what is in the folders among it. The folders below are
/// gone through one at a time: no link is followed or changed, since what it points at can be anywhere, even above where the
/// walk began. Something that cannot be changed is noted and the walk goes on, and the walk stops when it is cancelled.
/// </summary>
public static class PermissionChanger
{
    // So many failures one after another mean the rest would fail as well, such as when the connection is gone.
    private const int FailuresInARow = 25;

    // A list of failures longer than this is not read by anyone.
    private const int KeptFailures = 200;

    // How many files of a folder are changed at once. A server answers a few requests at a time about as fast as one.
    private const int AtOnce = 8;

    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);

    /// <param name="progress">Told now and then how many have been changed so far.</param>
    /// <exception cref="OperationCanceledException">Stopped. What was changed before stays changed.</exception>
    public static async Task<PermissionReport> ApplyAsync(IFileSource source, IReadOnlyList<FileRow> rows, PermissionChoice choice, IProgress<int>? progress, CancellationToken cancellation)
    {
        var walk = new Walk(source, choice, progress, cancellation);
        await walk.RunAsync(rows).ConfigureAwait(false);
        return walk.Report();
    }

    // One change of permissions as it goes. The work is done one step after another, never two at once, so the counts
    // need no lock: the files changed together are counted once they are all done.
    private sealed class Walk(IFileSource source, PermissionChoice choice, IProgress<int>? progress, CancellationToken cancellation)
    {
        private readonly List<string> _failures = [];
        private int _failureCount;
        private int _inARow;
        private int _changed;
        private int _links;
        private bool _gaveUp;
        private long _lastReport = Stopwatch.GetTimestamp();

        private bool Files => choice.Scope is PermissionScope.Everything or PermissionScope.OnlyFiles;

        private bool Folders => choice.Scope is PermissionScope.Everything or PermissionScope.OnlyFolders;

        public PermissionReport Report() => new(_changed, _failures, _failureCount, _links, _gaveUp);

        public async Task RunAsync(IReadOnlyList<FileRow> rows)
        {
            foreach (FileRow row in rows)
            {
                if (_gaveUp) return;
                cancellation.ThrowIfCancellationRequested();

                // Only what was chosen: done as it always was, a link included, which chmod changes through.
                if (choice.Scope == PermissionScope.OnlyThis)
                {
                    Count(await TryChangeAsync(row).ConfigureAwait(false), row);
                    continue;
                }

                if (row.IsLink)
                {
                    _links++;
                    continue;
                }

                if (!row.IsDirectory)
                {
                    if (Files) Count(await TryChangeAsync(row).ConfigureAwait(false), row);
                    continue;
                }

                // A server that can be given commands does the whole folder itself, far quicker than a request for each file.
                if (choice.Scope == PermissionScope.Everything && await source.ChangeModeTreeAsync(row, choice.Mode, cancellation).ConfigureAwait(false) is { } complaints)
                {
                    foreach (string complaint in complaints) Fail(complaint, inARow: false);
                    continue;
                }

                await FolderAsync(row, 0).ConfigureAwait(false);
            }
        }

        private async Task FolderAsync(FileRow folder, int depth)
        {
            List<FileRow> children;
            bool changedFirst = false;
            try
            {
                children = await source.ListAsync(folder.Path, cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellation.IsCancellationRequested && ex is not OutOfMemoryException)
            {
                // A folder that cannot be read may be one the new permissions open, so it is changed first and read again.
                if (!Folders)
                {
                    Fail($"{folder.Path}: {ex.Message}");
                    return;
                }

                string? problem = await TryChangeAsync(folder).ConfigureAwait(false);
                Count(problem, folder);
                if (problem is not null) return;

                changedFirst = true;
                try
                {
                    children = await source.ListAsync(folder.Path, cancellation).ConfigureAwait(false);
                }
                catch (Exception again) when (!cancellation.IsCancellationRequested && again is not OutOfMemoryException)
                {
                    Fail($"{folder.Path}: {again.Message}");
                    return;
                }
            }

            var files = new List<FileRow>();
            foreach (FileRow child in children)
            {
                if (child.IsLink) _links++;
                else if (!child.IsDirectory) files.Add(child);
            }

            if (Files)
            {
                for (int start = 0; start < files.Count && !_gaveUp; start += AtOnce)
                {
                    cancellation.ThrowIfCancellationRequested();
                    List<FileRow> some = files.GetRange(start, Math.Min(AtOnce, files.Count - start));
                    string?[] problems = await Task.WhenAll(some.Select(TryChangeAsync)).ConfigureAwait(false);
                    for (int i = 0; i < some.Count; i++) Count(problems[i], some[i]);
                }
            }

            foreach (FileRow child in children.Where(c => c.IsDirectory && !c.IsLink))
            {
                if (_gaveUp) return;
                cancellation.ThrowIfCancellationRequested();

                // A loop that links did not give away, such as a folder mounted inside itself.
                if (depth + 1 >= FileTreeScanner.MaxDepth) Fail($"{child.Path}: {L.T("Too many folders deep.")}");
                else await FolderAsync(child, depth + 1).ConfigureAwait(false);
            }

            // Last, so that a folder whose new permissions shut it can still be gone through first.
            if (Folders && !changedFirst && !_gaveUp) Count(await TryChangeAsync(folder).ConfigureAwait(false), folder);
        }

        // What went wrong, or null if it was changed. Nothing is counted here, since several of these run at once.
        private async Task<string?> TryChangeAsync(FileRow row)
        {
            try
            {
                await source.ChangeModeAsync(row, choice.Mode, cancellation).ConfigureAwait(false);
                return null;
            }
            catch (Exception ex) when (!cancellation.IsCancellationRequested && ex is not OutOfMemoryException)
            {
                return ex.Message;
            }
        }

        private void Count(string? problem, FileRow row)
        {
            cancellation.ThrowIfCancellationRequested();
            if (problem is not null)
            {
                Fail($"{row.Path}: {problem}");
                return;
            }

            _changed++;
            _inARow = 0;
            if (progress is not null && Stopwatch.GetElapsedTime(_lastReport) >= ReportEvery)
            {
                _lastReport = Stopwatch.GetTimestamp();
                progress.Report(_changed);
            }
        }

        private void Fail(string text, bool inARow = true)
        {
            _failureCount++;
            if (_failures.Count < KeptFailures) _failures.Add(text);
            if (inARow && ++_inARow >= FailuresInARow) _gaveUp = true;
        }
    }
}

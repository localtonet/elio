namespace Elio.Core;

/// <summary>One folder, how often it was opened and when last.</summary>
internal sealed class FrequentFolderEntry
{
    public string Path { get; set; } = string.Empty;

    /// <summary>A count that is made smaller as the whole grows, so it is not always whole.</summary>
    public double Visits { get; set; }

    public DateTime LastUtc { get; set; }
}

/// <summary>What the file of frequent folders holds, a list for each server.</summary>
internal sealed class FrequentFoldersFile : IVersionedFile
{
    public const int CurrentVersion = 1;

    public int SchemaVersion { get; set; } = CurrentVersion;
    int IVersionedFile.SupportedVersion => CurrentVersion;

    public Dictionary<string, List<FrequentFolderEntry>> Servers { get; set; } = [];
}

/// <summary>
/// The folders a file list has been in, for a quick way back to the ones that matter. Each is kept with how often it was
/// opened and when last, and told apart by server. The order is by frecency: a folder opened often counts for more, and
/// so does one opened lately, so a folder that was a favorite last year makes way for what is used this week.
/// </summary>
public sealed class FrequentFolders
{
    private const int MaxFoldersPerServer = 300;

    // Counts that add up to more than this are all made a tenth smaller, so a folder opened a great many times
    // cannot keep a newer one from ever catching up, and the numbers stay small.
    private const double AgingThreshold = 1000;
    private const double AgingFactor = 0.9;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly object _writing = new();
    private readonly JsonFileStore<FrequentFoldersFile> _store;
    private FrequentFoldersFile? _data;
    private bool _dirty;
    private bool _readOnly;
    private Timer? _timer;

    /// <param name="file">Where the folders are kept. A file that is missing or cannot be read is an empty list.</param>
    public FrequentFolders(string file)
    {
        _store = new JsonFileStore<FrequentFoldersFile>(file, CoreJsonContext.Default.FrequentFoldersFile);
    }

    /// <summary>The folders of this user, kept with the rest of what Elio keeps.</summary>
    public static FrequentFolders Shared { get; } = new(Path.Combine(ElioPaths.DataDirectory, "frequent-folders.json"));

    /// <summary>Notes that a folder was opened. It is written to disk a moment later, when the opening has stopped for a while.</summary>
    /// <param name="serverKey">What tells the server apart from the others, such as its id, or "local" for this computer.</param>
    /// <param name="nowUtc">When it was, if not now.</param>
    public void Visit(string serverKey, string path, DateTime? nowUtc = null)
    {
        string? folder = Normalize(path);
        if (string.IsNullOrEmpty(serverKey) || folder is null) return;

        DateTime now = Utc(nowUtc ?? DateTime.UtcNow);
        lock (_gate)
        {
            FrequentFoldersFile data = Load();
            if (!data.Servers.TryGetValue(serverKey, out List<FrequentFolderEntry>? list)) data.Servers[serverKey] = list = [];

            FrequentFolderEntry? entry = list.Find(e => e.Path == folder);
            if (entry is null) list.Add(entry = new FrequentFolderEntry { Path = folder });
            entry.Visits++;
            entry.LastUtc = now;

            Tidy(list, entry, now);
            _dirty = true;
            ScheduleSave();
        }
    }

    /// <summary>The folders of a server, the ones most worth going back to first.</summary>
    public IReadOnlyList<string> Top(string serverKey, int count, DateTime? nowUtc = null)
    {
        if (count <= 0 || string.IsNullOrEmpty(serverKey)) return [];

        DateTime now = Utc(nowUtc ?? DateTime.UtcNow);
        lock (_gate)
        {
            if (!Load().Servers.TryGetValue(serverKey, out List<FrequentFolderEntry>? list)) return [];

            return list
                .OrderByDescending(e => Frecency(e, now))
                .ThenByDescending(e => e.LastUtc)
                .ThenBy(e => e.Path, StringComparer.Ordinal)
                .Take(count)
                .Select(e => e.Path)
                .ToList();
        }
    }

    /// <summary>Takes a folder off the list, for one that is gone or not wanted.</summary>
    public void Forget(string serverKey, string path)
    {
        string? folder = Normalize(path);
        if (string.IsNullOrEmpty(serverKey) || folder is null) return;

        lock (_gate)
        {
            if (!Load().Servers.TryGetValue(serverKey, out List<FrequentFolderEntry>? list) || list.RemoveAll(e => e.Path == folder) == 0) return;

            _dirty = true;
            ScheduleSave();
        }
    }

    /// <summary>Writes what is not on disk yet. For when the program is closing.</summary>
    public void Flush()
    {
        lock (_gate) _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        Save();
    }

    // The two locks are always taken in this order, and the one that guards the data is let go of while the disk is
    // written to, so that opening a folder never waits for a disk.
    private void Save()
    {
        lock (_writing)
        {
            FrequentFoldersFile snapshot;
            lock (_gate)
            {
                if (!_dirty || _readOnly || _data is null) return;

                _dirty = false;
                snapshot = Copy(_data);
            }

            try
            {
                _store.Save(snapshot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A list of folders is not worth an error. It is tried again with the next folder opened.
                lock (_gate) _dirty = true;
            }
        }
    }

    // On a timer thread, where nobody is told of a failure, so nothing may escape.
    private void SaveFromTimer(object? state)
    {
        try
        {
            Save();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing to do with it.
        }
    }

    // Called with the gate held. Every opening moves the moment of saving back, so a run of them is one write.
    private void ScheduleSave()
    {
        _timer ??= new Timer(SaveFromTimer);
        _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    // Called with the gate held.
    private FrequentFoldersFile Load()
    {
        if (_data is not null) return _data;

        try
        {
            _data = _store.Load();
        }
        catch (NewerFileException)
        {
            // Written by a newer version. What it holds is not ours to read, and saving would take it away.
            _data = new FrequentFoldersFile();
            _readOnly = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _data = new FrequentFoldersFile();
        }

        Clean(_data);
        return _data;
    }

    // A file somebody edited by hand may have holes in it.
    private static void Clean(FrequentFoldersFile data)
    {
        data.Servers ??= [];
        foreach (string key in data.Servers.Keys.ToList())
        {
            List<FrequentFolderEntry>? list = data.Servers[key];
            if (list is null)
            {
                data.Servers.Remove(key);
                continue;
            }

            list.RemoveAll(e => e is null || string.IsNullOrEmpty(e.Path) || !(e.Visits > 0));
            foreach (FrequentFolderEntry entry in list) entry.LastUtc = Utc(entry.LastUtc);
        }
    }

    private static FrequentFoldersFile Copy(FrequentFoldersFile data) => new()
    {
        Servers = data.Servers.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(e => new FrequentFolderEntry { Path = e.Path, Visits = e.Visits, LastUtc = e.LastUtc }).ToList()),
    };

    // Keeps a server's list from growing for ever, and its counts from either.
    private static void Tidy(List<FrequentFolderEntry> list, FrequentFolderEntry visited, DateTime now)
    {
        if (list.Sum(e => e.Visits) > AgingThreshold)
        {
            foreach (FrequentFolderEntry entry in list) entry.Visits *= AgingFactor;

            // What was opened just now stays, however little it counts for yet.
            list.RemoveAll(e => e != visited && e.Visits < 1);
        }

        if (list.Count <= MaxFoldersPerServer) return;

        List<FrequentFolderEntry> least = list
            .Where(e => e != visited)
            .OrderBy(e => Frecency(e, now))
            .ThenBy(e => e.LastUtc)
            .Take(list.Count - MaxFoldersPerServer)
            .ToList();
        foreach (FrequentFolderEntry entry in least) list.Remove(entry);
    }

    /// <summary>The count of visits, made more or less by how long ago the last one was.</summary>
    private static double Frecency(FrequentFolderEntry entry, DateTime now)
    {
        TimeSpan age = now - entry.LastUtc;
        double weight = age <= TimeSpan.FromHours(1) ? 4
            : age <= TimeSpan.FromDays(1) ? 2
            : age <= TimeSpan.FromDays(7) ? 0.5
            : 0.25;
        return entry.Visits * weight;
    }

    // A folder is the same folder with or without a slash at the end, but a root is a root.
    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string trimmed = path.TrimEnd('/', '\\');
        if (trimmed.Length == 0) return path[..1];

        // "C:" alone names the current folder of a drive. "C:\" is the drive.
        if (trimmed.Length == 2 && trimmed[1] == ':' && trimmed.Length < path.Length) return trimmed + path[2];
        return trimmed;
    }

    private static DateTime Utc(DateTime time) =>
        time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : DateTime.SpecifyKind(time, DateTimeKind.Utc);
}

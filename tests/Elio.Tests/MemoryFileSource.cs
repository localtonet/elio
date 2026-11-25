using Elio.UI.Files;

namespace Elio.Tests;

/// <summary>
/// A tree of files and folders held in memory, with server-style paths, for testing what walks through a side of the file
/// screen without a server. It remembers every permission change made through it, in the order they were made.
/// </summary>
internal sealed class MemoryFileSource : IFileSource
{
    private sealed class Node
    {
        public required string Path { get; init; }
        public bool IsDirectory { get; init; }
        public bool IsLink { get; init; }
        public long Size { get; init; }
        public DateTime ModifiedUtc { get; init; }
        public int Mode { get; set; } = 0b110_100_100;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal) { ["/"] = new Node { Path = "/", IsDirectory = true } };
    private readonly List<(string Path, int Mode)> _changes = [];
    private readonly List<string> _listed = [];

    public bool IsRemote { get; init; } = true;

    /// <summary>Folders that cannot be listed until their permissions are changed, like one whose owner may not read it.</summary>
    public HashSet<string> ShutUntilChanged { get; } = new(StringComparer.Ordinal);

    /// <summary>Folders that can never be listed.</summary>
    public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);

    /// <summary>Paths whose permissions cannot be changed.</summary>
    public HashSet<string> Refuses { get; } = new(StringComparer.Ordinal);

    /// <summary>What a change of a whole folder in one go does, or null for a source that cannot.</summary>
    public Func<FileRow, int, IReadOnlyList<string>?>? Tree { get; set; }

    /// <summary>Called before each permission change, such as to cancel part way through.</summary>
    public Action<string>? BeforeChange { get; set; }

    public IReadOnlyList<(string Path, int Mode)> Changes
    {
        get
        {
            lock (_gate) return _changes.ToList();
        }
    }

    public IReadOnlyList<string> Listed
    {
        get
        {
            lock (_gate) return _listed.ToList();
        }
    }

    public MemoryFileSource Folder(string path)
    {
        _nodes[path] = new Node { Path = path, IsDirectory = true };
        return this;
    }

    public MemoryFileSource File(string path, long size = 10, DateTime? modifiedUtc = null)
    {
        _nodes[path] = new Node { Path = path, Size = size, ModifiedUtc = modifiedUtc ?? new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc) };
        return this;
    }

    /// <summary>A link, which lists as a folder when it points at one, as a server's listing shows it.</summary>
    public MemoryFileSource Link(string path, bool toFolder)
    {
        _nodes[path] = new Node { Path = path, IsLink = true, IsDirectory = toFolder };
        return this;
    }

    public int ModeOf(string path) => _nodes[path].Mode;

    public FileRow Row(string path)
    {
        Node node = _nodes[path];
        return new FileRow(RemoteNameOf(path), path, node.Size, node.ModifiedUtc, node.IsDirectory, node.IsLink, PermissionBits.Letters(node.Mode), null);
    }

    private static string RemoteNameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string ParentOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    public Task<string> GetStartPathAsync(CancellationToken cancellation) => Task.FromResult("/");

    public Task<List<FileRow>> ListAsync(string path, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _listed.Add(path);
            if (Unreadable.Contains(path) || ShutUntilChanged.Contains(path)) throw new IOException("Permission denied");
            if (!_nodes.TryGetValue(path, out Node? folder) || !folder.IsDirectory) throw new IOException("No such folder");
            if (folder.IsLink) throw new InvalidOperationException("A link was followed: " + path);

            return Task.FromResult(_nodes.Values.Where(n => n.Path != "/" && ParentOf(n.Path) == path).Select(n => Row(n.Path)).ToList());
        }
    }

    public string? Parent(string path) => path == "/" ? null : ParentOf(path);

    public string Combine(string folder, string name) => folder.EndsWith('/') ? folder + name : folder + "/" + name;

    public Task CreateFolderAsync(string path, CancellationToken cancellation) => throw new NotSupportedException();

    public Task RenameAsync(FileRow row, string newName, CancellationToken cancellation) => throw new NotSupportedException();

    public Task DeleteAsync(FileRow row, CancellationToken cancellation) => throw new NotSupportedException();

    public bool CanChangeMode => true;

    public Task ChangeModeAsync(FileRow row, int mode, CancellationToken cancellation)
    {
        BeforeChange?.Invoke(row.Path);
        cancellation.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (Refuses.Contains(row.Path)) throw new IOException("Operation not permitted");
            _changes.Add((row.Path, mode));
            _nodes[row.Path].Mode = mode;
            ShutUntilChanged.Remove(row.Path);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>?> ChangeModeTreeAsync(FileRow folder, int mode, CancellationToken cancellation) =>
        Task.FromResult(Tree?.Invoke(folder, mode));
}

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Elio.Core;

public interface IVersionedFile
{
    int SchemaVersion { get; }
    int SupportedVersion { get; }
}

/// <summary>The file was written by a newer version of the app, so saving over it would lose data.</summary>
public sealed class NewerFileException(string path) : Exception($"{path} was saved by a newer version of Elio.");

internal static class AtomicFile
{
    /// <summary>Either fully replaces the file or leaves it untouched, and keeps the previous version beside it.</summary>
    public static void Write(string path, Action<Stream> write)
    {
        PrivateFiles.CreateDirectory(Path.GetDirectoryName(path)!);

        string temporary = path + ".tmp";
        using (FileStream stream = PrivateFiles.Open(temporary, FileMode.Create))
        {
            write(stream);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(temporary, path, path + ".bak");

            // The copy kept beside it is the old file, with whatever an earlier version let others do with it.
            PrivateFiles.Restrict(path + ".bak");
        }
        else
        {
            File.Move(temporary, path);
        }
    }
}

/// <summary>
/// Keeps what Elio writes to itself on Linux, macOS and phones, where a new file can otherwise be read by everyone
/// with an account on the computer. On Windows the user's own folders are already closed to others, so nothing changes there.
/// </summary>
public static class PrivateFiles
{
    private const UnixFileMode OwnerOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Makes sure a folder exists, open to its owner alone. One that was already there is closed to others too.</summary>
    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, OwnerOnlyFolder);
        Restrict(path);
    }

    /// <summary>Opens a file for writing that only its owner can read, whether it is new or was there before.</summary>
    public static FileStream Open(string path, FileMode mode, FileShare share = FileShare.None)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = share };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;

        var stream = new FileStream(path, options);
        Restrict(path);
        return stream;
    }

    /// <summary>
    /// Takes a file or folder away from everyone but its owner, if it is there. Where that cannot be done, as on a
    /// memory stick whose file system has no owners, it stays as it is rather than stopping the app.
    /// </summary>
    public static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            bool folder = Directory.Exists(path);
            if (!folder && !File.Exists(path)) return;

            UnixFileMode wanted = folder ? OwnerOnlyFolder : OwnerOnlyFile;
            if (File.GetUnixFileMode(path) != wanted) File.SetUnixFileMode(path, wanted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not ours to change, or the file system keeps no permissions.
        }
    }
}

/// <summary>Keeps one object in a JSON file that survives crashes and damaged saves.</summary>
public sealed class JsonFileStore<T>(string path, JsonTypeInfo<T> typeInfo) where T : class, IVersionedFile, new()
{
    private string BackupPath => path + ".bak";

    public T Load()
    {
        // Files from before Elio kept them to itself are closed to other users the first time they are read.
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder) PrivateFiles.Restrict(folder);
        PrivateFiles.Restrict(path);
        PrivateFiles.Restrict(BackupPath);

        if (TryRead(path, out T? value) || TryRead(BackupPath, out value)) return value;
        if (!File.Exists(path)) return new T();

        // Neither copy can be read. Keep the evidence and start clean instead of refusing to open.
        File.Move(path, $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true);
        return new T();
    }

    private bool TryRead(string file, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? value)
    {
        value = null;
        if (!File.Exists(file)) return false;

        try
        {
            value = JsonSerializer.Deserialize(File.ReadAllBytes(file), typeInfo);
        }
        catch (JsonException)
        {
            return false;
        }

        if (value is null) return false;
        if (value.SchemaVersion > value.SupportedVersion) throw new NewerFileException(file);
        return true;
    }

    public void Save(T value) => AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream, value, typeInfo));
}

public static class ElioPaths
{
    private const string PortableFolder = "data";

    /// <summary>
    /// A folder that replaces every usual location, or null to use those. ELIO_DATA_DIR names one, and so does
    /// a folder called "data" beside the program: that is portable mode, where Elio and everything it keeps
    /// travel together on one drive and nothing is left behind on the computer.
    /// </summary>
    public static string? CustomDirectory
    {
        get
        {
            if (Environment.GetEnvironmentVariable("ELIO_DATA_DIR") is { Length: > 0 } custom) return custom;

            string portable = Path.Combine(AppContext.BaseDirectory, PortableFolder);
            return Directory.Exists(portable) ? portable : null;
        }
    }

    /// <summary>Whether everything is being kept beside the program.</summary>
    public static bool IsPortable =>
        Environment.GetEnvironmentVariable("ELIO_DATA_DIR") is not { Length: > 0 } && CustomDirectory is not null;

    /// <summary>Where settings live.</summary>
    public static string DataDirectory =>
        CustomDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Elio");

    /// <summary>Where files sent by a server through the terminal are saved.</summary>
    public static string DownloadDirectory =>
        CustomDirectory is string custom
            ? Path.Combine(custom, "downloads")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Where saved terminal output goes. Under Documents, because people go looking for these.</summary>
    public static string LogDirectory =>
        CustomDirectory is string custom
            ? Path.Combine(custom, "logs")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Elio", "logs");
}

public static class Stores
{
    private static string File(string name) => Path.Combine(ElioPaths.DataDirectory, name);

    public static JsonFileStore<HostLibrary> Hosts() => new(File("hosts.json"), CoreJsonContext.Default.HostLibrary);

    public static JsonFileStore<LocaltonetCache> Localtonet() => new(File("localtonet.json"), CoreJsonContext.Default.LocaltonetCache);

    public static JsonFileStore<KnownHostsFile> KnownHosts() => new(File("known-hosts.json"), CoreJsonContext.Default.KnownHostsFile);

    public static JsonFileStore<AppSettings> Settings() => new(File("settings.json"), CoreJsonContext.Default.AppSettings);

    public static JsonFileStore<SessionLayout> Layout() => new(File("session.json"), CoreJsonContext.Default.SessionLayout);

    public static JsonFileStore<WorkspaceFile> Workspaces() => new(File("workspaces.json"), CoreJsonContext.Default.WorkspaceFile);
}

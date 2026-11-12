using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Elio.Connections;

/// <summary>Which programs are running under a shell, for telling what a terminal is busy with.</summary>
public static partial class ProcessTree
{
    private readonly record struct Entry(int Id, int Parent, string Name);

    /// <summary>
    /// The names of everything started, directly or not, by the given processes. One look at the system
    /// answers for all of them, so asking for every terminal at once costs the same as asking for one.
    /// </summary>
    /// <returns>For each process asked about, the names of what runs under it, without paths or extensions.</returns>
    public static Dictionary<int, List<string>> Descendants(IReadOnlyCollection<int> roots)
    {
        var found = roots.ToDictionary(r => r, _ => new List<string>());
        if (roots.Count == 0) return found;

        List<Entry> all;
        try
        {
            all = OperatingSystem.IsWindows() ? WindowsProcesses() : OperatingSystem.IsLinux() ? LinuxProcesses() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return found;
        }

        ILookup<int, Entry> children = all.ToLookup(e => e.Parent);
        foreach (int root in roots)
        {
            // A process number can be reused, and a parent can be gone: the visited set keeps a loop from forming.
            var visited = new HashSet<int> { root };
            var pending = new Queue<int>([root]);
            while (pending.TryDequeue(out int current))
            {
                foreach (Entry child in children[current])
                {
                    if (!visited.Add(child.Id)) continue;
                    found[root].Add(child.Name);
                    pending.Enqueue(child.Id);
                }
            }
        }

        return found;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe List<Entry> WindowsProcesses()
    {
        const uint SnapProcess = 0x2;
        var entries = new List<Entry>();

        nint snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == -1) return entries;

        try
        {
            var entry = new ProcessEntry { Size = (uint)sizeof(ProcessEntry) };
            for (bool more = Process32FirstW(snapshot, &entry); more; more = Process32NextW(snapshot, &entry))
            {
                string file = new(entry.File);
                entries.Add(new Entry((int)entry.ProcessId, (int)entry.ParentProcessId, Path.GetFileNameWithoutExtension(file)));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return entries;
    }

    // A script run by node or python shows up under the interpreter's name, so the script's own name is added:
    // "node /usr/lib/node_modules/@scope/tool/cli.js" also answers to "tool".
    [SupportedOSPlatform("linux")]
    private static List<Entry> LinuxProcesses()
    {
        var entries = new List<Entry>();
        foreach (string folder in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(folder), out int id)) continue;

            try
            {
                // "pid (name) state ppid ...": the name may itself contain spaces and brackets.
                string stat = File.ReadAllText(Path.Combine(folder, "stat"));
                int open = stat.IndexOf('(');
                int close = stat.LastIndexOf(')');
                if (open < 0 || close < open) continue;

                string[] rest = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (rest.Length < 2 || !int.TryParse(rest[1], out int parent)) continue;

                entries.Add(new Entry(id, parent, stat[(open + 1)..close]));

                string[] arguments = File.ReadAllText(Path.Combine(folder, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                foreach (string argument in arguments.Take(3))
                {
                    foreach (string part in argument.Split('/', StringSplitOptions.RemoveEmptyEntries).TakeLast(3))
                        entries.Add(new Entry(-id, id, Path.GetFileNameWithoutExtension(part)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // It ended while being read, or belongs to someone else.
            }
        }

        return entries;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        public fixed char File[260];
    }

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32FirstW(nint snapshot, ProcessEntry* entry);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool Process32NextW(nint snapshot, ProcessEntry* entry);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

/// <summary>The command-line AI tools Elio can name when it sees one running in a terminal.</summary>
public static class CodingAgents
{
    // What each tool's process is called, and what to show for it.
    private static readonly (string Process, string Label)[] Known =
    [
        ("claude", "Claude"),
        ("codex", "Codex"),
        ("gemini", "Gemini"),
        ("copilot", "Copilot"),
        ("aider", "Aider"),
        ("cursor-agent", "Cursor"),
    ];

    /// <summary>The tool among these process names, if one of them is one.</summary>
    public static string? Among(IEnumerable<string> processNames)
    {
        foreach (string name in processNames)
        {
            foreach (var (process, label) in Known)
                if (name.Equals(process, StringComparison.OrdinalIgnoreCase)) return label;
        }

        return null;
    }
}

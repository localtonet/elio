using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Elio.Core;
using Microsoft.Win32;

namespace Elio.Connections;

/// <summary>The shells installed on this computer.</summary>
public static class LocalShells
{
    private const string AndroidShell = "/system/bin/sh";

    /// <summary>
    /// Finds the shells that can be opened here, the usual one first. They are not saved with the servers,
    /// because another computer has other shells, so each gets an id that stays the same between runs.
    /// </summary>
    public static List<HostEntry> Detect()
    {
        var found = new List<HostEntry>();

        void Add(string name, string commandLine)
        {
            if (found.Any(s => s.Address.Equals(commandLine, StringComparison.OrdinalIgnoreCase))) return;
            found.Add(new HostEntry { Id = IdFor(commandLine), Kind = HostKind.Local, Name = name, Address = commandLine, AutoReconnect = false });
        }

        if (OperatingSystem.IsWindows()) AddWindowsShells(Add);
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) AddUnixShells(Add);

        // A phone has one shell, and it is not listed anywhere.
        else if (OperatingSystem.IsAndroid() && File.Exists(AndroidShell)) Add("sh", AndroidShell);
        return found;
    }

    private static Guid IdFor(string commandLine) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("elio-local-shell:" + commandLine.ToLowerInvariant())));

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    [SupportedOSPlatform("windows")]
    private static void AddWindowsShells(Action<string, string> add)
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string programs = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        if (FirstExisting(OnPath("pwsh.exe"), Path.Combine(programs, "PowerShell", "7", "pwsh.exe")) is string pwsh)
            add("PowerShell", $"{Quote(pwsh)} -NoLogo");

        string windowsPowerShell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(windowsPowerShell)) add("Windows PowerShell", $"{Quote(windowsPowerShell)} -NoLogo");

        string cmd = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec && File.Exists(comSpec) ? comSpec : Path.Combine(system, "cmd.exe");
        if (File.Exists(cmd)) add(L.T("Command Prompt"), Quote(cmd));

        string wsl = Path.Combine(system, "wsl.exe");
        if (File.Exists(wsl))
        {
            foreach (string distribution in WslDistributions()) add($"WSL · {distribution}", $"{Quote(wsl)} -d {Quote(distribution)} --cd ~");
        }

        string? gitRoot = RegistryText(Registry.LocalMachine, @"SOFTWARE\GitForWindows", "InstallPath") ?? RegistryText(Registry.CurrentUser, @"SOFTWARE\GitForWindows", "InstallPath");
        if (FirstExisting(Join(gitRoot, "bin", "bash.exe"), Path.Combine(programs, "Git", "bin", "bash.exe")) is string gitBash)
            add("Git Bash", $"{Quote(gitBash)} --login -i");

        string? cygwinRoot = RegistryText(Registry.LocalMachine, @"SOFTWARE\Cygwin\setup", "rootdir") ?? RegistryText(Registry.CurrentUser, @"SOFTWARE\Cygwin\setup", "rootdir");
        if (FirstExisting(Join(cygwinRoot, "bin", "bash.exe"), @"C:\cygwin64\bin\bash.exe", @"C:\cygwin\bin\bash.exe") is string cygwin)
            add("Cygwin", $"{Quote(cygwin)} --login -i");

        if (FirstExisting(@"C:\msys64\usr\bin\bash.exe", @"C:\msys32\usr\bin\bash.exe") is string msys)
            add("MSYS2", $"{Quote(msys)} --login -i");

        if (Environment.GetEnvironmentVariable("CMDER_ROOT") is { Length: > 0 } cmder && File.Exists(Path.Combine(cmder, "vendor", "init.bat")))
            add("Cmder", $"{Quote(cmd)} /k \"{Path.Combine(cmder, "vendor", "init.bat")}\"");
    }

    private static string? Join(string? root, params string[] parts) => root is null ? null : Path.Combine([root, .. parts]);

    private static string? FirstExisting(params string?[] paths) => paths.FirstOrDefault(p => p is not null && File.Exists(p));

    private static string? OnPath(string file)
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(folder, file);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A PATH entry that isn't a usable path.
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? RegistryText(RegistryKey root, string path, string name)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<string> WslDistributions()
    {
        var names = new List<string>();
        try
        {
            using RegistryKey? lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss is null) return names;

            foreach (string id in lxss.GetSubKeyNames())
            {
                using RegistryKey? distribution = lxss.OpenSubKey(id);

                // Docker Desktop keeps its own distributions there, and they have no shell worth opening.
                if (distribution?.GetValue("DistributionName") is string { Length: > 0 } name && !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Without the list, WSL is simply not offered.
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    private static void AddUnixShells(Action<string, string> add)
    {
        var paths = new List<string>();
        if (Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } preferred) paths.Add(preferred);

        try
        {
            paths.AddRange(File.ReadLines("/etc/shells").Select(l => l.Trim()).Where(l => l.StartsWith('/')));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The user's own shell is enough.
        }

        if (paths.Count == 0) paths.Add("/bin/sh");

        // The same shell is often listed under both /bin and /usr/bin.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            // The list also names things that can be a login shell without being a shell to work in.
            if (name is "nologin" or "false" or "sync" or "tmux" or "screen" or "rbash" or "git-shell" || !File.Exists(path) || !seen.Add(name)) continue;
            add(name, $"{Quote(path)} -l");
        }
    }
}

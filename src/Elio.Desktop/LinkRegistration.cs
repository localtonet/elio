using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Elio.Core;
using Elio.UI;
using Microsoft.Win32;

namespace Elio.Desktop;

/// <summary>
/// Tells the operating system that ssh:// and elio:// links open in Elio, or stops telling it. Nothing is written until
/// the user asks for it in Settings, and taking it back removes only what Elio wrote.
/// </summary>
internal static class LinkRegistration
{
    private static readonly string[] Schemes = [LaunchRequest.SshScheme, LaunchRequest.ElioScheme];

    /// <summary>The registration for this platform, or null where links are set up some other way (a Mac reads them from the app bundle).</summary>
    public static ILinkRegistration? ForThisPlatform()
    {
        if (Program() is null) return null;
        if (OperatingSystem.IsWindows()) return new WindowsLinks();
        if (OperatingSystem.IsLinux()) return new LinuxLinks();
        return null;
    }

    // An AppImage runs from a folder that is gone once it exits; the file to start again is the AppImage itself.
    private static string? Program() =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage ? appImage : Environment.ProcessPath;

    [SupportedOSPlatform("windows")]
    private sealed class WindowsLinks : ILinkRegistration
    {
        private static string Command => $"\"{Program()}\" \"%1\"";

        private static string KeyPath(string scheme) => @"Software\Classes\" + scheme;

        public bool IsRegistered => Schemes.All(scheme => CommandOf(scheme) == Command);

        public void Register()
        {
            foreach (string scheme in Schemes)
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath(scheme));
                key.SetValue(string.Empty, $"URL:{scheme} link");
                key.SetValue("URL Protocol", string.Empty);
                using (RegistryKey icon = key.CreateSubKey("DefaultIcon")) icon.SetValue(string.Empty, $"\"{Program()}\",0");
                using RegistryKey command = key.CreateSubKey(@"shell\open\command");
                command.SetValue(string.Empty, Command);
            }
        }

        public void Unregister()
        {
            // Another program's registration is left alone, even if Elio's replaced it once.
            foreach (string scheme in Schemes)
                if (CommandOf(scheme) == Command) Registry.CurrentUser.DeleteSubKeyTree(KeyPath(scheme), throwOnMissingSubKey: false);
        }

        private static string? CommandOf(string scheme)
        {
            using RegistryKey? command = Registry.CurrentUser.OpenSubKey(KeyPath(scheme) + @"\shell\open\command");
            return command?.GetValue(string.Empty) as string;
        }
    }

    /// <summary>A desktop entry that is not shown in menus, named as the default for the two schemes, as freedesktop.org describes.</summary>
    private sealed class LinuxLinks : ILinkRegistration
    {
        private const string EntryName = "sh.elio.links.desktop";

        private static string Applications =>
            Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"), "applications");

        private static string EntryPath => Path.Combine(Applications, EntryName);

        public bool IsRegistered =>
            File.Exists(EntryPath) && Schemes.All(scheme => XdgMime("query", "default", "x-scheme-handler/" + scheme)?.Trim() == EntryName);

        public void Register()
        {
            Directory.CreateDirectory(Applications);
            var entry = new StringBuilder()
                .Append("[Desktop Entry]\n")
                .Append("Type=Application\n")
                .Append("Name=Elio\n")
                .Append("Comment=Opens ssh:// and elio:// links in Elio\n")
                .Append("Exec=").Append(Quote(Program()!)).Append(" %u\n")
                .Append("Terminal=false\n")
                .Append("NoDisplay=true\n")
                .Append("MimeType=").Append(string.Concat(Schemes.Select(s => $"x-scheme-handler/{s};"))).Append('\n');
            File.WriteAllText(EntryPath, entry.ToString());

            foreach (string scheme in Schemes)
                if (XdgMime("default", EntryName, "x-scheme-handler/" + scheme) is null) throw new IOException(L.T("xdg-mime could not be run, so links could not be set to open in Elio."));
        }

        public void Unregister()
        {
            // A default that names an entry which is not there any more is passed over, so removing the entry is enough.
            if (File.Exists(EntryPath)) File.Delete(EntryPath);
        }

        // The Exec key's own quoting: inside double quotes, a backslash, a double quote, a backtick and a dollar sign are escaped.
        private static string Quote(string path)
        {
            var quoted = new StringBuilder("\"");
            foreach (char c in path)
            {
                if (c is '"' or '`' or '$' or '\\') quoted.Append('\\');
                quoted.Append(c);
            }

            return quoted.Append('"').ToString();
        }

        private static string? XdgMime(params string[] args)
        {
            try
            {
                var start = new ProcessStartInfo("xdg-mime") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (string arg in args) start.ArgumentList.Add(arg);
                using Process? process = Process.Start(start);
                if (process is null) return null;

                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(5000)) return null;
                return process.ExitCode == 0 ? output : null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }
}

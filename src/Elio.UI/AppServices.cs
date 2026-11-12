using System.Text.RegularExpressions;
using Elio.Core;
using Elio.Terminal;
using Elio.UI.Terminal;

namespace Elio.UI;

/// <summary>State shared by every window.</summary>
public static class AppServices
{
    private static readonly JsonFileStore<AppSettings> SettingsStore = Stores.Settings();
    private static readonly JsonFileStore<SessionLayout> LayoutStore = Stores.Layout();
    private static readonly JsonFileStore<HostLibrary> HostStore = Stores.Hosts();
    private static List<(Regex Pattern, uint Color)>? _highlights;

    public static KnownHosts KnownHosts { get; } = new();

    public static LocaltonetAccount Localtonet { get; } = new();

    public static SyncCoordinator Sync { get; } = new();

    /// <summary>
    /// Reads a QR code with the camera and gives its text, or null if the user gave up. Set by a platform head
    /// that has a camera to do it with; null on the desktop, where the code is typed instead.
    /// </summary>
    public static Func<Task<string?>>? ScanQrCode { get; set; }

    /// <summary>Raised when the system hands Elio a link it was asked to open: a QR code read by the phone's own camera.</summary>
    public static event Action<string>? LinkOpened;

    private static string? _pendingLink;

    /// <summary>
    /// Asks the device to check who is holding it, with its fingerprint, face or passcode, showing the given reason.
    /// True only if the check succeeded. Set by a platform head that can do it; null elsewhere, and then there is no app lock.
    /// </summary>
    public static Func<string, Task<bool>>? Authenticate { get; set; }

    /// <summary>The lock that keeps Elio shut until the device's own check succeeds, for whoever turned it on.</summary>
    public static AppLock Lock { get; } = MakeLock();

    private static AppLock MakeLock()
    {
        var appLock = new AppLock(
            () => Settings.LockApp,
            on =>
            {
                Settings.LockApp = on;
                SaveSettings();
            },
            () => Authenticate,
            () => DateTimeOffset.UtcNow);

        // A link that came in while locked was kept back, and is due now.
        appLock.Changed += () =>
        {
            if (!appLock.IsLocked && TakePendingLink() is { } link) OpenLink(link);
        };
        return appLock;
    }

    /// <summary>Elio went out of sight. Every platform's own way of saying so ends up here.</summary>
    public static void AppWentToBackground() => Lock.WentToBackground();

    /// <summary>Elio is in front of the user again. Every platform's own way of saying so ends up here.</summary>
    public static void AppCameToForeground() => Lock.CameToForeground();

    /// <summary>Takes a link from the system. One that arrives before the window is up, or while Elio is locked, waits.</summary>
    public static void OpenLink(string link)
    {
        if (!Lock.IsLocked && LinkOpened is { } opened) opened(link);
        else _pendingLink = link;
    }

    /// <summary>The link that arrived before anything was listening, once. Nothing while Elio is locked: it is handed over at unlocking.</summary>
    public static string? TakePendingLink()
    {
        if (Lock.IsLocked) return null;

        string? link = _pendingLink;
        _pendingLink = null;
        return link;
    }

    /// <summary>Replaces opening a web page, in tests.</summary>
    public static Func<Uri, Task>? OpenUrlOverride { get; set; }

    /// <summary>Opens a page in the user's browser.</summary>
    public static async Task OpenUrlAsync(Avalonia.Visual? anchor, Uri url)
    {
        if (OpenUrlOverride is { } open) await open(url);
        else if (Avalonia.Controls.TopLevel.GetTopLevel(anchor)?.Launcher is { } launcher) await launcher.LaunchUriAsync(url);
    }

    private static readonly Lazy<List<HostEntry>> DetectedShells = new(Connections.LocalShells.Detect);

    /// <summary>The shells installed on this computer, the usual one first. Empty where there are none to open, as on a phone.</summary>
    public static IReadOnlyList<HostEntry> LocalShells => DetectedShells.Value;

    /// <summary>
    /// Whether the app is laid out for a phone: one column, a key row above the keyboard, touch that scrolls.
    /// ELIO_MOBILE_UI=1 turns it on elsewhere, for working on that layout without a phone.
    /// </summary>
    public static bool MobileLayout { get; } =
        OperatingSystem.IsAndroid() || OperatingSystem.IsIOS() || Environment.GetEnvironmentVariable("ELIO_MOBILE_UI") == "1";

    public static Vault Vault { get; } = new();

    public static AppSettings Settings { get; } = SettingsStore.Load();

    public static event Action? SettingsChanged;

    /// <summary>The saved servers and commands. One copy for every window, so no window can overwrite another's changes.</summary>
    public static HostLibrary Library { get; } = HostStore.Load();

    public static event Action? LibraryChanged;

    public static void SaveLibrary()
    {
        HostStore.Save(Library);
        LibraryChanged?.Invoke();
    }

    public static TerminalAppearance Appearance => new(
        Settings.FontSize,
        Settings.Ligatures,
        TerminalTheme.Named(Settings.FollowSystemTheme && !SystemIsDark ? Settings.LightTheme : Settings.Theme),
        Settings.CopyOnSelect,
        Settings.RightClickPaste,
        _highlights ??= CompileHighlights());

    /// <summary>Whether the operating system is set to dark. True where that cannot be told, since dark is Elio's own look.</summary>
    public static bool SystemIsDark =>
        Avalonia.Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant != Avalonia.Platform.PlatformThemeVariant.Light;

    /// <summary>Tells every terminal to take its colors again, after the operating system went light or dark.</summary>
    internal static void SystemAppearanceChanged()
    {
        if (Settings.FollowSystemTheme) SettingsChanged?.Invoke();
    }

    public static void SaveSettings()
    {
        _highlights = null;
        SettingsStore.Save(Settings);
        SettingsChanged?.Invoke();
    }

    private static List<(Regex Pattern, uint Color)> CompileHighlights()
    {
        var compiled = new List<(Regex Pattern, uint Color)>();
        foreach (HighlightRule rule in Settings.Highlights)
        {
            try
            {
                // Output comes from the server, so a pattern must never be able to stall on it.
                compiled.Add((new Regex(rule.Pattern, RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), rule.Color));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // A pattern that can't be used costs that one rule, not the terminal.
            }
        }

        return compiled;
    }

    private static readonly string HistoryPath = Path.Combine(ElioPaths.DataDirectory, "history.txt");

    /// <summary>Commands typed before, offered again while typing when the user has turned that on.</summary>
    public static Elio.Terminal.CommandHistory History { get; } = LoadHistory();

    private static Elio.Terminal.CommandHistory LoadHistory()
    {
        var history = new Elio.Terminal.CommandHistory();
        history.Load(HistoryPath);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SaveHistory();
        return history;
    }

    /// <summary>
    /// Gives a local shell something to suggest from the start: what that shell itself remembers having run. Done once,
    /// while Elio knows of no commands for it, and only when the user has asked for it in the settings.
    /// </summary>
    public static void SeedHistory(HostEntry host)
    {
        if (host.Kind != HostKind.Local || !Settings.SuggestCommands || !Settings.SeedSuggestionsFromShellHistory) return;

        string scope = host.Id.ToString("N");
        if (History.Commands(scope).Count > 0) return;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string shell = host.Address.ToLowerInvariant();
        string? file =
            shell.Contains("pwsh") || shell.Contains("powershell")
                ? (OperatingSystem.IsWindows()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "PowerShell", "PSReadLine", "ConsoleHost_history.txt")
                    : Path.Combine(home, ".local", "share", "powershell", "PSReadLine", "ConsoleHost_history.txt"))
            : shell.Contains("zsh") ? Path.Combine(home, ".zsh_history")
            : shell.Contains("bash") && !shell.Contains("wsl") ? Path.Combine(home, ".bash_history")
            : null;
        if (file is null || !File.Exists(file)) return;

        try
        {
            Remember(File.ReadLines(file).TakeLast(SeededCommands), scope);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without it the shell simply starts with nothing to suggest.
        }
    }

    /// <summary>How many commands are asked of a server's own history when it is first connected to.</summary>
    public const int SeededCommands = 400;

    /// <summary>
    /// Whether there is still too little to suggest for a server, so that its own history is worth asking for.
    /// Never unless the user turned that on: it copies what was typed on the server into a file on this computer.
    /// </summary>
    public static bool WantsHistoryFor(HostEntry host) =>
        Settings.SuggestCommands && Settings.SeedSuggestionsFromShellHistory && History.Commands(host.Id.ToString("N")).Count < 30;

    /// <summary>Takes lines of a shell's history file, oldest first, so that the newest ends up the most recent here too.</summary>
    public static void Remember(IEnumerable<string> lines, string scope)
    {
        foreach (string line in lines)
        {
            // zsh may put ": when:how long;" in front of each command.
            string command = line.StartsWith(": ", StringComparison.Ordinal) && line.IndexOf(';') is > 0 and var semicolon ? line[(semicolon + 1)..] : line;
            History.Add(command, scope);
        }
    }

    public static void SaveHistory()
    {
        if (History.Modified) History.Save(HistoryPath);
    }

    public static SessionLayout LoadLayout() => LayoutStore.Load();

    private static readonly JsonFileStore<WorkspaceFile> WorkspaceStore = Stores.Workspaces();

    /// <summary>The layouts saved under a name. One copy for every window, kept on this device only.</summary>
    public static WorkspaceFile Workspaces { get; } = WorkspaceStore.Load();

    public static void SaveWorkspaces() => WorkspaceStore.Save(Workspaces);

    /// <summary>Records the tabs open in every window, so a restart can bring them back.</summary>
    public static void SaveLayout()
    {
        var layout = new SessionLayout();
        foreach (WorkspaceView workspace in WorkspaceView.All) layout.Tabs.AddRange(workspace.ExportTabs());
        LayoutStore.Save(layout);
        SaveHistory();
    }
}

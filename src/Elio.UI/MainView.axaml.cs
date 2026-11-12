using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Elio.Core;

namespace Elio.UI;

public partial class MainView : UserControl
{
    private const double SidebarWidth = 250;
    private const double PhoneDrawerWidth = 300;
    private const double DefaultFontSize = 14;

    public DialogHost Dialogs { get; } = new();

    public WorkspaceView Workspace { get; }

    public HostListView HostList { get; }

    public MainView()
    {
        InitializeComponent();

        // Arabic and Persian read from the right, and the whole layout mirrors with them.
        FlowDirection = L.RightToLeft ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;

        Workspace = new WorkspaceView(Dialogs) { RunCommand = Run };
        StartPlugins();
        HostList = new HostListView(Dialogs);
        Workspace.FindHost = HostList.Find;
        HostList.ConnectRequested += host => _ = Workspace.OpenAsync(host);
        HostList.ActionRequested += Run;
        HostList.FilesRequested += host => _ = Workspace.OpenFilesAsync(host);
        HostList.Localtonet.ConnectRequested += host => _ = Workspace.OpenAsync(host);
        HostList.Localtonet.FilesRequested += host => _ = Workspace.OpenFilesAsync(host);
        HostList.FilesWithAnotherRequested += host => _ = OpenFilesWithAnotherAsync(host);
        HostList.Localtonet.FilesWithAnotherRequested += host => _ = OpenFilesWithAnotherAsync(host);
        HostList.OpenManyRequested += hosts =>
        {
            _ = Workspace.OpenGridAsync(hosts);
            HostList.ClearSelection();
        };
        HostList.RunManyRequested += (hosts, command) => _ = RunOnManyAsync(hosts, command);
        HostList.SnippetRequested += snippet => Workspace.TypeInActive(snippet.Command);
        HostList.Localtonet.OpenSync = ShowSyncAsync;
        AppServices.LinkOpened += OpenLink;

        // AI tools work in a process of their own; this is how the window gets to say what they did.
        if (!AppServices.MobileLayout)
        {
            // The line says that something happened; the column it opens shows the commands and what they printed.
            AiRuns.Watch();
            AiActivity.Watch(command =>
            {
                if (Workspace.AiPanelOpen) return;
                Workspace.ShowNotice(
                    L.T("An AI tool ran a command on {0}: {1}", command.Server, command.Command),
                    L.T("Show all"),
                    Workspace.ShowAiPanel);
            });
        }
        if (AppServices.TakePendingLink() is { } waiting) OpenLink(waiting);

        FontFamily = ElioStyle.Font;
        FontSize = ElioStyle.BaseFontSize;

        // A window paints what is behind this view, and may let the desktop show through. A phone has no window.
        if (AppServices.MobileLayout) Background = ElioColors.Window;

        Root.Children.Add(AppServices.MobileLayout ? PhoneLayout() : WideLayout());
        Root.Children.Add(Dialogs);

        // Last, so it lies over everything, an open dialog included.
        AddLockScreen();
    }

    private Grid WideLayout()
    {
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions($"{SidebarWidth},*") };
        Grid.SetColumn(Workspace, 1);
        layout.Children.Add(HostList);
        layout.Children.Add(Workspace);
        return layout;
    }

    // One thing at a time: the terminal fills the screen and the server list slides over it from a button.
    private Grid PhoneLayout()
    {
        HostList.HorizontalAlignment = HorizontalAlignment.Left;
        HostList.Width = PhoneDrawerWidth;
        HostList.IsVisible = true;

        // The list's own fill is a tint meant to lie on the window's backdrop. Over the terminal it needs something solid under it.
        HostList.Background = ElioColors.Window;

        var scrim = new Border { Background = ElioColors.Scrim, IsVisible = true };
        Button toggle = ElioStyle.IconButton(ElioStyle.Icon(ElioStyle.IconMenu, ElioColors.Text), null, 30);
        toggle.Width = 34;
        toggle.Margin = new Thickness(6, 4, 0, 0);
        toggle.HorizontalAlignment = HorizontalAlignment.Left;
        toggle.VerticalAlignment = VerticalAlignment.Top;
        toggle.Focusable = false;
        toggle.Tag = "drawer-toggle";

        void Show(bool open) => HostList.IsVisible = scrim.IsVisible = open;

        toggle.Click += (_, _) => Show(!HostList.IsVisible);
        scrim.PointerPressed += (_, _) => Show(false);
        HostList.ConnectRequested += _ => Show(false);
        HostList.FilesRequested += _ => Show(false);
        HostList.OpenManyRequested += _ => Show(false);
        HostList.Localtonet.ConnectRequested += _ => Show(false);
        HostList.Localtonet.FilesRequested += _ => Show(false);
        HostList.FilesWithAnotherRequested += _ => Show(false);
        HostList.Localtonet.FilesWithAnotherRequested += _ => Show(false);

        return new Grid { Children = { Workspace, toggle, scrim, HostList } };
    }

    // On a phone the keyboard covers the bottom of the screen. Everything moves up out of its way, so the
    // key row ends up resting on top of the keyboard. The system's own bars are already allowed for.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!AppServices.MobileLayout || TopLevel.GetTopLevel(this)?.InputPane is not { } keyboard) return;

        // The room is given back by what the keyboard says it has become, not by the rectangle it last
        // covered: an iPhone goes on reporting that rectangle after the keyboard has gone, and the app stayed
        // squeezed into the top half of the screen.
        keyboard.StateChanged += (_, e) =>
        {
            double covered = e.NewState == Avalonia.Controls.Platform.InputPaneState.Open ? e.EndRect.Height : 0;

            // Never more than the keyboard can really take, whatever is reported.
            Root.Margin = new Thickness(0, 0, 0, Math.Clamp(covered, 0, Bounds.Height * 0.7));
        };
    }

    /// <summary>Brings back the tabs from last time, if the user wants that.</summary>
    public void RestoreSession()
    {
        if (AppServices.Settings.RestoreSessions) Workspace.Restore(AppServices.LoadLayout(), HostList.Find);
    }

    private void Run(string action)
    {
        switch (action)
        {
            case "palette": _ = ShowPaletteAsync(); break;
            case "new-tab": _ = ShowHostPickerAsync(); break;
            case "local-shell": OpenLocalShell(); break;
            case "add-server": _ = HostList.AddAsync(); break;
            case "import-ssh": ShowImportResult(L.T("OpenSSH import"), HostList.ImportSshConfig); break;
            case "settings": _ = ShowSettingsAsync(); break;
            case "sync": _ = ShowSyncAsync(); break;
            case "keychain": _ = ShowKeychainAsync(); break;
            case "shortcuts": _ = ShowShortcutsAsync(); break;
            case "workspaces": _ = ShowWorkspacesAsync(); break;
            case "font-larger": ChangeFontSize(AppServices.Settings.FontSize + 1); break;
            case "font-smaller": ChangeFontSize(AppServices.Settings.FontSize - 1); break;
            case "font-reset": ChangeFontSize(DefaultFontSize); break;
        }
    }

    private static void ChangeFontSize(double size)
    {
        AppServices.Settings.FontSize = Math.Clamp(size, 8, 32);
        AppServices.SaveSettings();
    }

    /// <summary>Lets the user run one command on many servers, with these already ticked.</summary>
    public Task RunOnManyAsync(IReadOnlyList<HostEntry> preselected, string? command) =>
        Dialogs.RunOnHostsAsync(HostList.Hosts.Where(IsSsh).ToList(), preselected.Where(IsSsh).ToList(), command, Workspace.RunOnHostAsync);

    // Running a command without a shell is something only SSH offers.
    private static bool IsSsh(HostEntry host) => host.Kind == HostKind.Ssh;

    private void ShowImportResult(string title, Func<string> import)
    {
        string note;
        try
        {
            note = import();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note = ex.Message;
        }

        _ = Dialogs.ShowMessageAsync(title, note);
    }

    public async Task ShowSettingsAsync()
    {
        if (await Dialogs.EditSettingsAsync(AppServices.Settings)) AppServices.SaveSettings();
    }

    /// <summary>Opens this computer's usual shell in a new tab.</summary>
    public void OpenLocalShell()
    {
        if (AppServices.LocalShells.Count > 0) _ = Workspace.OpenAsync(AppServices.LocalShells[0]);
    }

    private IEnumerable<PaletteItem> HostItems() =>
        HostList.Hosts.Select(h => new PaletteItem(h.Name, HostListView.Describe(h), () => _ = Workspace.OpenAsync(h)))
            .Concat(AppServices.Localtonet.Devices.Select(d => new PaletteItem(d.Name, L.T("Localtonet device"),() => _ = Workspace.OpenAsync(d))))
            .Concat(AppServices.LocalShells.Select(s => new PaletteItem(s.Name, L.T("Local shell"),() => _ = Workspace.OpenAsync(s))));

    /// <summary>Asks which other server to put beside this one, and opens the files of both to copy between them.</summary>
    public async Task OpenFilesWithAnotherAsync(HostEntry? host)
    {
        if (host is null) return;

        if (!IsSsh(host))
        {
            await Dialogs.ShowMessageAsync(L.T("Files"), L.T("The file panel only opens for SSH servers."));
            return;
        }

        // The same server may be picked again: that is two folders of one machine side by side.
        HostEntry? other = null;
        List<PaletteItem> items = HostList.Hosts.Where(IsSsh).Select(h => new PaletteItem(h.Name, HostListView.Describe(h), () => other = h))
            .Concat(AppServices.Localtonet.Devices.Where(IsSsh).Select(d => new PaletteItem(d.Name, L.T("Localtonet device"), () => other = d)))
            .ToList();

        if (await Dialogs.ChooseAsync(items, L.T("Server to copy files to and from")) is not PaletteItem chosen) return;
        chosen.Run();
        if (other is not null) await Workspace.OpenFilesBetweenAsync(host, other);
    }

    public Task ShowSyncAsync() => Dialogs.ShowSyncAsync(AppServices.Sync, () => HostList.Localtonet.SignInAsync(offerSync: false));

    /// <summary>
    /// Takes a link the system handed over: the QR code a device that already syncs shows, read by the phone's own
    /// camera. It opens the step that adds this device, with the code filled in and nothing done yet.
    /// </summary>
    private void OpenLink(string link) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (!Elio.Sync.SyncLink.TryParse(link, out _, out _)) return;

        _ = AppServices.Sync.IsOn
            ? Dialogs.ShowMessageAsync(L.T("Sync"), L.T("Sync is already on on this device. Turn it off here first to join another one."))
            : Dialogs.ShowSyncAsync(AppServices.Sync, () => HostList.Localtonet.SignInAsync(offerSync: false), link);
    });

    private async Task ShowHostPickerAsync()
    {
        if (await Dialogs.ChooseAsync(HostItems().ToList(), L.T("Server to connect to")) is PaletteItem item) item.Run();
    }

    /// <summary>Lists the containers Docker is running here and opens a shell in the chosen one.</summary>
    public async Task ShowDockerAsync()
    {
        List<HostEntry> containers;
        try
        {
            containers = await Elio.Connections.DockerContainers.ListAsync(CancellationToken.None);
        }
        catch (IOException ex)
        {
            await Dialogs.ShowMessageAsync("Docker", ex.Message);
            return;
        }

        if (containers.Count == 0)
        {
            await Dialogs.ShowMessageAsync("Docker", L.T("No containers are running."));
            return;
        }

        var items = containers.Select(c => new PaletteItem(c.Name, string.Join("  \u00b7  ", c.Tags), () => _ = Workspace.OpenAsync(c))).ToList();
        if (await Dialogs.ChooseAsync(items, L.T("Container to open a shell in")) is PaletteItem chosen) chosen.Run();
    }

    /// <summary>The commands that can have a shortcut, under the names the user knows them by.</summary>
    private static List<(string Action, string Name)> ShortcutActions() =>
    [
        ("palette", L.T("Command palette")),
        ("new-tab", L.T("Connect to a server")),
        ("local-shell", L.T("Open local shell")),
        ("split-right", L.T("Split right")),
        ("split-down", L.T("Split down")),
        ("close-pane", L.T("Close pane")),
        ("next-tab", L.T("Next tab")),
        ("previous-tab", L.T("Previous tab")),
        ("broadcast", L.T("Turn broadcast input on / off")),
        ("find", L.T("Find in output")),
        ("reopen-tab", L.T("Reopen closed tab")),
        ("last-tab", L.T("Last used tab")),
        ("zoom-pane", L.T("Zoom pane in / out")),
        ("clear", L.T("Clear screen and history")),
        ("group-tabs", L.T("Group tabs by repository")),
        ("previous-prompt", L.T("Go to the previous command")),
        ("next-prompt", L.T("Go to the next command")),
        ("copy-output", L.T("Copy the last command's output")),
        ("picture", L.T("Take a picture of this terminal")),
        ("history", L.T("Command history")),
        ("font-larger", L.T("Larger text")),
        ("font-smaller", L.T("Smaller text")),
        ("font-reset", L.T("Reset text size")),
        ("settings", L.T("Settings")),
        ("shortcuts", L.T("Keyboard shortcuts")),
    ];

    public async Task ShowShortcutsAsync()
    {
        if (await Dialogs.EditShortcutsAsync(AppServices.Settings, ShortcutActions())) AppServices.SaveSettings();
    }

    public async Task ShowPaletteAsync()
    {
        var items = new List<PaletteItem>
        {
            new(L.T("Split right"), L.T("Pane"), () => Workspace.SplitActive(Orientation.Horizontal)),
            new(L.T("Split down"), L.T("Pane"), () => Workspace.SplitActive(Orientation.Vertical)),
            new(L.T("Close pane"), L.T("Pane"), () => _ = Workspace.CloseActivePaneAsync()),
            new(L.T("Add server"), L.T("Servers"), () => _ = HostList.AddAsync()),
            new(L.T("Add group"), L.T("Servers"), () => _ = HostList.AddGroupAsync(null)),
            new(L.T("Open selected side by side"), L.T("Servers"), () => _ = Workspace.OpenGridAsync(HostList.SelectedHosts)),
            new(L.T("Run a command on several servers"), L.T("Servers"), () => _ = RunOnManyAsync(HostList.SelectedHosts, null)),
            new(L.T("Import servers…"), ImportSources, () => _ = ShowImportAsync()),
            new(L.T("Export servers to a file"), L.T("Servers"), () => _ = ExportServersAsync()),
            new(L.T("Save this layout as a workspace…"), L.T("Workspaces"), () => _ = SaveWorkspaceAsync()),
            new(L.T("Open workspace…"), L.T("Workspaces"), () => _ = ChooseWorkspaceToOpenAsync()),
            new(L.T("Rename workspace…"), L.T("Workspaces"), () => _ = RenameWorkspaceAsync()),
            new(L.T("Delete workspace…"), L.T("Workspaces"), () => _ = DeleteWorkspaceAsync()),
            new(L.T("Open files beside"), L.T("Pane"), () => _ = Workspace.OpenFilesBesideAsync()),
            new(L.T("Open files with another server…"), L.T("Pane"), () => _ = OpenFilesWithAnotherAsync(Workspace.ActiveHost)),
            new(L.T("Turn broadcast input on / off"), L.T("Pane"), Workspace.ToggleBroadcast),
            new(L.T("Save output / stop"), L.T("Pane"), Workspace.ToggleOutputLog),
            new(L.T("Find in output"), L.T("Pane"), Workspace.FindInActive),
            new(L.T("Zoom pane in / out"), L.T("Pane"), Workspace.ToggleZoom),
            new(L.T("Clear screen and history"), L.T("Pane"), Workspace.ClearActive),
            new(L.T("Reopen closed tab"), L.T("Pane"), Workspace.ReopenClosedTab),
            new(L.T("Last used tab"), L.T("Pane"), Workspace.SelectPreviousTab),
            new(L.T("Diagnose connection"), L.T("Pane"), () => _ = Workspace.DiagnoseActiveAsync()),
            new(L.T("Sync"), L.T("App"), () => _ = ShowSyncAsync()),
            new(L.T("Keychain"), L.T("App"), () => _ = ShowKeychainAsync()),
            new(L.T("Commands run by AI tools"), L.T("App"), Workspace.ShowAiPanel),
            AppServices.Localtonet.SignedIn
                ? new(L.T("Sign out of Localtonet"), "Localtonet", AppServices.Localtonet.SignOut)
                : new(L.T("Sign in with Localtonet"), "Localtonet", () => _ = HostList.Localtonet.SignInAsync()),
            new(L.T("Refresh Localtonet devices"), "Localtonet", () => _ = AppServices.Localtonet.RefreshAsync()),
            new(L.T("Get access keys for all devices…"), "Localtonet", () => _ = HostList.Localtonet.ImportKeysAsync()),
            new(L.T("Add another Localtonet account"), "Localtonet", () => _ = HostList.Localtonet.SignInAsync()),
            new(L.T("Docker containers"), L.T("Servers"), () => _ = ShowDockerAsync()),
            new(L.T("Keyboard shortcuts"), L.T("App"), () => _ = ShowShortcutsAsync()),
            new(L.T("Settings"), L.T("App"), () => _ = ShowSettingsAsync()),
            new(L.T("Group tabs by repository"), L.T("Pane"), Workspace.GroupTabs),
            new(L.T("Share this terminal / stop"), L.T("Pane"), () => _ = Workspace.ShareActiveAsync(allowInput: false)),
            new(L.T("Share and let others type / stop"), L.T("Pane"), () => _ = Workspace.ShareActiveAsync(allowInput: true)),
            new(L.T("Watch a shared terminal"), L.T("App"), () => _ = Workspace.JoinSharedAsync()),
        };
        items.Add(new(L.T("Go to the previous command"), L.T("Pane"), () => Workspace.GoToPrompt(older: true)));
        items.Add(new(L.T("Go to the next command"), L.T("Pane"), () => Workspace.GoToPrompt(older: false)));
        items.Add(new(L.T("Copy the last command's output"), L.T("Pane"), () => _ = Workspace.CopyLastOutputAsync()));
        items.Add(new(L.T("Mark commands in this shell (bash, zsh, PowerShell)"), L.T("Pane"), Workspace.MarkCommandsInActive));
        items.Add(new(L.T("Take a picture of this terminal"), L.T("Pane"), Workspace.TakePictureOfActive));
        items.Add(new(L.T("Command history"), L.T("Pane"), () => _ = Workspace.ShowHistoryAsync()));
        items.AddRange(PluginItems());
        items.AddRange(HostList.Snippets.Select(s => new PaletteItem(s.Name, L.T("Command"), () => Workspace.TypeInActive(s.Command))));
        items.AddRange(HostItems());

        if (await Dialogs.ChooseAsync(items, L.T("Search for a command or server")) is PaletteItem chosen) chosen.Run();
    }
}

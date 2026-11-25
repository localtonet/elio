using Avalonia.Threading;
using Elio.Core;

namespace Elio.UI;

/// <content>
/// What Elio was asked to open from outside the window: a link the system handed over (ssh://, elio://), or the
/// command line it was started with, such as "elio ssh deploy@web-1".
/// </content>
public partial class MainView
{
    private void HandleLaunch(LaunchRequest request) => Dispatcher.UIThread.Post(() => _ = HandleLaunchAsync(request));

    private async Task HandleLaunchAsync(LaunchRequest request)
    {
        switch (request.Kind)
        {
            case LaunchKind.NewTab:
                OpenLocalShell();
                break;
            case LaunchKind.Ssh when request.Target is { } target:
                await ConnectToTargetAsync(target, request.FromLink);
                break;
            case LaunchKind.Connect when request.Name is { } name:
                await ConnectByNameAsync(name, request.FromLink);
                break;
            case LaunchKind.Workspace when request.Name is { } name:
                await OpenWorkspaceByNameAsync(name, request.FromLink);
                break;
            case LaunchKind.Link when request.Link is { } link:
                await OpenSyncLinkAsync(link);
                break;
            case LaunchKind.Invalid:
                await Dialogs.ShowMessageAsync("Elio", request.Error ?? L.T("That is not a link Elio can open."));
                break;
        }
    }

    /// <summary>
    /// Takes the QR code a device that already syncs shows, read by the phone's own camera. It opens the step that
    /// adds this device, with the code filled in and nothing done yet.
    /// </summary>
    private Task OpenSyncLinkAsync(string link)
    {
        if (!Elio.Sync.SyncLink.TryParse(link, out _, out _)) return Task.CompletedTask;

        return AppServices.Sync.IsOn
            ? Dialogs.ShowMessageAsync(L.T("Sync"), L.T("Sync is already on on this device. Turn it off here first to join another one."))
            : Dialogs.ShowSyncAsync(AppServices.Sync, () => HostList.Localtonet.SignInAsync(offerSync: false), link);
    }

    /// <summary>
    /// Connects to an address: through the saved server that has it, so its login and settings are used, or else
    /// as a server of its own that is not saved. A link is followed only after the user agrees.
    /// </summary>
    private async Task ConnectToTargetAsync(SshTarget target, bool fromLink)
    {
        HostEntry? saved = LaunchRequest.FindSaved(HostList.Hosts, target, host =>
        {
            ResolvedHost resolved = ConnectionResolver.Resolve(host, AppServices.Library);
            return (resolved.User, resolved.Port > 0 ? resolved.Port : ConnectionResolver.DefaultSshPort);
        });

        string shown = LaunchRequest.ToSshLink(target)[(LaunchRequest.SshScheme.Length + 3)..];
        if (fromLink)
        {
            string question = saved is null
                ? L.T("A link asks Elio to connect to {0}. Connect?", shown)
                : L.T("A link asks Elio to connect to {0}, saved as \"{1}\". Connect?", shown, saved.Name);
            if (!await Dialogs.ConfirmAsync(L.T("Open link"), question, L.T("Connect"))) return;
        }

        if (saved is not null)
        {
            await Workspace.OpenAsync(saved);
            return;
        }

        // Like ssh, a connection that names no user goes as the user signed in here, unless the user says otherwise.
        string user = target.User;
        if (user.Length == 0)
        {
            if (await Dialogs.AskTextAsync(shown, L.T("User name"), Environment.UserName, L.T("Connect")) is not { Length: > 0 } typed) return;
            user = typed.Trim();
        }

        var host = new HostEntry
        {
            Name = $"{user}@{target.Host}",
            Kind = HostKind.Ssh,
            Address = target.Host,
            Port = target.Port,
            User = user,
            KeyPath = target.KeyPath is { Length: > 0 } key ? Path.GetFullPath(key) : null,
        };
        await Workspace.OpenAsync(host);
    }

    /// <summary>A saved server, a Localtonet device or a local shell, by its name.</summary>
    private HostEntry? FindByName(string name) =>
        HostList.Hosts.FirstOrDefault(h => h.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))
        ?? AppServices.Localtonet.Devices.FirstOrDefault(h => h.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))
        ?? AppServices.LocalShells.FirstOrDefault(h => h.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));

    private async Task ConnectByNameAsync(string name, bool fromLink)
    {
        if (FindByName(name) is not HostEntry host)
        {
            await Dialogs.ShowMessageAsync("Elio", L.T("There is no server called \"{0}\".", name));
            return;
        }

        if (fromLink && !await Dialogs.ConfirmAsync(L.T("Open link"), L.T("A link asks Elio to connect to \"{0}\". Connect?", host.Name), L.T("Connect"))) return;
        await Workspace.OpenAsync(host);
    }

    private async Task OpenWorkspaceByNameAsync(string name, bool fromLink)
    {
        SavedWorkspace? workspace = SavedWorkspaces.FirstOrDefault(w => w.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        if (workspace is null)
        {
            await Dialogs.ShowMessageAsync(L.T("Workspaces"), L.T("There is no workspace called \"{0}\".", name));
            return;
        }

        if (fromLink && !await Dialogs.ConfirmAsync(L.T("Open link"), L.T("A link asks Elio to open the workspace \"{0}\". Open it?", workspace.Name), L.T("Open"))) return;
        await OpenWorkspaceAsync(workspace);
    }
}

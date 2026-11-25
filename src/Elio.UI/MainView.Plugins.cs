using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Elio.Core;

namespace Elio.UI;

/// <content>Plugins: programs of the user's own that add commands to the palette.</content>
public partial class MainView : IPluginActions
{
    private PluginHost? _plugins;

    private void StartPlugins()
    {
        // Phones do not let an app start other programs.
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()) return;

        // A plugin is somebody's program, and it is in the list only because its folder is in the plugins
        // folder. It runs once the user has said so, and is asked about again if its plugin.json changes.
        var approvals = new PluginApprovals(AppServices.Vault);
        var plugins = new PluginHost(this) { IsApproved = approvals.Contains };
        _plugins = plugins;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => plugins.Dispose();
        _ = Task.Run(async () =>
        {
            try
            {
                IReadOnlyList<PluginInfo> found = await plugins.LoadAsync();

                // With the vault shut an answer could not be kept, so there is no use in asking.
                if (AppServices.Vault.IsLocked) return;

                foreach (PluginInfo plugin in found.Where(p => p.Approval.Length > 0 && !approvals.Contains(p.Approval)))
                {
                    bool allowed = await Dispatcher.UIThread.InvokeAsync(() => Dialogs.ConfirmAsync(
                        L.T("Allow this plugin?"),
                        L.T("The plugin \"{0}\" in the folder {1} is a program. If you allow it, Elio starts it now and every time it opens, by running:\n\n{2}\n\nAllow it only if you put it there yourself or trust who did.", plugin.Name, plugin.Folder, plugin.CommandLine),
                        L.T("Allow"),
                        cancelText: L.T("Not now")));
                    if (!allowed) continue;

                    approvals.Add(plugin.Approval);
                    await plugins.LoadAsync();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // No plugins then. Each plugin's own trouble is kept with the plugin.
            }
        });
    }

    private IEnumerable<PaletteItem> PluginItems() =>
        (_plugins?.Commands ?? []).Select(command => new PaletteItem(command.Title, L.T("Plugin: {0}", command.Plugin), () => _ = RunPluginAsync(command)));

    private async Task RunPluginAsync(PluginCommand command)
    {
        if (_plugins is not PluginHost plugins) return;

        try
        {
            PluginContext context = Workspace.DescribeForPlugins();
            await Task.Run(() => plugins.RunAsync(command, context));
        }
        catch (PluginException ex)
        {
            await Dialogs.ConfirmAsync(command.Plugin, ex.Message, L.T("Close"));
        }
    }

    // Plugins call these from their own threads.
    Task IPluginActions.SendTextAsync(string plugin, string text) => Dispatcher.UIThread.InvokeAsync(() => Workspace.SendToActive(text)).GetTask();

    Task IPluginActions.RunCommandAsync(string plugin, string command) => Dispatcher.UIThread.InvokeAsync(() => Workspace.TypeInActive(command)).GetTask();

    Task IPluginActions.NotifyAsync(string plugin, string message)
    {
        // The plugin goes on while the note is on screen.
        Dispatcher.UIThread.Post(() => _ = Dialogs.ConfirmAsync(plugin, message, L.T("Close")));
        return Task.CompletedTask;
    }

    Task IPluginActions.WriteClipboardAsync(string plugin, string text) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    });

    Task IPluginActions.OpenUrlAsync(string plugin, Uri url) => Dispatcher.UIThread.InvokeAsync(() => AppServices.OpenUrlAsync(this, url));

    Task<string?> IPluginActions.PromptAsync(string plugin, string title, string? placeholder) =>
        Dispatcher.UIThread.InvokeAsync(() => Dialogs.AskTextAsync(plugin, title, placeholder, L.T("Continue")));

    // What a plugin may know about the servers: what the server list shows, never a secret.
    Task<IReadOnlyList<PluginServerListing>> IPluginActions.ListServersAsync(string plugin) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        HostLibrary library = AppServices.Library;
        return (IReadOnlyList<PluginServerListing>)library.Hosts
            .OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(h =>
            {
                ResolvedHost resolved = ConnectionResolver.Resolve(h, library);
                return new PluginServerListing(
                    h.Name,
                    h.Kind.ToString().ToLowerInvariant(),
                    h.Address,
                    resolved.User.Length > 0 ? resolved.User : null,
                    library.Groups.FirstOrDefault(g => g.Id == h.GroupId)?.Name,
                    h.Tags.ToList());
            })
            .ToList();
    }).GetTask();

    Task<string?> IPluginActions.LastOutputAsync(string plugin) =>
        Dispatcher.UIThread.InvokeAsync(() => Workspace.ActiveSession?.LastCommandOutput()).GetTask();

    Task<bool> IPluginActions.ConnectAsync(string plugin, string server) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        if (FindByName(server) is not HostEntry host) return false;
        _ = Workspace.OpenAsync(host);
        return true;
    }).GetTask();

    Task IPluginActions.ShowTextAsync(string plugin, string title, string text) =>
        Dispatcher.UIThread.InvokeAsync(() => Dialogs.ShowTextAsync(title, text));

    Task<int?> IPluginActions.ChooseAsync(string plugin, string title, IReadOnlyList<string> choices) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        int? picked = null;
        List<PaletteItem> items = choices.Select((choice, index) => new PaletteItem(choice, plugin, () => picked = index)).ToList();
        if (await Dialogs.ChooseAsync(items, title) is PaletteItem chosen) chosen.Run();
        return picked;
    });
}

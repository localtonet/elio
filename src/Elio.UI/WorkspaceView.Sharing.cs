using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Input.Platform;
using Elio.Connections;
using Elio.Core;
using Elio.Localtonet;
using Elio.Terminal;

namespace Elio.UI;

/// <content>Letting someone else watch a terminal, watching one of theirs, and what plugins may know and do.</content>
public sealed partial class WorkspaceView
{
    /// <summary>
    /// Puts the piece in place that can hand a copy of the output to people watching. It has to be there from the
    /// start, and until sharing is turned on it only passes everything along.
    /// </summary>
    private static ITerminalTransport Shareable(TerminalPane pane, ITerminalTransport transport, int columns, int rows)
    {
        var shared = new SharedTransport(transport, columns, rows);
        pane.Shared = shared;
        shared.Share.ViewersChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateBar(pane));
        return shared;
    }

    /// <summary>Starts sharing the current terminal, or offers to stop if it is being shared.</summary>
    public async Task ShareActiveAsync(bool allowInput)
    {
        if (_current?.ActiveTerminal is not { Shared: SharedTransport shared } pane) return;

        SessionShare share = shared.Share;
        if (share.IsRunning)
        {
            if (await _dialogs.ConfirmAsync(L.T("Stop sharing"), L.T("People watching this terminal now: {0}. Their link stops working.", share.ViewerCount), L.T("Stop sharing"))) await StopSharingAsync(pane);
            UpdateBar(pane);
            return;
        }

        string reach = allowInput
            ? L.T("Anyone who has the link and the password will see this terminal and can type into it, until you stop sharing.")
            : L.T("Anyone who has the link and the password will see this terminal, until you stop sharing.");
        if (!await _dialogs.ConfirmAsync(L.T("Share this terminal"), reach, L.T("Share"))) return;

        // The terminal goes out through the sync server, so nothing is opened on this computer and the other person can
        // be anywhere. An account is not needed for that: when there is one it only says whose share this is. If the
        // server cannot be used, nothing is shared unless the user then chooses the network this computer is on: opening
        // a port to it is not what was asked for, so it is asked for.
        string link;
        try
        {
            share.Start(IPAddress.Loopback, 0, allowInput);
            Uri server = AppServices.Sync.RelayServer;
            Func<CancellationToken, Task<string>>? proof = AppServices.Localtonet.SignedIn ? cancellation => AppServices.Sync.ProofForAsync(server, cancellation) : null;
            ShareRelayHost relay = await ShareRelayHost.StartAsync(server, proof, share.Port, CancellationToken.None);
            pane.Relay = relay;

            // The relay going away ends the sharing, or the bar would go on saying it is shared.
            relay.Closed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = StopSharingAsync(pane));
            link = relay.Link;
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or HttpRequestException or System.Net.WebSockets.WebSocketException or TaskCanceledException or LocaltonetSignedOutException or ArgumentException)
        {
            await StopSharingAsync(pane);
            string question = L.T("The sync server could not be reached: {0}\n\nShare on your own network instead? Anyone on the same network who has the link and the password can then connect to this computer directly.", ex.Message);
            if (!await _dialogs.ConfirmAsync(L.T("Share this terminal"), question, L.T("Share on my network"))) return;

            // Only the one address the link names is listened on, not every network this computer happens to be in.
            if (AddressOnNetwork() is not IPAddress address)
            {
                await _dialogs.ConfirmAsync(L.T("Share this terminal"), L.T("This computer is not on a network that others could reach it through."), L.T("Close"));
                return;
            }

            try
            {
                share.Start(address, 0, allowInput);
            }
            catch (Exception local) when (local is SocketException or InvalidOperationException or ArgumentException)
            {
                await _dialogs.ConfirmAsync(L.T("Share this terminal"), local.Message, L.T("Close"));
                return;
            }

            link = SessionShare.FormatLink(address.ToString(), share.Port);
        }

        UpdateBar(pane);

        // The link alone lets nobody in. The password goes another way, so a link that leaks is not enough;
        // that is why each has a button of its own and nothing copies the two together.
        await _dialogs.ShowShareAsync(link, share.Password, async text =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        });
    }

    private static async Task StopSharingAsync(TerminalPane pane)
    {
        pane.Shared?.Share.Stop();
        if (pane.Relay is ShareRelayHost relay)
        {
            pane.Relay = null;
            await relay.DisposeAsync();
        }

        UpdateBar(pane);
    }

    /// <summary>
    /// The address other computers on the network reach this one at, as well as it can be told. Null when there is
    /// none, so that nothing is offered on an address nobody else can use.
    /// </summary>
    private static IPAddress? AddressOnNetwork()
    {
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                // An adapter with no way out is a virtual one, such as the ones virtual machines add.
                IPInterfaceProperties properties = adapter.GetIPProperties();
                if (properties.GatewayAddresses.Count == 0) continue;

                foreach (UnicastIPAddressInformation address in properties.UnicastAddresses)
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address)) return address.Address;
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            // Falls through to there being none.
        }

        return null;
    }

    /// <summary>Asks for a link and opens the terminal it leads to in a new tab.</summary>
    public async Task JoinSharedAsync()
    {
        string? link = await _dialogs.AskTextAsync(L.T("Watch a shared terminal"), L.T("Link"), null, L.T("Open"));
        if (string.IsNullOrWhiteSpace(link)) return;

        link = link.Trim();

        // A link that names a room goes through the relay, which is reached as a port on this computer from then on.
        ShareRelayViewer? relay = null;
        string address;
        string target = link;
        if (ShareRelayLink.TryParse(link, out Uri? joinUrl))
        {
            try
            {
                relay = await ShareRelayViewer.OpenAsync(link, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or SocketException or System.Net.WebSockets.WebSocketException or TaskCanceledException)
            {
                await _dialogs.ConfirmAsync(L.T("Watch a shared terminal"), ex.Message, L.T("Close"));
                return;
            }

            address = joinUrl.Host;
            target = SessionShare.FormatLink(IPAddress.Loopback.ToString(), relay.LocalPort);
        }
        else if (!SessionShare.TryParseLink(link, out address, out _))
        {
            await _dialogs.ConfirmAsync(L.T("Watch a shared terminal"), L.T("The share link is not valid."), L.T("Close"));
            return;
        }

        // Asked until it is right or the user gives up. The other side slows guesses down by itself.
        SharedSessionTransport? opened = null;
        string question = L.T("Password");
        while (opened is null)
        {
            string? password = await _dialogs.AskTextAsync(L.T("Watch a shared terminal"), question, null, L.T("Open"));
            if (string.IsNullOrWhiteSpace(password))
            {
                if (relay is not null) await relay.DisposeAsync();
                return;
            }

            try
            {
                opened = await SharedSessionTransport.ConnectAsync(target, password, CancellationToken.None);
            }
            catch (WrongSharePasswordException ex)
            {
                question = ex.Message;

                // The relay carries one connection for each time it is opened, so another try needs another.
                if (relay is not null)
                {
                    await relay.DisposeAsync();
                    try
                    {
                        relay = await ShareRelayViewer.OpenAsync(link, CancellationToken.None);
                        target = SessionShare.FormatLink(IPAddress.Loopback.ToString(), relay.LocalPort);
                    }
                    catch (Exception again) when (again is IOException or SocketException or System.Net.WebSockets.WebSocketException or TaskCanceledException)
                    {
                        await _dialogs.ConfirmAsync(L.T("Watch a shared terminal"), again.Message, L.T("Close"));
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or FormatException)
            {
                if (relay is not null) await relay.DisposeAsync();
                await _dialogs.ConfirmAsync(L.T("Watch a shared terminal"), ex.Message, L.T("Close"));
                return;
            }
        }

        SharedSessionTransport transport = opened;
        var host = new HostEntry { Kind = HostKind.Local, Name = L.T("Shared session") + " \u00b7 " + address, AutoReconnect = false };
        TerminalPane pane = CreatePane(host, interactive: true, null, (_, _, _) => Task.FromResult<ITerminalTransport>(transport));
        AddTab(pane);
        LayoutChanged();
    }

    /// <summary>
    /// Lists the commands entered before, this server's first, to pick one and have it typed. It is typed without
    /// Enter, so it can be looked over or changed before it runs.
    /// </summary>
    public async Task ShowHistoryAsync()
    {
        if (_current?.ActiveTerminal is not TerminalPane active) return;

        string scope = active.Host.Id.ToString("N");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<PaletteItem>();
        foreach (string command in AppServices.History.Commands(scope).Concat(AppServices.History.Commands()))
        {
            if (!seen.Add(command)) continue;
            string text = command;
            items.Add(new PaletteItem(text, null, () => SendToActive(text)));
        }

        if (items.Count == 0)
        {
            await _dialogs.ShowMessageAsync(L.T("Command history"), L.T("Nothing yet. Commands you enter in a terminal are remembered here."));
            return;
        }

        if (await _dialogs.ChooseAsync(items, L.T("Search command history")) is PaletteItem chosen) chosen.Run();
    }

    /// <summary>Takes a picture of the terminal in front.</summary>
    public void TakePictureOfActive()
    {
        if (_current?.ActiveTerminal is TerminalPane active) _ = TakePictureAsync(active);
    }

    /// <summary>
    /// Saves what a terminal shows as a picture in the user's pictures folder and puts it on the clipboard as well,
    /// ready to paste into a chat or a ticket.
    /// </summary>
    private async Task TakePictureAsync(TerminalPane pane)
    {
        TopLevel? window = TopLevel.GetTopLevel(pane.View);
        double scale = window?.RenderScaling ?? 1;
        var size = new PixelSize((int)Math.Ceiling(pane.View.Bounds.Width * scale), (int)Math.Ceiling(pane.View.Bounds.Height * scale));
        if (size.Width <= 0 || size.Height <= 0) return;

        RenderTargetBitmap picture;
        string path;
        try
        {
            // Drawn at the screen's own sharpness, so it is not blurry on a dense display.
            picture = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            picture.Render(pane.View);

            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string folder = Path.Combine(pictures.Length > 0 ? pictures : ElioPaths.DownloadDirectory, "Elio");
            Directory.CreateDirectory(folder);

            string name = string.Concat(pane.Host.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
            path = Path.Combine(folder, $"{name} {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
            picture.Save(path, PngBitmapEncoderOptions.Default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            pane.Note = ex.Message;
            UpdateBar(pane);
            return;
        }

        async Task CopyAsync()
        {
            if (window?.Clipboard is not { } clipboard) return;

            // The clipboard hands the picture over only when something asks to paste it, which can be long after this
            // window's copy of it is gone. So it gets a copy of its own, read back from the file, which stays until the
            // next picture replaces it. Flushing leaves the picture there even after Elio closes.
            Bitmap onClipboard = new(path);
            await clipboard.SetBitmapAsync(onClipboard);
            await clipboard.FlushAsync();
            Interlocked.Exchange(ref _pictureOnClipboard, onClipboard)?.Dispose();
        }

        void Open(bool folderOnly)
        {
            string target = folderOnly ? Path.GetDirectoryName(path)! : path;
            if (window is not null) _ = window.Launcher.LaunchUriAsync(new Uri(target));
        }

        try
        {
            // On the clipboard straight away, and shown, so it can be checked before it is pasted anywhere.
            try
            {
                await CopyAsync();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
            {
                // The file is saved all the same, and the window that follows offers to copy again.
            }

            pane.Note = L.T("Picture saved: {0}", path);
            UpdateBar(pane);
            await _dialogs.ShowPictureAsync(L.T("Picture of the terminal"), picture, path, CopyAsync, Open);
        }
        finally
        {
            picture.Dispose();
        }
    }

    private static Bitmap? _pictureOnClipboard;

    /// <summary>Scrolls the current terminal to the command before or after the one in view.</summary>
    public void GoToPrompt(bool older) => _current?.ActiveTerminal?.View.Session?.ScrollToPrompt(older);

    /// <summary>Copies what the last finished command printed, when the shell marks its commands.</summary>
    public async Task CopyLastOutputAsync()
    {
        if (_current?.ActiveTerminal?.View.Session?.SelectLastCommandOutput() is not string output) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(output);
    }

    /// <summary>
    /// Types the line that makes a shell mark where each command starts and ends. That is what going from command to
    /// command and copying a command's output rely on. It lasts until that shell exits.
    /// </summary>
    public void MarkCommandsInActive()
    {
        if (_current?.ActiveTerminal is not TerminalPane active) return;

        // A server's shell cannot be seen from here, and bash is the one most of them run.
        string line = ShellIntegration.For(active.Host.Kind == HostKind.Local ? active.Host.Address : "bash") ?? ShellIntegration.Bash;
        active.View.Type(line + (char)13);
        active.View.Focus();
    }

    /// <summary>Brings tabs working in the same repository together.</summary>
    public void GroupTabs() => GroupTabsByRepository();

    /// <summary>Types into the current terminal exactly what is given, without pressing Enter.</summary>
    public void SendToActive(string text)
    {
        if (_current?.ActiveTerminal is not TerminalPane active) return;
        active.View.Type(text);
        active.View.Focus();
    }

    /// <summary>What a plugin is told about where the user is. Never anything secret.</summary>
    public PluginContext DescribeForPlugins()
    {
        if (_current?.Active is not ContentPane pane) return new PluginContext();

        HostEntry host = pane.Host;
        var server = new PluginServer(host.Name, host.Kind == HostKind.Local ? string.Empty : host.Address, Resolve(host).User, host.Kind.ToString().ToLowerInvariant());
        return new PluginContext(server, (pane as TerminalPane)?.CurrentDirectory);
    }
}

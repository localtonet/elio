using System.Text.RegularExpressions;
using Elio.Connections;
using Elio.Core;
using Elio.Terminal;

namespace Elio.UI;

/// <content>Local shells that outlive the window: they run in a small background process and are picked up again.</content>
public sealed partial class WorkspaceView
{
    /// <summary>
    /// Opens a local shell. With the setting on it runs in Elio's background process, so that closing the window, or
    /// replacing the program with a newer one, leaves it running for the next window to find.
    /// </summary>
    private static async Task<ITerminalTransport> OpenLocalShellAsync(TerminalPane pane, int columns, int rows, CancellationToken cancellation)
    {
        HostEntry host = pane.Host;
        string? directory = pane.StartDirectory ?? host.Directory;
        Dictionary<string, string> environment = EnvironmentFor(host);

        bool kept = AppServices.Settings.KeepShellsRunning && !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();
        if (kept && Environment.ProcessPath is string program)
        {
            try
            {
                if (await HostedShellTransport.EnsureHostAsync(program, cancellation))
                {
                    if (pane.ShellId is string saved)
                    {
                        try
                        {
                            HostedShellTransport found = await HostedShellTransport.AttachAsync(saved, columns, rows, cancellation);
                            pane.Hosted = found;
                            return new ReplayedShell(found);
                        }
                        catch (ShellGoneException)
                        {
                            // It ended while nobody was looking. A new one takes its place.
                        }
                    }

                    HostedShellTransport opened = await HostedShellTransport.OpenAsync(Guid.NewGuid().ToString("N"), host.Address, columns, rows, directory, environment, cancellation);
                    pane.ShellId = opened.Id;
                    pane.Hosted = opened;
                    return opened;
                }
            }
            catch (ShellHostUnavailableException)
            {
                // The shell is opened the ordinary way instead.
            }
        }

        pane.ShellId = null;
        pane.Hosted = null;
        return await Task.Run(() => LocalShellTransport.Start(host.Address, columns, rows, directory, environment), cancellation);
    }

    /// <summary>Ends a kept shell for good, for when the user closes its pane rather than the window.</summary>
    private static async Task EndKeptShellAsync(ContentPane pane)
    {
        if (pane is not TerminalPane { Hosted: HostedShellTransport hosted } terminal) return;

        terminal.Hosted = null;
        terminal.ShellId = null;
        try
        {
            await hosted.CloseAsync();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The background process is gone, and the shell with it.
        }
    }

    /// <summary>Ends the kept shells no tab came back for. Left alone they would run unseen for ever.</summary>
    private void EndForgottenShells()
    {
        if (!AppServices.Settings.KeepShellsRunning) return;

        var wanted = All.SelectMany(w => w._tabs).SelectMany(t => t.AllPanes).OfType<TerminalPane>().Select(p => p.ShellId).OfType<string>().ToHashSet();
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (HostedShell shell in await HostedShellTransport.ListAsync(CancellationToken.None))
                {
                    if (wanted.Contains(shell.Id)) continue;

                    HostedShellTransport orphan = await HostedShellTransport.AttachAsync(shell.Id, 80, 24, CancellationToken.None);
                    await orphan.CloseAsync();
                }
            }
            catch (IOException)
            {
                // Nothing is running in the background, so nothing was forgotten.
            }
        });
    }

    /// <summary>
    /// Gives a terminal the screen a kept shell had, with the questions taken out. Programs ask the terminal things, such
    /// as where the cursor is, and the answers are typed back: replaying an old question would type an answer into
    /// whatever the shell is running now.
    /// </summary>
    private sealed partial class ReplayedShell(HostedShellTransport inner) : ITerminalTransport
    {
        private byte[]? _replay;
        private int _served;
        private bool _replayed;

        public bool ConnectionLost => inner.ConnectionLost;

        public string? Description => inner.Description;

        // Cursor position, device attributes, mode and colour queries, window size reports, terminal name and settings.
        [GeneratedRegex(@"\x1b\[[?>=]?[0-9;]*[cn]|\x1b\[\?[0-9;]*\$p|\x1b\[[?>]?[0-9;]*u|\x1b\[1[4-9]t|\x1b\[>[0-9;]*q|\x1bZ|\x1b\][0-9;]*;\?(\x07|\x1b\\)|\x1bP[$+]q[^\x1b]*\x1b\\")]
        private static partial Regex Questions();

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (!_replayed)
            {
                byte[] raw = new byte[inner.ReplayLength];
                int filled = 0;
                while (filled < raw.Length)
                {
                    int read = await inner.ReadAsync(raw.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    filled += read;
                }

                // Latin-1 keeps every byte as one character, so the text can be searched and turned back unchanged.
                string text = System.Text.Encoding.Latin1.GetString(raw, 0, filled);
                _replay = System.Text.Encoding.Latin1.GetBytes(Questions().Replace(text, string.Empty));
                _replayed = true;
            }

            if (_replay is not null)
            {
                int count = Math.Min(buffer.Length, _replay.Length - _served);
                if (count > 0)
                {
                    _replay.AsMemory(_served, count).CopyTo(buffer);
                    _served += count;
                    return count;
                }

                _replay = null;
            }

            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => inner.WriteAsync(data, cancellationToken);

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken) => inner.ResizeAsync(columns, rows, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

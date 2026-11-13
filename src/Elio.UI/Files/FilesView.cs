using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Elio.Connections;
using Elio.Core;

namespace Elio.UI.Files;

/// <summary>A server as the file screen knows it: what to call it and how to reach it.</summary>
public sealed record FileServer(string Name, Func<CancellationToken, Task<SftpConnection>> Connect);

/// <summary>
/// This computer on the left, the server on the right, and what is moving between them below. The left side can
/// be a second server instead, and then files go straight from one server to the other.
/// </summary>
public sealed class FilesView : UserControl
{
    private const double SplitterSize = 3;
    private const double TransfersHeight = 170;

    /// <summary>The connection a panel lists folders on. Transfers have their own, so browsing stays quick during a large one.</summary>
    private sealed class Browser(Func<CancellationToken, Task<SftpConnection>> connect) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private SftpConnection? _connection;

        public async Task<SftpConnection> GetAsync(CancellationToken cancellation)
        {
            await _gate.WaitAsync(cancellation);
            try
            {
                if (_connection is { IsConnected: true }) return _connection;
                _connection?.Dispose();
                _connection = await connect(cancellation);
                return _connection;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            _connection?.Dispose();
            _connection = null;
        }
    }

    private readonly DialogHost _dialogs;
    private readonly Browser _browse;
    private readonly Browser? _browseOther;
    private readonly StackPanel _editRows = new() { Spacing = 2 };
    private readonly Dictionary<RemoteEdit, TextBlock> _edits = [];
    private readonly HashSet<TransferJob> _refreshed = [];
    private FilePanel _lastUsed = null!;

    public FilesView(Func<CancellationToken, Task<SftpConnection>> connect, DialogHost dialogs) : this(new FileServer(string.Empty, connect), null, dialogs)
    {
    }

    /// <param name="server">The server on the right.</param>
    /// <param name="other">The server on the left, or null for this computer.</param>
    /// <param name="sameServer">Both sides are the same machine, reached over two connections.</param>
    public FilesView(FileServer server, FileServer? other, DialogHost dialogs, bool sameServer = false)
    {
        _dialogs = dialogs;
        _browse = new Browser(server.Connect);

        if (other is null)
        {
            Queue = new TransferQueue(server.Connect) { ResolveConflict = ResolveConflictAsync };
            Local = new FilePanel(new LocalFileSource(), dialogs);
            Remote = new FilePanel(new RemoteFileSource(_browse.GetAsync), dialogs);

            Local.TransferRequested += rows => Upload(rows.Select(r => r.Path).ToList());
            Remote.TransferRequested += Download;
            Local.RowsDropped += Download;
            Remote.RowsDropped += rows => Upload(rows.Select(r => r.Path).ToList());
            Remote.PathsDropped += Upload;
            Remote.EditRequested += row => _ = EditAsync(row, _browse);
            Transfers = new TransferListView(Queue);
        }
        else
        {
            _browseOther = new Browser(other.Connect);
            Queue = new TransferQueue(server.Connect, other.Connect) { ResolveConflict = ResolveConflictAsync, SameServer = sameServer };
            Local = new FilePanel(new RemoteFileSource(_browseOther.GetAsync), dialogs, other.Name, L.T("Copy to {0}", server.Name));
            Remote = new FilePanel(new RemoteFileSource(_browse.GetAsync), dialogs, server.Name, L.T("Copy to {0}", other.Name));
            Local.Counterpart = Remote;
            Remote.Counterpart = Local;

            Local.TransferRequested += rows => Copy(rows, TransferServer.Second);
            Remote.TransferRequested += rows => Copy(rows, TransferServer.First);
            Local.RowsDropped += rows => Receive(rows, TransferServer.Second);
            Remote.RowsDropped += rows => Receive(rows, TransferServer.First);
            Local.PathsDropped += paths => Queue.Upload(paths, Local.CurrentPath, TransferServer.Second);
            Remote.PathsDropped += Upload;
            Local.EditRequested += row => _ = EditAsync(row, _browseOther);
            Remote.EditRequested += row => _ = EditAsync(row, _browse);
            Transfers = new TransferListView(Queue, server.Name, other.Name);
        }

        _lastUsed = Remote;
        Queue.Changed += () => Dispatcher.UIThread.Post(RefreshFinishedTargets);

        // Hiding hidden files is one choice for both sides.
        Local.HideHiddenChanged += hide => Remote.HideHidden = hide;
        Remote.HideHiddenChanged += hide => Local.HideHidden = hide;

        // Where the key falls on neither list, such as on the transfers, search goes to the side that was used last.
        Local.GotFocus += (_, _) => _lastUsed = Local;
        Remote.GotFocus += (_, _) => _lastUsed = Remote;
        KeyDown += (_, e) =>
        {
            if (e.Handled || e.Key != Key.F || e.KeyModifiers != (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control)) return;
            e.Handled = true;
            ShowFind();
        };

        var panels = new Grid { ColumnDefinitions = new ColumnDefinitions($"*,{SplitterSize},*") };
        Grid.SetColumn(Remote, 2);
        var splitter = new GridSplitter { Background = ElioColors.Border, ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetColumn(splitter, 1);
        panels.Children.Add(Local);
        panels.Children.Add(splitter);
        panels.Children.Add(Remote);

        var edits = new Border { Padding = new Thickness(12, 2), Child = _editRows, Tag = "edit-rows" };

        var layout = new Grid { RowDefinitions = new RowDefinitions($"*,{SplitterSize},Auto,{TransfersHeight}") };
        Grid.SetRow(panels, 0);
        var rowSplitter = new GridSplitter { Background = ElioColors.Border, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        Grid.SetRow(rowSplitter, 1);
        Grid.SetRow(edits, 2);
        Grid.SetRow(Transfers, 3);
        layout.Children.Add(panels);
        layout.Children.Add(rowSplitter);
        layout.Children.Add(edits);
        layout.Children.Add(Transfers);
        Content = layout;
    }

    /// <summary>Gives the keyboard to the side that was used last, or the server's when neither was.</summary>
    public void FocusContents() => _lastUsed.FocusContents();

    /// <summary>Opens the search bar of the side that was used last, or of the server's when neither was.</summary>
    public void ShowFind() => _lastUsed.ShowFind();

    /// <summary>The left side: this computer, or the other server when there are two.</summary>
    public FilePanel Local { get; }

    public FilePanel Remote { get; }

    /// <summary>Whether the left side is a server too.</summary>
    public bool BetweenServers => _browseOther is not null;

    public TransferQueue Queue { get; }

    public TransferListView Transfers { get; }

    public IReadOnlyCollection<RemoteEdit> Edits => _edits.Keys;

    /// <summary>Loads both sides.</summary>
    public Task StartAsync() => StartAsync(connect: true, connectOther: true);

    /// <summary>Loads this computer's side and leaves the server for when the user asks.</summary>
    public Task StartWithoutConnectingAsync() => StartAsync(connect: false, connectOther: true);

    /// <summary>Loads the sides, leaving a server that would have to ask for a password for when the user asks.</summary>
    /// <param name="path">The folder to show on the right, instead of the one the server starts in.</param>
    public Task StartAsync(bool connect, bool connectOther, string? path = null)
    {
        Task Begin(FilePanel panel, bool now, string? at)
        {
            if (now) return at is null ? panel.StartAsync() : panel.NavigateAsync(at);

            panel.ShowNotice(L.T("Password required. Refresh to connect."));
            return Task.CompletedTask;
        }

        return Task.WhenAll(Begin(Local, connectOther || !BetweenServers, null), Begin(Remote, connect, path));
    }

    public void Upload(IReadOnlyList<string> localPaths) => Queue.Upload(localPaths, Remote.CurrentPath);

    public void Download(IReadOnlyList<FileRow> rows) => Queue.Download(rows.Select(r => r.Remote!).ToList(), Local.CurrentPath);

    /// <summary>Copies rows of one server's panel into the folder the other server's panel shows.</summary>
    public void Copy(IReadOnlyList<FileRow> rows, TransferServer from) =>
        Queue.Copy(rows.Select(r => r.Remote!).ToList(), (from == TransferServer.First ? Local : Remote).CurrentPath, from);

    // What lands on a server's panel comes from the server opposite, or from this computer's side of another file pane.
    private void Receive(IReadOnlyList<FileRow> rows, TransferServer on)
    {
        if (rows[0].Remote is null) Queue.Upload(rows.Select(r => r.Path).ToList(), PanelOf(on).CurrentPath, on);
        else Copy(rows, on == TransferServer.First ? TransferServer.Second : TransferServer.First);
    }

    private FilePanel PanelOf(TransferServer server) => server == TransferServer.First ? Remote : Local;

    private Task<(ConflictChoice Choice, bool ForAll)> ResolveConflictAsync(TransferJob job, string path) =>
        Dispatcher.UIThread.InvokeAsync(() => _dialogs.AskConflictAsync(path));

    private void RefreshFinishedTargets()
    {
        foreach (TransferJob job in Queue.Jobs.Where(j => j.IsFinished && _refreshed.Add(j)))
        {
            FilePanel target = job.Kind == TransferKind.Download ? Local : PanelOf(job.TargetServer);
            if (job.Target == target.CurrentPath) _ = target.RefreshAsync(searchSubfolders: false);
        }
    }

    private async Task EditAsync(FileRow row, Browser browser)
    {
        try
        {
            RemoteEdit edit = await RemoteEdit.StartAsync(row, browser.GetAsync, _dialogs);
            AddEditRow(edit);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await _dialogs.ShowMessageAsync(L.T("Could not start editing"), ex.Message);
        }
    }

    private void AddEditRow(RemoteEdit edit)
    {
        var text = new TextBlock { FontSize = 11, Foreground = ElioColors.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "edit-status" };
        void Show() => text.Text = $"{edit.Name}: {edit.Status}";
        edit.Changed += () => Dispatcher.UIThread.Post(Show);
        Show();

        var done = new Button { Content = L.T("Finish"), FontSize = ElioStyle.SmallFontSize, Padding = new Thickness(9, 1), CornerRadius = new CornerRadius(6), Tag = "edit-done" };
        var row = new DockPanel();
        done.Click += (_, _) =>
        {
            _editRows.Children.Remove(row);
            _edits.Remove(edit);
            edit.Dispose();
        };
        DockPanel.SetDock(done, Dock.Right);
        row.Children.Add(done);
        row.Children.Add(text);
        _editRows.Children.Add(row);
        _edits[edit] = text;
    }

    /// <summary>Ends the connections and throws away the working copies.</summary>
    public async Task CloseAsync()
    {
        foreach (RemoteEdit edit in _edits.Keys.ToList()) edit.Dispose();
        _edits.Clear();
        await Queue.DisposeAsync();
        _browse.Dispose();
        _browseOther?.Dispose();
    }
}

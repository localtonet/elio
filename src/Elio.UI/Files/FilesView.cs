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
/// <param name="Run">Runs a command on the server, for unpacking archives and looking inside files. Null where commands are not wanted.</param>
/// <param name="Key">What tells this server from the others in the lists that are kept for each of them.</param>
public sealed record FileServer(string Name, Func<CancellationToken, Task<SftpConnection>> Connect, Func<string, CancellationToken, Task<CommandResult>>? Run = null, string Key = "");

/// <summary>
/// This computer on the left, the server on the right, and what is moving between them below. The left side can
/// be a second server instead, and then files go straight from one server to the other.
/// </summary>
public sealed class FilesView : UserControl
{
    private const double SplitterSize = 3;

    // Narrower than this and the two sides go one above the other.
    private const double StackBelowWidth = 640;
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
    private readonly Border _overlay = new() { Background = ElioColors.Elevated, IsVisible = false, Tag = "files-overlay" };
    private readonly string _serverName;
    private readonly string? _otherName;
    private FilePanel _lastUsed = null!;
    private bool _stacked;
    private FilePreviewView? _preview;
    private FilePanel? _previewPanel;
    private FileEditorView? _editor;

    // The last comparison of the two sides and the folders it was of, until either side is listed again.
    private FolderComparison? _comparison;
    private (string Left, string Right) _compared;
    private CancellationTokenSource? _comparing;

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
        _serverName = server.Name;
        _otherName = other?.Name;

        if (other is null)
        {
            Queue = new TransferQueue(server.Connect) { ResolveConflict = ResolveConflictAsync };
            Local = new FilePanel(new LocalFileSource(), dialogs);
            Remote = new FilePanel(new RemoteFileSource(_browse.GetAsync, server.Run), dialogs) { FolderKey = KeyOf(server) };
            Local.Counterpart = Remote;
            Remote.Counterpart = Local;

            Local.TransferRequested += rows => Upload(rows.Select(r => r.Path).ToList());
            Remote.TransferRequested += Download;
            Local.RowsDropped += Download;
            Remote.RowsDropped += rows => Upload(rows.Select(r => r.Path).ToList());
            Remote.PathsDropped += Upload;
            Remote.EditRequested += row => _ = EditAsync(row, _browse);
            Transfers = new TransferListView(Queue, dialogs);
        }
        else
        {
            _browseOther = new Browser(other.Connect);
            Queue = new TransferQueue(server.Connect, other.Connect) { ResolveConflict = ResolveConflictAsync, SameServer = sameServer };
            Local = new FilePanel(new RemoteFileSource(_browseOther.GetAsync, other.Run), dialogs, other.Name, L.T("Copy to {0}", server.Name)) { FolderKey = KeyOf(other) };
            Remote = new FilePanel(new RemoteFileSource(_browse.GetAsync, server.Run), dialogs, server.Name, L.T("Copy to {0}", other.Name)) { FolderKey = KeyOf(server) };
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
            Transfers = new TransferListView(Queue, server.Name, other.Name, dialogs);
        }

        _lastUsed = Remote;
        Queue.Changed += () => Dispatcher.UIThread.Post(RefreshFinishedTargets);

        foreach (FilePanel panel in new[] { Local, Remote })
        {
            FilePanel side = panel;
            side.PreviewRequested += row => _ = PreviewAsync(side, row);
            side.EditorRequested += row => _ = OpenEditorAsync(side, row);
            side.CompareMenuRequested += ShowCompareMenu;

            // Once either side is listed again, what was compared may have changed, so the marks go from both.
            side.Listed += OnListed;
        }

        // Hiding hidden files is one choice for both sides.
        Local.HideHiddenChanged += hide => Remote.HideHidden = hide;
        Remote.HideHiddenChanged += hide => Local.HideHidden = hide;

        // Where the key falls on neither list, such as on the transfers, search goes to the side that was used last.
        Local.GotFocus += (_, _) => _lastUsed = Local;
        Remote.GotFocus += (_, _) => _lastUsed = Remote;
        KeyDown += (_, e) =>
        {
            if (e.Handled) return;

            // A comparison is stopped like any other job: neither side's list has a job of its own to take the key.
            if (e.Key == Key.Escape && _comparing is not null)
            {
                e.Handled = true;
                _comparing.Cancel();
                return;
            }

            if (e.Key != Key.F || e.KeyModifiers != (OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control)) return;
            e.Handled = true;
            ShowFind();
        };

        var panels = new Grid();
        var splitter = new GridSplitter { Background = ElioColors.Border, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        panels.Children.Add(Local);
        panels.Children.Add(splitter);
        panels.Children.Add(Remote);
        ArrangePanels(panels, splitter, stacked: false);

        // Side by side, each side is too narrow to read once the screen is: a phone held upright, or a pane beside a terminal. One above the other, each has the whole width.
        panels.SizeChanged += (_, e) =>
        {
            bool stacked = e.NewSize.Width < StackBelowWidth;
            if (stacked != _stacked) ArrangePanels(panels, splitter, stacked);
        };

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
        Content = new Grid { Children = { layout, _overlay } };
    }

    // A server that was not given a key of its own is the only one, and a list kept for it cannot be mixed up with another.
    private static string KeyOf(FileServer server) => server.Key.Length > 0 ? server.Key : "server:" + server.Name;

    /// <summary>Whether a file is open in the editor with changes that were not saved.</summary>
    public bool HasUnsavedChanges => _editor?.IsDirty == true;

    private void ArrangePanels(Grid panels, GridSplitter splitter, bool stacked)
    {
        _stacked = stacked;
        panels.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : $"*,{SplitterSize},*");
        panels.RowDefinitions = new RowDefinitions(stacked ? $"*,{SplitterSize},*" : "*");
        splitter.ResizeDirection = stacked ? GridResizeDirection.Rows : GridResizeDirection.Columns;
        splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
        splitter.VerticalAlignment = VerticalAlignment.Stretch;

        Grid.SetRow(Local, 0);
        Grid.SetColumn(Local, 0);
        Grid.SetRow(splitter, stacked ? 1 : 0);
        Grid.SetColumn(splitter, stacked ? 0 : 1);
        Grid.SetRow(Remote, stacked ? 2 : 0);
        Grid.SetColumn(Remote, stacked ? 0 : 2);
    }

    /// <summary>Gives the keyboard to what is in front: the editor or the preview if one is open, else the side that was used last.</summary>
    public void FocusContents()
    {
        if (_editor is not null) _editor.FocusEditor();
        else if (_overlay.IsVisible) _preview?.Focus();
        else _lastUsed.FocusContents();
    }

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

    private string SideName(FilePanel panel) =>
        panel == Remote ? (_serverName.Length > 0 ? _serverName : L.T("the server")) : _otherName ?? L.T("this computer");

    // A server's names are POSIX ones, and so are Linux's. Windows and macOS usually take "A" and "a" for one name, and are
    // taken to: thinking two names clash when they do not only leaves a file uncopied, never lets one replace another.
    private static bool CaseSensitive(IFileSource source) => source.IsRemote || OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD();

    // The menu under either side's compare button.
    private void ShowCompareMenu(Control anchor)
    {
        bool busy = _comparing is not null;
        var compare = new MenuItem { Header = L.T("Compare folders"), IsEnabled = !busy, Tag = "compare-run" };
        compare.Click += (_, _) => _ = CompareAsync();

        // The menu stays open when this is ticked, so that the comparison can be started from it next.
        var subfolders = new MenuItem { Header = L.T("Include subfolders"), ToggleType = MenuItemToggleType.CheckBox, IsChecked = AppServices.Settings.CompareSubfolders, StaysOpenOnClick = true, Tag = "compare-subfolders" };
        subfolders.Click += (_, _) =>
        {
            bool on = !AppServices.Settings.CompareSubfolders;
            AppServices.Settings.CompareSubfolders = on;
            AppServices.SaveSettings();
            subfolders.IsChecked = on;
        };

        var synchronise = new MenuItem { Header = L.T("Synchronise..."), IsEnabled = !busy, Tag = "compare-sync" };
        synchronise.Click += (_, _) => _ = SynchroniseAsync();
        var clear = new MenuItem { Header = L.T("Clear the marks"), IsEnabled = _comparison is not null, Tag = "compare-clear" };
        clear.Click += (_, _) => EndComparison();

        new ContextMenu { ItemsSource = new Control[] { compare, subfolders, new Separator(), synchronise, clear } }.Open(anchor);
    }

    private void OnListed()
    {
        // A comparison still running is of a folder that one side no longer shows.
        if (_comparing is not null && (Local.CurrentPath, Remote.CurrentPath) != _compared) _comparing.Cancel();
        EndComparison();
    }

    private void EndComparison()
    {
        _comparison = null;
        Local.ShowMarks(null);
        Remote.ShowMarks(null);
    }

    /// <summary>
    /// Compares the folders the two sides show, and the folders below them when that is ticked, and marks on each side what
    /// is only there, newer, older or otherwise not the same. Esc stops it.
    /// </summary>
    public async Task CompareAsync()
    {
        if (_comparing is not null) return;

        string left = Local.CurrentPath;
        string right = Remote.CurrentPath;

        // Windows's list of drives is not a folder, and a server that was never reached has not shown one yet.
        if (left.Length == 0 || right.Length == 0)
        {
            Local.ShowProblem(L.T("Open a folder on both sides first."));
            Remote.ShowProblem(L.T("Open a folder on both sides first."));
            return;
        }

        var comparing = new CancellationTokenSource();
        _comparing = comparing;
        _compared = (left, right);
        EndComparison();

        bool subfolders = AppServices.Settings.CompareSubfolders;
        bool StillShown() => Local.CurrentPath == left && Remote.CurrentPath == right;
        int seenLeft = 0, seenRight = 0;
        void Show()
        {
            if (!StillShown() || comparing.IsCancellationRequested) return;

            string text = L.T("Comparing... {0} items looked at  (Esc stops)", seenLeft + seenRight);
            Local.ShowNotice(text);
            Remote.ShowNotice(text);
        }

        string? failure = null;
        async Task<FolderSnapshot> SnapshotAsync(FilePanel side, string path, Action<int> seen)
        {
            try
            {
                return await FolderSnapshot.TakeAsync(side.Source, path, subfolders, CaseSensitive(side.Source), new Progress<int>(seen), comparing.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // The other side's walk has nothing left to be compared with.
                failure ??= $"{path}: {ex.Message}";
                await comparing.CancelAsync();
                throw;
            }
        }

        Show();
        try
        {
            FolderSnapshot[] snapshots = await Task.WhenAll(
                SnapshotAsync(Local, left, n => { seenLeft = n; Show(); }),
                SnapshotAsync(Remote, right, n => { seenRight = n; Show(); }));
            FolderComparison comparison = await Task.Run(() => FolderComparison.Compare(snapshots[0], snapshots[1]), comparing.Token);
            if (!StillShown()) return;

            _comparison = comparison;
            Local.ShowMarks(comparison.MarksFor(left: true));
            Remote.ShowMarks(comparison.MarksFor(left: false));
            Local.ShowNotice(Summary(comparison, left: true));
            Remote.ShowNotice(Summary(comparison, left: false));
        }
        catch (Exception) when (failure is not null)
        {
            Local.ShowProblem(L.T("Could not compare: {0}", failure));
            Remote.ShowProblem(L.T("Could not compare: {0}", failure));
        }
        catch (OperationCanceledException) when (comparing.IsCancellationRequested)
        {
            if (StillShown())
            {
                Local.ShowNotice(L.T("Stopped."));
                Remote.ShowNotice(L.T("Stopped."));
            }
        }
        finally
        {
            _comparing = null;
            comparing.Dispose();
        }
    }

    private static string Summary(FolderComparison comparison, bool left)
    {
        CompareTally tally = comparison.Tally(left);
        string text = comparison.Items.Count == 0
            ? (comparison.Subfolders ? L.T("Compared: both sides are the same, and so are the folders below.") : L.T("Compared: both sides are the same."))
            : L.T("Compared: {0} only here, {1} newer here, {2} older here, {3} other differences.", tally.OnlyHere, tally.Newer, tally.Older, tally.Other);
        if (comparison.LinksLeftOut > 0) text += " " + L.T("{0} links were left out.", comparison.LinksLeftOut);
        if (comparison.Incomplete) text += " " + L.T("There was too much to look through all of it, so this is not the whole story.");
        return text;
    }

    /// <summary>
    /// Shows what synchronising the two sides would copy, comparing them first unless that was just done for the folders
    /// shown, and puts the copies in the transfer queue once the user agrees. Nothing is ever deleted.
    /// </summary>
    public async Task SynchroniseAsync()
    {
        if (_comparing is not null) return;
        if (_comparison is null || _compared != (Local.CurrentPath, Remote.CurrentPath)) await CompareAsync();
        if (_comparison is not { } comparison) return;

        if (comparison.Incomplete)
        {
            await _dialogs.ShowMessageAsync(L.T("Cannot synchronise"), L.T("The comparison could not look through everything, so what seems to be missing may only not have been seen. Compare a smaller folder, or leave out the subfolders."));
            return;
        }

        (string left, string right) = _compared;
        SyncPlan? plan = await _dialogs.AskSyncAsync(SideName(Local), SideName(Remote), (direction, replaceNewer) => SyncPlan.Build(comparison, direction, replaceNewer));
        if (plan is null || plan.Copies.Count == 0) return;

        // A job for each folder copied into, so that the list says where each lot goes. What is there is replaced, as
        // the user saw in the list, by the same swap at the end as every other transfer: never half a file.
        foreach (IGrouping<(bool ToRight, string Folder), SyncCopy> group in plan.Copies.GroupBy(c => (c.ToRight, c.Item.Folder)))
        {
            (bool toRight, string folder) = group.Key;
            string target = Below(toRight ? Remote.Source : Local.Source, toRight ? right : left, folder);
            List<FileRow> sources = group.Select(c => c.Source).ToList();

            if (BetweenServers)
            {
                // The left side is the second server, the right side the first.
                Queue.Copy(sources.Select(r => r.Remote!).ToList(), target, toRight ? TransferServer.Second : TransferServer.First, ConflictChoice.Overwrite);
            }
            else if (toRight)
            {
                Queue.Upload(sources.Select(r => r.Path).ToList(), target, whenExists: ConflictChoice.Overwrite);
            }
            else
            {
                Queue.Download(sources.Select(r => r.Remote!).ToList(), target, whenExists: ConflictChoice.Overwrite);
            }
        }

        // The marks are about to be out of date.
        EndComparison();
        string queued = L.T("{0} items were put in the transfer queue.", plan.Copies.Count);
        Local.ShowNotice(queued);
        Remote.ShowNotice(queued);
    }

    // A folder below a side's top folder, from its path with / between the names.
    private static string Below(IFileSource source, string root, string folder) =>
        folder.Length == 0 ? root : folder.Split('/').Aggregate(root, source.Combine);

    private void ShowOverlay(Control view)
    {
        _overlay.Child = view;
        _overlay.IsVisible = true;
    }

    private void HideOverlay()
    {
        _overlay.IsVisible = false;
        _overlay.Child = null;
    }

    private async Task PreviewAsync(FilePanel panel, FileRow row)
    {
        // The editor holds the screen until it is closed.
        if (_editor is not null) return;

        if (_preview is null)
        {
            _preview = new FilePreviewView();
            _preview.CloseRequested += ClosePreview;
            _preview.Moved += delta =>
            {
                if (_previewPanel is { } side && side.Step(delta) is FileRow next) _ = _preview.ShowAsync(side.Source, next);
            };
            _preview.EditRequested += file =>
            {
                FilePanel? side = _previewPanel;
                ClosePreview();
                if (side is not null) _ = OpenEditorAsync(side, file);
            };
        }

        _previewPanel = panel;
        ShowOverlay(_preview);
        _preview.Focus();
        await _preview.ShowAsync(panel.Source, row);
    }

    private void ClosePreview()
    {
        _preview?.Clear();
        HideOverlay();
        (_previewPanel ?? _lastUsed).FocusContents();
        _previewPanel = null;
    }

    private string NameOf(FilePanel panel) => panel == Remote ? _serverName : _otherName ?? L.T("This computer");

    /// <summary>Opens a file in the editor of the app. Saving writes it back to where it came from.</summary>
    private async Task OpenEditorAsync(FilePanel panel, FileRow row)
    {
        if (_editor is not null) return;

        IFileSource source = panel.Source;
        try
        {
            // One byte more than is allowed, to tell a file that is exactly as big as that from one that is bigger.
            byte[] bytes = await source.ReadAsync(row.Path, TextCodec.MaxBytes + 1, CancellationToken.None);
            if (bytes.Length > TextCodec.MaxBytes)
            {
                await _dialogs.ShowMessageAsync(L.T("This file is too big to edit here"), L.T("The editor opens files up to {0}. Download it to change it in another program.", FileText.Size(TextCodec.MaxBytes)));
                return;
            }

            if (!TextCodec.TryDecode(bytes, out string text, out TextFormat format))
            {
                await _dialogs.ShowMessageAsync(L.T("This is not a text file"), L.T("{0} holds data that is not text, and changing it as text would damage it.", row.Name));
                return;
            }

            (long Size, DateTime ModifiedUtc)? stamp = await source.StatAsync(row.Path, CancellationToken.None);
            string location = source.IsRemote ? $"{NameOf(panel)}: {row.Path}" : row.Path;

            var editor = new FileEditorView(_dialogs, row.Name, location, text, format)
            {
                Saver = async data =>
                {
                    try
                    {
                        // Someone else may have saved the file since it was opened, and that is theirs to lose only by choice.
                        (long Size, DateTime ModifiedUtc)? now = await source.StatAsync(row.Path, CancellationToken.None);
                        if (now is { } current && stamp is { } known && current != known)
                        {
                            bool overwrite = await _dialogs.ConfirmAsync(
                                L.T("The file changed meanwhile"),
                                L.T("{0} changed while you were editing. If you save, the changes made there will be lost.", row.Name),
                                L.T("Overwrite"),
                                danger: true);
                            if (!overwrite) return L.T("Not saved: the file changed meanwhile");
                        }

                        await source.WriteAsync(row.Path, data, CancellationToken.None);
                        stamp = await source.StatAsync(row.Path, CancellationToken.None);
                        _ = panel.RefreshAsync(searchSubfolders: false);
                        return null;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        return ex.Message;
                    }
                },
            };

            editor.CloseRequested += () => CloseEditor(panel);
            _editor = editor;
            ShowOverlay(editor);
            editor.FocusEditor();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await _dialogs.ShowMessageAsync(L.T("Could not open the file"), ex.Message);
        }
    }

    private void CloseEditor(FilePanel panel)
    {
        _editor = null;
        HideOverlay();
        panel.FocusContents();
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

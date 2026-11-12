using System.Net.Sockets;
using Elio.Core;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Elio.Connections;

public enum TransferKind
{
    Upload,
    Download,

    /// <summary>From one server straight to another, without passing through this computer's disk.</summary>
    Copy,
}

/// <summary>Which of a queue's servers a job is about. A queue made for one server only has the first.</summary>
public enum TransferServer
{
    First,
    Second,
}

public enum TransferState
{
    Queued,
    Preparing,
    Running,
    Reconnecting,
    Paused,
    Completed,
    CompletedWithErrors,
    Failed,
    Canceled,
}

/// <summary>What to do with a file that already exists at the destination.</summary>
public enum ConflictChoice
{
    Overwrite,
    Skip,
}

/// <summary>Asked when a destination file exists. Returns the choice, and whether to use it for the rest of the job.</summary>
public delegate Task<(ConflictChoice Choice, bool ForAll)> ConflictResolver(TransferJob job, string path);

internal sealed class TransferFile(TransferJob job, string source, string destination, long size, DateTime modifiedUtc)
{
    private long _counted;

    public TransferJob Job { get; } = job;

    /// <summary>Where the file is read from: a path here for an upload, on a server otherwise.</summary>
    public string Source { get; } = source;

    /// <summary>Where the file ends up: a path here for a download, on a server otherwise.</summary>
    public string Destination { get; } = destination;
    public long Size { get; } = size;
    public DateTime ModifiedUtc { get; } = modifiedUtc;

    public void SetPosition(long offset) => Job.AddBytes(offset - Interlocked.Exchange(ref _counted, offset));

    public void Add(long bytes)
    {
        Interlocked.Add(ref _counted, bytes);
        Job.AddBytes(bytes);
    }

    /// <summary>Takes this file's bytes back out of the job, when it failed or is being sent from scratch.</summary>
    public void Forget() => SetPosition(0);
}

/// <summary>One thing the user asked for: a file, several, or whole folders.</summary>
public sealed class TransferJob
{
    private readonly object _gate = new();
    private readonly List<string> _errors = [];
    private long _doneBytes;
    private long _totalBytes;
    private int _doneFiles;
    private int _totalFiles;
    private TransferState _state = TransferState.Queued;

    internal TransferJob(TransferKind kind, string title, IReadOnlyList<string> localSources, IReadOnlyList<RemoteEntry> remoteSources, string target, TransferServer server = TransferServer.First)
    {
        Kind = kind;
        Title = title;
        LocalSources = localSources;
        RemoteSources = remoteSources;
        Target = target;
        Server = server;
    }

    public TransferKind Kind { get; }

    /// <summary>The server an upload goes to or a download comes from. A copy reads from it and writes to the other one.</summary>
    public TransferServer Server { get; }

    /// <summary>The server whose files this job changes, for the kinds that write to one.</summary>
    public TransferServer TargetServer => Kind != TransferKind.Copy ? Server : Server == TransferServer.First ? TransferServer.Second : TransferServer.First;

    public string Title { get; }

    public string Target { get; }

    internal IReadOnlyList<string> LocalSources { get; }

    internal IReadOnlyList<RemoteEntry> RemoteSources { get; }

    internal LinkedList<TransferFile> Pending { get; } = new();

    internal List<TransferFile> Active { get; } = [];

    internal Dictionary<string, ConflictChoice> Decisions { get; } = [];

    internal ConflictChoice? DecisionForAll { get; set; }

    internal CancellationTokenSource Control { get; set; } = new();

    internal bool CancelRequested { get; set; }

    public TransferState State
    {
        get => _state;
        internal set => _state = value;
    }

    public long DoneBytes => Interlocked.Read(ref _doneBytes);

    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public int DoneFiles => Volatile.Read(ref _doneFiles);

    public int TotalFiles => Volatile.Read(ref _totalFiles);

    /// <summary>Why the job is waiting, if it is.</summary>
    public string? Note { get; internal set; }

    public string? CurrentFile { get; internal set; }

    public IReadOnlyList<string> Errors
    {
        get
        {
            lock (_gate) return _errors.ToArray();
        }
    }

    public bool IsFinished => State is TransferState.Completed or TransferState.CompletedWithErrors or TransferState.Failed or TransferState.Canceled;

    internal void AddBytes(long bytes) => Interlocked.Add(ref _doneBytes, bytes);

    internal void FileDone() => Interlocked.Increment(ref _doneFiles);

    internal void AddError(string message)
    {
        lock (_gate) _errors.Add(message);
    }

    internal void ClearErrors()
    {
        lock (_gate) _errors.Clear();
    }

    internal void AddPlanned(long bytes, int files)
    {
        Interlocked.Add(ref _totalBytes, bytes);
        Interlocked.Add(ref _totalFiles, files);
    }

    internal void ResetProgress()
    {
        Interlocked.Exchange(ref _doneBytes, 0);
        Interlocked.Exchange(ref _totalBytes, 0);
        Volatile.Write(ref _doneFiles, 0);
        Volatile.Write(ref _totalFiles, 0);
    }
}

/// <summary>
/// Runs transfers on their own connection. If it breaks, the queue reconnects and every unfinished
/// file carries on from where it stopped. A queue given two servers can also copy from one to the other,
/// with a connection to each.
/// </summary>
public sealed class TransferQueue : IAsyncDisposable
{
    private const int ParallelFiles = 4;
    private const int MaxRetrySeconds = 30;
    private static readonly TimeSpan IdleConnectionLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(25);

    /// <summary>One of the queue's servers: how to reach it, and the connection to it while there is one.</summary>
    private sealed class Link(Func<CancellationToken, Task<SftpConnection>> connect)
    {
        public Func<CancellationToken, Task<SftpConnection>> Connect { get; } = connect;

        public SftpConnection? Connection { get; set; }
    }

    /// <summary>The connections one stretch of work runs on: one to each server its jobs need.</summary>
    private sealed class Session : IDisposable
    {
        private readonly SftpConnection?[] _connections;
        private readonly CancellationTokenSource _restart = new();
        private readonly CancellationTokenSource _ended;

        public Session(SftpConnection?[] connections)
        {
            _connections = connections;
            _ended = CancellationTokenSource.CreateLinkedTokenSource([_restart.Token, .. Connections.Select(c => c.Lost)]);
        }

        public IEnumerable<SftpConnection> Connections => _connections.OfType<SftpConnection>();

        public SftpConnection Get(TransferServer server) => _connections[(int)server]!;

        /// <summary>Whether this session has a connection to every server the job needs.</summary>
        public bool Covers(TransferJob job) => _connections[(int)job.Server] is not null && _connections[(int)job.TargetServer] is not null;

        public bool AllConnected => Connections.All(c => c.IsConnected);

        /// <summary>A job turned up for a server this session has no connection to.</summary>
        public bool NeedsMore { get; private set; }

        /// <summary>Nothing more can be done on these connections as they are.</summary>
        public bool IsOver => NeedsMore || !AllConnected;

        /// <summary>Signalled when a connection breaks, or when the session has to be set up again.</summary>
        public CancellationToken Ended => _ended.Token;

        /// <summary>Stops the work in hand so that it can carry on with the missing connection added.</summary>
        public void AskForMore()
        {
            if (NeedsMore) return;
            NeedsMore = true;
            _ = _restart.CancelAsync();
        }

        public void Dispose()
        {
            _ended.Dispose();
            _restart.Dispose();
        }
    }

    private readonly Link[] _links;
    private readonly object _gate = new();
    private readonly List<TransferJob> _jobs = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private TaskCompletionSource _pulse = NewPulse();

    public TransferQueue(Func<CancellationToken, Task<SftpConnection>> connect)
    {
        _links = [new Link(connect)];
        _loop = Task.Run(RunAsync);
    }

    /// <summary>A queue for two servers, which can copy between them as well as to and from this computer.</summary>
    public TransferQueue(Func<CancellationToken, Task<SftpConnection>> connectFirst, Func<CancellationToken, Task<SftpConnection>> connectSecond)
    {
        _links = [new Link(connectFirst), new Link(connectSecond)];
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Raised when a job starts, stops or changes state, not for every byte.</summary>
    public event Action? Changed;

    public ConflictResolver? ResolveConflict { get; set; }

    /// <summary>
    /// Both servers are the same machine, reached twice. A folder copied into itself there would never end,
    /// so that is refused.
    /// </summary>
    public bool SameServer { get; init; }

    public IReadOnlyList<TransferJob> Jobs
    {
        get
        {
            lock (_gate) return _jobs.ToArray();
        }
    }

    private static TaskCompletionSource NewPulse() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Pulse()
    {
        Interlocked.Exchange(ref _pulse, NewPulse()).TrySetResult();
        Changed?.Invoke();
    }

    private static string TitleFor(IReadOnlyList<string> names) => names.Count == 1 ? names[0] : L.T("{0} and {1} more items", names[0], names.Count - 1);

    public TransferJob Upload(IReadOnlyList<string> localPaths, string remoteFolder, TransferServer server = TransferServer.First)
    {
        string title = TitleFor(localPaths.Select(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))).ToList());
        return Add(new TransferJob(TransferKind.Upload, title, localPaths, [], remoteFolder, server));
    }

    public TransferJob Download(IReadOnlyList<RemoteEntry> entries, string localFolder, TransferServer server = TransferServer.First) =>
        Add(new TransferJob(TransferKind.Download, TitleFor(entries.Select(e => e.Name).ToList()), [], entries, localFolder, server));

    /// <summary>Copies files and folders from one of the queue's two servers into a folder on the other.</summary>
    public TransferJob Copy(IReadOnlyList<RemoteEntry> entries, string targetFolder, TransferServer from = TransferServer.First) =>
        Add(new TransferJob(TransferKind.Copy, TitleFor(entries.Select(e => e.Name).ToList()), [], entries, targetFolder, from));

    private TransferJob Add(TransferJob job)
    {
        if ((int)job.Server >= _links.Length || (int)job.TargetServer >= _links.Length) throw new InvalidOperationException("This queue was made for one server.");

        lock (_gate) _jobs.Add(job);
        Pulse();
        return job;
    }

    /// <summary>Stops a job but keeps what it has done so far.</summary>
    public void Pause(TransferJob job)
    {
        lock (_gate)
        {
            if (job.IsFinished || job.State == TransferState.Paused) return;
            job.State = TransferState.Paused;
            job.Note = null;
        }

        job.Control.Cancel();
        Pulse();
    }

    public void Resume(TransferJob job)
    {
        lock (_gate)
        {
            if (job.State != TransferState.Paused) return;
            job.Control = new CancellationTokenSource();
            job.State = job.TotalFiles == 0 ? TransferState.Queued : TransferState.Running;
        }

        Pulse();
    }

    /// <summary>Stops a job and removes its unfinished files.</summary>
    public void Cancel(TransferJob job)
    {
        List<TransferFile> pending;
        lock (_gate)
        {
            if (job.IsFinished) return;
            job.CancelRequested = true;
            job.State = TransferState.Canceled;
            pending = [.. job.Pending, .. job.Active];
            job.Pending.Clear();
        }

        job.Control.Cancel();
        _ = RemovePartsAsync(pending);
        Pulse();
    }

    private async Task RemovePartsAsync(List<TransferFile> files)
    {
        foreach (TransferFile file in files)
        {
            if (file.Job.Kind == TransferKind.Download) SftpCopy.DeleteLocalPart(file.Destination);
            else if (_links[(int)file.Job.TargetServer].Connection is { IsConnected: true } connection)
                await SftpCopy.DeleteRemotePartAsync(connection.Client, file.Destination, _stop.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Tries a finished job again, from the top. Files already there are skipped or resumed as usual.</summary>
    public void Retry(TransferJob job)
    {
        lock (_gate)
        {
            if (job.State is not (TransferState.Failed or TransferState.CompletedWithErrors or TransferState.Canceled)) return;
            job.CancelRequested = false;
            job.Control = new CancellationTokenSource();
            job.ClearErrors();
            job.Note = null;
            job.State = TransferState.Queued;
        }

        Pulse();
    }

    /// <summary>Drops jobs that have nothing left to do from the list.</summary>
    public void RemoveFinished()
    {
        lock (_gate) _jobs.RemoveAll(j => j.IsFinished);
        Changed?.Invoke();
    }

    private static bool IsWaitingOrRunning(TransferJob job) => job.State is TransferState.Queued or TransferState.Preparing or TransferState.Running or TransferState.Reconnecting;

    private bool HasWork()
    {
        lock (_gate) return _jobs.Any(IsWaitingOrRunning);
    }

    /// <summary>Connects to every server the waiting jobs need, keeping a connection that is still good.</summary>
    private async Task<Session> OpenSessionAsync()
    {
        bool[] needed = new bool[_links.Length];
        lock (_gate)
        {
            foreach (TransferJob job in _jobs.Where(IsWaitingOrRunning)) needed[(int)job.Server] = needed[(int)job.TargetServer] = true;
        }

        var connections = new SftpConnection?[_links.Length];
        for (int i = 0; i < _links.Length; i++)
        {
            if (!needed[i]) continue;

            Link link = _links[i];
            if (link.Connection is { IsConnected: false } broken)
            {
                link.Connection = null;
                broken.Dispose();
            }

            link.Connection ??= await link.Connect(_stop.Token).ConfigureAwait(false);
            connections[i] = link.Connection;
        }

        return new Session(connections);
    }

    private void CloseConnections(bool onlyBroken)
    {
        foreach (Link link in _links)
        {
            if (link.Connection is not SftpConnection connection || (onlyBroken && connection.IsConnected)) continue;

            link.Connection = null;
            connection.Dispose();
        }
    }

    private void SetWaiting(string? note)
    {
        lock (_gate)
        {
            foreach (TransferJob job in _jobs.Where(j => j.State is TransferState.Running or TransferState.Preparing or TransferState.Reconnecting or TransferState.Queued))
            {
                job.Note = note;
                if (note is not null && job.TotalFiles > 0) job.State = TransferState.Reconnecting;
            }
        }

        Changed?.Invoke();
    }

    private async Task RunAsync()
    {
        try
        {
            await RunUntilStoppedAsync().ConfigureAwait(false);
        }
        finally
        {
            CloseConnections(onlyBroken: false);
        }
    }

    private async Task RunUntilStoppedAsync()
    {
        int failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                Task wake = Volatile.Read(ref _pulse).Task;
                if (!HasWork())
                {
                    CloseConnections(onlyBroken: false);
                    await wake.WaitAsync(_stop.Token).ConfigureAwait(false);
                    continue;
                }

                if (failures > 0)
                {
                    int seconds = Math.Min(1 << Math.Min(failures - 1, 16), MaxRetrySeconds);
                    SetWaiting(L.T("The connection dropped. Trying again in {0} s.", seconds));
                    await Task.Delay(TimeSpan.FromSeconds(seconds), _stop.Token).ConfigureAwait(false);
                }

                using Session session = await OpenSessionAsync().ConfigureAwait(false);
                failures = 0;
                SetWaiting(null);

                using var finished = new CancellationTokenSource();
                Task watch = WatchAsync(session, finished.Token);
                try
                {
                    await Task.WhenAll(Enumerable.Range(0, ParallelFiles).Select(_ => WorkerAsync(session))).ConfigureAwait(false);
                    if (!session.AllConnected) failures = 1;
                }
                finally
                {
                    await finished.CancelAsync().ConfigureAwait(false);
                    await watch.ConfigureAwait(false);

                    // With two servers, the connection that did not break is kept for the next attempt.
                    CloseConnections(onlyBroken: failures > 0 || session.NeedsMore);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (IsLoginProblem(ex))
            {
                FailAll(ex.Message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures++;
                lock (_gate)
                {
                    foreach (TransferJob job in _jobs.Where(j => !j.IsFinished && j.State != TransferState.Paused)) job.Note = ex.Message;
                }
            }
        }
    }

    // A stalled link reports nothing: no error arrives, replies just stop. The same goes for a file service
    // that died while its SSH connection stayed up. Progress is the only thing that shows either has happened.
    private async Task WatchAsync(Session session, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(WatchInterval);
        long lastBytes = -1;
        DateTime lastMoved = DateTime.UtcNow;

        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                if (session.Connections.FirstOrDefault(c => !c.IsConnected) is SftpConnection broken)
                {
                    broken.Abandon();
                    return;
                }

                long bytes;
                bool copying;
                lock (_gate)
                {
                    bytes = _jobs.Sum(j => j.DoneBytes);
                    copying = _jobs.Any(j => j.Active.Count > 0);
                }

                if (!copying || bytes != lastBytes)
                {
                    lastBytes = bytes;
                    lastMoved = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastMoved > StallLimit)
                {
                    // Between two servers there is no telling which one went quiet.
                    foreach (SftpConnection connection in session.Connections) connection.Abandon();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Nothing is using the connection any more.
        }
    }

    // These need the user, so retrying on a timer would only repeat the refusal.
    private static bool IsLoginProblem(Exception ex) => ex is HostKeyRejectedException or LoginRefusedException or KeyPassphraseNeededException or OperationCanceledException;

    private void FailAll(string message)
    {
        lock (_gate)
        {
            foreach (TransferJob job in _jobs.Where(j => !j.IsFinished && j.State != TransferState.Paused))
            {
                job.AddError(message);
                job.Pending.Clear();
                job.State = TransferState.Failed;
            }
        }

        Changed?.Invoke();
    }

    private (TransferJob? Prepare, TransferFile? File) TakeNext(Session session)
    {
        lock (_gate)
        {
            foreach (TransferJob job in _jobs)
            {
                if (!session.Covers(job))
                {
                    // Only a queue for two servers gets here: the session was opened before this job needed the other one.
                    if (job.State == TransferState.Queued || (job.State is TransferState.Running or TransferState.Reconnecting && job.Pending.Count > 0)) session.AskForMore();
                    continue;
                }

                if (job.State == TransferState.Queued)
                {
                    job.State = TransferState.Preparing;
                    return (job, null);
                }

                if (job.State is TransferState.Running or TransferState.Reconnecting && job.Pending.First is { } node)
                {
                    job.Pending.RemoveFirst();
                    job.Active.Add(node.Value);
                    job.State = TransferState.Running;
                    job.CurrentFile = node.Value.Source;
                    return (null, node.Value);
                }
            }

            return (null, null);
        }
    }

    private async Task WorkerAsync(Session session)
    {
        while (!session.IsOver && !_stop.IsCancellationRequested)
        {
            Task wake = Volatile.Read(ref _pulse).Task;
            var (prepare, file) = TakeNext(session);

            if (prepare is not null)
            {
                await PrepareAsync(prepare, session).ConfigureAwait(false);
            }
            else if (file is not null)
            {
                await CopyAsync(file, session).ConfigureAwait(false);
            }
            else
            {
                FinishEmptyJobs();
                try
                {
                    await wake.WaitAsync(IdleConnectionLifetime, session.Ended).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void FinishEmptyJobs()
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (TransferJob job in _jobs.Where(j => j.State is TransferState.Running or TransferState.Reconnecting && j.Pending.Count == 0 && j.Active.Count == 0))
            {
                job.CurrentFile = null;
                job.State = job.Errors.Count > 0 ? TransferState.CompletedWithErrors : TransferState.Completed;
                changed = true;
            }
        }

        if (changed) Pulse();
    }

    private async Task CopyAsync(TransferFile file, Session session)
    {
        TransferJob job = file.Job;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Control.Token, session.Ended, _stop.Token);
        SftpClient client = session.Get(job.Server).Client;

        try
        {
            await (job.Kind switch
            {
                TransferKind.Upload => SftpCopy.UploadAsync(client, file.Source, file.Destination, file.SetPosition, file.Add, linked.Token),
                TransferKind.Download => SftpCopy.DownloadAsync(client, file.Source, file.Destination, file.ModifiedUtc, file.SetPosition, file.Add, linked.Token),
                _ => SftpCopy.CopyAsync(client, session.Get(job.TargetServer).Client, file.Source, file.Destination, file.SetPosition, file.Add, linked.Token),
            }).ConfigureAwait(false);

            job.FileDone();
            Done(file);
        }
        catch (Exception ex) when (job.Control.IsCancellationRequested || session.IsOver || IsLinkProblem(ex))
        {
            // Paused, cancelled or cut off. Unless cancelled, the file goes back in line and carries on later.
            Requeue(file);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            file.Forget();
            job.AddError($"{file.Source}: {ex.Message}");
            Done(file);
        }

        Pulse();
    }

    private static bool IsLinkProblem(Exception ex) =>
        ex is SshConnectionException or SocketException or SshOperationTimeoutException or ObjectDisposedException
        || (ex is OperationCanceledException)
        || (ex is IOException && ex.InnerException is SocketException);

    private void Done(TransferFile file)
    {
        lock (_gate) file.Job.Active.Remove(file);
    }

    private void Requeue(TransferFile file)
    {
        lock (_gate)
        {
            file.Job.Active.Remove(file);
            if (!file.Job.CancelRequested) file.Job.Pending.AddFirst(file);
        }
    }

    private async Task PrepareAsync(TransferJob job, Session session)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Control.Token, session.Ended, _stop.Token);
        SftpConnection connection = session.Get(job.Server);
        job.ResetProgress();
        Changed?.Invoke();

        try
        {
            await (job.Kind switch
            {
                TransferKind.Upload => PlanUploadAsync(job, connection, linked.Token),
                TransferKind.Download => PlanDownloadAsync(job, connection, linked.Token),
                _ => PlanCopyAsync(job, connection, session.Get(job.TargetServer), linked.Token),
            }).ConfigureAwait(false);

            lock (_gate)
            {
                if (job.State == TransferState.Preparing) job.State = TransferState.Running;
            }
        }
        catch (Exception ex) when (job.Control.IsCancellationRequested || session.IsOver)
        {
            // Planned again from the top once it can continue. Earlier answers to conflict questions are kept.
            _ = ex;
            lock (_gate)
            {
                job.Pending.Clear();
                if (job.State == TransferState.Preparing) job.State = TransferState.Queued;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (_gate)
            {
                job.Pending.Clear();
                job.AddError(ex.Message);
                job.State = TransferState.Failed;
            }
        }

        Pulse();
    }

    private async Task<bool> ShouldCopyAsync(TransferJob job, string destination, CancellationToken cancellation)
    {
        if (job.DecisionForAll is ConflictChoice all) return all == ConflictChoice.Overwrite;
        if (job.Decisions.TryGetValue(destination, out ConflictChoice known)) return known == ConflictChoice.Overwrite;
        if (ResolveConflict is not ConflictResolver resolve) return true;

        var (choice, forAll) = await resolve(job, destination).WaitAsync(cancellation).ConfigureAwait(false);
        job.Decisions[destination] = choice;
        if (forAll) job.DecisionForAll = choice;
        return choice == ConflictChoice.Overwrite;
    }

    private void Plan(TransferJob job, TransferFile file)
    {
        job.AddPlanned(file.Size, 1);
        lock (_gate) job.Pending.AddLast(file);
    }

    private async Task PlanUploadAsync(TransferJob job, SftpConnection connection, CancellationToken cancellation)
    {
        async Task PlanFolderAsync(string local, string remote)
        {
            cancellation.ThrowIfCancellationRequested();
            if (await connection.StatAsync(remote, cancellation).ConfigureAwait(false) is null)
                await connection.CreateDirectoryAsync(remote, cancellation).ConfigureAwait(false);

            HashSet<string> existing = (await connection.ListAsync(remote, cancellation).ConfigureAwait(false)).Select(e => e.Name).ToHashSet();

            foreach (string entry in Directory.EnumerateFileSystemEntries(local))
            {
                string name = Path.GetFileName(entry);
                string target = RemotePath.Combine(remote, name);

                if (Directory.Exists(entry))
                {
                    // A link could lead back up the tree and never end.
                    if (new DirectoryInfo(entry).LinkTarget is null) await PlanFolderAsync(entry, target).ConfigureAwait(false);
                    continue;
                }

                await PlanFileAsync(entry, target, existing.Contains(name)).ConfigureAwait(false);
            }
        }

        async Task PlanFileAsync(string local, string target, bool exists)
        {
            if (exists && !await ShouldCopyAsync(job, target, cancellation).ConfigureAwait(false)) return;
            var info = new FileInfo(local);
            Plan(job, new TransferFile(job, local, target, info.Length, info.LastWriteTimeUtc));
        }

        foreach (string source in job.LocalSources)
        {
            string trimmed = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string target = RemotePath.Combine(job.Target, Path.GetFileName(trimmed));

            if (Directory.Exists(trimmed)) await PlanFolderAsync(trimmed, target).ConfigureAwait(false);
            else await PlanFileAsync(trimmed, target, await connection.StatAsync(target, cancellation).ConfigureAwait(false) is not null).ConfigureAwait(false);
        }
    }

    private async Task PlanDownloadAsync(TransferJob job, SftpConnection connection, CancellationToken cancellation)
    {
        async Task PlanEntryAsync(RemoteEntry entry, string folder)
        {
            cancellation.ThrowIfCancellationRequested();
            string target = Path.Combine(folder, LocalNames.Safe(entry.Name));

            if (entry.IsDirectory)
            {
                // Same reason as for uploads: a link can point back at a parent.
                if (entry.IsSymbolicLink) return;

                Directory.CreateDirectory(target);
                foreach (RemoteEntry child in await connection.ListAsync(entry.FullPath, cancellation).ConfigureAwait(false))
                    await PlanEntryAsync(child, target).ConfigureAwait(false);
                return;
            }

            if (File.Exists(target) && !await ShouldCopyAsync(job, target, cancellation).ConfigureAwait(false)) return;
            Plan(job, new TransferFile(job, entry.FullPath, target, entry.Size, entry.ModifiedUtc));
        }

        Directory.CreateDirectory(job.Target);
        foreach (RemoteEntry entry in job.RemoteSources) await PlanEntryAsync(entry, job.Target).ConfigureAwait(false);
    }

    private async Task PlanCopyAsync(TransferJob job, SftpConnection source, SftpConnection target, CancellationToken cancellation)
    {
        // What is already in the folder being filled, or null to ask about one name at a time.
        async Task PlanEntryAsync(RemoteEntry entry, string folder, HashSet<string>? existing)
        {
            cancellation.ThrowIfCancellationRequested();

            // A server chooses the names it reports, and none of them may lead out of the folder being filled.
            string name = entry.Name.Replace('/', '_');
            if (name is "" or "." or "..") name = "_";
            string destination = RemotePath.Combine(folder, name);

            if (entry.IsDirectory)
            {
                // Same reason as for uploads: a link can point back at a parent.
                if (entry.IsSymbolicLink) return;

                if (SameServer && (destination == entry.FullPath || destination.StartsWith(entry.FullPath.TrimEnd('/') + "/", StringComparison.Ordinal)))
                    throw new IOException(L.T("A folder cannot be copied into itself."));

                // Read first, so that a folder that cannot be read leaves nothing behind on the other server.
                List<RemoteEntry> children = await source.ListAsync(entry.FullPath, cancellation).ConfigureAwait(false);

                if (!(existing?.Contains(name) ?? await target.StatAsync(destination, cancellation).ConfigureAwait(false) is not null))
                    await target.CreateDirectoryAsync(destination, cancellation).ConfigureAwait(false);

                HashSet<string> inside = (await target.ListAsync(destination, cancellation).ConfigureAwait(false)).Select(e => e.Name).ToHashSet();
                foreach (RemoteEntry child in children) await PlanEntryAsync(child, destination, inside).ConfigureAwait(false);
                return;
            }

            bool exists = existing?.Contains(name) ?? await target.StatAsync(destination, cancellation).ConfigureAwait(false) is not null;
            if (exists && !await ShouldCopyAsync(job, destination, cancellation).ConfigureAwait(false)) return;
            Plan(job, new TransferFile(job, entry.FullPath, destination, entry.Size, entry.ModifiedUtc));
        }

        foreach (RemoteEntry entry in job.RemoteSources) await PlanEntryAsync(entry, job.Target, null).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (TransferJob job in Jobs) await job.Control.CancelAsync().ConfigureAwait(false);

        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Nothing else can be done for a connection that won't close.
        }
    }
}

/// <summary>Turns names chosen by a server into names that are safe to create here.</summary>
public static class LocalNames
{
    private static readonly HashSet<string> ReservedOnWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Removes anything that could reach outside the chosen folder or that the file system would refuse.</summary>
    public static string Safe(string name)
    {
        string cleaned = string.Concat(name.Select(c => c is '/' or '\\' || c < ' ' || (OperatingSystem.IsWindows() && "<>:\"|?*".Contains(c)) ? '_' : c));
        if (OperatingSystem.IsWindows()) cleaned = cleaned.TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned is "." or "..") return "_";
        if (OperatingSystem.IsWindows() && ReservedOnWindows.Contains(cleaned.Split('.')[0])) return "_" + cleaned;
        return cleaned;
    }
}

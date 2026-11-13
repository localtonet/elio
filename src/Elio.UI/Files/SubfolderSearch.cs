using Avalonia.Threading;

namespace Elio.UI.Files;

/// <summary>
/// What a panel has found so far below the folder it shows. The walk runs in the background and what it finds is handed
/// over a few times a second, so that a folder with a hundred thousand files below it keeps the window moving, and it
/// stops the moment it is not wanted any more.
/// </summary>
internal sealed class SubfolderSearch(IFileSource source, Action changed)
{
    private static readonly TimeSpan HandOverEvery = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly List<FileRow> _arrived = [];
    private readonly List<FileRow> _rows = [];
    private CancellationTokenSource? _run;
    private bool _handOverScheduled;

    /// <summary>What was found below the folder, each with the folder it is in. Only to be read on the UI thread.</summary>
    public IReadOnlyList<FileRow> Rows => _rows;

    public bool IsRunning { get; private set; }

    /// <summary>How it ended, once it has.</summary>
    public FileScanSummary? Summary { get; private set; }

    /// <summary>Whether there is a walk running or finished whose rows are in <see cref="Rows"/>.</summary>
    public bool HasStarted => _run is not null;

    /// <summary>Looks through the folders of a folder, in place of what was found before.</summary>
    /// <param name="topLevel">What the panel shows: the folders in it are where the walk goes.</param>
    public void Start(IReadOnlyList<FileRow> topLevel)
    {
        Stop();
        var run = new CancellationTokenSource();
        _run = run;
        IsRunning = true;
        char separator = source.IsRemote ? '/' : Path.DirectorySeparatorChar;

        _ = Task.Run(async () =>
        {
            try
            {
                FileScanSummary summary = await FileTreeScanner.ScanAsync(source, topLevel, separator, batch => Arrive(batch, run), source.IsRemote ? 4 : 2, FileTreeScanner.DefaultLimit, run.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => Finish(run, summary));
            }
            catch (OperationCanceledException)
            {
                // Stopped by the user, or replaced by a newer walk.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Dispatcher.UIThread.Post(() => Finish(run, new FileScanSummary(0, 0, 0, Truncated: false)));
            }
        });
    }

    /// <summary>Ends a walk that is still going and forgets what it found.</summary>
    public void Stop()
    {
        _run?.Cancel();
        _run = null;
        IsRunning = false;
        Summary = null;
        _rows.Clear();
        lock (_gate)
        {
            _arrived.Clear();
            _handOverScheduled = false;
        }
    }

    // On a thread of the walk. Many folders are listed in a second, and each is not worth waking the window for.
    private void Arrive(IReadOnlyList<FileRow> batch, CancellationTokenSource run)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_run, run)) return;
            _arrived.AddRange(batch);
            if (_handOverScheduled) return;
            _handOverScheduled = true;
        }

        _ = Task.Delay(HandOverEvery).ContinueWith(_ => Dispatcher.UIThread.Post(() => HandOver(run)), TaskScheduler.Default);
    }

    private void HandOver(CancellationTokenSource run)
    {
        if (!ReferenceEquals(_run, run)) return;
        if (Take()) changed();
    }

    private bool Take()
    {
        lock (_gate)
        {
            _handOverScheduled = false;
            if (_arrived.Count == 0) return false;
            _rows.AddRange(_arrived);
            _arrived.Clear();
            return true;
        }
    }

    private void Finish(CancellationTokenSource run, FileScanSummary summary)
    {
        if (!ReferenceEquals(_run, run)) return;
        Take();
        IsRunning = false;
        Summary = summary;
        changed();
    }
}

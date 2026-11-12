using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Elio.Connections;
using Elio.Core;

namespace Elio.UI.Files;

/// <summary>What the file connection is doing. Refreshes only while something is moving.</summary>
public sealed class TransferListView : UserControl
{
    private const double SpeedSmoothing = 0.3;

    private sealed class Row
    {
        public required TransferJob Job { get; init; }
        public required TextBlock Title { get; init; }
        public required TextBlock Detail { get; init; }
        public required ProgressBar Bar { get; init; }
        public required Button Pause { get; init; }
        public required Button Cancel { get; init; }
        public required Button Retry { get; init; }
        public long LastBytes { get; set; }
        public double Speed { get; set; }
        public DateTime LastSample { get; set; } = DateTime.UtcNow;
    }

    private readonly TransferQueue _queue;
    private readonly StackPanel _rows = new() { Spacing = 2 };
    private readonly Dictionary<TransferJob, Row> _known = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly TextBlock _summary = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = ElioColors.Text, VerticalAlignment = VerticalAlignment.Center, Tag = "transfers-summary" };

    private readonly string[]? _servers;

    public TransferListView(TransferQueue queue) : this(queue, null, null)
    {
    }

    /// <summary>A list for a queue with two servers, which says by name where each job is going.</summary>
    public TransferListView(TransferQueue queue, string? first, string? second)
    {
        _queue = queue;
        _servers = first is null || second is null ? null : [first, second];
        _timer.Tick += (_, _) => Refresh();
        queue.Changed += () => Dispatcher.UIThread.Post(Sync);

        var clear = new Button { Content = L.T("Clear finished"), FontSize = ElioStyle.SmallFontSize, Padding = new Thickness(9, 2), CornerRadius = new CornerRadius(6), Tag = "transfers-clear" };
        clear.Click += (_, _) => queue.RemoveFinished();

        var header = new DockPanel { Margin = new Thickness(12, 6, 8, 5) };
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(clear);
        header.Children.Add(_summary);
        DockPanel.SetDock(header, Dock.Top);

        Content = new Border
        {
            Child = new DockPanel { Children = { header, new ScrollViewer { Content = _rows } } },
        };
        Sync();
    }

    private void Sync()
    {
        IReadOnlyList<TransferJob> jobs = _queue.Jobs;

        foreach (TransferJob gone in _known.Keys.Where(j => !jobs.Contains(j)).ToList())
        {
            _rows.Children.Remove((Control)_known[gone].Title.Parent!.Parent!.Parent!);
            _known.Remove(gone);
        }

        // Newest first, so what was just started is what's on screen.
        foreach (TransferJob job in jobs.Where(j => !_known.ContainsKey(j))) _rows.Children.Insert(0, BuildRow(job));

        Refresh();
        _timer.IsEnabled = jobs.Any(j => !j.IsFinished && j.State != TransferState.Paused);
    }

    private Control BuildRow(TransferJob job)
    {
        Button Small(string text, string tag, Action action)
        {
            var button = new Button { Content = text, FontSize = ElioStyle.SmallFontSize, Padding = new Thickness(9, 2), CornerRadius = new CornerRadius(6), Tag = tag };
            button.Click += (_, _) => action();
            return button;
        }

        var title = new TextBlock { FontSize = 12, Foreground = ElioColors.Text, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "transfer-title" };
        var detail = new TextBlock { FontSize = 11, Foreground = ElioColors.TextMuted, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "transfer-detail" };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 3, MinHeight = 3, Foreground = ElioColors.Accent, Background = ElioColors.Surface, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 4, 0, 0), Tag = "transfer-bar" };

        Button pause = Small(L.T("Pause"), "transfer-pause", () =>
        {
            if (job.State == TransferState.Paused) _queue.Resume(job);
            else _queue.Pause(job);
        });
        Button cancel = Small(L.T("Cancel"), "transfer-cancel", () => _queue.Cancel(job));
        Button retry = Small(L.T("Retry"), "transfer-retry", () => _queue.Retry(job));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { retry, pause, cancel } };
        DockPanel.SetDock(actions, Dock.Right);

        var row = new Border
        {
            Padding = new Thickness(12, 4, 8, 4),
            Child = new DockPanel
            {
                Children =
                {
                    actions,
                    new StackPanel { Children = { title, detail, bar } },
                },
            },
        };

        _known[job] = new Row { Job = job, Title = title, Detail = detail, Bar = bar, Pause = pause, Cancel = cancel, Retry = retry };
        return row;
    }

    private void Refresh()
    {
        DateTime now = DateTime.UtcNow;
        double totalSpeed = 0;

        foreach (Row row in _known.Values)
        {
            TransferJob job = row.Job;
            double seconds = (now - row.LastSample).TotalSeconds;
            if (seconds > 0.05)
            {
                double instant = Math.Max(0, job.DoneBytes - row.LastBytes) / seconds;
                row.Speed = row.Speed == 0 ? instant : ((1 - SpeedSmoothing) * row.Speed) + (SpeedSmoothing * instant);
                row.LastBytes = job.DoneBytes;
                row.LastSample = now;
            }

            bool moving = job.State == TransferState.Running;
            if (moving) totalSpeed += row.Speed;

            row.Title.Text = TitleOf(job);
            row.Bar.Value = job.TotalBytes == 0 ? (job.IsFinished ? 100 : 0) : Math.Min(100, 100.0 * job.DoneBytes / job.TotalBytes);
            row.Detail.Text = Describe(job, row.Speed);
            row.Detail.Foreground = job.State is TransferState.Failed or TransferState.CompletedWithErrors ? ElioColors.Danger : ElioColors.TextMuted;

            row.Pause.Content = job.State == TransferState.Paused ? L.T("Resume") : L.T("Pause");
            row.Pause.IsVisible = !job.IsFinished;
            row.Cancel.IsVisible = !job.IsFinished;
            row.Retry.IsVisible = job.State is TransferState.Failed or TransferState.CompletedWithErrors or TransferState.Canceled;
        }

        int active = _known.Keys.Count(j => !j.IsFinished);
        _summary.Text = active == 0 ? L.T("Transfers") : L.T("Transfers: {0} jobs · {1}/s", active, FileText.Size((long)totalSpeed));
    }

    // With one server the arrow says it all. With two, every line names the server, or both for a copy between them.
    private string TitleOf(TransferJob job)
    {
        string arrow = job.Kind switch { TransferKind.Upload => "↑", TransferKind.Download => "↓", _ => "⇄" };
        if (_servers is null) return $"{arrow} {job.Title}  →  {job.Target}";

        string server = _servers[(int)job.Server];
        return job.Kind switch
        {
            TransferKind.Copy => $"{arrow} {job.Title}  ·  {server}  →  {_servers[(int)job.TargetServer]}: {job.Target}",
            TransferKind.Upload => $"{arrow} {job.Title}  →  {server}: {job.Target}",
            _ => $"{arrow} {job.Title}  ·  {server}  →  {job.Target}",
        };
    }

    private static string Describe(TransferJob job, double speed)
    {
        string amounts = $"{FileText.Size(job.DoneBytes)} / {FileText.Size(job.TotalBytes)}";
        string files = job.TotalFiles > 1 ? L.T("{0}/{1} files", job.DoneFiles, job.TotalFiles) + " · " : string.Empty;

        return job.State switch
        {
            TransferState.Queued => L.T("Queued"),
            TransferState.Preparing => L.T("Preparing... {0} files found", job.TotalFiles),
            TransferState.Running when job.TotalBytes > job.DoneBytes && speed > 0 => L.T("{0} · {1}/s · {2} left", files + amounts, FileText.Size((long)speed), FileText.Duration((job.TotalBytes - job.DoneBytes) / speed)),
            TransferState.Running => $"{files}{amounts}",
            TransferState.Reconnecting => job.Note ?? L.T("Reconnecting..."),
            TransferState.Paused => $"{L.T("Paused")} · {files}{amounts}",
            TransferState.Completed => $"{L.T("Completed")} · {files}{FileText.Size(job.TotalBytes)}",
            TransferState.CompletedWithErrors => $"{L.T("{0} errors", job.Errors.Count)} · {job.Errors[0]}",
            TransferState.Failed => job.Errors.Count > 0 ? L.T("Failed: {0}", job.Errors[0]) : L.T("Failed"),
            _ => L.T("Canceled"),
        };
    }
}

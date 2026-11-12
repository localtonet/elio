using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Elio.Core;

namespace Elio.UI;

/// <summary>
/// A column beside the terminals that shows what AI tools are doing through Elio: each command as it starts,
/// and what it printed when it ended. It is the place to watch them work, where the line above the terminals
/// only says that something happened.
/// </summary>
public sealed class AiActivityPanel : UserControl
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, monospace");

    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _empty;
    private readonly Dictionary<string, Border> _cards = [];

    /// <summary>Raised when the panel's own close button is pressed.</summary>
    public event Action? CloseRequested;

    public AiActivityPanel()
    {
        Width = 420;
        Tag = "ai-panel";

        Button close = ElioStyle.IconButton(ElioStyle.Icon(ElioStyle.IconClose, ElioColors.TextMuted, 12, 1.8), L.T("Close"), 26);
        close.Tag = "ai-close";
        close.Click += (_, _) => CloseRequested?.Invoke();

        var clear = new Button
        {
            Content = L.T("Clear"),
            Padding = new Thickness(10, 2),
            FontSize = ElioStyle.SmallFontSize,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = "ai-clear",
        };
        clear.Click += (_, _) => AiRuns.Clear();

        var title = new TextBlock { Text = L.T("AI tools"), FontWeight = FontWeight.SemiBold, Foreground = ElioColors.Text, VerticalAlignment = VerticalAlignment.Center };
        var header = new DockPanel { Margin = new Thickness(14, 10, 8, 8) };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(clear, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(clear);
        header.Children.Add(title);

        _empty = new TextBlock
        {
            Text = L.T("No AI tool has run a command yet."),
            Foreground = ElioColors.TextMuted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 4, 4, 0),
        };
        _scroll = new ScrollViewer { Content = new StackPanel { Children = { _empty, _rows }, Margin = new Thickness(10, 0, 10, 10) } };

        var layout = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(_scroll);

        Content = new Border
        {
            Background = ElioColors.Chrome,
            BorderBrush = ElioColors.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ElioStyle.CardRadius),
            Margin = new Thickness(0, 0, 2 * ContentPane.PaneMargin, 2 * ContentPane.PaneMargin),
            ClipToBounds = true,
            Child = layout,
        };

        foreach (AiRun run in AiRuns.All) Show(run);
        AiRuns.Started += Show;
        AiRuns.Ended += Show;
        AiRuns.Cleared += Forget;
    }

    private void Forget()
    {
        _rows.Children.Clear();
        _cards.Clear();
        _empty.IsVisible = true;
    }

    // Draws a command, or draws it again now that it has ended, and keeps the newest in view.
    private void Show(AiRun run)
    {
        _empty.IsVisible = false;
        Control content = Describe(run);
        if (_cards.TryGetValue(run.Id, out Border? card))
        {
            card.Child = content;
        }
        else
        {
            card = new Border
            {
                Background = ElioColors.Surface,
                BorderBrush = ElioColors.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(ElioStyle.ControlRadius),
                Padding = new Thickness(10, 8),
                Child = content,
            };
            _cards[run.Id] = card;
            _rows.Children.Add(card);
        }

        Dispatcher.UIThread.Post(_scroll.ScrollToEnd, DispatcherPriority.Background);
    }

    private static StackPanel Describe(AiRun run)
    {
        bool running = run.Outcome is null;
        bool fine = run.Outcome == "exit 0";
        string state = running ? L.T("running…") : $"{run.Outcome} · {run.Duration.TotalSeconds:0.0} s";

        var rows = new StackPanel { Spacing = 6 };
        rows.Children.Add(new TextBlock
        {
            Text = $"{run.StartedUtc.ToLocalTime():T}  ·  {run.Server}  ·  {state}",
            FontSize = 12,
            Foreground = running ? ElioColors.Accent : fine ? ElioColors.TextMuted : ElioColors.Danger,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        rows.Children.Add(new SelectableTextBlock { Text = "$ " + run.Command, FontFamily = Mono, FontSize = 12.5, Foreground = ElioColors.Text, TextWrapping = TextWrapping.Wrap });

        if (run.Output.Length > 0) rows.Children.Add(Printed(run.Output, ElioColors.Text));
        if (run.Error.Length > 0) rows.Children.Add(Printed(run.Error, ElioColors.Danger));
        return rows;
    }

    // What a command printed, in a box that scrolls on its own once it is long, so one chatty command does not
    // push the rest away.
    private static Border Printed(string text, IBrush color)
    {
        var printed = new SelectableTextBlock { Text = text.TrimEnd('\n', '\r'), FontFamily = Mono, FontSize = 12, Foreground = color };
        var scroll = new ScrollViewer { MaxHeight = 220, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = printed };

        // The bar under long lines floats over the text, so it would lie on the last line. Room is left below
        // for it, but only where there is such a bar: a short answer keeps its even edges.
        void LeaveRoom() => printed.Margin = new Thickness(0, 0, 0, scroll.Extent.Width > scroll.Viewport.Width + 0.5 ? BarRoom : 0);
        scroll.GetObservable(ScrollViewer.ExtentProperty).Subscribe(new Listener<Size>(_ => LeaveRoom()));
        scroll.GetObservable(ScrollViewer.ViewportProperty).Subscribe(new Listener<Size>(_ => LeaveRoom()));

        return new Border { Background = ElioColors.Window, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6), Child = scroll };
    }

    private const double BarRoom = 14;

    private sealed class Listener<T>(Action<T> changed) : IObserver<T>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(T value) => changed(value);
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Elio.Connections;
using Elio.Core;

namespace Elio.UI;

public delegate Task<CommandResult> HostCommandRunner(HostEntry host, string command, CancellationToken cancellation);

public sealed partial class DialogHost
{
    private const int MaxParallelRuns = 8;
    private const int MaxOutputShown = 64 * 1024;

    private sealed class RunRow(HostEntry host, CheckBox pick, TextBlock status, Button details, TextBox output)
    {
        public HostEntry Host { get; } = host;
        public CheckBox Pick { get; } = pick;
        public TextBlock Status { get; } = status;
        public Button Details { get; } = details;
        public TextBox Output { get; } = output;
        public bool Failed { get; set; }
    }

    /// <summary>Asks what to do with a file that already exists. Closing the dialog skips it.</summary>
    public Task<(ConflictChoice Choice, bool ForAll)> AskConflictAsync(string path) => Enqueue((ConflictChoice.Skip, false), result =>
    {
        var forAll = new CheckBox { Content = L.T("Do the same for the other files in this job"), Foreground = ElioColors.Text, Tag = "conflict-all" };
        Button overwrite = MakeButton(L.T("Overwrite"), primary: false, danger: true);
        Button skip = MakeButton(L.T("Skip"), primary: false);
        overwrite.Click += (_, _) => result.TrySetResult((ConflictChoice.Overwrite, forAll.IsChecked == true));
        skip.Click += (_, _) => result.TrySetResult((ConflictChoice.Skip, forAll.IsChecked == true));
        return Frame(L.T("File already exists"),new StackPanel { Spacing = 10, Children = { Paragraph(path), forAll } }, skip, overwrite);
    });

    /// <summary>Asks whether to keep a file the server is pushing through the terminal.</summary>
    public Task<(bool Accept, bool ForAll)> AskZmodemFileAsync(string name, string size, string folder) => Enqueue((false, false), result =>
    {
        var forAll = new CheckBox { Content = L.T("Also accept the other files in this transfer"), Foreground = ElioColors.Text, Tag = "zmodem-all" };
        Button accept = MakeButton(L.T("Save"), primary: true);
        Button refuse = MakeButton(L.T("Decline"), primary: false);
        accept.Click += (_, _) => result.TrySetResult((true, forAll.IsChecked == true));
        refuse.Click += (_, _) => result.TrySetResult((false, false));
        return Frame(L.T("The server is sending a file"), new StackPanel { Spacing = 10, Children = { Paragraph(L.T("{0} ({1})\n\nWill be saved to: {2}", name, size, folder)), forAll } }, refuse, accept);
    });

    /// <summary>Shows a message while some work runs. Returns true if the work finished, false if the user cancelled.</summary>
    /// <param name="code">A code the user compares with one shown elsewhere. When given, <paramref name="message"/> holds {0} where the code belongs, and it is shown large on a line of its own.</param>
    public Task<bool> WaitAsync(string title, string message, string? actionText, Action? action, Func<CancellationToken, Task> work, string? code = null) => Enqueue(false, result =>
    {
        var cancel = new CancellationTokenSource();
        Button stop = MakeButton(L.T("Cancel"), primary: false);
        stop.Click += (_, _) =>
        {
            cancel.Cancel();
            result.TrySetResult(false);
        };

        var buttons = new List<Button> { stop };
        if (actionText is not null)
        {
            Button extra = MakeButton(actionText, primary: true);
            extra.Click += (_, _) => action?.Invoke();
            buttons.Add(extra);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await work(cancel.Token);
                result.TrySetResult(true);
            }
            catch (OperationCanceledException)
            {
                result.TrySetResult(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result.TrySetException(ex);
            }
        });

        var progress = new ProgressBar { IsIndeterminate = true, Height = 3, MinHeight = 3, CornerRadius = new CornerRadius(1.5) };
        int at = code is null ? -1 : message.IndexOf("{0}", StringComparison.Ordinal);
        if (code is null || at < 0) return Frame(title, new StackPanel { Spacing = 10, Children = { Paragraph(code is null ? message : string.Format(message, code)), progress } }, [.. buttons]);

        // The code is what the eye has to find twice, here and in the browser, so it stands apart from the sentences around it.
        var badge = new Border
        {
            Background = ElioColors.Surface,
            BorderBrush = ElioColors.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ElioStyle.ControlRadius),
            Padding = new Thickness(12, 10),
            Child = new SelectableTextBlock { Text = code, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 22, FontWeight = FontWeight.SemiBold, LetterSpacing = 2, Foreground = ElioColors.Text, HorizontalAlignment = HorizontalAlignment.Center, Tag = "wait-code" },
        };
        TextBlock after = Paragraph(message[(at + 3)..].Trim());
        after.Foreground = ElioColors.TextMuted;
        return Frame(title, new StackPanel { Spacing = 12, Children = { Paragraph(message[..at].Trim()), badge, after, progress } }, [.. buttons]);
    });

    /// <summary>Lists the commands AI tools have run through Elio, newest first: when, where, how it ended, and the command.</summary>
    public Task ShowAiCommandsAsync(IReadOnlyList<AiCommand> commands) => Enqueue(false, result =>
    {
        Button close = MakeButton(L.T("Close"), primary: true);
        close.Click += (_, _) => result.TrySetResult(true);

        var rows = new StackPanel { Spacing = 10 };
        if (commands.Count == 0) rows.Children.Add(Paragraph(L.T("No AI tool has run a command yet.")));
        foreach (AiCommand command in commands)
        {
            bool fine = command.Outcome == "exit 0";
            rows.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{command.Utc.ToLocalTime():g}  ·  {command.Server}  ·  {command.Outcome}",
                        FontSize = 12,
                        Foreground = fine ? ElioColors.TextMuted : ElioColors.Danger,
                    },
                    new SelectableTextBlock { Text = command.Command, FontFamily = new FontFamily("Consolas, Menlo, monospace"), Foreground = ElioColors.Text, TextWrapping = TextWrapping.Wrap },
                },
            });
        }

        var list = new ScrollViewer { MaxHeight = 380, Content = rows, Padding = ScrollInset, Margin = ScrollOutset };
        Control frame = Frame(L.T("Commands run by AI tools"), list, close);
        if (frame is Border card) card.Width = 560;
        return frame;
    });

    public Task ShowMessageAsync(string title, string message) => Enqueue(false, result =>
    {
        Button ok = MakeButton(L.T("OK"), primary: true);
        ok.Click += (_, _) => result.TrySetResult(true);
        return Frame(title, Paragraph(message), ok);
    });

    /// <summary>
    /// Shows a shared terminal's link and its password, each with a button that copies that one alone: the two
    /// are meant to reach the other person by different ways, so they are never copied together.
    /// </summary>
    public Task ShowShareAsync(string link, string password, Func<string, Task> copy) => Enqueue(false, result =>
    {
        Button ok = MakeButton(L.T("OK"), primary: true);
        ok.Click += (_, _) => result.TrySetResult(true);

        Control Row(string label, string value, string tag)
        {
            Button button = MakeButton(L.T("Copy"), primary: false);
            button.VerticalAlignment = VerticalAlignment.Center;
            button.Tag = tag;
            button.Click += async (_, _) =>
            {
                await copy(value);

                // A mark in place of the word for a moment, so a press is seen to have done something.
                button.Content = "✓";
                await Task.Delay(1200);
                button.Content = L.T("Copy");
            };
            Grid.SetColumn(button, 1);

            var text = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = label, Foreground = ElioColors.TextMuted, FontSize = ElioStyle.BaseFontSize - 1 },
                    new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Foreground = ElioColors.Text },
                },
            };

            return new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12, Children = { text, button } };
        }

        var body = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Row(L.T("Link"), link, "copy-link"),
                Paragraph(L.T("Whoever opens the link is asked for this password. Give it to them another way, by phone or in a different chat.")),
                Row(L.T("Password"), password, "copy-password"),
            },
        };
        return Frame(L.T("Share this terminal"), body, ok);
    });

    /// <returns>The snippet as edited, or null if cancelled.</returns>
    public Task<Snippet?> EditSnippetAsync(Snippet? existing) => Enqueue<Snippet?>(null, result =>
    {
        TextBox name = Input(existing?.Name, tag: "snippet-name");
        TextBox command = MultiLine(existing?.Command, "snippet-command");
        Button save = MakeButton(L.T("Save"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);

        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(command.Text)) return;
            string body = command.Text.Trim();
            result.TrySetResult(new Snippet { Id = existing?.Id ?? Guid.NewGuid(), Name = string.IsNullOrWhiteSpace(name.Text) ? body.Split('\n')[0].Trim() : name.Text.Trim(), Command = body });
        };
        cancel.Click += (_, _) => result.TrySetResult(null);
        FocusWhenShown(name);

        return Frame(
            existing is null ? L.T("Add command") : L.T("Edit command"),
            new StackPanel { Spacing = 12, Children = { Field(L.T("Name"), name), Field(L.T("Command (each line is typed in order)"), command) } },
            cancel,
            save);
    });

    /// <summary>Runs one command on the chosen servers at once and shows what happened on each.</summary>
    public Task RunOnHostsAsync(IReadOnlyList<HostEntry> hosts, IReadOnlyCollection<HostEntry> preselected, string? command, HostCommandRunner runner) => Enqueue(false, result =>
    {
        TextBox input = MultiLine(command, "run-command");
        var rows = new StackPanel { Spacing = 2 };
        var runs = new List<RunRow>();
        Button run = MakeButton(L.T("Run"), primary: true);
        Button retry = MakeButton(L.T("Run failed ones again"), primary: false);
        Button close = MakeButton(L.T("Close"), primary: false);
        retry.IsVisible = false;
        retry.Tag = "run-retry";
        CancellationTokenSource? running = null;

        foreach (HostEntry host in hosts)
        {
            var pick = new CheckBox { Content = host.Name, IsChecked = preselected.Contains(host), Foreground = ElioColors.Text, Tag = "run-pick" };
            var status = new TextBlock { Foreground = ElioColors.TextMuted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0), Tag = "run-status" };
            var details = new Button { Content = L.T("Output"), FontSize = ElioStyle.SmallFontSize, Padding = new Thickness(9, 2), CornerRadius = new CornerRadius(6), IsVisible = false, Tag = "run-details" };
            var output = new TextBox { IsReadOnly = true, IsVisible = false, TextWrapping = TextWrapping.Wrap, MaxHeight = 200, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12, Margin = new Thickness(0, 2, 0, 6), Tag = "run-output" };
            details.Click += (_, _) => output.IsVisible = !output.IsVisible;

            runs.Add(new RunRow(host, pick, status, details, output));
            rows.Children.Add(new StackPanel { Children = { new DockPanel { Children = { DockRight(details), DockRight(status), pick } }, output } });
        }

        async Task Start(IReadOnlyList<RunRow> chosen)
        {
            if (running is not null || chosen.Count == 0 || string.IsNullOrWhiteSpace(input.Text)) return;

            string text = input.Text.Trim();
            using var cancel = new CancellationTokenSource();
            running = cancel;
            run.Content = L.T("Stop");
            retry.IsVisible = false;

            foreach (RunRow row in chosen) ShowWaiting(row);

            using var slots = new SemaphoreSlim(MaxParallelRuns);
            await Task.WhenAll(chosen.Select(row => RunOne(row, text, runner, slots, cancel.Token)));

            running = null;
            run.Content = L.T("Run");
            retry.IsVisible = runs.Any(r => r.Failed);
        }

        run.Click += (_, _) =>
        {
            if (running is not null) running.Cancel();
            else _ = Start(runs.Where(r => r.Pick.IsChecked == true).ToList());
        };
        retry.Click += (_, _) => _ = Start(runs.Where(r => r.Failed).ToList());
        close.Click += (_, _) =>
        {
            running?.Cancel();
            result.TrySetResult(true);
        };
        FocusWhenShown(input);

        Border card = Card(
            new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    Title(L.T("Run a command on multiple servers")),
                    Field(L.T("Command"), input),
                    new ScrollViewer { MaxHeight = 320, Margin = ScrollOutset, Content = new Border { Padding = ScrollInset, Child = rows } },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { close, retry, run } },
                },
            },
            640);
        return card;
    });

    private static void ShowWaiting(RunRow row)
    {
        row.Failed = false;
        row.Status.Text = L.T("running...");
        row.Status.Foreground = ElioColors.TextMuted;
        row.Details.IsVisible = false;
        row.Output.IsVisible = false;
        row.Output.Text = string.Empty;
    }

    private static async Task RunOne(RunRow row, string command, HostCommandRunner runner, SemaphoreSlim slots, CancellationToken cancellation)
    {
        CommandResult outcome;
        try
        {
            await slots.WaitAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            row.Failed = true;
            row.Status.Text = L.T("stopped");
            return;
        }

        try
        {
            outcome = await runner(row.Host, command, cancellation);
        }
        finally
        {
            slots.Release();
        }

        row.Failed = !outcome.Succeeded;
        row.Status.Foreground = outcome.Succeeded ? ElioColors.Accent : ElioColors.Danger;
        row.Status.Text = outcome.Failure is not null
            ? L.T("✕ could not connect")
            : L.T("{0} exit {1} · {2:0.0} s", outcome.Succeeded ? "✓" : "✕", outcome.ExitCode, outcome.Duration.TotalSeconds);

        string shown = outcome.Failure ?? (outcome.Output + outcome.Error);
        row.Output.Text = shown.Length <= MaxOutputShown ? shown : shown[^MaxOutputShown..];
        row.Details.IsVisible = shown.Length > 0;
    }
}

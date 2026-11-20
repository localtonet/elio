using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Elio.Connections;
using Elio.Core;
using Elio.UI.Files;

namespace Elio.UI;

/// <summary>What the user chose to pack files into.</summary>
/// <param name="FileName">The name of the archive, with its extension.</param>
public sealed record ArchiveChoice(string FileName, ArchiveKind Kind);

// The dialogs of the file screen.
public sealed partial class DialogHost
{
    // The changes shown before the rest are counted: enough to see what the others will look like.
    private const int RenamePreviewRows = 12;

    /// <returns>The name and kind of the archive to make, or null if cancelled.</returns>
    /// <param name="onServer">The archive is made on a server, where .tar.gz is the one that is always there.</param>
    public Task<ArchiveChoice?> AskArchiveAsync(string suggestedName, bool onServer) => Enqueue<ArchiveChoice?>(null, result =>
    {
        ArchiveKind[] kinds = onServer ? [ArchiveKind.TarGz, ArchiveKind.Zip, ArchiveKind.Tar] : [ArchiveKind.Zip, ArchiveKind.TarGz, ArchiveKind.Tar];
        TextBox name = Input(suggestedName, tag: "archive-name");
        var kind = new ComboBox { ItemsSource = kinds.Select(Archives.Extension).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Tag = "archive-kind" };
        Button ok = MakeButton(L.T("Compress"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);

        void Submit()
        {
            string text = name.Text?.Trim() ?? string.Empty;
            ArchiveKind chosen = kinds[Math.Max(0, kind.SelectedIndex)];
            string extension = Archives.Extension(chosen);
            if (text.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) text = text[..^extension.Length];
            if (!FileNaming.IsPlainName(text)) return;

            result.TrySetResult(new ArchiveChoice(text + extension, chosen));
        }

        ok.Click += (_, _) => Submit();
        cancel.Click += (_, _) => result.TrySetResult(null);
        SubmitOnEnter(name, Submit);
        FocusWhenShown(name);

        var body = new StackPanel { Spacing = 10, Children = { Field(L.T("Name"), name), Field(L.T("Kind"), kind) } };
        if (onServer) body.Children.Add(new TextBlock { Text = L.T(".zip needs the zip program to be on the server."), FontSize = 12, Foreground = ElioColors.TextMuted, TextWrapping = TextWrapping.Wrap });
        return Frame(L.T("Compress"), body, cancel, ok);
    });

    /// <summary>
    /// Asks how several files are to be renamed, showing what each would be called as the answer is changed.
    /// </summary>
    /// <param name="names">The names to change, in the order they are shown.</param>
    /// <param name="siblings">Every name in the folder, so that a new name that is already taken is seen.</param>
    /// <returns>The plan to carry out, or null if cancelled.</returns>
    public Task<BulkRenamePlan?> EditBulkRenameAsync(IReadOnlyList<string> names, IReadOnlyCollection<string> siblings) => Enqueue<BulkRenamePlan?>(null, result =>
    {
        var mode = new ComboBox
        {
            ItemsSource = new[] { L.T("Find and replace"), L.T("Number them"), L.T("Change upper and lower case"), L.T("Add text before or after") },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = "rename-mode",
        };

        TextBox find = Input(tag: "rename-find");
        TextBox replace = Input(tag: "rename-replace");
        CheckBox regex = Check(L.T("Regular expression"), false, "rename-regex");
        CheckBox ignoreCase = Check(L.T("Ignore case"), true, "rename-ignore-case");
        var findPanel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Field(L.T("Find"), find),
                Field(L.T("Replace with"), replace),
                new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 16, LineSpacing = 6, Children = { regex, ignoreCase } },
            },
        };

        TextBox template = Input("{name}-{n}", tag: "rename-template");
        TextBox start = Input("1", tag: "rename-start");
        TextBox step = Input("1", tag: "rename-step");
        TextBox pad = Input("0", tag: "rename-pad");
        var counting = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        void Column(Control field, int column, double right)
        {
            field.Margin = new Thickness(0, 0, right, 0);
            Grid.SetColumn(field, column);
            counting.Children.Add(field);
        }

        Column(Field(L.T("Start at"), start), 0, 8);
        Column(Field(L.T("Step"), step), 1, 8);
        Column(Field(L.T("Digits"), pad), 2, 0);
        var numberPanel = new StackPanel
        {
            Spacing = 10,
            Children = { Field(L.T("New name: {name} is the old one, {n} the number, {ext} the extension"), template), counting },
        };

        var caseBox = new ComboBox
        {
            ItemsSource = new[] { L.T("all lowercase"), L.T("ALL UPPERCASE"), L.T("Each Word Starts With A Capital") },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = "rename-case",
        };
        var casePanel = new StackPanel { Children = { Field(L.T("Write the names in"), caseBox) } };

        TextBox prefix = Input(tag: "rename-prefix");
        TextBox suffix = Input(tag: "rename-suffix");
        var affixPanel = new StackPanel { Spacing = 10, Children = { Field(L.T("Add before the name"), prefix), Field(L.T("Add after the name, before the extension"), suffix) } };

        CheckBox keepExtension = Check(L.T("Leave the extensions as they are"), true, "rename-keep-extension");
        var preview = new StackPanel { Spacing = 3, Tag = "rename-preview" };
        var summary = new TextBlock { FontSize = 12, Foreground = ElioColors.TextMuted, TextWrapping = TextWrapping.Wrap, Tag = "rename-summary" };

        Button ok = MakeButton(L.T("Rename"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);
        BulkRenamePlan plan = new([], []);

        static int Number(TextBox box, int fallback) => int.TryParse(box.Text?.Trim(), out int value) ? Math.Clamp(value, -1_000_000, 1_000_000) : fallback;

        void Recompute()
        {
            int chosen = Math.Max(0, mode.SelectedIndex);
            findPanel.IsVisible = chosen == 0;
            numberPanel.IsVisible = chosen == 1;
            casePanel.IsVisible = chosen == 2;
            affixPanel.IsVisible = chosen == 3;

            plan = BulkRename.Plan(names, siblings, new BulkRenameOptions
            {
                Mode = (BulkRenameMode)chosen,
                Find = find.Text ?? string.Empty,
                Replace = replace.Text ?? string.Empty,
                Regex = regex.IsChecked == true,
                IgnoreCase = ignoreCase.IsChecked == true,
                Template = template.Text ?? string.Empty,
                Start = Number(start, 1),
                Step = Number(step, 1),
                Pad = Math.Clamp(Number(pad, 0), 0, 12),
                Case = (BulkRenameCase)Math.Max(0, caseBox.SelectedIndex),
                Prefix = prefix.Text ?? string.Empty,
                Suffix = suffix.Text ?? string.Empty,
                KeepExtension = keepExtension.IsChecked == true,
            });

            // What cannot be done is shown first, since that is what has to be put right before anything can happen.
            preview.Children.Clear();
            foreach (BulkRenameEntry entry in plan.Entries.OrderBy(e => e.Problem is null).ThenBy(e => e.New == e.Old).Take(RenamePreviewRows))
            {
                var line = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = ElioColors.TextMuted };
                line.Inlines = new InlineCollection
                {
                    new Run(entry.Old),
                    new Run("  →  "),
                    new Run(entry.New) { Foreground = entry.New == entry.Old ? ElioColors.TextMuted : ElioColors.Text, FontWeight = entry.New == entry.Old ? FontWeight.Normal : FontWeight.SemiBold },
                };
                if (entry.Problem is not null) line.Inlines.Add(new Run("  " + entry.Problem) { Foreground = ElioColors.Danger });
                preview.Children.Add(line);
            }

            int more = plan.Entries.Count - preview.Children.Count;
            if (more > 0) preview.Children.Add(new TextBlock { Text = L.T("... and {0} more", more), FontSize = 12, Foreground = ElioColors.TextFaint });

            ok.IsEnabled = plan.Changes > 0 && !plan.HasProblems;
            summary.Text = plan.HasProblems ? L.T("Some names cannot be used, so nothing can be renamed yet.") : plan.Changes == 0 ? L.T("No name would change.") : L.T("{0} of {1} names will change.", plan.Changes, names.Count);
            summary.Foreground = plan.HasProblems ? ElioColors.Danger : ElioColors.TextMuted;
        }

        foreach (TextBox box in new[] { find, replace, template, start, step, pad, prefix, suffix }) box.TextChanged += (_, _) => Recompute();
        foreach (CheckBox box in new[] { regex, ignoreCase, keepExtension }) box.IsCheckedChanged += (_, _) => Recompute();
        mode.SelectionChanged += (_, _) => Recompute();
        caseBox.SelectionChanged += (_, _) => Recompute();

        ok.Click += (_, _) =>
        {
            if (plan.Changes > 0 && !plan.HasProblems) result.TrySetResult(plan);
        };
        cancel.Click += (_, _) => result.TrySetResult(null);
        FocusWhenShown(find);
        Recompute();

        Control frame = Frame(
            L.T("Rename {0} items", names.Count),
            new ScrollViewer
            {
                MaxHeight = 520,
                Margin = ScrollOutset,
                Content = new StackPanel
                {
                    Margin = ScrollInset,
                    Spacing = 12,
                    Children = { Field(L.T("How"), mode), findPanel, numberPanel, casePanel, affixPanel, keepExtension, summary, preview },
                },
            },
            cancel,
            ok);

        if (frame is Border card) card.Width = 520;
        return frame;
    });
}

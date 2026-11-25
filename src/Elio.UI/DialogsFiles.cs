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

    /// <summary>Asks for new permissions, and for a folder whether what is in it is to change too.</summary>
    /// <param name="what">What is being changed, as the user will recognise it: a name in quotes, or how many.</param>
    /// <param name="current">The digits it has now, such as 644, or empty when that is not known or not the same for all.</param>
    /// <param name="offerContents">A folder is among what was chosen, so its contents can be changed as well.</param>
    /// <returns>The permissions and how far they reach, or null if cancelled.</returns>
    public Task<PermissionChoice?> AskPermissionsAsync(string what, string current, bool offerContents) => Enqueue<PermissionChoice?>(null, result =>
    {
        TextBox digits = Input(current, tag: "permission-digits");
        var meaning = new TextBlock { FontSize = 12, FontFamily = ElioStyle.CodeFont, Foreground = ElioColors.TextMuted, Tag = "permission-letters" };
        CheckBox inside = Check(L.T("Apply to what is inside the folder too"), false, "permission-recurse");

        // Each dialog's own group, so that two of them never share their choice.
        string group = "permission-scope-" + Guid.NewGuid().ToString("N");
        RadioButton Scope(string text, string tag, bool chosen) => new() { Content = text, GroupName = group, IsChecked = chosen, Foreground = ElioColors.Text, Tag = tag };
        RadioButton everything = Scope(L.T("To the folder and everything in it"), "permission-everything", true);
        RadioButton files = Scope(L.T("Only to the files in it"), "permission-files", false);
        RadioButton folders = Scope(L.T("Only to the folder and the folders in it"), "permission-folders", false);
        var scopes = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(26, 0, 0, 0),
            Children =
            {
                everything,
                files,
                folders,
                new TextBlock { Text = L.T("Links inside are left as they are, and not followed."), FontSize = 12, Foreground = ElioColors.TextMuted, TextWrapping = TextWrapping.Wrap },
            },
        };

        Button ok = MakeButton(L.T("Apply"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);

        void Update()
        {
            int? mode = PermissionBits.Parse(digits.Text);
            meaning.Text = mode is int bits ? PermissionBits.Letters(bits) : L.T("Permission must be three digits, for example 644.");
            meaning.Foreground = mode is null && (digits.Text?.Trim().Length ?? 0) > 0 ? ElioColors.Danger : ElioColors.TextMuted;
            scopes.IsEnabled = inside.IsChecked == true;
            ok.IsEnabled = mode is not null;
        }

        void Submit()
        {
            if (PermissionBits.Parse(digits.Text) is not int mode) return;

            PermissionScope scope = inside.IsChecked != true ? PermissionScope.OnlyThis
                : files.IsChecked == true ? PermissionScope.OnlyFiles
                : folders.IsChecked == true ? PermissionScope.OnlyFolders
                : PermissionScope.Everything;
            result.TrySetResult(new PermissionChoice(mode, scope));
        }

        digits.TextChanged += (_, _) => Update();
        inside.IsCheckedChanged += (_, _) => Update();
        ok.Click += (_, _) => Submit();
        cancel.Click += (_, _) => result.TrySetResult(null);
        SubmitOnEnter(digits, Submit);
        FocusWhenShown(digits);
        Update();

        var body = new StackPanel { Spacing = 10, Children = { Paragraph(what), Field(L.T("Numeric permission (example: 644)"), digits), meaning } };
        if (offerContents)
        {
            body.Children.Add(inside);
            body.Children.Add(scopes);
        }

        return Frame(L.T("Permissions"), body, cancel, ok);
    });

    /// <returns>The limits in KB a second, 0 meaning none, or null if cancelled.</returns>
    public Task<(int DownloadKb, int UploadKb)?> EditSpeedLimitsAsync(int downloadKb, int uploadKb) => Enqueue<(int, int)?>(null, result =>
    {
        // A hundred gigabytes a second: past anything a link carries, and far from where the bytes a second would overflow.
        const int MostKb = 100 * 1024 * 1024;

        static string Shown(int kb) => kb > 0 ? kb.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

        // Empty is no limit, like 0. Null for what is not a whole number of KB.
        static int? Read(TextBox box)
        {
            string text = box.Text?.Trim() ?? string.Empty;
            if (text.Length == 0) return 0;
            return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int kb) && kb <= MostKb ? kb : null;
        }

        TextBox down = Input(Shown(downloadKb), tag: "limit-download");
        TextBox up = Input(Shown(uploadKb), tag: "limit-upload");
        down.PlaceholderText = up.PlaceholderText = L.T("No limit");
        var problem = new TextBlock { Text = L.T("Write a whole number of KB a second, or leave it empty for no limit."), FontSize = 12, Foreground = ElioColors.Danger, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Button ok = MakeButton(L.T("Apply"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);

        void Submit()
        {
            if (Read(down) is int downKb && Read(up) is int upKb) result.TrySetResult((downKb, upKb));
            else problem.IsVisible = true;
        }

        ok.Click += (_, _) => Submit();
        cancel.Click += (_, _) => result.TrySetResult(null);
        SubmitOnEnter(down, Submit);
        SubmitOnEnter(up, Submit);
        FocusWhenShown(down);

        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Field(L.T("Downloads, in KB a second"), down),
                Field(L.T("Uploads, in KB a second"), up),
                new TextBlock
                {
                    Text = L.T("The limit is shared by all the transfers going that way, in every file tab. A copy between two servers counts as both, and goes by the lower one."),
                    FontSize = 12,
                    Foreground = ElioColors.TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                },
                problem,
            },
        };

        return Frame(L.T("Speed limit"), body, cancel, ok);
    });

    /// <summary>
    /// Shows what synchronising the two sides would copy, and lets the user choose the way it goes before anything happens.
    /// </summary>
    /// <param name="leftName">What the left side is, such as "this computer" or a server's name.</param>
    /// <param name="plan">Works out what would be copied for a direction, and whether files newer on the other side are replaced.</param>
    /// <returns>The plan to carry out, or null if cancelled.</returns>
    public Task<SyncPlan?> AskSyncAsync(string leftName, string rightName, Func<SyncDirection, bool, SyncPlan> plan) => Enqueue<SyncPlan?>(null, result =>
    {
        const int ShownRows = 60;

        var direction = new ComboBox
        {
            ItemsSource = new[]
            {
                L.T("From {0} to {1}", leftName, rightName),
                L.T("From {0} to {1}", rightName, leftName),
                L.T("Both ways: the newer file wins"),
            },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = "sync-direction",
        };
        CheckBox replaceNewer = Check(L.T("Also replace files that are newer on the other side"), false, "sync-replace-newer");
        var summary = new TextBlock { FontSize = 13, Foreground = ElioColors.Text, TextWrapping = TextWrapping.Wrap, Tag = "sync-summary" };
        var preview = new StackPanel { Spacing = 3, Tag = "sync-preview" };
        var note = new TextBlock { FontSize = 12, Foreground = ElioColors.TextMuted, TextWrapping = TextWrapping.Wrap };
        Button ok = MakeButton(L.T("Synchronise"), primary: true);
        Button cancel = MakeButton(L.T("Cancel"), primary: false);
        SyncPlan current = plan(SyncDirection.LeftToRight, false);

        string Reason(SkipReason reason) => reason switch
        {
            SkipReason.NewerOnTheOtherSide => L.T("newer on the other side"),
            SkipReason.CannotTellWhichIsNewer => L.T("same time, different size: which is newer cannot be told"),
            SkipReason.NamesDifferInCase => L.T("the names differ only in upper and lower case"),
            SkipReason.NamesClashByCase => L.T("names that differ only in case would take each other's place"),
            SkipReason.FileAndFolder => L.T("a file on one side, a folder on the other"),
            SkipReason.Unreadable => L.T("could not be read"),
            _ => L.T("a folder, which is only copied when subfolders are included"),
        };

        TextBlock Line(string text, IBrush brush) => new() { Text = text, FontSize = 12, Foreground = brush, TextTrimming = TextTrimming.CharacterEllipsis };

        void Recompute()
        {
            var chosen = (SyncDirection)Math.Max(0, direction.SelectedIndex);
            replaceNewer.IsVisible = chosen != SyncDirection.BothWays;
            current = plan(chosen, replaceNewer.IsVisible && replaceNewer.IsChecked == true);

            bool right = current.Copies.Any(c => c.ToRight);
            bool left = current.Copies.Any(c => !c.ToRight);
            summary.Text = (right, left) switch
            {
                (true, true) => L.T("{0} files ({1}) would be copied to {2}, and {3} files ({4}) to {5}.", current.FilesToRight, FileText.Size(current.BytesToRight), rightName, current.FilesToLeft, FileText.Size(current.BytesToLeft), leftName),
                (true, false) => L.T("{0} files ({1}) would be copied to {2}.", current.FilesToRight, FileText.Size(current.BytesToRight), rightName),
                (false, true) => L.T("{0} files ({1}) would be copied to {2}.", current.FilesToLeft, FileText.Size(current.BytesToLeft), leftName),
                _ => L.T("Nothing would be copied."),
            };

            // What would be copied first, then what is left as it is and why.
            preview.Children.Clear();
            foreach (SyncCopy copy in current.Copies.Take(ShownRows))
            {
                string size = copy.Item.IsDirectory ? L.T("folder, {0} files, {1}", copy.Files, FileText.Size(copy.Bytes)) : FileText.Size(copy.Bytes);
                preview.Children.Add(Line($"{(copy.ToRight ? "→" : "←")}  {copy.Item.RelativePath}   {size}", ElioColors.Text));
            }

            int room = Math.Max(0, ShownRows - preview.Children.Count);
            foreach (SyncSkip skip in current.Skipped.Take(room)) preview.Children.Add(Line($"·  {skip.Item.RelativePath}   {Reason(skip.Reason)}", ElioColors.TextMuted));

            int more = current.Copies.Count + current.Skipped.Count - preview.Children.Count;
            if (more > 0) preview.Children.Add(new TextBlock { Text = L.T("... and {0} more", more), FontSize = 12, Foreground = ElioColors.TextFaint });

            string skipped = current.Skipped.Count > 0 ? L.T("{0} differences are left as they are.", current.Skipped.Count) + " " : string.Empty;
            string kept = current.Untouched > 0 ? L.T("{0} items that only the side being copied to has stay there.", current.Untouched) + " " : string.Empty;
            note.Text = skipped + kept + L.T("Nothing is deleted. A file that is replaced is only swapped for the new one once that has fully arrived.");
            ok.IsEnabled = current.Copies.Count > 0;
        }

        direction.SelectionChanged += (_, _) => Recompute();
        replaceNewer.IsCheckedChanged += (_, _) => Recompute();
        ok.Click += (_, _) =>
        {
            if (current.Copies.Count > 0) result.TrySetResult(current);
        };
        cancel.Click += (_, _) => result.TrySetResult(null);
        Recompute();

        Control frame = Frame(
            L.T("Synchronise"),
            new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Field(L.T("Copy"), direction),
                    replaceNewer,
                    summary,
                    new ScrollViewer { MaxHeight = 300, Margin = ScrollOutset, Content = new StackPanel { Margin = ScrollInset, Children = { preview } } },
                    note,
                },
            },
            cancel,
            ok);

        if (frame is Border card) card.Width = 580;
        return frame;
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

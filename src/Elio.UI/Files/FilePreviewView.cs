using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Elio.Core;

namespace Elio.UI.Files;

/// <summary>
/// A look inside the chosen file without copying it anywhere: text, a picture, the names in a folder, or the first bytes
/// of anything else. It lies over the file screen, fills it on a phone, and follows the choice in the list when the
/// arrows are used, so that a folder of files can be gone through one after the other.
/// </summary>
public sealed class FilePreviewView : UserControl
{
    /// <summary>How much of a text is read. Enough to see what a file is; a text box is not the place to read a book.</summary>
    public const int TextBytes = 256 * 1024;

    private const int PictureBytes = 16 * 1024 * 1024;
    private const long PictureMaxPixels = 64_000_000;
    private const int HexBytes = 1024;
    private const int FolderNames = 400;

    private static readonly string[] PictureKinds = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico"];

    private readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = ElioColors.Text, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "preview-title" };
    private readonly TextBlock _details = new() { FontSize = ElioStyle.SmallFontSize, Foreground = ElioColors.TextMuted, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "preview-details" };
    private readonly Border _body = new() { Background = ElioColors.Elevated, Tag = "preview-body" };
    private readonly Button _edit;
    private CancellationTokenSource? _loading;
    private Bitmap? _picture;

    public FilePreviewView()
    {
        bool phone = AppServices.MobileLayout;
        double size = phone ? 40 : 30;

        Button close = ElioStyle.IconButton(ElioStyle.Icon(ElioStyle.IconClose, ElioColors.Text, 16, 1.8), L.T("Close"), size);
        close.Tag = "preview-close";
        close.Click += (_, _) => CloseRequested?.Invoke();

        Button previous = ElioStyle.IconButton("‹", L.T("Previous file"), size);
        previous.FontSize = 18;
        previous.Tag = "preview-previous";
        previous.Click += (_, _) => Moved?.Invoke(-1);

        Button next = ElioStyle.IconButton("›", L.T("Next file"), size);
        next.FontSize = 18;
        next.Tag = "preview-next";
        next.Click += (_, _) => Moved?.Invoke(1);

        _edit = new Button { Content = L.T("Edit"), Padding = new Thickness(12, 4), CornerRadius = new CornerRadius(ElioStyle.ControlRadius), IsVisible = false, Tag = "preview-edit", VerticalAlignment = VerticalAlignment.Center };
        _edit.Click += (_, _) =>
        {
            if (Current is FileRow row) EditRequested?.Invoke(row);
        };

        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _details } };

        var bar = new DockPanel { Margin = new Thickness(8, phone ? 6 : 4), LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(_edit, Dock.Right);
        DockPanel.SetDock(next, Dock.Left);
        DockPanel.SetDock(previous, Dock.Left);
        _edit.Margin = new Thickness(0, 0, 6, 0);
        titles.Margin = new Thickness(6, 0);
        bar.Children.Add(close);
        bar.Children.Add(_edit);
        bar.Children.Add(previous);
        bar.Children.Add(next);
        bar.Children.Add(titles);

        var top = new Border { Background = ElioColors.Chrome, BorderBrush = ElioColors.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
        DockPanel.SetDock(top, Dock.Top);

        Focusable = true;
        Content = new DockPanel { Children = { top, _body } };

        // On the way down, so that the keys work wherever the keyboard happens to be.
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>The preview was closed.</summary>
    public event Action? CloseRequested;

    /// <summary>The previous (-1) or the next (1) file in the list was asked for.</summary>
    public event Action<int>? Moved;

    /// <summary>The user wants to edit what is shown.</summary>
    public event Action<FileRow>? EditRequested;

    /// <summary>What is shown now.</summary>
    public FileRow? Current { get; private set; }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
            case Key.Space when e.Source is not TextBox { IsReadOnly: false }:
                CloseRequested?.Invoke();
                break;
            case Key.Up when e.KeyModifiers == KeyModifiers.Alt:
            case Key.Left when e.KeyModifiers == KeyModifiers.Alt:
                Moved?.Invoke(-1);
                break;
            case Key.Down when e.KeyModifiers == KeyModifiers.Alt:
            case Key.Right when e.KeyModifiers == KeyModifiers.Alt:
                Moved?.Invoke(1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Shows a file. A newer call replaces one that is still loading.</summary>
    public async Task ShowAsync(IFileSource source, FileRow row)
    {
        _loading?.Cancel();
        var loading = new CancellationTokenSource();
        _loading = loading;
        Current = row;

        _title.Text = row.Name;
        _details.Text = Describe(row);
        _edit.IsVisible = false;
        _body.Child = Message(L.T("Loading..."));

        try
        {
            (Control content, bool editable) = await BuildAsync(source, row, loading.Token);
            if (loading.IsCancellationRequested) return;

            _body.Child = content;
            _edit.IsVisible = editable;
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
            // Replaced by a newer file.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!loading.IsCancellationRequested) _body.Child = Message(ex.Message, problem: true);
        }
    }

    /// <summary>Stops loading and lets go of what was shown.</summary>
    public void Clear()
    {
        _loading?.Cancel();
        _loading = null;
        Current = null;
        _body.Child = null;
        _picture?.Dispose();
        _picture = null;
    }

    private static string Describe(FileRow row)
    {
        var parts = new List<string>();
        if (row.IsDirectory) parts.Add(L.T("Folder"));
        else if (row.Size > 0 || row.Snippet is null) parts.Add(FileText.Size(row.Size));
        if (row.ModifiedUtc != DateTime.MinValue) parts.Add(row.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        if (row.Permissions.Length > 0) parts.Add(row.Permissions);
        if (row.IsContentHit) parts.Add(L.T("line {0}", row.Line));
        return string.Join("  ·  ", parts);
    }

    private async Task<(Control Content, bool Editable)> BuildAsync(IFileSource source, FileRow row, CancellationToken cancellation)
    {
        if (row.IsDirectory)
        {
            List<FileRow> children = await source.ListAsync(row.Path, cancellation);
            IEnumerable<string> names = children.OrderByDescending(c => c.IsDirectory).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Take(FolderNames).Select(c => c.IsDirectory ? c.Name + "/" : c.Name);
            string more = children.Count > FolderNames ? "\n" + L.T("... and {0} more", children.Count - FolderNames) : string.Empty;
            string head = children.Count == 0 ? L.T("This folder is empty.") : L.T("{0} items", children.Count) + "\n\n" + string.Join('\n', names) + more;
            return (TextArea(head), false);
        }

        if (row.Size == 0 && !row.IsContentHit) return (Message(L.T("This file is empty.")), true);

        string extension = Path.GetExtension(row.Name).ToLowerInvariant();
        if (PictureKinds.Contains(extension)) return (await PictureAsync(source, row, cancellation), false);

        bool truncated = row.Size > TextBytes || row.IsContentHit;
        byte[] bytes = await source.ReadAsync(row.Path, TextBytes, cancellation);
        cancellation.ThrowIfCancellationRequested();

        // The cut may fall in the middle of a letter, which must not make a text look like something else.
        byte[] whole = truncated ? WithoutPartialLetter(bytes) : bytes;
        if (TextCodec.TryDecode(whole, out string text, out _))
        {
            string note = truncated && bytes.Length >= TextBytes ? "\n\n" + L.T("... only the first {0} are shown.", FileText.Size(TextBytes)) : string.Empty;
            return (TextArea(text + note), row.Size <= TextCodec.MaxBytes);
        }

        return (TextArea(HexDump(bytes, row.Size)), false);
    }

    private async Task<Control> PictureAsync(IFileSource source, FileRow row, CancellationToken cancellation)
    {
        if (row.Size > PictureBytes) return Message(L.T("This picture is larger than {0}, so it is not shown here.", FileText.Size(PictureBytes)));

        byte[] bytes = await source.ReadAsync(row.Path, PictureBytes, cancellation);
        cancellation.ThrowIfCancellationRequested();

        // A small file can still unpack into a picture too big to hold: its size is in the first bytes of a PNG.
        if (PngPixels(bytes) > PictureMaxPixels) return Message(L.T("This picture is too large to show."));

        Bitmap? bitmap = await Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                return new Bitmap(stream);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
            {
                return null;
            }
        }, cancellation);

        if (bitmap is null) return Message(L.T("This picture could not be read."));
        if (cancellation.IsCancellationRequested)
        {
            bitmap.Dispose();
            throw new OperationCanceledException(cancellation);
        }

        _picture?.Dispose();
        _picture = bitmap;
        return new Border
        {
            Margin = new Thickness(12),
            Child = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Tag = "preview-picture" },
        };
    }

    // The width and the height are the first two numbers after the signature and the name of the first chunk.
    internal static long PngPixels(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(signature)) return 0;

        long width = ((long)bytes[16] << 24) | ((long)bytes[17] << 16) | ((long)bytes[18] << 8) | bytes[19];
        long height = ((long)bytes[20] << 24) | ((long)bytes[21] << 16) | ((long)bytes[22] << 8) | bytes[23];
        return width * height;
    }

    /// <summary>Drops the last bytes of a UTF-8 text if they are the start of a letter whose end was cut off.</summary>
    internal static byte[] WithoutPartialLetter(byte[] bytes)
    {
        int end = bytes.Length;
        int back = 0;

        // At most three bytes of a letter can be missing, so only the last four are looked at.
        while (back < 4 && end - back > 0)
        {
            byte b = bytes[end - back - 1];
            back++;
            if ((b & 0b1100_0000) == 0b1000_0000) continue;

            int needed = b >= 0b1111_0000 ? 4 : b >= 0b1110_0000 ? 3 : b >= 0b1100_0000 ? 2 : 1;
            return needed > back ? bytes[..(end - back)] : bytes;
        }

        return bytes;
    }

    /// <summary>Sixteen bytes to a line: where they are, what they are, and what they would be as letters.</summary>
    internal static string HexDump(byte[] bytes, long fullSize)
    {
        var text = new StringBuilder();
        text.AppendLine(L.T("This is not text. The first bytes are:"));
        text.AppendLine();

        int shown = Math.Min(bytes.Length, HexBytes);
        for (int at = 0; at < shown; at += 16)
        {
            int count = Math.Min(16, shown - at);
            text.Append(at.ToString("x8")).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                text.Append(i < count ? bytes[at + i].ToString("x2") : "  ").Append(i == 7 ? "  " : " ");
            }

            text.Append(' ');
            for (int i = 0; i < count; i++) text.Append(bytes[at + i] is >= 0x20 and < 0x7f ? (char)bytes[at + i] : '.');
            text.AppendLine();
        }

        if (fullSize > shown) text.AppendLine().Append(L.T("... {0} in all.", FileText.Size(fullSize)));
        return text.ToString();
    }

    private static Control Message(string text, bool problem = false) => new TextBlock
    {
        Text = text,
        Foreground = problem ? ElioColors.Danger : ElioColors.TextMuted,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(24),
    };

    // A box that cannot be typed in, so that its text can be scrolled and copied but not changed by accident.
    private static Control TextArea(string text) => new TextBox
    {
        Text = text,
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = ElioStyle.CodeFont,
        FontSize = AppServices.Settings.EditorFontSize > 0 ? AppServices.Settings.EditorFontSize : 13,
        Background = ElioColors.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(12, 10),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        [ScrollViewer.HorizontalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        [ScrollViewer.VerticalScrollBarVisibilityProperty] = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        Tag = "preview-text",
    };
}

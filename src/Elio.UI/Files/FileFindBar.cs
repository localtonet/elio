using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Elio.Core;

namespace Elio.UI.Files;

/// <summary>
/// The bar over a file list that keeps only the names that fit what is typed. It looks like the terminal's find bar, and
/// sits above the list instead of over it, so that it does not cover the columns.
/// </summary>
public sealed class FileFindBar : Border
{
    private readonly TextBox _box = new() { MinHeight = 26, Padding = new Thickness(8, 3), FontSize = 12, CornerRadius = new CornerRadius(6), VerticalContentAlignment = VerticalAlignment.Center, Tag = "panel-find-box" };
    private readonly TextBlock _count = new() { FontSize = ElioStyle.SmallFontSize, Foreground = ElioColors.TextMuted, VerticalAlignment = VerticalAlignment.Center, MinWidth = 52, Margin = new Thickness(6, 0, 4, 0), TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Tag = "panel-find-count" };
    private readonly Button _regex;
    private readonly Button _subfolders;
    private bool _useRegex;
    private bool _includeSubfolders;
    private string _announced = string.Empty;

    public FileFindBar(bool useRegex)
    {
        _regex = Tool(".*", L.T("Read as a regular expression: on / off"), "panel-find-regex");
        _subfolders = Tool(ElioStyle.IconSubfolders, L.T("Look in subfolders too: on / off. Turning it off stops a search that is still running."), "panel-find-subfolders");
        Button close = Tool(ElioStyle.IconClose, L.T("Close"), "panel-find-close");
        close.Click += (_, _) => Closed?.Invoke();

        _regex.Click += (_, _) => UseRegex = !UseRegex;
        _subfolders.Click += (_, _) => IncludeSubfolders = !IncludeSubfolders;
        UseRegex = useRegex;

        // The box reports a change a moment after it was made, so a text set from outside is told apart by what it says.
        _box.TextChanged += (_, _) =>
        {
            if (Text == _announced) return;
            _announced = Text;
            Changed?.Invoke(true);
        };
        _box.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _count, _regex, _subfolders, close } };
        DockPanel.SetDock(buttons, Dock.Right);

        Background = ElioColors.Elevated;
        BorderBrush = ElioColors.Border;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(ElioStyle.CardRadius);
        Padding = new Thickness(5);
        Margin = new Thickness(8, 0, 8, 4);
        IsVisible = false;
        Tag = "panel-find-bar";
        Child = new DockPanel { LastChildFill = true, Children = { buttons, _box } };
    }

    /// <summary>The text or one of the two switches was changed.</summary>
    /// <remarks>Typing is told apart because it comes in bursts, which the list waits out.</remarks>
    public event Action<bool>? Changed;

    /// <summary>Enter was pressed in the box.</summary>
    public event Action? Accepted;

    /// <summary>The user is done: Escape, or the close button.</summary>
    public event Action? Closed;

    /// <summary>Up (-1) or Down (1) was pressed: the choice moves to the name above or below, without leaving the box.</summary>
    public event Action<int>? Moved;

    /// <summary>What is typed. Setting it from outside is not a change the user made, and does not raise <see cref="Changed"/>.</summary>
    public string Text
    {
        get => _box.Text ?? string.Empty;
        set
        {
            _announced = value;
            _box.Text = value;
        }
    }

    public bool UseRegex
    {
        get => _useRegex;
        set
        {
            bool changed = _useRegex != value;
            _useRegex = value;
            Mark(_regex, value);
            _box.PlaceholderText = value ? L.T("Regular expression") : L.T("Name, or a pattern such as *.log");
            if (changed) Changed?.Invoke(false);
        }
    }

    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            bool changed = _includeSubfolders != value;
            _includeSubfolders = value;
            Mark(_subfolders, value);
            if (changed) Changed?.Invoke(false);
        }
    }

    /// <summary>Whether the cursor is in the box, so that what is typed there is what is being looked for.</summary>
    public bool IsTyping => _box.IsFocused;

    /// <summary>Puts the cursor in the box, with what is in it selected so that typing starts again.</summary>
    public void Open()
    {
        IsVisible = true;
        _box.Focus();
        _box.SelectAll();
    }

    /// <summary>Says how many names fit, or why none can.</summary>
    public void ShowCount(string text, bool problem)
    {
        _count.Text = text;
        _count.Foreground = problem ? ElioColors.Danger : ElioColors.TextMuted;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Accepted?.Invoke();
                break;
            case Key.Escape:
                Closed?.Invoke();
                break;
            case Key.Down:
                Moved?.Invoke(1);
                break;
            case Key.Up:
                Moved?.Invoke(-1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static Button Tool(string symbol, string tip, string tag)
    {
        // A name starting with M is one of the drawn icons. Anything else is shown as the text it is.
        Button button = ElioStyle.IconButton(symbol.StartsWith('M') ? ElioStyle.Icon(symbol, ElioColors.TextMuted, 14, 1.6) : symbol, tip, 24);
        button.FontSize = 12;
        button.CornerRadius = new CornerRadius(6);
        button.Tag = tag;
        return button;
    }

    // The same mark as a selected row, so that an option that is on is seen without opening anything.
    private static void Mark(Button button, bool on)
    {
        button.Background = on ? ElioColors.Selection : ElioColors.Transparent;
        button.Foreground = on ? ElioColors.Accent : ElioColors.TextMuted;
    }
}

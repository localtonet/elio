using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Elio.Terminal;

namespace Elio.UI.Terminal;

/// <summary>
/// The keys a phone's keyboard does not have, in a row above it: Esc, Tab, Ctrl, Alt, the arrows and the
/// symbols a shell needs all the time.
/// </summary>
/// <remarks>
/// Ctrl and Alt are pressed first and apply to the next key, since a touch screen cannot hold one key down
/// while tapping another. None of the buttons take the focus, so the keyboard stays up.
/// </remarks>
public sealed class TerminalKeyRow : UserControl
{
    private const double KeyHeight = 34;
    private const double KeyMinWidth = 42;

    private readonly TerminalView _terminal;
    private readonly Button _control;
    private readonly Button _alt;

    public TerminalKeyRow(TerminalView terminal)
    {
        _terminal = terminal;
        _control = Toggle("Ctrl", TerminalMods.Control);
        _alt = Toggle("Alt", TerminalMods.Alt);

        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4, 3) };

        // First in the row, where it is always in reach: once the keyboard has been sent away there is
        // otherwise nothing on the screen that says how to get it back.
        Button keyboard = Make(string.Empty, "key-keyboard");
        keyboard.Content = ElioStyle.Icon(ElioStyle.IconKeyboard, ElioColors.Text);
        keyboard.Click += (_, _) => _terminal.ToggleKeyboard();
        keys.Children.Add(keyboard);

        keys.Children.Add(Key("Esc", TerminalKey.Escape));
        keys.Children.Add(Key("Tab", TerminalKey.Tab));
        keys.Children.Add(_control);
        keys.Children.Add(_alt);
        keys.Children.Add(Key("←", TerminalKey.ArrowLeft));
        keys.Children.Add(Key("↓", TerminalKey.ArrowDown));
        keys.Children.Add(Key("↑", TerminalKey.ArrowUp));
        keys.Children.Add(Key("→", TerminalKey.ArrowRight));
        foreach (string symbol in (string[])["|", "/", "-", "~", "_", ":", "*", "&", "$", "\\"]) keys.Children.Add(Text(symbol));
        keys.Children.Add(Key("Home", TerminalKey.Home));
        keys.Children.Add(Key("End", TerminalKey.End));
        keys.Children.Add(Key("PgUp", TerminalKey.PageUp));
        keys.Children.Add(Key("PgDn", TerminalKey.PageDown));

        // More keys than fit on a narrow screen, so the row itself scrolls sideways.
        Content = new Border
        {
            Background = ElioColors.Chrome,
            BorderBrush = ElioColors.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = keys,
            },
        };

        terminal.StickyModsChanged += ShowSticky;
    }

    private static Button Make(string label, string tag) => new()
    {
        Content = label,
        MinWidth = KeyMinWidth,
        Height = KeyHeight,
        Padding = new Thickness(8, 0),
        Focusable = false,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        CornerRadius = new CornerRadius(ElioStyle.RowRadius),
        Background = ElioColors.Control,
        Foreground = ElioColors.Text,
        FontSize = ElioStyle.BaseFontSize,
        Tag = tag,
    };

    private Button Key(string label, TerminalKey key)
    {
        Button button = Make(label, "key-" + key);
        button.Click += (_, _) => _terminal.PressKey(key);
        return button;
    }

    private Button Text(string symbol)
    {
        Button button = Make(symbol, "key-text");
        button.Click += (_, _) => _terminal.TypeWithSticky(symbol);
        return button;
    }

    private Button Toggle(string label, TerminalMods modifier)
    {
        Button button = Make(label, "key-" + label);
        button.Click += (_, _) => _terminal.StickyMods ^= modifier;
        return button;
    }

    private void ShowSticky()
    {
        Paint(_control, _terminal.StickyMods.HasFlag(TerminalMods.Control));
        Paint(_alt, _terminal.StickyMods.HasFlag(TerminalMods.Alt));
    }

    private static void Paint(Button button, bool held)
    {
        button.Background = held ? ElioColors.Accent : ElioColors.Control;
        button.Foreground = held ? ElioColors.AccentText : ElioColors.Text;
    }
}

using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Elio.UI;

/// <summary>
/// The look the views share beyond their colors: the font, the corner sizes, the small drawn icons, and the
/// pieces every list is made of. The built-in controls get the same look from the styles in App.axaml, which
/// read the brushes this class hands to the application.
/// </summary>
internal static class ElioStyle
{
    public const double BaseFontSize = 13;
    public const double SmallFontSize = 11;
    public const double RowRadius = 7;
    public const double ControlRadius = 8;
    public const double CardRadius = 10;
    public const double DialogRadius = 14;

    /// <summary>How long a hover or a selection takes to fade in. Short enough not to feel like waiting.</summary>
    public static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(120);

    /// <summary>The system's own interface font on each platform, with Inter first for those who installed it.</summary>
    public static readonly FontFamily Font = new("Inter, Segoe UI Variable Text, Segoe UI, -apple-system, San Francisco, Helvetica Neue, Ubuntu, sans-serif");

    public static readonly BoxShadows DialogShadow = new(
        new BoxShadow { OffsetY = 18, Blur = 48, Color = ElioColors.Shadow },
        [new BoxShadow { OffsetY = 2, Blur = 6, Color = Color.FromArgb(ElioColors.Light ? (byte)0x1F : (byte)0x40, 0, 0, 0) }]);

    public static readonly BoxShadows PopoverShadow = new(new BoxShadow { OffsetY = 6, Blur = 20, Color = Color.FromArgb(ElioColors.Light ? (byte)0x2E : (byte)0x73, 0, 0, 0) });

    /// <summary>Under the tab in front, so it reads as lying on top of the strip.</summary>
    public static readonly BoxShadows TabShadow = new(new BoxShadow { OffsetY = 1, Blur = 3, Color = Color.FromArgb(ElioColors.Light ? (byte)0x2B : (byte)0x66, 0, 0, 0) });

    /// <summary>A soft halo around the pane or the text box that has the keyboard.</summary>
    public static readonly BoxShadows FocusRing = new(new BoxShadow { Spread = 3, Color = ElioColors.AccentAt(ElioColors.Light ? (byte)0x40 : (byte)0x52) });

    /// <summary>The same halo, thinner, for the pane being typed into: it has to fit in the gap between two panes.</summary>
    public static readonly BoxShadows PaneRing = new(new BoxShadow { Spread = 2, Color = ElioColors.AccentAt(ElioColors.Light ? (byte)0x38 : (byte)0x47) });

    public static readonly BoxShadows NoShadow = default;

    // Drawn on a 16 by 16 grid with a round pen, so they stay crisp next to 13 px text on every platform
    // without depending on which symbol font happens to be installed.
    public const string IconCheck = "M3.5,8.5 L6.5,11.5 L12.5,4.5";
    public const string IconLock = "M4.5,7.5 H11.5 V13 H4.5 Z M6,7.5 V5.5 A2,2 0 0 1 10,5.5 V7.5";
    public const string IconDevices = "M1.5,3.5 H9.5 V9.5 H1.5 Z M0.5,11.5 H10.5 M11.5,6 H14.5 V13 H11.5 Z";
    public const string IconCopy = "M5.5,5.5 H12.5 V13.5 H5.5 Z M3.5,10.5 V2.5 H10.5";
    public const string IconPlus ="M8,3.5 V12.5 M3.5,8 H12.5";
    public const string IconGear = "M8,5.4 A2.6,2.6 0 1 1 8,10.6 A2.6,2.6 0 1 1 8,5.4 M8,1.6 V3.3 M8,12.7 V14.4 M1.6,8 H3.3 M12.7,8 H14.4 M3.5,3.5 L4.7,4.7 M11.3,11.3 L12.5,12.5 M3.5,12.5 L4.7,11.3 M11.3,4.7 L12.5,3.5";
    public const string IconKeyboard = "M2,4.5 H14 V11.5 H2 Z M4.5,7 H5 M7,7 H7.5 M9.5,7 H10 M11.5,7 H12 M5,9.5 H11";
    public const string IconTerminal = "M3,4.5 L6.5,8 L3,11.5 M8.5,11.5 H13";
    public const string IconFolderPlus = "M1.75,4.25 a1,1 0 0 1 1,-1 h3 l1.5,1.5 h6 a1,1 0 0 1 1,1 v6 a1,1 0 0 1 -1,1 h-10.5 a1,1 0 0 1 -1,-1 z M8,7.4 v3.4 M6.3,9.1 h3.4";
    public const string IconRefresh = "M12.6,6 A5,5 0 1 0 13,8 M12.9,2.9 V6.1 H9.7";
    public const string IconKey = "M7.5,10.5 A2.75,2.75 0 1 1 2,10.5 A2.75,2.75 0 1 1 7.5,10.5 M6.7,8.55 L13,2.25 M11,4.25 L12.75,6 M13,2.25 L14.2,3.45";
    public const string IconSearch ="M11.5,7 A4.5,4.5 0 1 1 2.5,7 A4.5,4.5 0 1 1 11.5,7 M10.4,10.4 L13.5,13.5";
    public const string IconChevronRight = "M6,3.5 L10.5,8 L6,12.5";
    public const string IconChevronDown = "M3.5,6 L8,10.5 L12.5,6";
    public const string IconClose = "M4.5,4.5 L11.5,11.5 M11.5,4.5 L4.5,11.5";
    public const string IconPanes = "M2,3.5 H14 V12.5 H2 Z M8,3.5 V12.5 M8,8 H14";
    public const string IconMenu ="M2.5,4.5 H13.5 M2.5,8 H13.5 M2.5,11.5 H13.5";
    public const string IconPin = "M5.5,2.5 H10.5 L9.8,6.6 L12,9 H4 L6.2,6.6 Z M8,9 V13.5";
    public const string IconUp = "M8,12.5 V3.5 M4.5,7 L8,3.5 L11.5,7";
    public const string IconDown = "M8,3.5 V12.5 M4.5,9 L8,12.5 L11.5,9";
    public const string IconSubfolders = "M1.5,2.5 H5 L6,3.7 H9.5 V6.5 H1.5 Z M3.5,6.5 V11.5 H6 M6,9 H9 L9.8,10 H14.5 V13.5 H6 Z";
    public const string IconEye = "M1.5,8 C3,5 5.5,3.5 8,3.5 C10.5,3.5 13,5 14.5,8 C13,11 10.5,12.5 8,12.5 C5.5,12.5 3,11 1.5,8 Z M8,6 A2,2 0 1 1 8,10 A2,2 0 1 1 8,6";

    // The colors of macOS, in the order its own apps hand them out.
    private static readonly uint[] AvatarColors = ElioColors.Light
        ? [0x007AFF, 0xAF52DE, 0xFF2D55, 0xFF9500, 0x34C759, 0x30B0C7, 0x5856D6, 0xA2845E]
        : [0x0A84FF, 0xBF5AF2, 0xFF375F, 0xFF9F0A, 0x30D158, 0x40C8E0, 0x5E5CE6, 0xAC8E68];

    /// <summary>
    /// Gives the application the palette under the names the built-in controls look their brushes up by,
    /// so text boxes, menus and buttons made anywhere match the hand-built views without each being painted.
    /// </summary>
    public static void Apply(Application app)
    {
        IResourceDictionary r = app.Resources;

        r["ElioFont"] = Font;
        r["ElioAccent"] = ElioColors.Accent;
        r["ElioAccentHover"] = ElioColors.AccentHover;
        r["ElioAccentPressed"] = ElioColors.AccentPressed;
        r["ElioAccentText"] = ElioColors.AccentText;
        r["ElioDanger"] = ElioColors.Danger;
        r["ElioDangerHover"] = ElioColors.DangerHover;
        r["ElioDangerPressed"] = ElioColors.DangerPressed;
        r["ElioHover"] = ElioColors.SurfaceHover;
        r["ElioControlPressed"] = ElioColors.ControlPressed;
        r["ElioFocusRing"] = FocusRing;

        r["ContentControlThemeFontFamily"] = Font;
        r["ControlContentThemeFontSize"] = BaseFontSize;
        r["ControlCornerRadius"] = new CornerRadius(ControlRadius);
        r["OverlayCornerRadius"] = new CornerRadius(CardRadius);

        Set(r, ElioColors.Control, "ButtonBackground", "ButtonBackgroundDisabled");
        Set(r, ElioColors.ControlHover, "ButtonBackgroundPointerOver");
        Set(r, ElioColors.ControlPressed, "ButtonBackgroundPressed");
        Set(r, ElioColors.Text, "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed");
        Set(r, ElioColors.TextFaint, "ButtonForegroundDisabled");
        Set(r, ElioColors.Transparent, "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled");

        Set(r, ElioColors.Surface, "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled");
        Set(r, ElioColors.BorderStrong, "TextControlBorderBrush", "TextControlBorderBrushPointerOver");
        Set(r, ElioColors.Border, "TextControlBorderBrushDisabled");
        Set(r, ElioColors.Accent, "TextControlBorderBrushFocused");
        Set(r, ElioColors.Text, "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused");
        Set(r, ElioColors.TextFaint, "TextControlForegroundDisabled", "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused", "TextControlPlaceholderForegroundDisabled");

        // The outline stays one pixel when a box gets the keyboard, and a halo is added around it instead,
        // so nothing inside the box moves.
        r["TextControlBorderThemeThicknessFocused"] = new Thickness(1);

        Set(r, ElioColors.Surface, "ComboBoxBackground", "ComboBoxBackgroundUnfocused", "ComboBoxBackgroundDisabled");
        Set(r, ElioColors.SurfaceHover, "ComboBoxBackgroundPointerOver");
        Set(r, ElioColors.ControlHover, "ComboBoxBackgroundPressed");
        Set(r, ElioColors.BorderStrong, "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed");
        Set(r, ElioColors.Border, "ComboBoxBorderBrushDisabled", "ComboBoxDropDownBorderBrush");
        Set(r, ElioColors.Elevated, "ComboBoxDropDownBackground");
        Set(r, ElioColors.TextMuted, "ComboBoxDropDownGlyphForeground");
        Set(r, ElioColors.Text, "ComboBoxItemForeground", "ComboBoxItemForegroundPointerOver", "ComboBoxItemForegroundPressed");
        Set(r, ElioColors.SurfaceHover, "ComboBoxItemBackgroundPointerOver", "ComboBoxItemBackgroundPressed");
        Set(r, ElioColors.Accent, "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedPointerOver", "ComboBoxItemBackgroundSelectedPressed");
        Set(r, ElioColors.AccentText, "ComboBoxItemForegroundSelected", "ComboBoxItemForegroundSelectedPointerOver", "ComboBoxItemForegroundSelectedPressed");

        Set(r, ElioColors.Surface, "CheckBoxCheckBackgroundFillUnchecked");
        Set(r, ElioColors.SurfaceHover, "CheckBoxCheckBackgroundFillUncheckedPointerOver");
        Set(r, ElioColors.ControlHover, "CheckBoxCheckBackgroundFillUncheckedPressed");
        Set(r, ElioColors.BorderStrong, "CheckBoxCheckBackgroundStrokeUnchecked", "CheckBoxCheckBackgroundStrokeUncheckedPointerOver", "CheckBoxCheckBackgroundStrokeUncheckedPressed");
        Set(r, ElioColors.Accent, "CheckBoxCheckBackgroundFillChecked");
        Set(r, ElioColors.AccentHover, "CheckBoxCheckBackgroundFillCheckedPointerOver");
        Set(r, ElioColors.AccentPressed, "CheckBoxCheckBackgroundFillCheckedPressed");
        Set(r, ElioColors.AccentText, "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed");

        Set(r, ElioColors.Elevated, "MenuFlyoutPresenterBackground", "ToolTipBackground");
        Set(r, ElioColors.Border, "MenuFlyoutPresenterBorderBrush", "ToolTipBorderBrush");
        Set(r, ElioColors.Text, "MenuFlyoutItemForeground", "ToolTipForeground");
        Set(r, ElioColors.TextFaint, "MenuFlyoutItemForegroundDisabled");
        Set(r, ElioColors.Accent, "MenuFlyoutItemBackgroundPointerOver");
        Set(r, ElioColors.AccentPressed, "MenuFlyoutItemBackgroundPressed");
        Set(r, ElioColors.AccentText, "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed", "MenuFlyoutSubItemChevronPointerOver", "MenuFlyoutSubItemChevronPressed");
        Set(r, ElioColors.TextMuted, "MenuFlyoutSubItemChevron");
        r["ToolTipContentThemeFontSize"] = 12d;

        Set(r, ElioColors.BorderStrong, "ScrollBarThumbBackgroundColor");
        Set(r, ElioColors.TextFaint, "ScrollBarThumbFillPointerOver", "ScrollBarThumbFillPressed");

        // The theme derives its own highlights (selected text, focus marks, progress bars) from this one color.
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
            fluent.Palettes[ElioColors.Light ? ThemeVariant.Light : ThemeVariant.Dark] = new ColorPaletteResources { Accent = ElioColors.ColorOf(ElioColors.Accent) };
    }

    private static void Set(IResourceDictionary resources, object value, params string[] keys)
    {
        foreach (string key in keys) resources[key] = value;
    }

    /// <summary>One of the drawn icons, in the given color.</summary>
    public static Control Icon(string data, IBrush? color = null, double size = 16, double weight = 1.4)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
            Stroke = color ?? ElioColors.TextMuted,
            StrokeThickness = weight,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };
        if (size == 16) return path;

        return new Viewbox { Width = size, Height = size, Child = path, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>A small square button that is only an icon until the pointer is over it.</summary>
    public static Button IconButton(object content, string? tip, double size = 28)
    {
        var button = new Button
        {
            Content = content,
            Width = size,
            Height = size,
            Padding = default,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ElioColors.Transparent,
            Foreground = ElioColors.TextMuted,
            CornerRadius = new CornerRadius(RowRadius),
            Classes = { "icon" },
        };
        if (tip is not null) ToolTip.SetTip(button, tip);
        return button;
    }

    /// <summary>The title over a part of the sidebar: small capitals that label without competing with the rows.</summary>
    public static TextBlock SectionTitle(string text) => new()
    {
        Text = text.ToUpper(System.Globalization.CultureInfo.CurrentUICulture),
        FontSize = SmallFontSize,
        FontWeight = FontWeight.SemiBold,
        LetterSpacing = 0.6,
        Foreground = ElioColors.TextFaint,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>A rounded square with the name's first letter, colored by the name so a server keeps its color.</summary>
    public static Control Avatar(string name, double size = 26)
    {
        string trimmed = name.Trim();
        string letter = trimmed.Length == 0 ? "?" : System.Globalization.StringInfo.GetNextTextElement(trimmed).ToUpper(System.Globalization.CultureInfo.CurrentUICulture);

        // Not the runtime's string hash, which differs from one run to the next.
        uint sum = 0;
        foreach (char c in trimmed) sum = (sum * 31) + c;
        uint rgb = AvatarColors[sum % (uint)AvatarColors.Length];
        Color color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.27),
            Background = new SolidColorBrush(color, ElioColors.Light ? 0.16 : 0.24),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = letter,
                FontSize = size * 0.46,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(ElioColors.Light ? Darken(color) : Lighten(color)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    // The letter has to stay readable on a tint of its own color.
    private static Color Lighten(Color c) => Color.FromRgb((byte)(c.R + ((255 - c.R) * 0.35)), (byte)(c.G + ((255 - c.G) * 0.35)), (byte)(c.B + ((255 - c.B) * 0.35)));

    private static Color Darken(Color c) => Color.FromRgb((byte)(c.R * 0.8), (byte)(c.G * 0.8), (byte)(c.B * 0.8));

    /// <summary>Lets a border's fill fade rather than jump when it changes.</summary>
    public static T Fading<T>(T border) where T : Border
    {
        border.Transitions = [new BrushTransition { Property = Border.BackgroundProperty, Duration = Fade }];
        return border;
    }

    /// <summary>Fills a row while the pointer is over it, and puts back what it should show at rest when it leaves.</summary>
    public static void HoverFill(Border row, Func<IBrush> resting)
    {
        Fading(row);
        row.PointerEntered += (_, _) => row.Background = ElioColors.SurfaceHover;
        row.PointerExited += (_, _) => row.Background = resting();
    }

    /// <summary>
    /// Elio's mark: a prompt, and beside it the sun coming up over a line. Drawn on a 64 by 64 grid and scaled.
    /// </summary>
    /// <param name="size">The side of the square it is fitted into.</param>
    /// <param name="ink">The prompt and the line.</param>
    /// <param name="sun">The sun and its rays.</param>
    public static Control Mark(double size, IBrush ink, IBrush sun)
    {
        Path Stroke(string data, IBrush brush, double thickness) => new()
        {
            Data = Geometry.Parse(data),
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };

        var drawing = new Canvas
        {
            Width = 64,
            Height = 64,
            Children =
            {
                Stroke("M17,22 L27,32 L17,42", ink, 5.2),
                Stroke("M43,19 V23 M34.5,22.5 L37,25 M51.5,22.5 L49,25", sun, 3),
                new Path { Data = Geometry.Parse("M34,37 A9,9 0 0 1 52,37 Z"), Fill = sun },
                new Path { Data = Geometry.Parse("M33.5,40 H52.5 A1,1 0 0 1 53.5,41 V43 A1,1 0 0 1 52.5,44 H33.5 A1,1 0 0 1 32.5,43 V41 A1,1 0 0 1 33.5,40 Z"), Fill = ink },
            },
        };
        return new Viewbox { Width = size, Height = size, Child = drawing };
    }

    /// <summary>A text box for searching: a magnifier in front of the text.</summary>
    public static TextBox SearchBox(string placeholder) => new()
    {
        PlaceholderText = placeholder,
        InnerLeftContent = new Border { Padding = new Thickness(8, 0, 0, 0), Child = Icon(IconSearch, ElioColors.TextFaint, 14, 1.6), VerticalAlignment = VerticalAlignment.Center },
        Padding = new Thickness(6, 5, 8, 5),
    };
}

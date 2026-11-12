using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Elio.UI;

/// <summary>
/// The interface's palette in one place: dark, or light when the user asked for that. It is chosen once,
/// when the app starts, because every control takes its brushes as it is built.
/// </summary>
/// <remarks>
/// The values come from Elio's mark: its near-black, its warm white and the amber of its sun. Fills that sit on more than one kind of background
/// (hover, selection, separators) are translucent, so they look right on the sidebar, a dialog and the window alike.
/// </remarks>
internal static class ElioColors
{
    /// <summary>Whether the light palette is the one in use.</summary>
    public static readonly bool Light = AppServices.Settings.LightInterface;

    /// <summary>What the window is filled with where nothing else is drawn. Opaque.</summary>
    public static readonly IBrush Window = Brush(0x0E1116, 0xFBFAF7);

    /// <summary>
    /// A tint laid over the window's backdrop, which is the blurred desktop where the system offers that
    /// and the window color where it does not. Never the only thing behind text: use <see cref="Chrome"/> for that.
    /// </summary>
    public static readonly IBrush Sidebar = Brush(0x1B2029, 0xE6E3DA, 0x99, 0x99);

    /// <summary>Opaque bars and panels that belong to the window's frame rather than its content.</summary>
    public static readonly IBrush Chrome = Brush(0x161A21, 0xF1EFE8);

    /// <summary>Things that float above the window: dialogs, the find bar, the active tab.</summary>
    public static readonly IBrush Elevated = Brush(0x1D222B, 0xFFFFFF);

    /// <summary>The fill of text boxes and other wells.</summary>
    public static readonly IBrush Surface = Brush(0xFFFFFF, 0x000000, 0x12, 0x0B);

    /// <summary>What a row or a quiet button is filled with while the pointer is over it.</summary>
    public static readonly IBrush SurfaceHover = Brush(0xFFFFFF, 0x000000, 0x16, 0x10);

    /// <summary>The resting fill of an ordinary button, and the two steps it goes through when used.</summary>
    public static readonly IBrush Control = Brush(0xFFFFFF, 0x000000, 0x1C, 0x10);
    public static readonly IBrush ControlHover = Brush(0xFFFFFF, 0x000000, 0x28, 0x1A);
    public static readonly IBrush ControlPressed = Brush(0xFFFFFF, 0x000000, 0x34, 0x26);

    /// <summary>A hairline between two areas.</summary>
    public static readonly IBrush Border = Brush(0xFFFFFF, 0x000000, 0x1A, 0x1A);

    /// <summary>The outline of something that has to stand out from its surroundings, such as a text box.</summary>
    public static readonly IBrush BorderStrong = Brush(0xFFFFFF, 0x000000, 0x26, 0x26);

    public static readonly IBrush Text = Brush(0xF1EFE8, 0x14171C);
    public static readonly IBrush TextMuted = Brush(0x969BA6, 0x676A70);

    /// <summary>Section titles and other text that should stay out of the way.</summary>
    public static readonly IBrush TextFaint = Brush(0x747A86, 0x8C8E93);

    public static readonly IBrush Accent = Brush(0xFFB020, 0xF0A000);
    public static readonly IBrush AccentHover = Brush(0xFFC24D, 0xF7AE1F);
    public static readonly IBrush AccentPressed = Brush(0xE89C12, 0xD68C00);
    public static readonly IBrush AccentText = Brush(0x0E1116, 0x0E1116);

    /// <summary>The fill of a selected row: the accent, thinned so the row's own text stays readable.</summary>
    public static readonly IBrush Selection = Brush(0xFFB020, 0xF0A000, 0x38, 0x38);

    /// <summary>The outline of the pane or the text box being typed into.</summary>
    public static readonly IBrush AccentRing = Brush(0xFFB020, 0xF0A000, 0xB8, 0xC0);

    public static readonly IBrush Danger = Brush(0xFF453A, 0xFF3B30);
    public static readonly IBrush DangerHover = Brush(0xFF6259, 0xFF5850);
    public static readonly IBrush DangerPressed = Brush(0xE03A30, 0xE0342A);

    /// <summary>Marks a computer that can be reached right now.</summary>
    public static readonly IBrush Online = Brush(0x30D158, 0x34C759);

    /// <summary>The trough the tabs sit in.</summary>
    public static readonly IBrush TabTrack = Brush(0x000000, 0x000000, 0x47, 0x14);

    /// <summary>The tab in front, lying on top of the trough.</summary>
    public static readonly IBrush TabActive = Brush(0x262C36, 0xFFFFFF);

    /// <summary>What dims the window behind a dialog.</summary>
    public static readonly IBrush Scrim = Brush(0x000000, 0x000000, 0x8C, 0x47);

    /// <summary>Under the phone's drawer and other large things that slide over the content.</summary>
    public static readonly Color Shadow = Color.FromArgb(Light ? (byte)0x38 : (byte)0x8C, 0, 0, 0);

    public static readonly IBrush Transparent = Brushes.Transparent;

    /// <summary>The color of a brush from this palette, for the places that need a color rather than a brush.</summary>
    public static Color ColorOf(IBrush brush) => ((ISolidColorBrush)brush).Color;

    /// <summary>The accent at the given opacity, for glows and soft rings.</summary>
    public static Color AccentAt(byte alpha)
    {
        Color accent = ColorOf(Accent);
        return Color.FromArgb(alpha, accent.R, accent.G, accent.B);
    }

    private static ImmutableSolidColorBrush Brush(uint dark, uint light, byte darkAlpha = 0xFF, byte lightAlpha = 0xFF)
    {
        uint rgb = Light ? light : dark;
        return new ImmutableSolidColorBrush(Color.FromArgb(Light ? lightAlpha : darkAlpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }
}

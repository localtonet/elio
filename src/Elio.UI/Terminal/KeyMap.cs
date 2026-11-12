using Avalonia.Input;
using Elio.Terminal;

namespace Elio.UI.Terminal;

internal static class KeyMap
{
    // Both enums follow the W3C key names, so they can be matched by name.
    private static readonly Dictionary<PhysicalKey, TerminalKey> Keys = Build();

    private static Dictionary<PhysicalKey, TerminalKey> Build()
    {
        var map = new Dictionary<PhysicalKey, TerminalKey>();
        foreach (PhysicalKey physical in Enum.GetValues<PhysicalKey>())
            if (Enum.TryParse(physical.ToString(), out TerminalKey key)) map[physical] = key;
        return map;
    }

    /// <summary>The terminal's name for a key press, going by where the key is, or by what it means when the system does not say where.</summary>
    public static bool TryGet(PhysicalKey physical, Key logical, out TerminalKey key)
    {
        if (physical != PhysicalKey.None && Keys.TryGetValue(physical, out key)) return true;

        // On-screen keyboards, and some phones' hardware keys, report only what the key means.
        TerminalKey? named = logical switch
        {
            Key.Enter => TerminalKey.Enter,
            Key.Back => TerminalKey.Backspace,
            Key.Tab => TerminalKey.Tab,
            Key.Escape => TerminalKey.Escape,
            Key.Delete => TerminalKey.Delete,
            Key.Insert => TerminalKey.Insert,
            Key.Space => TerminalKey.Space,
            Key.Up => TerminalKey.ArrowUp,
            Key.Down => TerminalKey.ArrowDown,
            Key.Left => TerminalKey.ArrowLeft,
            Key.Right => TerminalKey.ArrowRight,
            Key.Home => TerminalKey.Home,
            Key.End => TerminalKey.End,
            Key.PageUp => TerminalKey.PageUp,
            Key.PageDown => TerminalKey.PageDown,
            _ => null,
        };
        key = named ?? default;
        return named is not null;
    }

    public static TerminalMods Mods(KeyModifiers modifiers)
    {
        var mods = TerminalMods.None;
        if (modifiers.HasFlag(KeyModifiers.Shift)) mods |= TerminalMods.Shift;
        if (modifiers.HasFlag(KeyModifiers.Control)) mods |= TerminalMods.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) mods |= TerminalMods.Alt;
        if (modifiers.HasFlag(KeyModifiers.Meta)) mods |= TerminalMods.Super;
        return mods;
    }

    /// <summary>The character the key gives without Shift. Uses the logical key, so non-US layouts stay correct.</summary>
    public static uint Unshifted(Key key, TerminalKey physical)
    {
        if (key is >= Key.A and <= Key.Z) return (uint)('a' + (key - Key.A));
        if (key is >= Key.D0 and <= Key.D9) return (uint)('0' + (key - Key.D0));

        return physical switch
        {
            TerminalKey.Backquote => '`',
            TerminalKey.Backslash => '\\',
            TerminalKey.BracketLeft => '[',
            TerminalKey.BracketRight => ']',
            TerminalKey.Comma => ',',
            TerminalKey.Equal => '=',
            TerminalKey.Minus => '-',
            TerminalKey.Period => '.',
            TerminalKey.Quote => '\'',
            TerminalKey.Semicolon => ';',
            TerminalKey.Slash => '/',
            TerminalKey.Space => ' ',
            _ => 0,
        };
    }
}

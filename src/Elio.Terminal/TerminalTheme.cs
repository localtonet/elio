namespace Elio.Terminal;

/// <summary>Terminal colors as 0xRRGGBB.</summary>
public sealed record TerminalTheme(string Name, uint Foreground, uint Background, uint Cursor, uint SelectionBackground, uint[] Ansi)
{
    public static TerminalTheme Default { get; } = new(
        "Elio",
        0xD6DCE5, 0x0F1216, 0x7AA2F7, 0x2B3A55,
        [
            0x1C2128, 0xF7768E, 0x9ECE6A, 0xE0AF68, 0x7AA2F7, 0xBB9AF7, 0x7DCFFF, 0xC0CAD8,
            0x4A5365, 0xFF8FA3, 0xB5E07E, 0xF0C080, 0x94B6FF, 0xCDB0FF, 0x99DCFF, 0xE9EEF5,
        ]);

    private static readonly TerminalTheme[] BuiltIn =
    [
        Default,
        new(
            "Midnight",
            0xD8DEE9, 0x2E3440, 0xD8DEE9, 0x434C5E,
            [
                0x3B4252, 0xBF616A, 0xA3BE8C, 0xEBCB8B, 0x81A1C1, 0xB48EAD, 0x88C0D0, 0xE5E9F0,
                0x4C566A, 0xBF616A, 0xA3BE8C, 0xEBCB8B, 0x81A1C1, 0xB48EAD, 0x8FBCBB, 0xECEFF4,
            ]),
        new(
            "Violet",
            0xF8F8F2, 0x282A36, 0xF8F8F2, 0x44475A,
            [
                0x21222C, 0xFF5555, 0x50FA7B, 0xF1FA8C, 0xBD93F9, 0xFF79C6, 0x8BE9FD, 0xF8F8F2,
                0x6272A4, 0xFF6E6E, 0x69FF94, 0xFFFFA5, 0xD6ACFF, 0xFF92DF, 0xA4FFFF, 0xFFFFFF,
            ]),
        new(
            "Sand",
            0x657B83, 0xFDF6E3, 0x586E75, 0xEEE8D5,
            [
                0x073642, 0xDC322F, 0x859900, 0xB58900, 0x268BD2, 0xD33682, 0x2AA198, 0xEEE8D5,
                0x002B36, 0xCB4B16, 0x586E75, 0x657B83, 0x839496, 0x6C71C4, 0x93A1A1, 0xFDF6E3,
            ]),
        new(
            "Elio Light",
            0x24292F, 0xFFFFFF, 0x2F6FEB, 0xCFE0FF,
            [
                0x24292F, 0xCF222E, 0x116329, 0x9A6700, 0x0969DA, 0x8250DF, 0x1B7C83, 0x6E7781,
                0x57606A, 0xA40E26, 0x1A7F37, 0x633C01, 0x218BFF, 0xA475F9, 0x3192AA, 0x8C959F,
            ]),
    ];

    /// <summary>Elio's own themes first, then the collection that ships with it.</summary>
    public static IReadOnlyList<TerminalTheme> All { get; } = [.. BuiltIn, .. LoadCollection()];

    /// <summary>Whether the theme has a light background, going by how bright it looks.</summary>
    public bool IsLight =>
        ((((Background >> 16) & 0xFF) * 299) + (((Background >> 8) & 0xFF) * 587) + ((Background & 0xFF) * 114)) / 1000 > 140;

    // One theme per line: name|foreground|background|cursor|selection|sixteen colors.
    private static List<TerminalTheme> LoadCollection()
    {
        var themes = new List<TerminalTheme>();
        System.Reflection.Assembly assembly = typeof(TerminalTheme).Assembly;
        string? resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(".Themes.schemes.txt", StringComparison.Ordinal));
        if (resource is null) return themes;

        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        while (reader.ReadLine() is string line)
        {
            if (line.Length == 0 || line[0] == '#') continue;

            string[] fields = line.Split('|');
            string[] colors = fields.Length == 6 ? fields[5].Split(',') : [];
            if (colors.Length != 16 || BuiltIn.Any(t => t.Name == fields[0])) continue;

            try
            {
                themes.Add(new TerminalTheme(fields[0], Hex(fields[1]), Hex(fields[2]), Hex(fields[3]), Hex(fields[4]), [.. colors.Select(Hex)]));
            }
            catch (FormatException)
            {
                // A damaged line costs that one theme.
            }
        }

        return themes;
    }

    private static uint Hex(string text) => uint.Parse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);

    public static TerminalTheme Named(string name) => All.FirstOrDefault(t => t.Name == name) ?? Default;
}

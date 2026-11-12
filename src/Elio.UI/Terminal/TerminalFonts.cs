using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Elio.UI.Terminal;

/// <summary>The fonts and cell size for one display scale and text size. Glyph lookups only happen on the render thread.</summary>
internal sealed class TerminalFonts
{
    private const int MaxCachedLigatures = 4096;

    // Bundled so the terminal looks the same everywhere. System fonts also differ in how they
    // render: Cascadia Mono, a variable font, comes out bold through Skia on Windows.
    private const string Family = "JetBrains Mono";
    private static readonly SKTypeface[] Typefaces = [Load("Regular"), Load("Bold"), Load("Italic"), Load("BoldItalic")];

    private static readonly Dictionary<(double, double), TerminalFonts> Cache = [];

    private readonly float _size;
    private readonly SKFont[] _styles = new SKFont[4];
    private readonly Dictionary<ulong, (SKFont Font, ushort Glyph)> _glyphs = [];
    private readonly Dictionary<string, SKFont> _fallbacks = [];
    private readonly Dictionary<(string, SKFont), (ushort[], SKPoint[])> _clusters = [];
    private readonly Dictionary<(string, SKFont), ushort[]?> _ligatures = [];
    private readonly Dictionary<SKTypeface, SKShaper> _shapers = [];

    public float CellWidth { get; }
    public float CellHeight { get; }
    public float Baseline { get; }
    public float UnderlineOffset { get; }
    public double Scale { get; }

    public static TerminalFonts For(double scale, double size)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue((scale, size), out TerminalFonts? fonts)) Cache[(scale, size)] = fonts = new TerminalFonts(scale, (float)size);
            return fonts;
        }
    }

    private TerminalFonts(double scale, float size)
    {
        Scale = scale;
        _size = size;
        for (int i = 0; i < _styles.Length; i++) _styles[i] = Configure(new SKFont(Typefaces[i], _size));

        // The grid is rounded to whole device pixels, otherwise neighbouring cells drift apart.
        SKFont regular = _styles[0];
        regular.GetFontMetrics(out SKFontMetrics metrics);
        float deviceScale = (float)scale;
        CellWidth = MathF.Round(regular.MeasureText("M") * deviceScale) / deviceScale;
        CellHeight = MathF.Ceiling((metrics.Descent - metrics.Ascent) * deviceScale) / deviceScale;
        Baseline = MathF.Round(-metrics.Ascent * deviceScale) / deviceScale;
        UnderlineOffset = Math.Max(metrics.UnderlinePosition ?? 2, 1);
    }

    private static SKTypeface Load(string style)
    {
        using Stream stream = typeof(TerminalFonts).Assembly.GetManifestResourceStream($"Elio.UI.Fonts.JetBrainsMono-{style}.ttf")!;
        return SKTypeface.FromStream(stream);
    }

    private static SKFont Configure(SKFont font)
    {
        font.Edging = SKFontEdging.Antialias;
        font.Hinting = SKFontHinting.Slight;
        font.Subpixel = true;
        return font;
    }

    public SKFont Style(bool bold, bool italic) => _styles[(bold ? 1 : 0) + (italic ? 2 : 0)];

    public (SKFont Font, ushort Glyph) Resolve(uint codepoint, bool bold, bool italic)
    {
        ulong key = codepoint | (bold ? 1UL << 32 : 0) | (italic ? 1UL << 33 : 0);
        if (_glyphs.TryGetValue(key, out var found)) return found;

        SKFont font = Style(bold, italic);
        ushort glyph = font.GetGlyph((int)codepoint);
        if (glyph == 0)
        {
            SKFont? fallback = Fallback(codepoint, bold, italic);
            if (fallback is not null && fallback.GetGlyph((int)codepoint) is var other && other != 0) (font, glyph) = (fallback, other);
        }

        return _glyphs[key] = (font, glyph);
    }

    private SKShaper Shaper(SKFont font)
    {
        if (!_shapers.TryGetValue(font.Typeface, out SKShaper? shaper)) _shapers[font.Typeface] = shaper = new SKShaper(font.Typeface);
        return shaper;
    }

    /// <summary>Places a character made of several code points, such as a letter with a combining accent.</summary>
    public (ushort[] Glyphs, SKPoint[] Positions) Shape(string cluster, SKFont font)
    {
        var key = (cluster, font);
        if (_clusters.TryGetValue(key, out var shaped)) return shaped;

        SKShaper.Result result = Shaper(font).Shape(cluster, 0, 0, font);
        return _clusters[key] = (result.Codepoints.Select(c => (ushort)c).ToArray(), result.Points);
    }

    /// <summary>
    /// Swaps in ligature glyphs for a run of cells. The font keeps one glyph per cell, so the grid is unaffected.
    /// </summary>
    public void ApplyLigatures(string text, SKFont font, Span<ushort> glyphs)
    {
        var key = (text, font);
        if (!_ligatures.TryGetValue(key, out ushort[]? shaped))
        {
            if (_ligatures.Count >= MaxCachedLigatures) _ligatures.Clear();

            uint[] output = Shaper(font).Shape(text, 0, 0, font).Codepoints;
            shaped = output.Length == text.Length ? output.Select(c => (ushort)c).ToArray() : null;
            _ligatures[key] = shaped;
        }

        shaped?.CopyTo(glyphs);
    }

    private SKFont? Fallback(uint codepoint, bool bold, bool italic)
    {
        var style = (bold, italic) switch
        {
            (true, true) => SKFontStyle.BoldItalic,
            (true, false) => SKFontStyle.Bold,
            (false, true) => SKFontStyle.Italic,
            _ => SKFontStyle.Normal,
        };

        SKTypeface? typeface = SKFontManager.Default.MatchCharacter(Family, style, null, (int)codepoint);
        if (typeface is null) return null;

        string name = $"{typeface.FamilyName}/{typeface.FontWeight}/{typeface.FontSlant}";
        if (!_fallbacks.TryGetValue(name, out SKFont? font)) _fallbacks[name] = font = Configure(new SKFont(typeface, _size));
        return font;
    }
}

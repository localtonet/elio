using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Elio.Core;

/// <summary>A language the app can be shown in.</summary>
/// <param name="Code">The language tag, such as "tr" or "zh-Hans".</param>
/// <param name="Name">The language's name in that language, which is how a list of languages should show it.</param>
public sealed record Language(string Code, string Name, bool RightToLeft = false);

/// <summary>
/// Turns the English text written in the code into the user's language.
/// </summary>
/// <remarks>
/// The English text is the key, so the code reads as plain English and a missing translation shows English
/// rather than a name nobody was meant to see. Translations are the JSON files in the Languages folder,
/// each a flat map from the English text to the translated one.
/// </remarks>
public static class L
{
    private const string DefaultCode = "en";

    private static Dictionary<string, string> _translations = [];

    private const string NamesFile = "_names";

    // Written from right to left, so the layout is mirrored for them.
    private static readonly string[] RightToLeftCodes = ["ar", "fa", "he", "ur"];

    /// <summary>Every language that has a translation file, English first, each under its own name.</summary>
    public static IReadOnlyList<Language> Languages { get; } = LoadLanguages();

    // The names are kept in a file beside the translations, so the code holds English only.
    private static List<Language> LoadLanguages()
    {
        var languages = Load(NamesFile)
            .Select(pair => new Language(pair.Key, pair.Value, RightToLeftCodes.Contains(pair.Key)))
            .OrderBy(language => language.Code != DefaultCode)
            .ThenBy(language => language.Code, StringComparer.Ordinal)
            .ToList();
        if (languages.Count == 0) languages.Add(new Language(DefaultCode, "English"));
        return languages;
    }

    /// <summary>The language in use.</summary>
    public static Language Current { get; private set; } = Languages[0];

    /// <summary>Whether the language in use is written from right to left.</summary>
    public static bool RightToLeft => Current.RightToLeft;

    /// <summary>Switches language. Text already on screen stays as it is until it is drawn again.</summary>
    /// <param name="code">A language tag, or null to follow the operating system.</param>
    public static void Use(string? code)
    {
        Current = Find(code) ?? Find(CultureInfo.CurrentUICulture.Name) ?? Languages[0];
        _translations = Current.Code == DefaultCode ? [] : Load(Current.Code);
    }

    // "pt-BR" falls back to "pt", and any Chinese to the one Chinese there is.
    private static Language? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        if (code.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return Languages.FirstOrDefault(l => l.Code == "zh-Hans");

        string primary = code.Split('-', '_')[0];
        return Languages.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
            ?? Languages.FirstOrDefault(l => l.Code.Equals(primary, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, string> Load(string code)
    {
        Assembly assembly = typeof(L).Assembly;
        string? resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".Languages.{code}.json", StringComparison.OrdinalIgnoreCase));
        if (resource is null) return [];

        try
        {
            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            return JsonSerializer.Deserialize(stream, CoreJsonContext.Default.DictionaryStringString) ?? [];
        }
        catch (JsonException)
        {
            // A damaged translation file costs the translation, not the app.
            return [];
        }
    }

    /// <summary>The text in the user's language, or as given if there is no translation for it.</summary>
    public static string T(string english) =>
        _translations.TryGetValue(english, out string? translated) && translated.Length > 0 ? translated : english;

    /// <summary>The text in the user's language, with {0}, {1} and so on filled in.</summary>
    public static string T(string english, params object?[] values)
    {
        string format = T(english);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, values);
        }
        catch (FormatException)
        {
            // A translation with a broken placeholder must not take the feature down with it.
            return string.Format(CultureInfo.CurrentCulture, english, values);
        }
    }
}

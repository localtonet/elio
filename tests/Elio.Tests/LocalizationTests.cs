using System.Text.Json;
using System.Text.RegularExpressions;
using Elio.Core;

namespace Elio.Tests;

public sealed partial class LocalizationTests
{
    private const string NamesFile = "_names";

    // The files as they are in src/Elio.Core/Languages, copied beside the tests by the project file.
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Languages");

    private static IEnumerable<string> Codes() =>
        Directory.GetFiles(Folder, "*.json")
            .Select(file => Path.GetFileNameWithoutExtension(file))
            .Where(code => code != NamesFile)
            .Order(StringComparer.Ordinal);

    public static TheoryData<string> LanguageCodes()
    {
        var codes = new TheoryData<string>();
        foreach (string code in Codes()) codes.Add(code);
        return codes;
    }

    private static Dictionary<string, string> Read(string code)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, code + ".json")));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in document.RootElement.EnumerateObject())
        {
            Assert.Equal(JsonValueKind.String, entry.Value.ValueKind);
            Assert.True(entries.TryAdd(entry.Name, entry.Value.GetString()!), $"{code}.json has \"{entry.Name}\" twice.");
        }

        return entries;
    }

    [GeneratedRegex(@"\{(\d+)(?:[,:][^{}]*)?\}")]
    private static partial Regex Placeholder();

    private static SortedSet<int> Placeholders(string text) =>
        [.. Placeholder().Matches(text.Replace("{{", string.Empty).Replace("}}", string.Empty)).Select(match => int.Parse(match.Groups[1].Value))];

    [Fact]
    public void LanguagesFolder_HasTranslations() =>
        Assert.NotEmpty(Codes());

    [Theory]
    [MemberData(nameof(LanguageCodes))]
    public void LanguageFile_IsAFlatJsonMapOfTextToText(string code) =>
        Assert.NotEmpty(Read(code));

    [Theory]
    [MemberData(nameof(LanguageCodes))]
    public void LanguageFile_EveryTranslationHasThePlaceholdersOfItsEnglishText(string code)
    {
        var wrong = new List<string>();
        foreach ((string english, string translated) in Read(code))
        {
            // An empty translation is shown as the English text.
            if (translated.Length == 0) continue;
            if (!Placeholders(english).SetEquals(Placeholders(translated))) wrong.Add($"{code}.json: \"{english}\" => \"{translated}\"");
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void NamesFile_NamesExactlyEnglishAndTheLanguagesThatHaveAFile() =>
        Assert.Equal(Codes().Append("en").Order(StringComparer.Ordinal), Read(NamesFile).Keys.Order(StringComparer.Ordinal));

    [Fact]
    public void Languages_ListsEnglishFirst() =>
        Assert.Equal("en", L.Languages[0].Code);

    [Fact]
    public void T_WithoutATranslation_ReturnsTheEnglishTextWithValuesFilledIn() =>
        Assert.Equal("No such text 7 anywhere", L.T("No such text {0} anywhere", 7));
}

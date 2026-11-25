using System.Text.RegularExpressions;

namespace Elio.Core;

/// <summary>A blank in a snippet, written {{name}} or {{name:what it is unless changed}}.</summary>
public sealed record SnippetBlank(string Name, string Default);

/// <summary>
/// Snippets with blanks to fill in: "git checkout {{branch:main}}" asks for the branch, with main already there, before
/// it is typed. Only a plain name between the braces is a blank, so "docker ps --format '{{.Names}}'" and other
/// templates that a command passes on to a program are typed as they are.
/// </summary>
public static partial class SnippetTemplate
{
    // A name, then optionally a colon and a default that stays on one line and holds no braces.
    [GeneratedRegex(@"\{\{([A-Za-z_][A-Za-z0-9_-]{0,63})(?::([^{}\r\n]{0,500}))?\}\}")]
    private static partial Regex BlankPattern();

    // Words that stand alone between braces in Go templates, which kubectl, docker and helm commands pass on:
    // "{{range .items}}...{{end}}". They are never blanks.
    private static readonly HashSet<string> TemplateWords = new(StringComparer.Ordinal) { "end", "else", "break", "continue", "nil", "true", "false" };

    /// <summary>The blanks of a command, each name once, in the order they first appear. The first default given for a name wins.</summary>
    public static IReadOnlyList<SnippetBlank> Blanks(string command)
    {
        var blanks = new List<SnippetBlank>();
        foreach (Match match in BlankPattern().Matches(command))
        {
            string name = match.Groups[1].Value;
            if (TemplateWords.Contains(name) || blanks.Any(b => b.Name == name)) continue;
            blanks.Add(new SnippetBlank(name, match.Groups[2].Value));
        }

        return blanks;
    }

    /// <summary>
    /// The command with every blank filled in. A name with no value given keeps its default. A value never brings a
    /// line break with it: a line break is what makes a terminal run a line, and a blank is part of one line.
    /// </summary>
    public static string Fill(string command, IReadOnlyDictionary<string, string> values) =>
        BlankPattern().Replace(command, match =>
        {
            string name = match.Groups[1].Value;
            if (TemplateWords.Contains(name)) return match.Value;
            string value = values.TryGetValue(name, out string? given) ? given : match.Groups[2].Value;
            return OneLine(value);
        });

    private static string OneLine(string value) =>
        string.Concat(value.Select(c => c is '\r' or '\n' ? ' ' : c)).Replace("\u2028", " ", StringComparison.Ordinal).Replace("\u2029", " ", StringComparison.Ordinal);
}

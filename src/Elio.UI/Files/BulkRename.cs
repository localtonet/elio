using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Elio.Core;

namespace Elio.UI.Files;

/// <summary>What a bulk rename does to each name.</summary>
public enum BulkRenameMode
{
    /// <summary>Replaces text in the names, as plain text or as a regular expression.</summary>
    FindReplace,

    /// <summary>Gives every name a counter, from a template such as <c>{name}-{n}</c>.</summary>
    Number,

    /// <summary>Puts the names in lower case, upper case or title case.</summary>
    Case,

    /// <summary>Puts text in front of the names and behind them.</summary>
    Affix,
}

public enum BulkRenameCase
{
    Lower,
    Upper,
    Title,
}

/// <summary>What to do to the names. Only the fields of the chosen mode are read.</summary>
public sealed record BulkRenameOptions
{
    public BulkRenameMode Mode { get; init; }

    public string Find { get; init; } = string.Empty;

    /// <summary>What replaces what was found. For a regular expression, <c>$1</c> and the like stand for its groups.</summary>
    public string Replace { get; init; } = string.Empty;

    public bool Regex { get; init; }

    public bool IgnoreCase { get; init; } = true;

    /// <summary>
    /// How a numbered name is made: <c>{name}</c> is the name without its extension, <c>{ext}</c> the extension with
    /// its dot and <c>{n}</c> the counter. A template without <c>{ext}</c> keeps the extension at the end.
    /// </summary>
    public string Template { get; init; } = "{name}-{n}";

    public int Start { get; init; } = 1;

    public int Step { get; init; } = 1;

    /// <summary>How many digits the counter has at least, filled up with zeros in front.</summary>
    public int Pad { get; init; }

    public BulkRenameCase Case { get; init; }

    public string Prefix { get; init; } = string.Empty;

    /// <summary>Goes behind the name and in front of the extension.</summary>
    public string Suffix { get; init; } = string.Empty;

    /// <summary>Leaves the extension alone, so that only the name in front of it is changed.</summary>
    public bool KeepExtension { get; init; } = true;
}

/// <param name="Problem">Why the name cannot be used, or null. The text is for the user.</param>
public sealed record BulkRenameEntry(string Old, string New, string? Problem);

/// <param name="Entries">One per name given, in the order given.</param>
/// <param name="Steps">The renames to carry out, in the order that is safe to carry them out in.</param>
public sealed record BulkRenamePlan(IReadOnlyList<BulkRenameEntry> Entries, IReadOnlyList<(string From, string To)> Steps)
{
    public bool HasProblems => Entries.Any(e => e.Problem is not null);

    /// <summary>How many names really change.</summary>
    public int Changes => Entries.Count(e => e.Problem is null && e.New != e.Old);
}

/// <summary>
/// Works out what renaming many files at once would do, before anything is touched: the new names, what is wrong with
/// any of them, and an order to rename in that never overwrites a file that has yet to be moved. Nothing here reads
/// a disk, so the same code serves this computer and a server, and a preview can be made on every keystroke.
/// </summary>
public static class BulkRename
{
    // A pattern that cannot finish on a name is a mistake, and many names are tried with it one after the other.
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    // Longest a name can be on the file systems servers have, counted in bytes.
    private const int MaxNameBytes = 255;

    /// <param name="names">The names to change, in the order they are shown. Numbering follows this order.</param>
    /// <param name="siblings">Every name in the folder, the ones to change included.</param>
    public static BulkRenamePlan Plan(IReadOnlyList<string> names, IReadOnlyCollection<string> siblings, BulkRenameOptions options)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(siblings);
        ArgumentNullException.ThrowIfNull(options);

        var proposed = new string[names.Count];
        var problems = new string?[names.Count];
        Propose(names, options, proposed, problems);

        for (int i = 0; i < names.Count; i++)
        {
            if (problems[i] is null && proposed[i] != names[i]) problems[i] = Invalid(proposed[i]);
        }

        FindCollisions(names, siblings, proposed, problems);

        var entries = new List<BulkRenameEntry>(names.Count);
        var moves = new List<(string From, string To)>();
        for (int i = 0; i < names.Count; i++)
        {
            entries.Add(new BulkRenameEntry(names[i], proposed[i], problems[i]));
            if (problems[i] is null && proposed[i] != names[i]) moves.Add((names[i], proposed[i]));
        }

        // Names that a temporary one must not be mistaken for: everything that is there and everything that is to be.
        var taken = new HashSet<string>(siblings, StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(proposed);
        return new BulkRenamePlan(entries, Order(moves, taken));
    }

    private static void Propose(IReadOnlyList<string> names, BulkRenameOptions options, string[] proposed, string?[] problems)
    {
        for (int i = 0; i < names.Count; i++) proposed[i] = names[i];

        Regex? pattern = null;
        if (options is { Mode: BulkRenameMode.FindReplace, Regex: true, Find.Length: > 0 })
        {
            try
            {
                pattern = new Regex(options.Find, RegexOptions.CultureInvariant | (options.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), RegexTimeout);
            }
            catch (ArgumentException)
            {
                // The same words for every name, so the preview says what is wrong once and keeps the names as they are.
                for (int i = 0; i < names.Count; i++) problems[i] = L.T("Not a valid pattern");
                return;
            }
        }

        for (int i = 0; i < names.Count; i++)
        {
            try
            {
                proposed[i] = Rename(names[i], i, options, pattern);
            }
            catch (RegexMatchTimeoutException)
            {
                problems[i] = L.T("Took too long");
            }
        }
    }

    private static string Rename(string name, int index, BulkRenameOptions options, Regex? pattern)
    {
        switch (options.Mode)
        {
            case BulkRenameMode.FindReplace:
            {
                if (options.Find.Length == 0) return name;

                (string stem, string extension) = options.KeepExtension ? Split(name) : (name, string.Empty);
                string changed = pattern is not null
                    ? pattern.Replace(stem, options.Replace)
                    : stem.Replace(options.Find, options.Replace, options.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                return changed + extension;
            }

            case BulkRenameMode.Number:
            {
                // Asking for {ext} in the template is asking to see the extension apart from the name.
                bool placesExtension = options.Template.Contains("{ext}", StringComparison.Ordinal);
                (string stem, string extension) = options.KeepExtension || placesExtension ? Split(name) : (name, string.Empty);
                string filled = Fill(options.Template, stem, extension, Counter(options, index));
                return placesExtension || !options.KeepExtension ? filled : filled + extension;
            }

            case BulkRenameMode.Case:
            {
                (string stem, string extension) = options.KeepExtension ? Split(name) : (name, string.Empty);
                return ChangeCase(stem, options.Case) + extension;
            }

            case BulkRenameMode.Affix:
            {
                (string stem, string extension) = options.KeepExtension ? Split(name) : (name, string.Empty);
                return options.Prefix + stem + options.Suffix + extension;
            }

            default:
                return name;
        }
    }

    // The last dot starts the extension, except at the front of a name: ".bashrc" is all name.
    private static (string Stem, string Extension) Split(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot <= 0 ? (name, string.Empty) : (name[..dot], name[dot..]);
    }

    private static string Counter(BulkRenameOptions options, int index)
    {
        long value = options.Start + (long)index * options.Step;
        return value.ToString("D" + Math.Clamp(options.Pad, 0, 18), CultureInfo.InvariantCulture);
    }

    // One pass over the template, so that what is put in is never read as a placeholder itself.
    private static string Fill(string template, string stem, string extension, string counter)
    {
        var text = new StringBuilder();
        for (int i = 0; i < template.Length; i++)
        {
            ReadOnlySpan<char> rest = template.AsSpan(i);
            if (rest.StartsWith("{name}"))
            {
                text.Append(stem);
                i += "{name}".Length - 1;
            }
            else if (rest.StartsWith("{ext}"))
            {
                text.Append(extension);
                i += "{ext}".Length - 1;
            }
            else if (rest.StartsWith("{n}"))
            {
                text.Append(counter);
                i += "{n}".Length - 1;
            }
            else
            {
                text.Append(template[i]);
            }
        }

        return text.ToString();
    }

    private static string ChangeCase(string text, BulkRenameCase how) => how switch
    {
        BulkRenameCase.Lower => text.ToLowerInvariant(),
        BulkRenameCase.Upper => text.ToUpperInvariant(),
        _ => TitleCase(text),
    };

    // A word starts after anything that is not a letter or a digit, except an apostrophe: "don't" stays one word.
    private static string TitleCase(string text)
    {
        var result = new StringBuilder(text.Length);
        bool wordStart = true;
        foreach (char c in text)
        {
            result.Append(wordStart ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            wordStart = !char.IsLetterOrDigit(c) && c is not ('\'' or '’');
        }

        return result.ToString();
    }

    private static string? Invalid(string name)
    {
        if (name.Trim().Length == 0) return L.T("The name is empty.");
        if (name.AsSpan().IndexOfAny('/', '\\', '\0') >= 0) return L.T("The name has a character that is not allowed.");
        if (name is "." or "..") return L.T("\".\" and \"..\" cannot be used as names.");
        if (Encoding.UTF8.GetByteCount(name) > MaxNameBytes) return L.T("The name is too long.");
        return null;
    }

    /// <summary>
    /// A name that would be taken twice, or that something in the folder already has, cannot be used. Names are
    /// compared without regard to case: a computer that does not tell them apart would otherwise lose a file.
    /// A file that cannot be renamed keeps its name, which can in turn be the one another wanted, so this goes on
    /// until nothing new is found.
    /// </summary>
    private static void FindCollisions(IReadOnlyList<string> names, IReadOnlyCollection<string> siblings, string[] proposed, string?[] problems)
    {
        var renamed = new HashSet<string>(names, StringComparer.Ordinal);
        var foreign = new HashSet<string>(siblings.Where(s => !renamed.Contains(s)), StringComparer.OrdinalIgnoreCase);

        bool found;
        do
        {
            found = false;

            var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var wanted = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Count; i++)
            {
                if (problems[i] is not null || proposed[i] == names[i])
                {
                    held.Add(names[i]);
                    continue;
                }

                if (!wanted.TryGetValue(proposed[i], out List<int>? asking)) wanted[proposed[i]] = asking = [];
                asking.Add(i);
            }

            foreach ((string name, List<int> asking) in wanted)
            {
                string? problem = asking.Count > 1 ? L.T("Two files would get the same name.")
                    : foreign.Contains(name) || held.Contains(name) ? L.T("Another file in the folder already has this name.")
                    : null;
                if (problem is null) continue;

                foreach (int i in asking) problems[i] = problem;
                found = true;
            }
        }
        while (found);
    }

    /// <summary>
    /// Puts the renames in an order in which none of them lands on a name that is still to be moved away: with a→b and
    /// b→c, b goes first. Where they go round in a circle, as in a swap, one of them is parked under a temporary name
    /// to break it.
    /// </summary>
    private static List<(string From, string To)> Order(List<(string From, string To)> moves, HashSet<string> taken)
    {
        int count = moves.Count;
        var order = new List<(string From, string To)>(count);
        if (count == 0) return order;

        var movers = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            if (!movers.TryGetValue(moves[i].From, out List<int>? same)) movers[moves[i].From] = same = [];
            same.Add(i);
        }

        // Step i has to wait for every other step whose source is the name it is going to.
        var blockedBy = new List<int>[count];
        var dependents = new List<int>[count];
        var waiting = new int[count];
        for (int i = 0; i < count; i++) blockedBy[i] = [];
        for (int i = 0; i < count; i++) dependents[i] = [];
        for (int i = 0; i < count; i++)
        {
            if (!movers.TryGetValue(moves[i].To, out List<int>? holders)) continue;

            foreach (int holder in holders.Where(h => h != i))
            {
                blockedBy[i].Add(holder);
                dependents[holder].Add(i);
                waiting[i]++;
            }
        }

        // Lowest first, so that the order of the names given is kept wherever it does not matter.
        var ready = new PriorityQueue<int, int>();
        for (int i = 0; i < count; i++)
        {
            if (waiting[i] == 0) ready.Enqueue(i, i);
        }

        var source = moves.ConvertAll(m => m.From);
        var done = new bool[count];
        var freed = new bool[count];
        int emitted = 0;
        int temporaries = 0;

        void Free(int step)
        {
            freed[step] = true;
            foreach (int dependent in dependents[step])
            {
                if (!done[dependent] && --waiting[dependent] == 0) ready.Enqueue(dependent, dependent);
            }
        }

        while (emitted < count)
        {
            if (ready.TryDequeue(out int next, out _))
            {
                order.Add((source[next], moves[next].To));
                done[next] = true;
                emitted++;
                if (!freed[next]) Free(next);
                continue;
            }

            // Every step left waits for another one. Following who waits for whom ends on a step that is in a circle.
            int circle = Array.FindIndex(done, d => !d);
            var seen = new HashSet<int>();
            while (seen.Add(circle)) circle = blockedBy[circle].First(b => !freed[b]);

            string temporary;
            do
            {
                temporary = $".elio-rename-{++temporaries}";
            }
            while (!taken.Add(temporary));

            order.Add((source[circle], temporary));
            source[circle] = temporary;
            Free(circle);
        }

        return order;
    }
}

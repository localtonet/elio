namespace Elio.UI.Files;

/// <summary>
/// Typing in a list to move to a name, as in a file manager: the letters typed within a moment of each other are the start
/// of the name to move to, and the same letter again goes on to the next name that starts with it.
/// </summary>
public sealed class TypeAhead(TimeSpan timeout)
{
    private string _typed = string.Empty;
    private DateTime _last = DateTime.MinValue;

    public TypeAhead() : this(TimeSpan.FromMilliseconds(900))
    {
    }

    /// <summary>The letters taken so far.</summary>
    public string Typed => _typed;

    /// <summary>Forgets what was typed.</summary>
    public void Reset() => _typed = string.Empty;

    /// <summary>The index of the name to move to, or -1 when none starts with what was typed.</summary>
    /// <param name="text">What was just typed.</param>
    /// <param name="names">The names in the order they are listed.</param>
    /// <param name="current">The name now chosen, or -1.</param>
    public int Type(string text, IReadOnlyList<string> names, int current, DateTime now)
    {
        if (text.Length == 0 || names.Count == 0) return -1;

        if (now - _last > timeout) _typed = string.Empty;
        _last = now;

        // A space that begins nothing is the list's own key, to pick the row, and not the start of a name.
        if (_typed.Length == 0 && string.IsNullOrWhiteSpace(text)) return -1;
        _typed += text;

        // One letter typed over and over walks through the names that start with it.
        bool repeated = _typed.Length > 1 && _typed.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(_typed[0]));
        string prefix = repeated ? _typed[..1] : _typed;
        int start = repeated || _typed.Length == text.Length ? current + 1 : Math.Max(current, 0);

        for (int i = 0; i < names.Count; i++)
        {
            int index = ((start + i) % names.Count + names.Count) % names.Count;
            if (names[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return index;
        }

        return -1;
    }
}

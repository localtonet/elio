using Avalonia.Input;

namespace Elio.UI;

/// <summary>Key sequences for commands, including multi-step chords such as "Ctrl+K Ctrl+S".</summary>
public sealed class ShortcutMap
{
    private static readonly TimeSpan ChordTimeout = TimeSpan.FromSeconds(2);

    private readonly List<(string Action, KeyGesture[] Steps)> _bindings = [];
    private List<(string Action, KeyGesture[] Steps)>? _candidates;
    private int _step;
    private DateTime _chordStarted;

    public static IReadOnlyDictionary<string, string> Defaults { get; } = BuildDefaults();

    private static Dictionary<string, string> BuildDefaults()
    {
        string primary = OperatingSystem.IsMacOS() ? "Cmd" : "Ctrl";
        return new Dictionary<string, string>
        {
            ["palette"] = $"{primary}+Shift+P",
            ["new-tab"] = $"{primary}+Shift+T",
            ["local-shell"] = $"{primary}+Shift+L",
            ["split-right"] = $"{primary}+Shift+D",
            ["split-down"] = $"{primary}+Shift+E",
            ["close-pane"] = $"{primary}+Shift+W",
            ["next-tab"] = "Ctrl+Tab",
            ["previous-tab"] = "Ctrl+Shift+Tab",
            ["font-larger"] = $"{primary}+OemPlus",
            ["font-smaller"] = $"{primary}+OemMinus",
            ["font-reset"] = $"{primary}+D0",
            ["settings"] = $"{primary}+OemComma",
            ["shortcuts"] = $"{primary}+Shift+K",
            ["broadcast"] = $"{primary}+Shift+B",
            ["find"] = $"{primary}+Shift+F",
            ["reopen-tab"] = $"{primary}+Shift+Z",
            ["last-tab"] = $"{primary}+Shift+O",
            ["zoom-pane"] = $"{primary}+Shift+M",
            ["clear"] = $"{primary}+Shift+Delete",
            ["previous-prompt"] = $"{primary}+Shift+Up",
            ["next-prompt"] = $"{primary}+Shift+Down",
            ["copy-output"] = $"{primary}+Shift+Y",
            ["picture"] = $"{primary}+Shift+S",
            ["history"] = $"{primary}+Shift+H",
        };
    }

    public ShortcutMap(IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var (action, defaultKeys) in Defaults)
        {
            string keys = overrides.TryGetValue(action, out string? custom) ? custom : defaultKeys;
            if (TryParse(keys, out KeyGesture[]? steps)) _bindings.Add((action, steps));
        }
    }

    /// <summary>Whether the text can be read as a key sequence, such as "Ctrl+K Ctrl+S".</summary>
    public static bool IsValid(string keys) => TryParse(keys, out _);

    private static bool TryParse(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out KeyGesture[]? steps)
    {
        try
        {
            steps = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(KeyGesture.Parse).ToArray();
            return steps.Length > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // A typo in the settings file should cost one shortcut, not the app.
            steps = null;
            return false;
        }
    }

    public string? KeysFor(string action) => _bindings.FirstOrDefault(b => b.Action == action).Steps is { } steps
        ? string.Join(' ', steps.Select(s => s.ToString()))
        : null;

    /// <summary>Follows a key press through the bindings.</summary>
    /// <param name="action">The command to run, once a whole sequence has been typed.</param>
    /// <returns>True if the key belongs to a shortcut and should go no further.</returns>
    public bool Process(KeyEventArgs e, out string? action)
    {
        action = null;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return false;

        if (_candidates is not null && DateTime.UtcNow - _chordStarted > ChordTimeout) _candidates = null;

        var pool = _candidates ?? _bindings;
        int step = _candidates is null ? 0 : _step;
        var matches = pool.Where(b => b.Steps.Length > step && b.Steps[step].Matches(e)).ToList();

        if (matches.Count == 0)
        {
            bool wasChord = _candidates is not null;
            _candidates = null;
            return wasChord;
        }

        var finished = matches.FirstOrDefault(b => b.Steps.Length == step + 1);
        if (finished.Action is not null)
        {
            _candidates = null;
            action = finished.Action;
            return true;
        }

        _candidates = matches;
        _step = step + 1;
        _chordStarted = DateTime.UtcNow;
        return true;
    }
}

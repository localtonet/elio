using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Elio.Terminal.Native;

namespace Elio.Terminal;

/// <summary>A command the shell says has finished. Only shells with integration (OSC 133) say so.</summary>
/// <param name="ExitCode">What the command returned, when the shell reported it.</param>
/// <param name="Duration">How long it ran, counted from when its output started.</param>
/// <param name="Command">The command line, which few shells report.</param>
public readonly record struct CommandFinished(int? ExitCode, TimeSpan Duration, string? Command);

// The part of a session that follows the shell's prompts: where each one is, and what the last command did.
public sealed partial class TerminalSession
{
    private const int MaxCommandBytes = 4096;

    private readonly record struct PromptEvent(int Kind, int PromptKind, int? ExitCode, string? Command);

    // Filled by the engine in the middle of taking output, and dealt with as soon as that write returns.
    private readonly List<PromptEvent> _promptEvents = [];
    private readonly List<CommandFinished> _finishedPending = [];

    // Places in the terminal that move along with their text as it scrolls and reflows. Zero until first needed.
    private nint _outputStart;
    private nint _lastOutputStart;
    private nint _lastOutputEnd;
    private bool _outputOpen;
    private bool _hasLastOutput;
    private long _outputStartedAt;
    private string? _runningCommand;
    private volatile bool _promptMarksSeen;

    /// <summary>
    /// A command finished. Raised on the thread output arrives on, and only for shells that mark where
    /// a command's output starts.
    /// </summary>
    public event Action<CommandFinished>? CommandFinished;

    /// <summary>What the last command returned. Null until a shell with integration reports one.</summary>
    public int? LastExitCode { get; private set; }

    /// <summary>The shell has marked at least one prompt, so moving between prompts has something to go by.</summary>
    public bool PromptMarksSeen => _promptMarksSeen;

    /// <summary>
    /// Whether the cursor is at a prompt or in what is being typed after one. Only means something once
    /// <see cref="PromptMarksSeen"/> is true.
    /// </summary>
    public unsafe bool CursorAtPrompt
    {
        get
        {
            byte atPrompt = 0;
            lock (_terminalGate)
            {
                if (_disposed != 0 || Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataCursorAtPrompt, &atPrompt) != Ghostty.Success) return false;
            }
            return atPrompt != 0;
        }
    }

    // Runs inside the engine's write, like the other callbacks, so it only takes note.
    [UnmanagedCallersOnly]
    private static unsafe void OnEnginePrompt(nint terminal, nint userdata, Ghostty.SemanticPrompt* prompt)
    {
        var session = (TerminalSession)GCHandle.FromIntPtr(userdata).Target!;
        Ghostty.NativeString command = prompt->Command;
        string? text = command.Pointer is null || command.Length == 0 ? null : Encoding.UTF8.GetString(command.Pointer, (int)Math.Min(command.Length, MaxCommandBytes));
        session._promptEvents.Add(new PromptEvent(prompt->Kind, prompt->PromptKind, prompt->HasExitCode != 0 ? prompt->ExitCode : null, text));
    }

    /// <summary>
    /// Gives output to the engine, pausing after each OSC sequence. A prompt mark is one of those, and where
    /// the cursor is at that moment is where a command's output starts or ends: by the end of the whole
    /// write it may be many rows further on.
    /// </summary>
    private unsafe void Write(byte* data, nuint length)
    {
        var stop = new Ghostty.WriteStop { Size = (nuint)sizeof(Ghostty.WriteStop) };
        nuint offset = 0;

        while (offset < length)
        {
            int result = Ghostty.TerminalVtWriteUntil(_terminal, data + offset, length - offset, Ghostty.BoundaryOscEnd, &stop);
            if ((result != Ghostty.Success && result != Ghostty.NoValue) || stop.Consumed == 0 || stop.Consumed > length - offset)
            {
                // Not expected, and no reason to lose output over it.
                Ghostty.TerminalVtWrite(_terminal, data + offset, length - offset);
                offset = length;
            }
            else
            {
                offset += stop.Consumed;
            }

            if (_promptEvents.Count > 0) PlacePromptMarks();
        }
    }

    private unsafe void PlacePromptMarks()
    {
        // A full-screen program has no prompts, and a mark made on its screen would point nowhere afterwards.
        int screen = 0;
        bool alternate = Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataActiveScreen, &screen) == Ghostty.Success && screen == Ghostty.ScreenAlternate;

        foreach (PromptEvent mark in _promptEvents)
        {
            if (alternate) break;

            switch (mark.Kind)
            {
                case Ghostty.SemanticPromptStart when mark.PromptKind == Ghostty.SemanticPromptPrimary:
                    _promptMarksSeen = true;

                    // Some shells never say a command ended. The next prompt says it for them.
                    if (_outputOpen) FinishCommand(null);
                    break;

                case Ghostty.SemanticOutputStart:
                    if (!TrackCursor(ref _outputStart)) break;
                    _outputOpen = true;
                    _outputStartedAt = Stopwatch.GetTimestamp();
                    _runningCommand = mark.Command;
                    break;

                case Ghostty.SemanticCommandEnd:
                    // Bash and zsh report an end before every prompt, including the first one and the one
                    // after an empty line. Without output having started there was no command.
                    if (_outputOpen) FinishCommand(mark.ExitCode);
                    else if (mark.ExitCode is not null) LastExitCode = mark.ExitCode;
                    break;
            }
        }

        _promptEvents.Clear();
    }

    private void FinishCommand(int? exitCode)
    {
        _outputOpen = false;
        LastExitCode = exitCode;

        (_outputStart, _lastOutputStart) = (_lastOutputStart, _outputStart);
        _hasLastOutput = TrackCursor(ref _lastOutputEnd);
        _finishedPending.Add(new CommandFinished(exitCode, Stopwatch.GetElapsedTime(_outputStartedAt), _runningCommand));
        _runningCommand = null;
    }

    /// <summary>Makes a mark follow the cell the cursor is on now.</summary>
    private unsafe bool TrackCursor(ref nint mark)
    {
        ushort x = 0, y = 0;
        if (Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataCursorX, &x) != Ghostty.Success
            || Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataCursorY, &y) != Ghostty.Success) return false;

        var point = new Ghostty.Point { Tag = Ghostty.PointTagActive, X = x, Y = y };
        if (mark != 0) return Ghostty.TrackedGridRefSet(mark, _terminal, point) == Ghostty.Success;

        fixed (nint* created = &mark) return Ghostty.TerminalGridRefTrack(_terminal, point, created) == Ghostty.Success;
    }

    /// <summary>
    /// Moves the screen to the prompt before or after the one at its top. Needs a shell that marks its
    /// prompts with OSC 133.
    /// </summary>
    /// <returns>False if there was no such prompt to move to.</returns>
    public unsafe bool ScrollToPrompt(bool older)
    {
        var behavior = new Ghostty.ScrollViewport { Tag = Ghostty.ScrollViewportDeltaPrompt, Delta = older ? -1 : 1 };
        Ghostty.Scrollbar before = default, after = default;

        lock (_terminalGate)
        {
            if (_disposed != 0 || Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataScrollbar, &before) != Ghostty.Success) return false;
            Check(Ghostty.TerminalScrollViewport(_terminal, behavior));
            if (Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataScrollbar, &after) != Ghostty.Success) return false;
        }

        if (before.Offset == after.Offset) return false;
        SignalChanged();
        return true;
    }

    /// <summary>
    /// What the last finished command printed, with wrapped lines joined back together. Null when the
    /// shell has not marked a finished command, when that output has left the history, or while a
    /// full-screen program is showing.
    /// </summary>
    public string? LastCommandOutput() => LastOutput(select: false);

    /// <summary>
    /// The same text as <see cref="LastCommandOutput"/>, and makes it the selection so that what is
    /// about to be copied can be seen.
    /// </summary>
    public string? SelectLastCommandOutput() => LastOutput(select: true);

    private unsafe string? LastOutput(bool select)
    {
        string text;
        lock (_terminalGate)
        {
            if (_disposed != 0 || !_hasLastOutput) return null;

            // The marks belong to the shell's own screen, and rows are counted differently on the other one.
            int screen = 0;
            if (Ghostty.TerminalGet(_terminal, Ghostty.TerminalDataActiveScreen, &screen) != Ghostty.Success || screen == Ghostty.ScreenAlternate) return null;

            Ghostty.PointCoordinate start = default, end = default;
            if (Ghostty.TrackedGridRefPoint(_lastOutputEnd, Ghostty.PointTagScreen, &end) != Ghostty.Success) return null;

            // Output so long that its start has left the history still has an end.
            if (_lastOutputStart == 0 || Ghostty.TrackedGridRefPoint(_lastOutputStart, Ghostty.PointTagScreen, &start) != Ghostty.Success) start = default;

            // The end mark is where the cursor stood afterwards, one cell past the last one that counts.
            if (end.Y < start.Y || (end.Y == start.Y && end.X <= start.X)) return string.Empty;
            if (end.X > 0) end.X--;
            else end = new Ghostty.PointCoordinate { X = (ushort)Math.Max(0, _columns - 1), Y = end.Y - 1 };

            Ghostty.Selection selection = default;
            if (Ghostty.TerminalGridRef(_terminal, new Ghostty.Point { Tag = Ghostty.PointTagScreen, X = start.X, Y = start.Y }, &selection.Start) != Ghostty.Success
                || Ghostty.TerminalGridRef(_terminal, new Ghostty.Point { Tag = Ghostty.PointTagScreen, X = end.X, Y = end.Y }, &selection.End) != Ghostty.Success) return null;

            text = PlainText(&selection);
            if (select) Check(Ghostty.TerminalSet(_terminal, Ghostty.TerminalOptSelection, &selection));
        }

        if (select) SignalChanged();
        return text;
    }
}

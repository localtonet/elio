using System.Text;
using Elio.Terminal;
using Elio.UI.Terminal;

namespace Elio.Tests;

/// <summary>
/// The cursor has to stay in sight while a line is edited. It blinks only on servers that ask for it, and there it used
/// to keep its own beat while the arrow keys moved it, so it was as likely as not to be off where the user looked. And in
/// text a shell shows in reverse, as bash and zsh do with what was just pasted, it took the colors of the text around it.
/// </summary>
public sealed class CursorDrawingTests
{
    private static readonly TerminalTheme Elio = TerminalTheme.Default;
    private static readonly TerminalTheme Midnight = TerminalTheme.All.Single(t => t.Name == "Midnight");

    private static double Distance(uint a, uint b)
    {
        int red = (int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF);
        int green = (int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF);
        int blue = (int)(a & 0xFF) - (int)(b & 0xFF);
        return Math.Sqrt((red * red) + (green * green) + (blue * blue));
    }

    private static TerminalCell Reversed(TerminalTheme theme) =>
        new() { Codepoint = 'e', Foreground = theme.Background, Background = theme.Foreground, Flags = CellFlags.HasBackground };

    [Fact]
    public void ACursorThatStandsOut_IsDrawnAsItWasBefore()
    {
        // Elio's blue cursor is not the color of its text, so reversed text does not hide it.
        Assert.Equal((Elio.Cursor, Elio.Background), TerminalRenderer.CursorColors(Elio.Cursor, Elio.Foreground, Elio.Background, Reversed(Elio)));

        // Nor does a plain cell, or one past the end of the line that has no colors at all.
        var plain = new TerminalCell { Codepoint = 'e', Foreground = Midnight.Foreground, Background = Midnight.Background };
        Assert.Equal((Midnight.Cursor, Midnight.Background), TerminalRenderer.CursorColors(Midnight.Cursor, Midnight.Foreground, Midnight.Background, plain));
        Assert.Equal((Midnight.Cursor, Midnight.Background), TerminalRenderer.CursorColors(Midnight.Cursor, Midnight.Foreground, Midnight.Background, default));
    }

    [Fact]
    public void ACursorTheColorOfReversedText_DrawsTheCellTheOtherWayRound()
    {
        // Midnight's cursor is its text color, which is the background of reversed text.
        Assert.Equal(Midnight.Foreground, Midnight.Cursor);

        (uint cursor, uint text) = TerminalRenderer.CursorColors(Midnight.Cursor, Midnight.Foreground, Midnight.Background, Reversed(Midnight));

        Assert.Equal(Midnight.Background, cursor);
        Assert.Equal(Midnight.Foreground, text);
    }

    [Fact]
    public void ACellWithABackgroundLikeTheCursor_DoesNotHideIt()
    {
        var blue = new TerminalCell { Codepoint = 'x', Foreground = 0xFFFFFF, Background = Elio.Cursor, Flags = CellFlags.HasBackground };

        (uint cursor, uint text) = TerminalRenderer.CursorColors(Elio.Cursor, Elio.Foreground, Elio.Background, blue);

        Assert.Equal(0xFFFFFFu, cursor);
        Assert.True(Distance(text, cursor) > 100);
    }

    private static TerminalFrame Frame(bool visible, bool inView) => new(40, 4, 0, 0xFFFFFF, 0xFFFFFF, visible, 3, 0, CursorShape.Block, false, inView);

    [Fact]
    public void AHiddenCursor_IsDrawnOnlyWhileALineIsTypedAtThePrompt()
    {
        Assert.True(TerminalRenderer.ShowsCursor(Frame(visible: true, inView: true), typingAtPrompt: false));
        Assert.False(TerminalRenderer.ShowsCursor(Frame(visible: false, inView: true), typingAtPrompt: false));
        Assert.True(TerminalRenderer.ShowsCursor(Frame(visible: false, inView: true), typingAtPrompt: true));

        // Scrolled back through the history, the cursor is not on the rows shown, wherever it is.
        Assert.False(TerminalRenderer.ShowsCursor(Frame(visible: false, inView: false), typingAtPrompt: true));
    }

    private static void Print(TerminalSession terminal, string text) => terminal.Feed(Encoding.UTF8.GetBytes(text));

    [EngineFact]
    public async Task ACursorAProgramLeftHidden_ComesBackOnceALineIsTyped_WhereTheShellDoesNotMarkItsPrompts()
    {
        await using var terminal = new TerminalSession(new ScriptedTransport(), 40, 4);

        // A program hid the cursor and was stopped before showing it again. The shell's next prompt does not show it.
        Print(terminal, "\u001b[?25l^C\r\n$ ");
        TerminalFrame frame = terminal.ReadFrame((_, _) => { }, FrameRead.All)!.Value;
        Assert.False(frame.CursorVisible);
        Assert.True(frame.CursorInView);
        Assert.False(terminal.TypingAtPrompt);

        // The first character typed says a line begins here, as the view does it; a paste and the arrow keys come after.
        terminal.MarkInputStart();
        Print(terminal, "echo hello world\u001b[5D");
        Assert.True(terminal.TypingAtPrompt);

        // Enter starts a command, which may hide the cursor on purpose.
        terminal.ClearInputStart();
        Assert.False(terminal.TypingAtPrompt);
    }

    [EngineFact]
    public async Task WhereTheShellMarksItsPrompts_TheCursorIsShownFromThePromptUntilTheCommandStarts()
    {
        await using var terminal = new TerminalSession(new ScriptedTransport(), 40, 4);

        Print(terminal, "\u001b[?25l\u001b]133;A\u0007$ \u001b]133;B\u0007");
        Assert.True(terminal.TypingAtPrompt);

        Print(terminal, "sleep 1\r\n\u001b]133;C\u0007");
        Assert.False(terminal.TypingAtPrompt);

        Print(terminal, "\u001b]133;D;0\u0007\u001b]133;A\u0007$ \u001b]133;B\u0007");
        Assert.True(terminal.TypingAtPrompt);
    }

    [EngineFact]
    public async Task AFullScreenProgram_KeepsItsCursorHidden()
    {
        await using var terminal = new TerminalSession(new ScriptedTransport(), 40, 4);
        Print(terminal, "$ ");
        terminal.MarkInputStart();

        // As vim, htop or less do: their own screen, with the cursor hidden where they want it so.
        Print(terminal, "\u001b[?1049h\u001b[?25l");

        Assert.False(terminal.TypingAtPrompt);
    }

    [EngineFact]
    public async Task OnlyAServerThatAsks_GetsABlinkingCursor()
    {
        await using var terminal = new TerminalSession(new ScriptedTransport(), 40, 4);
        bool Blinks(string sent)
        {
            terminal.Feed(Encoding.UTF8.GetBytes(sent));
            return terminal.ReadFrame((_, _) => { }, FrameRead.All)!.Value.CursorBlinking;
        }

        Assert.False(Blinks("$ "));
        Assert.True(Blinks("\u001b[5 q"));
        Assert.False(Blinks("\u001b[2 q"));
        Assert.True(Blinks("\u001b[?12h"));
        Assert.False(Blinks("\u001b[?12l"));
    }

    [EngineFact]
    public async Task InPastedTextTheShellShowsInReverse_TheCursorStaysInSight_WithEveryTheme()
    {
        foreach (TerminalTheme theme in TerminalTheme.All)
        {
            await using var terminal = new TerminalSession(new ScriptedTransport(), 40, 4);
            terminal.SetTheme(theme);

            // A prompt, the paste highlighted the way readline and zle do, and Left three times into it.
            terminal.Feed(Encoding.UTF8.GetBytes("$ \u001b[?2004h\u001b[7mhello world\u001b[27m\u001b[3D"));
            TerminalCell[] row = [];
            TerminalFrame frame = terminal.ReadFrame((y, cells) => { if (y == 0) row = cells.ToArray(); }, FrameRead.All)!.Value;
            TerminalCell under = row[frame.CursorX];
            Assert.Equal('r', (char)under.Codepoint);

            (uint cursor, uint text) = TerminalRenderer.CursorColors(frame.CursorColor, frame.Foreground, frame.Background, under);

            Assert.True(Distance(cursor, under.Background) >= 48, $"{theme.Name}: the cursor is lost in the reversed text.");
            Assert.True(Distance(text, cursor) >= 48, $"{theme.Name}: the character under the cursor cannot be read.");
        }
    }
}

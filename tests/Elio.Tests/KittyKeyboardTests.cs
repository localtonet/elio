using System.Text;
using Elio.Terminal;

namespace Elio.Tests;

/// <summary>
/// The Kitty keyboard protocol, through the real terminal engine: a program pushes flags with CSI &gt; flags u, and from
/// then on keys reach it as CSI u, until it pops them with CSI &lt; u. What the session sends is what the program reads.
/// </summary>
public sealed class KittyKeyboardTests : IAsyncLifetime
{
    private readonly ScriptedTransport _transport = new();
    private readonly TerminalSession _terminal;

    public KittyKeyboardTests() => _terminal = new TerminalSession(_transport, 80, 24);

    public Task InitializeAsync()
    {
        _terminal.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _terminal.DisposeAsync();

    private void Print(string text) => _terminal.Feed(Encoding.UTF8.GetBytes(text));

    private async Task<string> HeardAsync() => Encoding.UTF8.GetString(await _transport.HeardAsync());

    private async Task<string> KeyAsync(TerminalKey key, TerminalMods mods = TerminalMods.None, uint unshifted = 0, string? text = null, TerminalKeyAction action = TerminalKeyAction.Press)
    {
        Assert.True(_terminal.SendKey(key, mods, unshifted, text, action), $"{action} of {key} sent nothing");
        return await HeardAsync();
    }

    [EngineFact]
    public async Task Escape_IsCsiU_OnlyWhileTheProgramAsks()
    {
        Assert.Equal("\u001b", await KeyAsync(TerminalKey.Escape));

        Print("\u001b[>1u");
        Assert.Equal(KittyKeyboardFlags.Disambiguate, _terminal.KeyboardFlags);
        Assert.Equal("\u001b[27u", await KeyAsync(TerminalKey.Escape));

        Print("\u001b[<u");
        Assert.Equal(KittyKeyboardFlags.None, _terminal.KeyboardFlags);
        Assert.Equal("\u001b", await KeyAsync(TerminalKey.Escape));
    }

    [EngineFact]
    public async Task CtrlI_AndTab_AreTold_Apart()
    {
        Assert.Equal("\t", await KeyAsync(TerminalKey.Tab));
        Assert.Equal("\u001b[Z", await KeyAsync(TerminalKey.Tab, TerminalMods.Shift));

        // The text is the character on the key, which is what the view passes along with Ctrl.
        Print("\u001b[>1u");
        Assert.Equal("\u001b[105;5u", await KeyAsync(TerminalKey.I, TerminalMods.Control, 'i', "i"));
        Assert.Equal("\u001b[91;5u", await KeyAsync(TerminalKey.BracketLeft, TerminalMods.Control, '[', "["));
        Assert.Equal("\u001b[97;3u", await KeyAsync(TerminalKey.A, TerminalMods.Alt, 'a', "a"));
        Assert.Equal("\u001b[9;2u", await KeyAsync(TerminalKey.Tab, TerminalMods.Shift));

        // Tab, Enter and Backspace stay as they were, so a shell left in this mode by a crashed program still works.
        Assert.Equal("\t", await KeyAsync(TerminalKey.Tab));
        Assert.Equal("\r", await KeyAsync(TerminalKey.Enter));
    }

    [EngineFact]
    public async Task ReleaseAndRepeat_AreReported_OnlyWithFlag2()
    {
        // Without the flag a release produces nothing, and a repeat is one more press.
        Assert.False(_terminal.SendKey(TerminalKey.Escape, TerminalMods.None, 0, null, TerminalKeyAction.Release));
        Assert.Equal("\u001b", await KeyAsync(TerminalKey.Escape, action: TerminalKeyAction.Repeat));

        Print("\u001b[>3u");
        Assert.Equal(KittyKeyboardFlags.Disambiguate | KittyKeyboardFlags.ReportEvents, _terminal.KeyboardFlags);
        Assert.Equal("\u001b[27u", await KeyAsync(TerminalKey.Escape));
        Assert.Equal("\u001b[27;1:2u", await KeyAsync(TerminalKey.Escape, action: TerminalKeyAction.Repeat));
        Assert.Equal("\u001b[27;1:3u", await KeyAsync(TerminalKey.Escape, action: TerminalKeyAction.Release));
        Assert.Equal("\u001b[1;1:3A", await KeyAsync(TerminalKey.ArrowUp, action: TerminalKeyAction.Release));

        // A letter is typed as text, but letting it go is still news to the program.
        Assert.Equal("\u001b[97;1:3u", await KeyAsync(TerminalKey.A, unshifted: 'a', action: TerminalKeyAction.Release));

        Print("\u001b[<u");
        Assert.False(_terminal.SendKey(TerminalKey.Escape, TerminalMods.None, 0, null, TerminalKeyAction.Release));
    }

    [EngineFact]
    public async Task EveryKey_IncludingPlainCharacters_IsAnEscapeCode_WithFlag8()
    {
        Assert.Equal("a", await KeyAsync(TerminalKey.A, unshifted: 'a', text: "a"));

        Print("\u001b[>8u");
        Assert.Equal("\u001b[97u", await KeyAsync(TerminalKey.A, unshifted: 'a', text: "a"));
        Assert.Equal("\u001b[13u", await KeyAsync(TerminalKey.Enter));

        // With flag 16 as well, the text the key typed comes along.
        Print("\u001b[>24u");
        Assert.Equal("\u001b[97;;97u", await KeyAsync(TerminalKey.A, unshifted: 'a', text: "a"));
    }

    [EngineFact]
    public async Task Query_IsAnsweredWithTheFlagsInForce()
    {
        Print("\u001b[?u");
        Assert.Equal("\u001b[?0u", await HeardAsync());

        Print("\u001b[>1u\u001b[>11u\u001b[?u");
        Assert.Equal("\u001b[?11u", await HeardAsync());

        Print("\u001b[<u\u001b[?u");
        Assert.Equal("\u001b[?1u", await HeardAsync());

        Print("\u001b[<u\u001b[?u");
        Assert.Equal("\u001b[?0u", await HeardAsync());
    }

    [EngineFact]
    public async Task Flags_CanBeSetAndChangedInPlace()
    {
        // CSI = flags ; mode u: 1 sets them, 2 adds to them, 3 takes them away.
        Print("\u001b[=1;1u\u001b[=2;2u");
        Assert.Equal(KittyKeyboardFlags.Disambiguate | KittyKeyboardFlags.ReportEvents, _terminal.KeyboardFlags);

        Print("\u001b[=1;3u");
        Assert.Equal(KittyKeyboardFlags.ReportEvents, _terminal.KeyboardFlags);
        Assert.Equal("\u001b[27;1:3u", await KeyAsync(TerminalKey.Escape, action: TerminalKeyAction.Release));
    }

    [EngineFact]
    public void FullScreenProgram_HasFlagsOfItsOwn()
    {
        // The main and alternate screens each keep their own stack, so a program that forgets to pop leaves the shell alone.
        Print("\u001b[?1049h\u001b[>1u");
        Assert.Equal(KittyKeyboardFlags.Disambiguate, _terminal.KeyboardFlags);

        Print("\u001b[?1049l");
        Assert.Equal(KittyKeyboardFlags.None, _terminal.KeyboardFlags);
    }

    [EngineFact]
    public void Reset_ForgetsTheFlags()
    {
        Print("\u001b[>1u\u001bc");
        Assert.Equal(KittyKeyboardFlags.None, _terminal.KeyboardFlags);
    }
}

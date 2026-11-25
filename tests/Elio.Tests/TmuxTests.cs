using System.Text;
using Elio.Connections;

namespace Elio.Tests;

/// <summary>tmux layout strings, which come from whatever tmux is running on the server.</summary>
public sealed class TmuxLayoutTests
{
    private static string WithChecksum(string body) => $"{TmuxLayout.Checksum(body):x4},{body}";

    [Fact]
    public void Checksum_MatchesWhatTmuxPrints() =>
        Assert.Equal(0xbb62, TmuxLayout.Checksum("159x48,0,0{79x48,0,0,79x48,80,0}"));

    [Fact]
    public void Parse_SinglePane()
    {
        TmuxLayoutNode root = TmuxLayout.Parse(WithChecksum("80x24,0,0,5"));

        Assert.Equal((TmuxLayoutKind.Pane, 80, 24, 0, 0, "%5"), (root.Kind, root.Width, root.Height, root.X, root.Y, root.PaneId));
        Assert.Empty(root.Children);
    }

    [Fact]
    public void Parse_NestedSplits_GivesThePanesInOrder()
    {
        TmuxLayoutNode root = TmuxLayout.Parse(WithChecksum("213x55,0,0[213x27,0,0,0,213x27,0,28{106x27,0,28,1,106x27,107,28,2}]"));

        Assert.Equal(TmuxLayoutKind.Rows, root.Kind);
        Assert.Equal(TmuxLayoutKind.Columns, root.Children[1].Kind);
        Assert.Equal(["%0", "%1", "%2"], root.Panes().Select(p => p.PaneId));
        TmuxLayoutNode right = root.Find("%2")!;
        Assert.Equal((106, 27, 107, 28), (right.Width, right.Height, right.X, right.Y));
        Assert.Null(root.Find("%9"));
    }

    [Fact]
    public void Parse_OldLayoutWithoutPaneIds_IsStillRead()
    {
        TmuxLayoutNode root = TmuxLayout.Parse("bb62,159x48,0,0{79x48,0,0,79x48,80,0}");

        Assert.Equal(2, root.Children.Count);
        Assert.All(root.Panes(), pane => Assert.Null(pane.PaneId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("80x24,0,0,1")]
    [InlineData("zzzz,80x24,0,0,1")]
    [InlineData("12345,80x24,0,0,1")]
    [InlineData("0000,80x24,0,0,1")]
    public void Parse_WithoutAGoodChecksum_IsRefused(string text)
    {
        Assert.Throws<FormatException>(() => TmuxLayout.Parse(text));
        Assert.False(TmuxLayout.TryParse(text, out _));
    }

    [Theory]
    [InlineData("80x24,0,0,1 trailing")]
    [InlineData("80x24,0,0{40x24,0,0,1")]
    [InlineData("80x24,0,0{}")]
    [InlineData("80x24,0,0[40x24,0,0,1}")]
    [InlineData("80x,0,0,1")]
    [InlineData("x24,0,0,1")]
    [InlineData("1234567890x24,0,0,1")]
    [InlineData("80x24,0,0,99999999999")]
    [InlineData("-80x24,0,0,1")]
    public void Parse_OfSomethingThatIsNotALayout_IsAFormatError(string body) =>
        Assert.Throws<FormatException>(() => TmuxLayout.Parse(WithChecksum(body)));

    [Fact]
    public void Parse_NestedFarTooDeeply_IsRefusedWithoutRunningOutOfStack()
    {
        string body = string.Concat(Enumerable.Repeat("1x1,0,0{", 100_000)) + "1x1,0,0,1" + new string('}', 100_000);

        Fuzz.Within(() => Assert.Throws<FormatException>(() => TmuxLayout.Parse(WithChecksum(body))));
    }

    [Fact]
    public void TryParse_OfNull_IsFalse() => Assert.False(TmuxLayout.TryParse(null, out _));

    [Fact]
    public void Parse_OfDamagedLayouts_OnlyEverFailsAsAFormatError()
    {
        var random = new Random(77);
        byte[][] valid =
        [
            "213x55,0,0[213x27,0,0,0,213x27,0,28{106x27,0,28,1,106x27,107,28,2}]"u8.ToArray(),
            "159x48,0,0{79x48,0,0,79x48,80,0}"u8.ToArray(),
            "80x24,0,0,5"u8.ToArray(),
        ];

        for (int round = 0; round < 20_000; round++)
        {
            byte[] body = round % 5 == 0 ? Fuzz.Garbage(random, 60, "0123456789x,{}[]"u8) : Fuzz.Mutate(random, valid[round % valid.Length]);
            string text = WithChecksum(Encoding.Latin1.GetString(body));

            try
            {
                TmuxLayoutNode root = TmuxLayout.Parse(text);
                Assert.True(root.Panes().Count() <= text.Length, $"round {round}");
            }
            catch (FormatException)
            {
                // Refused, as it should be.
            }
        }
    }
}

/// <summary>
/// tmux control mode ("tmux -CC"): the session as it reads what tmux says, line by line, and what it sends back. tmux
/// here is played by the test, which also plays a broken or hostile one.
/// </summary>
public sealed class TmuxSessionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static string Layout(string body) => $"{TmuxLayout.Checksum(body):x4},{body}";

    /// <summary>A session and the tmux it talks to.</summary>
    private sealed class Tmux
    {
        private readonly List<string> _commands = [];
        private int _number = 100;

        public Tmux() => Session = new TmuxSession((data, _) =>
        {
            lock (_commands) _commands.Add(Encoding.UTF8.GetString(data.Span).TrimEnd('\n'));
            return ValueTask.CompletedTask;
        });

        public TmuxSession Session { get; }

        public int Feed(string text) => Session.Feed(Encoding.UTF8.GetBytes(text));

        /// <summary>Answers the oldest command still waiting, as tmux would.</summary>
        public void Reply(params string[] lines)
        {
            int number = ++_number;
            Feed($"%begin 1700000000 {number} 1\n" + string.Concat(lines.Select(line => line + "\n")) + $"%end 1700000000 {number} 1\n");
        }

        public void Refuse(string message)
        {
            int number = ++_number;
            Feed($"%begin 1700000000 {number} 1\n{message}\n%error 1700000000 {number} 1\n");
        }

        /// <summary>The commands sent so far. They go out on their own a moment after they are asked for.</summary>
        public string[] Commands
        {
            get
            {
                lock (_commands) return [.. _commands];
            }
        }

        public async Task<string> SentAsync(Func<string, bool> which)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (Commands.LastOrDefault(which) is string command) return command;
                if (clock.Elapsed > Patience) Assert.Fail("Never sent. Sent: " + string.Join(" | ", Commands));
                await Task.Delay(5);
            }
        }

        /// <summary>tmux says yes to the attach, then lists one window with two panes.</summary>
        public void Attach()
        {
            Feed("%begin 1700000000 1 0\n%end 1700000000 1 0\n");
            Reply($"@1 1 {Layout("80x24,0,0{40x24,0,0,1,39x24,41,0,2}")} editor");
            Reply("%1 @1 1", "%2 @1 0");
        }
    }

    [Fact]
    public void Attach_ListsTheWindowsAndTheirPanes()
    {
        var tmux = new Tmux();
        TmuxWindow? added = null;
        tmux.Session.WindowAdded += window => added = window;

        tmux.Attach();

        TmuxWindow window = Assert.Single(tmux.Session.Windows);
        Assert.Same(window, added);
        Assert.Equal(("@1", "editor", true, "%1"), (window.Id, window.Name, window.Active, window.ActivePaneId));
        Assert.Equal(["%1", "%2"], window.Layout.Panes().Select(p => p.PaneId));
    }

    [Fact]
    public async Task Attach_IsAskedAboutOnlyOnceTmuxHasSaidYes()
    {
        var tmux = new Tmux();

        tmux.Feed("%begin 1700000000 1 0\n");
        await Task.Delay(50);
        Assert.Empty(tmux.Commands);

        tmux.Feed("%end 1700000000 1 0\n");
        await tmux.SentAsync(c => c.StartsWith("list-panes", StringComparison.Ordinal));
        Assert.StartsWith("list-windows", tmux.Commands[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attach_RefusedByTmux_SendsNothingAndKeepsTheReason()
    {
        var tmux = new Tmux();

        tmux.Feed("%begin 1700000000 1 0\nno sessions\n%error 1700000000 1 0\n");
        int used = tmux.Feed("\u001b\\$ ");

        await Task.Delay(50);
        Assert.Empty(tmux.Commands);
        Assert.Equal("no sessions", tmux.Session.ExitReason);
        Assert.Equal(2, used);
        Assert.True(tmux.Session.Terminated);
        Assert.True(tmux.Session.IsClosed);
    }

    [Fact]
    public void Notifications_KeepTheWindowsUpToDate()
    {
        var tmux = new Tmux();
        tmux.Attach();
        TmuxWindow window = tmux.Session.Windows[0];
        var events = new List<string>();
        tmux.Session.WindowRenamed += w => events.Add("renamed " + w.Name);
        tmux.Session.LayoutChanged += w => events.Add("layout " + w.Layout.Panes().Count());
        tmux.Session.ActiveChanged += w => events.Add("active " + w.ActivePaneId);
        tmux.Session.WindowClosed += w => events.Add("closed " + w.Id);
        tmux.Session.SessionRenamed += () => events.Add("session " + tmux.Session.SessionName);

        tmux.Feed("%window-renamed @1 my editor\n");
        tmux.Feed($"%layout-change @1 {Layout("80x24,0,0,1")} {Layout("80x24,0,0,1")} *\n");
        tmux.Feed("%window-pane-changed @1 %1\n");
        tmux.Feed("%window-pane-changed @1 %3\n");
        tmux.Feed("%session-renamed $0 work\n");
        tmux.Feed("%unlinked-window-close @1\n");

        Assert.Equal(["renamed my editor", "layout 1", "active %3", "session work", "closed @1"], events);
        Assert.Equal("my editor", window.Name);
        Assert.Empty(tmux.Session.Windows);
    }

    [Fact]
    public void Notifications_AboutWindowsThatAreNotThere_AreIgnored()
    {
        var tmux = new Tmux();
        tmux.Attach();

        tmux.Feed("%window-renamed @9 other\n%window-close @9\n%layout-change @9 garbage\n%window-pane-changed @9 %4\n%layout-change @1 not-a-layout\n");

        TmuxWindow window = Assert.Single(tmux.Session.Windows);
        Assert.Equal(2, window.Layout.Panes().Count());
    }

    [Fact]
    public async Task Pane_ShowsItsScreenThenItsOutput()
    {
        var tmux = new Tmux();
        tmux.Attach();
        Terminal.ITerminalTransport pane = tmux.Session.OpenPane("%1");
        byte[] buffer = new byte[4096];

        Task<int> first = pane.ReadAsync(buffer, CancellationToken.None).AsTask();
        await pane.ResizeAsync(40, 24, CancellationToken.None);
        await tmux.SentAsync(c => c.StartsWith("capture-pane", StringComparison.Ordinal));

        // The cursor at column 5, row 1; the screen two lines long.
        tmux.Reply("5,1,0,1,0,0,0,0,0,0,0,23,24");
        tmux.Reply("$ ls", "notes.txt");
        int count = await first.WaitAsync(Patience);
        string screen = Encoding.ASCII.GetString(buffer, 0, count);
        Assert.Contains("$ ls\r\nnotes.txt", screen, StringComparison.Ordinal);
        Assert.EndsWith("\u001b[2;6H", screen, StringComparison.Ordinal);

        // Octal escapes stand for the bytes tmux would not put on a line as they are.
        tmux.Feed("%output %1 hello\\015\\012\\033[1mworld\\\n");
        count = await pane.ReadAsync(buffer, CancellationToken.None).AsTask().WaitAsync(Patience);
        Assert.Equal("hello\r\n\u001b[1mworld\\", Encoding.ASCII.GetString(buffer, 0, count));

        tmux.Feed("%output %2 for another pane\n");
        tmux.Feed("%extended-output %1 7 : more\n");
        count = await pane.ReadAsync(buffer, CancellationToken.None).AsTask().WaitAsync(Patience);
        Assert.Equal("more", Encoding.ASCII.GetString(buffer, 0, count));
    }

    [Fact]
    public async Task Pane_TypingIsSentAsHexSoNothingIsReadAsACommand()
    {
        var tmux = new Tmux();
        tmux.Attach();
        Terminal.ITerminalTransport pane = tmux.Session.OpenPane("%1");

        await pane.WriteAsync("ls; kill-server\n'é"u8.ToArray(), CancellationToken.None);

        string sent = await tmux.SentAsync(c => c.StartsWith("send-keys", StringComparison.Ordinal));
        Assert.Equal("send-keys -H -t %1 6c 73 3b 20 6b 69 6c 6c 2d 73 65 72 76 65 72 0a 27 c3 a9", sent);
    }

    [Theory]
    [InlineData("@1; kill-server")]
    [InlineData("@1 ")]
    [InlineData("%1")]
    [InlineData("@")]
    [InlineData("@-1")]
    [InlineData("@1234567890123")]
    public void Ids_ThatAreNotPlainIds_NeverReachACommandLine(string id)
    {
        var tmux = new Tmux();
        tmux.Attach();

        // Refused on the spot, before anything is queued or sent.
        Assert.Throws<ArgumentException>(() => { _ = tmux.Session.KillWindow(id); });
        Assert.Throws<ArgumentException>(() => tmux.Session.OpenPane(id.Replace('@', '%') + "x"));
    }

    [Fact]
    public async Task RenameWindow_QuotesTheNameSoItStaysOneArgument()
    {
        var tmux = new Tmux();
        tmux.Attach();

        _ = tmux.Session.RenameWindow("@1", "it's; kill-server\n\u0007");

        Assert.Equal("rename-window -t @1 'it'\\''s; kill-server'", await tmux.SentAsync(c => c.StartsWith("rename-window", StringComparison.Ordinal)));
    }

    [Fact]
    public void SendCommand_OfMoreThanOneLine_IsRefused()
    {
        var tmux = new Tmux();

        Assert.Throws<ArgumentException>(() => { _ = tmux.Session.SendCommandAsync("list-sessions\nkill-server"); });
        Assert.Throws<ArgumentException>(() => { _ = tmux.Session.SendCommandAsync("  "); });
    }

    [Fact]
    public async Task Replies_GoToTheCommandsThatAskedInOrder()
    {
        var tmux = new Tmux();
        tmux.Attach();

        Task<TmuxReply> first = tmux.Session.SendCommandAsync("display-message -p one");
        Task<TmuxReply> second = tmux.Session.SendCommandAsync("display-message -p two");

        // Something tmux wraps in a block on its own, not for a command of ours.
        tmux.Feed("%begin 1700000000 900 0\nnot ours\n%end 1700000000 900 0\n");
        tmux.Reply("one");
        tmux.Refuse("no such thing");

        Assert.Equal(("one", true), ((await first.WaitAsync(Patience)).Text, (await first).Success));
        Assert.Equal(("no such thing", false), ((await second.WaitAsync(Patience)).Text, (await second).Success));
    }

    [Fact]
    public async Task Pause_WithAnIdThatIsNotPlain_IsNotTurnedIntoACommand()
    {
        var tmux = new Tmux();
        tmux.Attach();
        int before = tmux.Commands.Length;

        tmux.Feed("%pause %1';kill-server;'\n");
        tmux.Feed("%pause %2\n");

        string sent = await tmux.SentAsync(c => c.StartsWith("refresh-client -A", StringComparison.Ordinal));
        Assert.Equal("refresh-client -A '%2:continue'", sent);
        Assert.Single(tmux.Commands.Skip(before), c => c.StartsWith("refresh-client -A", StringComparison.Ordinal));
    }

    [Fact]
    public async Task End_OfControlMode_ClosesEverythingAndHandsBackTheRest()
    {
        var tmux = new Tmux();
        tmux.Attach();
        Terminal.ITerminalTransport pane = tmux.Session.OpenPane("%1");
        Task<TmuxReply> waiting = tmux.Session.SendCommandAsync("list-sessions");
        bool closed = false;
        tmux.Session.Closed += () => closed = true;

        byte[] tail = "%exit server exited\n\u001b\\$ prompt"u8.ToArray();
        int used = tmux.Session.Feed(tail);

        Assert.Equal(tail.Length - "$ prompt".Length, used);
        Assert.True(closed);
        Assert.Equal("server exited", tmux.Session.ExitReason);
        Assert.False((await waiting.WaitAsync(Patience)).Success);
        Assert.Equal(0, await pane.ReadAsync(new byte[16], CancellationToken.None).AsTask().WaitAsync(Patience));
        Assert.False((await tmux.Session.NewWindow().WaitAsync(Patience)).Success);
    }

    [Fact]
    public async Task Escape_InsideAReply_IsContentNotTheEnd()
    {
        var tmux = new Tmux();
        tmux.Attach();
        Task<TmuxReply> capture = tmux.Session.SendCommandAsync("capture-pane -p -e");

        int used = tmux.Feed("%begin 1 300 1\n\u001b\\[31mred\n%end 1 300 1\n");

        Assert.False(tmux.Session.Terminated);
        Assert.True(used > 0);
        Assert.Contains("red", (await capture.WaitAsync(Patience)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unescape_LeavesBackslashesThatAreNotOctalAlone()
    {
        Assert.Equal("a\\b\\7\\08\\", Encoding.ASCII.GetString(TmuxSession.Unescape("a\\b\\7\\08\\"u8)));
        Assert.Equal([0xFF, 0x00, (byte)'A'], TmuxSession.Unescape("\\377\\000\\101"u8));
    }

    [Fact]
    public void Feed_OfGarbage_NeverThrowsAndStopsOnlyAtTheEndOfControlMode()
    {
        var random = new Random(404);
        byte[] alphabet = "%@$ \n\r\\012345679abcdefghijklmnoprstuvwxyz-:,{}[]x\u001b"u8.ToArray();
        string[] words = ["%begin 1 2 1\n", "%end 1 2 1\n", "%error 1 2 1\n", "%output %1 ", "%layout-change @1 ", "%window-add @2\n", "%session-changed $1 s\n", "%exit\n", "%pause %1\n", "%extended-output %1 1 : x\n", "%window-close @1\n"];

        for (int round = 0; round < 500; round++)
        {
            var tmux = new Tmux();
            if (round % 2 == 0) tmux.Attach();

            var stream = new List<byte>();
            for (int i = 0; i < 40; i++) stream.AddRange(random.Next(3) == 0 ? Encoding.ASCII.GetBytes(words[random.Next(words.Length)]) : Fuzz.Garbage(random, 30, alphabet));

            Fuzz.Within(() =>
            {
                foreach (byte[] piece in Fuzz.Pieces(random, [.. stream], 50))
                {
                    int used = tmux.Session.Feed(piece);
                    Assert.InRange(used, 0, piece.Length);
                    if (tmux.Session.Terminated) break;
                    Assert.Equal(piece.Length, used);
                }
            });
        }
    }

    [Fact]
    public void Feed_OfAnEndlessLine_DoesNotKeepItAllInMemory()
    {
        var tmux = new Tmux();
        tmux.Attach();
        byte[] chunk = new byte[64 * 1024];
        chunk.AsSpan().Fill((byte)'x');

        long before = GC.GetTotalMemory(forceFullCollection: true);
        for (int i = 0; i < 1024; i++) tmux.Session.Feed(chunk);
        long held = GC.GetTotalMemory(forceFullCollection: true) - before;

        Assert.True(held < 16L * 1024 * 1024, $"{held} bytes are held for one unfinished line.");
    }
}

/// <summary>
/// The terminal's connection while tmux may enter control mode: ordinary output passes as it is, and the moment tmux
/// starts control mode, the stream goes to the session instead.
/// </summary>
public sealed class TmuxGatewayTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static string Layout(string body) => $"{TmuxLayout.Checksum(body):x4},{body}";

    private static async Task<string> ReadAsync(TmuxGatewayTransport gateway)
    {
        byte[] buffer = new byte[64 * 1024];
        int count = await gateway.ReadAsync(buffer, CancellationToken.None).AsTask().WaitAsync(Patience);
        return Encoding.UTF8.GetString(buffer, 0, count);
    }

    // Reads until the text has been seen, however the gateway cut it up.
    private static async Task<string> ReadUntilAsync(TmuxGatewayTransport gateway, string expected)
    {
        var seen = new StringBuilder();
        while (!seen.ToString().Contains(expected, StringComparison.Ordinal))
        {
            string piece = await ReadAsync(gateway);
            if (piece.Length == 0) Assert.Fail($"Ended before \"{expected}\". Seen: {seen}");
            seen.Append(piece);
        }

        return seen.ToString();
    }

    [Fact]
    public async Task OrdinaryOutput_PassesUnchanged_EscapesIncluded()
    {
        var inner = new ScriptedTransport();
        await using var gateway = new TmuxGatewayTransport(inner);

        inner.Say("\u001b[1mbold\u001b[0m \u001bP+q544e\u001b\\ \u001bP1000x");
        inner.Say("\u001bP100");
        inner.Say("0q after");
        inner.End();

        string all = await ReadUntilAsync(gateway, "after");
        Assert.Equal("\u001b[1mbold\u001b[0m \u001bP+q544e\u001b\\ \u001bP1000x\u001bP1000q after", all);
        Assert.Null(gateway.Session);
    }

    [Fact]
    public async Task ControlMode_SplitAcrossReads_IsEnteredAndLeft()
    {
        var inner = new ScriptedTransport();
        await using var gateway = new TmuxGatewayTransport(inner);
        TmuxSession? attached = null, detached = null;
        gateway.Attached += session => attached = session;
        gateway.Detached += session => detached = session;

        // The terminal keeps reading all along, which is what drives the session.
        var shown = new StringBuilder();
        Task reading = Task.Run(async () =>
        {
            byte[] buffer = new byte[64 * 1024];
            int count;
            while ((count = await gateway.ReadAsync(buffer, CancellationToken.None)) > 0)
            {
                lock (shown) shown.Append(Encoding.UTF8.GetString(buffer, 0, count));
            }
        });
        async Task<string> ShownAsync(string expected)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                lock (shown)
                {
                    if (shown.ToString().Contains(expected, StringComparison.Ordinal)) return shown.ToString();
                }

                if (clock.Elapsed > Patience) Assert.Fail($"Never shown: \"{expected}\". Shown: {shown}");
                await Task.Delay(5);
            }
        }

        inner.Say("$ tmux -CC attach\r\n\u001bP10");
        inner.Say("00p%begin 1700000000 1 0\n%end 1700000000 1 0\n");

        // tmux is asked for its windows and panes, and none of control mode reaches the screen.
        Assert.StartsWith("list-windows", Encoding.UTF8.GetString(await inner.HeardAsync(Patience)), StringComparison.Ordinal);
        Assert.StartsWith("list-panes", Encoding.UTF8.GetString(await inner.HeardAsync(Patience)), StringComparison.Ordinal);
        Assert.Equal("$ tmux -CC attach\r\n", await ShownAsync("attach\r\n"));

        inner.Say($"%begin 1 2 1\n@1 1 {Layout("80x24,0,0,1")} shell\n%end 1 2 1\n%begin 1 3 1\n%1 @1 1\n%end 1 3 1\n");
        await ShownAsync("tmux is attached");
        Assert.NotNull(attached);
        Assert.Same(attached, gateway.Session);

        // Typing into this terminal while tmux owns the stream would be read as tmux commands.
        await gateway.WriteAsync("rm -rf ~\r"u8.ToArray(), CancellationToken.None);
        await Task.Delay(50);
        Assert.Empty(inner.HeardSoFar());

        inner.Say("%exit\n\u001b\\$ back to the shell");
        string after = await ShownAsync("back to the shell");
        Assert.Same(attached, detached);
        Assert.Null(gateway.Session);
        Assert.DoesNotContain("%exit", after, StringComparison.Ordinal);
        Assert.Contains("tmux detached.", after, StringComparison.Ordinal);
        Assert.EndsWith("$ back to the shell", after, StringComparison.Ordinal);

        await gateway.WriteAsync("ls\r"u8.ToArray(), CancellationToken.None);
        Assert.Equal("ls\r"u8.ToArray(), await inner.HeardAsync(Patience));
    }

    [Fact]
    public async Task ControlMode_ThatTmuxRefuses_GivesTheTerminalItsShellBackWithTheReason()
    {
        var inner = new ScriptedTransport();
        await using var gateway = new TmuxGatewayTransport(inner);

        inner.Say("\u001bP1000p%begin 1 1 0\nsessions should be nested with care\n%error 1 1 0\n\u001b\\$ ");

        string shown = await ReadUntilAsync(gateway, "$ ");
        Assert.Contains("sessions should be nested with care", shown, StringComparison.Ordinal);
        Assert.Null(gateway.Session);
    }

    [Fact]
    public async Task Connection_ThatEndsDuringControlMode_EndsTheTerminalToo()
    {
        var inner = new ScriptedTransport();
        await using var gateway = new TmuxGatewayTransport(inner);

        inner.Say("\u001bP1000p%begin 1 1 0\n%end 1 1 0\n");
        inner.End();

        byte[] buffer = new byte[1024];
        while (await gateway.ReadAsync(buffer, CancellationToken.None).AsTask().WaitAsync(Patience) > 0)
        {
        }
    }

    [Fact]
    public async Task Garbage_InAndAroundControlMode_NeverThrowsOrHangs()
    {
        var random = new Random(13);
        byte[] alphabet = "\u001bP1000p%\\\n begin end exit output @%1"u8.ToArray();
        for (int round = 0; round < 100; round++)
        {
            var inner = new ScriptedTransport();
            await using var gateway = new TmuxGatewayTransport(inner);
            foreach (byte[] piece in Fuzz.Pieces(random, Fuzz.Garbage(random, 2000, alphabet), 40)) inner.Say(piece);
            inner.End();

            byte[] buffer = new byte[4096];
            using var limit = new CancellationTokenSource(Patience);
            while (await gateway.ReadAsync(buffer, limit.Token) > 0)
            {
            }
        }
    }
}

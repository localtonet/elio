using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Elio.Core;

namespace Elio.Tests;

/// <summary>
/// The plugin host against real processes: the test assembly started as a program plays the plugin (see
/// <see cref="FakePlugin"/>), so what is tested is the conversation over real pipes, a plugin that misbehaves
/// included. Nothing here starts anything but that.
/// </summary>
public sealed class PluginHostTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _folder = Path.Combine(TestData.Root, "plugins-" + Guid.NewGuid().ToString("N"));
    private readonly Actions _actions = new();

    public PluginHostTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => TestData.Delete(_folder);

    private sealed class Actions : IPluginActions
    {
        public ConcurrentQueue<string> Done { get; } = new();

        public TaskCompletionSource<string> Notified { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan PromptTime { get; set; }

        public Task SendTextAsync(string plugin, string text) => Record($"send {plugin}: {text}");

        public Task RunCommandAsync(string plugin, string command) => Record($"run {plugin}: {command}");

        public Task NotifyAsync(string plugin, string message)
        {
            Notified.TrySetResult(message);
            return Record($"notify {plugin}: {message}");
        }

        public Task WriteClipboardAsync(string plugin, string text) => Record($"clipboard {plugin}: {text}");

        public Task OpenUrlAsync(string plugin, Uri url) => Record($"open {plugin}: {url}");

        public async Task<string?> PromptAsync(string plugin, string title, string? placeholder)
        {
            await Record($"prompt {plugin}: {title} ({placeholder})");
            await Task.Delay(PromptTime);
            return "Ada";
        }

        public Task<IReadOnlyList<PluginServerListing>> ListServersAsync(string plugin) =>
            Task.FromResult<IReadOnlyList<PluginServerListing>>([new PluginServerListing("web-1", "ssh", "10.0.0.5", "deploy", "Production", ["eu"])]);

        public Task<string?> LastOutputAsync(string plugin) => Task.FromResult<string?>("done");

        public async Task<bool> ConnectAsync(string plugin, string server)
        {
            await Record($"connect {plugin}: {server}");
            return server == "web-1";
        }

        public Task<int?> ChooseAsync(string plugin, string title, IReadOnlyList<string> choices) => Task.FromResult<int?>(choices.Count - 1);

        private Task Record(string what)
        {
            Done.Enqueue(what);
            return Task.CompletedTask;
        }
    }

    // The program that runs this assembly: the dotnet host the tests themselves were started with.
    private static string DotnetHost()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)) return host;

        string root = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        return Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    private string AddPlugin(string folderName, string scenario, string? name = null)
    {
        string folder = Path.Combine(_folder, folderName);
        Directory.CreateDirectory(folder);
        string manifest = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = name ?? folderName,
            ["description"] = "A plugin played by the test assembly.",
            ["version"] = "1.0",
            ["command"] = DotnetHost(),
            ["args"] = new[] { typeof(FakePlugin).Assembly.Location, "fake-plugin", scenario },
        });
        File.WriteAllText(Path.Combine(folder, "plugin.json"), manifest);
        return folder;
    }

    private void AddManifest(string folderName, string json)
    {
        string folder = Path.Combine(_folder, folderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), json);
    }

    private PluginHost Host(TimeSpan? commandTimeout = null) =>
        new(_actions, _folder) { IsApproved = _ => true, CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(20) };

    // ---- Reading manifests ------------------------------------------------------------------

    [Fact]
    public void Discover_ManifestWithCommentsAndATrailingComma_IsAccepted()
    {
        AddManifest("hand-written", """
            {
              // Written by hand.
              "name": "Hand written",
              "command": "python",
              "args": ["plugin.py",],
            }
            """);

        PluginInfo info = Assert.Single(new PluginHost(_actions, _folder).Discover());

        Assert.Null(info.Error);
        Assert.Equal("Hand written", info.Name);
        Assert.Equal("python plugin.py", info.CommandLine);
        Assert.Equal(64, info.Approval.Length);
    }

    [Theory]
    [InlineData("""{"name":"x"}""")]
    [InlineData("""{"command":"   "}""")]
    [InlineData("""{"command":"python","args":"plugin.py"}""")]
    [InlineData("""{"command":"python","args":[1,2]}""")]
    [InlineData("""{"command":"python","env":["A=1"]}""")]
    [InlineData("""{"command":"python","env":{"A":1}}""")]
    [InlineData("""["command","python"]""")]
    [InlineData("""{"command":""")]
    [InlineData("")]
    public void Discover_BrokenManifest_IsListedWithAnErrorAndCannotBeApproved(string json)
    {
        AddManifest("broken", json);

        PluginInfo info = Assert.Single(new PluginHost(_actions, _folder) { IsApproved = _ => true }.Discover());

        Assert.NotNull(info.Error);
        Assert.Equal("broken", info.Name);
        Assert.Equal(string.Empty, info.Approval);
    }

    [Fact]
    public void Discover_FolderWithoutManifest_IsNotAPlugin()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "empty"));
        File.WriteAllText(Path.Combine(_folder, "stray.json"), "{}");

        Assert.Empty(new PluginHost(_actions, _folder).Discover());
    }

    [Fact]
    public void Discover_TwoPluginsWithOneName_TheSecondIsAnErrorAndCannotBeApproved()
    {
        AddManifest("a", """{"name":"Same","command":"python"}""");
        AddManifest("b", """{"name":"same","command":"python"}""");

        IReadOnlyList<PluginInfo> found = new PluginHost(_actions, _folder).Discover();

        Assert.Equal(2, found.Count);
        Assert.Null(found[0].Error);
        Assert.NotNull(found[1].Error);
        Assert.Equal(string.Empty, found[1].Approval);
    }

    [Fact]
    public void Approval_ChangesWithTheManifestAndWithTheFolder()
    {
        const string Manifest = """{"command":"python","args":["plugin.py"]}""";
        AddManifest("one", Manifest);
        string first = Assert.Single(new PluginHost(_actions, _folder).Discover()).Approval;

        AddManifest("one", Manifest.Replace("plugin.py", "other.py", StringComparison.Ordinal));
        string edited = Assert.Single(new PluginHost(_actions, _folder).Discover()).Approval;

        Directory.Delete(Path.Combine(_folder, "one"), recursive: true);
        AddManifest("two", Manifest);
        string moved = Assert.Single(new PluginHost(_actions, _folder).Discover()).Approval;

        Assert.Equal(3, new[] { first, edited, moved }.Distinct().Count());
    }

    [Fact]
    public void Discover_CommandWithANulCharacter_IsListedAsBroken()
    {
        AddManifest("nul-in-command", """{"command":"py\u0000thon"}""");
        AddPlugin("fine", "good");

        IReadOnlyList<PluginInfo> found = new PluginHost(_actions, _folder).Discover();

        Assert.Equal(2, found.Count);
        Assert.NotNull(found.Single(p => p.Name == "nul-in-command").Error);
    }

    [Fact]
    public void Discover_ManifestWithInvalidUtf8InAString_IsListedAsBroken()
    {
        string folder = Path.Combine(_folder, "latin1");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "plugin.json"), [.. "{\"command\":\"python\",\"args\":[\"caf"u8, 0xE9, .. "\"]}"u8]);
        AddPlugin("fine", "good");

        IReadOnlyList<PluginInfo> found = new PluginHost(_actions, _folder).Discover();

        Assert.Equal(2, found.Count);
        Assert.NotNull(found.Single(p => p.Name == "latin1").Error);
    }

    [Fact]
    public void Discover_GarbledManifests_NeverThrow()
    {
        var random = new Random(1234);
        byte[] valid = Encoding.UTF8.GetBytes("""{"name":"fuzzed","description":"d","version":"1","command":"python","args":["a","b"],"env":{"A":"1"}}""");
        for (int round = 0; round < 300; round++)
        {
            byte[] garbled = round % 3 == 0 ? Fuzz.Garbage(random, 200) : Fuzz.Mutate(random, valid);

            // Invalid UTF-8 and a NUL in the command are known bugs, reported on their own above.
            byte[] manifest = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(garbled));
            if (Encoding.UTF8.GetString(manifest).Contains("\\u0000", StringComparison.Ordinal)) continue;

            string folder = Path.Combine(_folder, "fuzzed");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "plugin.json"), manifest);

            IReadOnlyList<PluginInfo> found = new PluginHost(_actions, _folder) { IsApproved = _ => false }.Discover();
            PluginInfo info = Assert.Single(found);
            Assert.True(info.Error is not null || info.Approval.Length == 64, $"round {round}");
        }
    }

    // ---- Talking to a running plugin -------------------------------------------------------

    [Fact]
    public async Task LoadAsync_PluginNotApproved_IsNeverStarted()
    {
        AddPlugin("unapproved", "good");
        using var host = new PluginHost(_actions, _folder) { IsApproved = _ => false };

        PluginInfo info = Assert.Single(await host.LoadAsync().WaitAsync(Patience));

        Assert.Null(info.Error);
        Assert.Empty(host.Commands);
        Assert.False(host.IsRunning("unapproved"));
        await Assert.ThrowsAsync<PluginException>(() => host.RunAsync(new PluginCommand("unapproved", "hello", "Say hello"), new PluginContext()));
    }

    [Fact]
    public async Task LoadAsync_ApprovedPlugin_OffersOnlyItsWellFormedCommands()
    {
        AddPlugin("good", "good");
        using PluginHost host = Host();

        PluginInfo info = Assert.Single(await host.LoadAsync().WaitAsync(Patience));

        Assert.Null(info.Error);
        Assert.True(host.IsRunning("good"));
        Assert.Equal(
            [new PluginCommand("good", "hello", "Say hello", "Types a greeting"), new PluginCommand("good", "prompt", "Ask"), new PluginCommand("good", "look", "Look around")],
            host.Commands);
    }

    [Fact]
    public async Task RunAsync_APluginCanListServers_ReadOutput_Choose_AndConnect()
    {
        AddPlugin("good", "good");
        using PluginHost host = Host();
        await host.LoadAsync().WaitAsync(Patience);

        await host.RunAsync(new PluginCommand("good", "look", "Look around"), new PluginContext()).WaitAsync(Patience);
        string said = await _actions.Notified.Task.WaitAsync(Patience);

        // The first server, no password in its listing, the output, the last of three choices, an empty list refused
        // (-32602) and an unknown server reported (-32000).
        Assert.Equal("web-1 False done 2 -32602 -32000", said);
        Assert.Contains("connect good: web-1", _actions.Done);
    }

    [Fact]
    public async Task RunAsync_RequestsFromThePlugin_AreServedAndUnsafeOnesRefused()
    {
        AddPlugin("good", "good");
        using PluginHost host = Host();
        await host.LoadAsync().WaitAsync(Patience);

        await host.RunAsync(new PluginCommand("good", "hello", "Say hello"), new PluginContext(Directory: "/srv/app")).WaitAsync(Patience);
        string codes = await _actions.Notified.Task.WaitAsync(Patience);

        // A file address is refused (-32602), an unknown method is not found (-32601), a number where text belongs is refused (-32602).
        Assert.Equal("codes -32602 -32601 -32602", codes);
        Assert.Contains("send good: hello from /srv/app", _actions.Done);
        Assert.Equal(["open good: https://example.invalid/page"], _actions.Done.Where(d => d.StartsWith("open", StringComparison.Ordinal)));
        Assert.DoesNotContain(_actions.Done, d => d.StartsWith("run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostMethods_CalledWhileNoCommandRuns_AreRefused()
    {
        AddPlugin("early", "early");
        using PluginHost host = Host();

        await host.LoadAsync().WaitAsync(Patience);

        // The plugin asked to type into the terminal while starting up, and put the answer's code in its command's title.
        Assert.Equal("-32001", Assert.Single(host.Commands).Title);
        Assert.Empty(_actions.Done);
    }

    [Fact]
    public async Task LoadAsync_PluginThatWritesNoiseAndWrongAnswers_StillWorks()
    {
        AddPlugin("noisy", "noise");
        using PluginHost host = Host();

        PluginInfo info = Assert.Single(await host.LoadAsync().WaitAsync(Patience));

        Assert.Null(info.Error);
        Assert.Equal("Real", Assert.Single(host.Commands).Title);
        IReadOnlyList<string> log = host.Log("noisy");
        Assert.Contains("stdout: this is not JSON at all", log);
        Assert.Contains("stdout: [1,2,3]", log);

        // The line of two megabytes was dropped rather than kept.
        Assert.All(log, line => Assert.True(line.Length <= 2000 + "stdout: ".Length));
        await WaitForAsync(() => host.Log("noisy").Contains("a line on the error output"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task LoadAsync_PluginThatWritesGarbageFirst_StillWorks(int seed)
    {
        AddPlugin("garbled", "garbage-" + seed);
        using PluginHost host = Host();

        PluginInfo info = Assert.Single(await host.LoadAsync().WaitAsync(Patience));

        Assert.Null(info.Error);
        Assert.Equal("Survived", Assert.Single(host.Commands).Title);
    }

    [Fact]
    public async Task LoadAsync_PluginThatNeverAnswers_IsReportedAndStopped()
    {
        AddPlugin("silent", "silent");
        using PluginHost host = Host();

        PluginInfo info = Assert.Single(await host.LoadAsync().WaitAsync(Patience));

        Assert.NotNull(info.Error);
        Assert.Empty(host.Commands);
        Assert.False(host.IsRunning("silent"));
    }

    [Fact]
    public async Task LoadAsync_PluginThatExits_IsReported()
    {
        AddPlugin("quitter", "exit");
        using PluginHost host = Host();

        PluginInfo info = Assert.Single(await host.LoadAsync().WaitAsync(Patience));

        Assert.NotNull(info.Error);
        Assert.False(host.IsRunning("quitter"));
    }

    [Fact]
    public async Task LoadAsync_PluginThatAnswersWithAnError_IsReportedWithItsMessage()
    {
        AddPlugin("failing", "error");
        using PluginHost host = Host();

        PluginInfo info = Assert.Single(await host.LoadAsync().WaitAsync(Patience));

        Assert.Contains("broken on purpose", info.Error);
    }

    [Fact]
    public async Task RunAsync_CommandThePluginDoesNotKnow_ReportsThePluginsError()
    {
        AddPlugin("good", "good");
        using PluginHost host = Host();
        await host.LoadAsync().WaitAsync(Patience);

        PluginException error = await Assert.ThrowsAsync<PluginException>(() => host.RunAsync(new PluginCommand("good", "nope", "Nope"), new PluginContext()).WaitAsync(Patience));

        Assert.Contains("no such command here", error.Message);
        Assert.True(host.IsRunning("good"));
    }

    [Fact]
    public async Task RunAsync_CommandThatNeverAnswers_TimesOutAndTheNextUseStartsAFreshPlugin()
    {
        AddPlugin("slow", "slow-command");
        using PluginHost host = Host(TimeSpan.FromSeconds(1));
        await host.LoadAsync().WaitAsync(Patience);

        await Assert.ThrowsAsync<PluginException>(() => host.RunAsync(new PluginCommand("slow", "hello", "Say hello"), new PluginContext()).WaitAsync(Patience));
        Assert.False(host.IsRunning("slow"));

        await host.LoadAsync().WaitAsync(Patience);
        Assert.True(host.IsRunning("slow"));
    }

    [Fact]
    public async Task RunAsync_TimeTheUserSpendsOnAPrompt_DoesNotCountAgainstThePlugin()
    {
        AddPlugin("asking", "good");
        _actions.PromptTime = TimeSpan.FromSeconds(2.5);
        using PluginHost host = Host(TimeSpan.FromSeconds(1));
        await host.LoadAsync().WaitAsync(Patience);

        await host.RunAsync(new PluginCommand("asking", "prompt", "Ask"), new PluginContext()).WaitAsync(Patience);

        Assert.Equal("typed Ada", await _actions.Notified.Task.WaitAsync(Patience));
        Assert.Contains("prompt asking: Name? (someone)", _actions.Done);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > Patience) Assert.Fail("The condition never came true.");
            await Task.Delay(20);
        }
    }
}

/// <summary>Reading lines from a plugin, which may write anything, of any length, in pieces of any size.</summary>
public sealed class PluginLinesTests
{
    /// <summary>Hands over its bytes in pieces of the given sizes, the way a pipe does.</summary>
    private sealed class PiecesStream(IEnumerable<byte[]> pieces) : Stream
    {
        private readonly IEnumerator<byte[]> _pieces = pieces.GetEnumerator();
        private byte[] _current = [];
        private int _offset;

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (_offset == _current.Length)
            {
                if (!_pieces.MoveNext()) return 0;
                (_current, _offset) = (_pieces.Current, 0);
            }

            int taken = Math.Min(count, _current.Length - _offset);
            Array.Copy(_current, _offset, buffer, offset, taken);
            _offset += taken;
            return taken;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Endless bytes without a line break, made up as they are read, so the test itself holds none of them.</summary>
    private sealed class EndlessLine(long length, byte[] after) : Stream
    {
        private long _left = length;
        private int _afterOffset;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left > 0)
            {
                int filled = (int)Math.Min(count, _left);
                buffer.AsSpan(offset, filled).Fill((byte)'x');
                _left -= filled;
                return filled;
            }

            int taken = Math.Min(count, after.Length - _afterOffset);
            Array.Copy(after, _afterOffset, buffer, offset, taken);
            _afterOffset += taken;
            return taken;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<List<string>> ReadAllAsync(Stream stream)
    {
        var lines = new List<string>();
        await foreach (string line in PluginJson.LinesAsync(stream).WithCancellation(CancellationToken.None)) lines.Add(line);
        return lines;
    }

    // What the lines are, worked out from the whole input at once.
    private static List<string> Expected(byte[] data) =>
        SplitLines(data)
            .Where(line => line.Length <= 1024 * 1024)
            .Select(line => Encoding.UTF8.GetString(line).Trim())
            .Where(text => text.Length > 0)
            .ToList();

    private static IEnumerable<byte[]> SplitLines(byte[] data)
    {
        int start = 0;
        for (int i = 0; i <= data.Length; i++)
        {
            if (i < data.Length && data[i] != (byte)'\n') continue;
            yield return data[start..i];
            start = i + 1;
        }
    }

    [Fact]
    public async Task LinesAsync_SplitsOnLineBreaksAndSkipsEmptyLines()
    {
        byte[] data = "{\"a\":1}\r\n\n   \n{\"b\":2}\nlast without a break"u8.ToArray();

        List<string> lines = await ReadAllAsync(new PiecesStream([data]));

        Assert.Equal(["{\"a\":1}", "{\"b\":2}", "last without a break"], lines);
    }

    [Fact]
    public async Task LinesAsync_LineOverOneMegabyte_IsDroppedAndTheNextOneRead()
    {
        byte[] after = "\n{\"ok\":true}\n"u8.ToArray();

        List<string> lines = await ReadAllAsync(new EndlessLine((1024 * 1024) + 1, after));

        Assert.Equal(["{\"ok\":true}"], lines);
    }

    [Fact]
    public async Task LinesAsync_EndlessLine_DoesNotKeepItInMemory()
    {
        // 64 MB without a break. Kept, it would be seen in what was allocated.
        long before = GC.GetTotalAllocatedBytes(precise: true);
        List<string> lines = await ReadAllAsync(new EndlessLine(64L * 1024 * 1024, "\nend\n"u8.ToArray()));
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(["end"], lines);
        Assert.True(allocated < 16L * 1024 * 1024, $"{allocated} bytes were allocated to read past one long line.");
    }

    [Fact]
    public async Task LinesAsync_HowTheInputIsCutUp_NeverChangesTheLines()
    {
        var random = new Random(42);
        for (int round = 0; round < 300; round++)
        {
            byte[] data = round % 10 == 0
                ? [.. Fuzz.Garbage(random, 50), .. new byte[(1024 * 1024) + random.Next(3)], .. "\nafter\n"u8]
                : Fuzz.Garbage(random, 4000, "{}\"\n\r \téab"u8);
            List<string> expected = Expected(data);

            List<string> lines = await ReadAllAsync(new PiecesStream(Fuzz.Pieces(random, data, 1 + random.Next(20000)))).WaitAsync(Fuzz.Patience);

            Assert.True(expected.SequenceEqual(lines), $"round {round}");
        }
    }

    [Fact]
    public void PluginLog_KeepsOnlyTheLastLinesAndCutsLongOnes()
    {
        var log = new PluginLog();
        for (int i = 0; i < 500; i++) log.Add(i == 499 ? new string('y', 10_000) : $"line {i}");

        IReadOnlyList<string> lines = log.Lines();

        Assert.Equal(200, lines.Count);
        Assert.Equal("line 300", lines[0]);
        Assert.Equal(2000, lines[^1].Length);
    }
}

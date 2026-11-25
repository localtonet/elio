using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Elio.Core;
using Elio.Desktop;

namespace Elio.Tests;

/// <summary>
/// `elio --mcp`: what an AI tool on the other end of its input and output can get it to do. Most of what matters
/// is what it refuses: secrets, commands while that is switched off, servers not allowed one by one.
/// </summary>
public sealed class McpServerTests : TempDataDirectoryTest, IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly TestVaultKeys _keys = new();

    public new void Dispose()
    {
        _keys.Dispose();
        base.Dispose();
    }

    private static async Task<List<JsonElement>> ConverseAsync(params string[] lines)
    {
        byte[] input = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        using var output = new MemoryStream();

        int code = await McpServer.RunAsync(new MemoryStream(input), output).WaitAsync(Patience);

        Assert.Equal(0, code);
        return Encoding.UTF8.GetString(output.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                using JsonDocument document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToList();
    }

    private static async Task<JsonElement> AskAsync(string line) => Assert.Single(await ConverseAsync(line));

    private static string Call(string tool, object? arguments = null, int id = 1) =>
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name = tool, arguments } });

    // What a tool said, and whether it says it failed.
    private static async Task<(string Text, bool Failed)> CallToolAsync(string tool, object? arguments = null)
    {
        JsonElement result = (await AskAsync(Call(tool, arguments))).GetProperty("result");
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, result.GetProperty("isError").GetBoolean());
    }

    private static int ErrorCode(JsonElement reply) => reply.GetProperty("error").GetProperty("code").GetInt32();

    private static void AllowCommands(bool allowed = true)
    {
        AppSettings settings = Stores.Settings().Load();
        settings.McpRunCommands = allowed;
        Stores.Settings().Save(settings);
    }

    private static HostEntry SaveHost(string name, Action<HostEntry>? change = null, HostLibrary? library = null)
    {
        library ??= Stores.Hosts().Load();
        var host = new HostEntry { Name = name, Kind = HostKind.Ssh, Address = name.ToLowerInvariant() + ".example.invalid", User = "deploy", Port = 2222 };
        change?.Invoke(host);
        library.Hosts.Add(host);
        Stores.Hosts().Save(library);
        return host;
    }

    // ---- The protocol --------------------------------------------------------------------

    [Fact]
    public async Task Initialize_AnswersWithTheVersionAskedForAndItsName()
    {
        JsonElement reply = await AskAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}""");

        JsonElement result = reply.GetProperty("result");
        Assert.Equal("2.0", reply.GetProperty("jsonrpc").GetString());
        Assert.Equal(1, reply.GetProperty("id").GetInt32());
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("elio", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, result.GetProperty("capabilities").GetProperty("tools").ValueKind);
    }

    [Fact]
    public async Task Initialize_WithoutAVersion_OffersItsOwn()
    {
        JsonElement reply = await AskAsync("""{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":7}}""");

        Assert.Equal("a", reply.GetProperty("id").GetString());
        Assert.Equal("2025-06-18", reply.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task Ping_IsAnsweredWithAnEmptyResult()
    {
        JsonElement reply = await AskAsync("""{"jsonrpc":"2.0","id":9,"method":"ping"}""");

        Assert.Equal(JsonValueKind.Object, reply.GetProperty("result").ValueKind);
        Assert.Empty(reply.GetProperty("result").EnumerateObject());
    }

    [Fact]
    public async Task Notifications_AndBlankLines_GetNoAnswer()
    {
        List<JsonElement> replies = await ConverseAsync(
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            "",
            "   ",
            """{"jsonrpc":"2.0","method":"tools/call","params":{"name":"run_command","arguments":{"server":"x","command":"reboot"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""");

        Assert.Equal(2, Assert.Single(replies).GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"id\":1,")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("null")]
    public async Task MessageThatIsNotAJsonObject_IsAParseErrorWithoutAnId(string line)
    {
        JsonElement reply = await AskAsync(line);

        Assert.Equal(-32700, ErrorCode(reply));
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task DeeplyNestedMessage_IsAParseError()
    {
        string line = new string('[', 10_000) + new string(']', 10_000);

        Assert.Equal(-32700, ErrorCode(await AskAsync(line)));
    }

    [Fact]
    public async Task UnknownMethod_IsNotFound()
    {
        JsonElement reply = await AskAsync("""{"jsonrpc":"2.0","id":{"odd":"id"},"method":"resources/list"}""");

        Assert.Equal(-32601, ErrorCode(reply));
        Assert.Equal("id", reply.GetProperty("id").GetProperty("odd").GetString());
    }

    [Fact]
    public async Task ToolsList_OffersListingReadingAndRunning_AndSaysWhichOnlyRead()
    {
        JsonElement reply = await AskAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/list"}""");

        Dictionary<string, bool> readOnly = reply.GetProperty("result").GetProperty("tools").EnumerateArray()
            .ToDictionary(tool => tool.GetProperty("name").GetString()!, tool => tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal(["list_directory", "list_servers", "read_file", "run_command"], readOnly.Keys.Order(StringComparer.Ordinal));
        Assert.False(readOnly["run_command"]);
        Assert.True(readOnly["list_servers"] && readOnly["read_file"] && readOnly["list_directory"]);
    }

    [Theory]
    [InlineData("/etc/os-release", "head -c 65536 -- /etc/os-release")]
    [InlineData("my file; rm -rf ~", "head -c 65536 -- 'my file; rm -rf ~'")]
    [InlineData("-rf", "head -c 65536 -- -rf")]
    public void ReadFile_QuotesThePath(string path, string command) =>
        Assert.Equal(command, McpServer.ReadFileCommand(path));

    [Theory]
    [InlineData("", "ls -la")]
    [InlineData("/var/log", "ls -la -- /var/log")]
    [InlineData("$(id)", "ls -la -- '$(id)'")]
    public void ListDirectory_QuotesThePath(string path, string command) =>
        Assert.Equal(command, McpServer.ListDirectoryCommand(path));

    [Fact]
    public async Task ReadFile_WithoutAPath_IsRefused()
    {
        JsonElement reply = await AskAsync("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"read_file","arguments":{"server":"web"}}}""");

        Assert.True(reply.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"delete_everything"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"arguments":{}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":"list_servers"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":4,"method":"tools/call"}""")]
    public async Task ToolsCall_ForNoSuchTool_IsInvalidParams(string line) =>
        Assert.Equal(-32602, ErrorCode(await AskAsync(line)));

    [Fact]
    public async Task ToolsCall_WithANameThatIsNotText_IsInvalidParamsAndTheServerCarriesOn()
    {
        List<JsonElement> replies = await ConverseAsync(
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":5}}""",
            """{"jsonrpc":"2.0","id":5,"method":"ping"}""");

        Assert.Equal(2, replies.Count);
        Assert.Equal(-32602, ErrorCode(replies[0]));
    }

    // ---- Listing servers -----------------------------------------------------------------

    [Fact]
    public async Task ListServers_ShowsWhatIsNeededToPickOneAndNoSecret()
    {
        var library = new HostLibrary();
        var group = new HostGroup { Name = "Production" };
        library.Groups.Add(group);
        HostEntry web = SaveHost("Web", h => (h.GroupId, h.Tags) = (group.Id, ["linux", "nginx"]), library);
        Stores.Localtonet().Save(new LocaltonetCache { Devices = [new HostEntry { Name = "Laptop", Kind = HostKind.Ssh, Address = "my-laptop", Localtonet = true }] });
        new Vault().Set(web.Id, "hunter2-secret");

        var (text, failed) = await CallToolAsync("list_servers");

        Assert.False(failed);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        using JsonDocument listed = JsonDocument.Parse(text);
        JsonElement[] servers = [.. listed.RootElement.EnumerateArray()];
        Assert.Equal(["Laptop", "Web"], servers.Select(s => s.GetProperty("name").GetString()));

        JsonElement laptop = servers[0];
        Assert.Equal("localtonet", laptop.GetProperty("via").GetString());
        Assert.False(laptop.TryGetProperty("port", out _));

        JsonElement server = servers[1];
        Assert.Equal(("web.example.invalid", 2222, "deploy", "Production"), (server.GetProperty("address").GetString(), server.GetProperty("port").GetInt32(), server.GetProperty("user").GetString(), server.GetProperty("group").GetString()));
        Assert.Equal(["linux", "nginx"], server.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
    }

    // ---- Running commands: everything that stops it ------------------------------------------

    [Fact]
    public async Task RunCommand_WhileTurnedOffInSettings_IsRefusedAndNothingIsRecorded()
    {
        SaveHost("Web", h => h.AllowMcpCommands = true);

        var (text, failed) = await CallToolAsync("run_command", new { server = "Web", command = "uptime" });

        Assert.True(failed);
        Assert.Contains("turned off", text, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(DataDirectory, "mcp-commands.log")));
    }

    [Fact]
    public async Task RunCommand_OnAServerNotAllowedOneByOne_IsRefused()
    {
        AllowCommands();
        SaveHost("Web");

        var (text, failed) = await CallToolAsync("run_command", new { server = "web", command = "uptime" });

        Assert.True(failed);
        Assert.Contains("not allowed to run commands on Web", text, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(DataDirectory, "mcp-commands.log")));
    }

    [Theory]
    [InlineData("Nowhere", "uptime", "There is no server named")]
    [InlineData("Twin", "uptime", "More than one server is named")]
    [InlineData("Web", "", "Both a server and a command are needed")]
    [InlineData("", "uptime", "Both a server and a command are needed")]
    public async Task RunCommand_WithoutExactlyOneServerOrACommand_IsRefused(string server, string command, string expected)
    {
        AllowCommands();
        SaveHost("Web", h => h.AllowMcpCommands = true);
        SaveHost("Twin", h => h.AllowMcpCommands = true);
        SaveHost("twin", h => h.AllowMcpCommands = true);

        var (text, failed) = await CallToolAsync("run_command", new { server, command });

        Assert.True(failed);
        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCommand_ArgumentsThatAreNotText_AreTakenAsMissing()
    {
        AllowCommands();
        SaveHost("Web", h => h.AllowMcpCommands = true);

        var (text, failed) = await CallToolAsync("run_command", new { server = "Web", command = new[] { "rm", "-rf" } });

        Assert.True(failed);
        Assert.Contains("Both a server and a command are needed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCommand_WithoutASavedPassword_IsRefusedBeforeConnecting()
    {
        AllowCommands();
        SaveHost("Web", h => h.AllowMcpCommands = true);

        var (text, failed) = await CallToolAsync("run_command", new { server = "Web", command = "uptime" });

        Assert.True(failed);
        Assert.Contains("No password is saved for Web", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCommand_JumpHostsThatLeadInACircle_AreRefused()
    {
        AllowCommands();
        var library = new HostLibrary();
        var a = new HostEntry { Name = "A", Kind = HostKind.Ssh, Address = "a.invalid", User = "u", AllowMcpCommands = true };
        var b = new HostEntry { Name = "B", Kind = HostKind.Ssh, Address = "b.invalid", User = "u" };
        (a.JumpHosts, b.JumpHosts) = ([b.Id], [a.Id]);
        library.Hosts.AddRange([a, b]);
        Stores.Hosts().Save(library);

        var (text, failed) = await CallToolAsync("run_command", new { server = "A", command = "uptime" }).WaitAsync(Patience);

        Assert.True(failed);
        Assert.Contains("in its own chain of jump hosts", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCommand_OnALocaltonetDeviceWithoutAnAccessKey_IsRefused()
    {
        AllowCommands();
        Stores.Localtonet().Save(new LocaltonetCache { Devices = [new HostEntry { Name = "Laptop", Kind = HostKind.Ssh, Address = "my-laptop", Localtonet = true, AllowMcpCommands = true }] });

        var (text, failed) = await CallToolAsync("run_command", new { server = "Laptop", command = "uptime" });

        Assert.True(failed);
        Assert.Contains("No access key is saved for Laptop", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCommand_ThatGetsAsFarAsConnecting_IsRecordedOnOneLine()
    {
        // A port on this computer that nothing listens on: the connection is refused at once, and nothing leaves the machine.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        AllowCommands();
        HostEntry host = SaveHost("Web", h => (h.Address, h.Port, h.AllowMcpCommands) = ("127.0.0.1", port, true));
        new Vault().Set(host.Id, "password");

        var (text, failed) = await CallToolAsync("run_command", new { server = "Web\r\nforged", command = "echo one\necho two" });
        Assert.True(failed);
        Assert.Contains("no server named", text, StringComparison.Ordinal);

        (text, failed) = await CallToolAsync("run_command", new { server = "Web", command = "echo one\necho two\t\u0007" });

        Assert.True(failed);
        string[] log = File.ReadAllLines(Path.Combine(DataDirectory, "mcp-commands.log"));
        string line = Assert.Single(log);
        string[] fields = line.Split('\t');
        Assert.Equal(["Web", "failed", "echo one\\necho two\\u0009\\u0007"], fields[1..]);
    }

    // ---- Garbage -----------------------------------------------------------------------------

    [Fact]
    public async Task AnyLine_GetsAtMostOneWellFormedAnswer()
    {
        string[] valid =
        [
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_servers","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"run_command","arguments":{"server":"Web","command":"ls"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":"x","method":"ping"}""",
        ];
        SaveHost("Web", h => h.AllowMcpCommands = true);

        var random = new Random(7);
        for (int round = 0; round < 20; round++)
        {
            var lines = new List<string>();
            for (int i = 0; i < 100; i++)
            {
                byte[] bytes = random.Next(5) == 0 ? Fuzz.Garbage(random, 120) : Fuzz.Mutate(random, Encoding.UTF8.GetBytes(valid[random.Next(valid.Length)]));
                string line = Encoding.UTF8.GetString(bytes).Replace('\r', ' ').Replace('\n', ' ');
                if (line.Trim().Length > 0) lines.Add(line);
            }

            List<JsonElement> replies = await ConverseAsync([.. lines]);

            Assert.True(replies.Count == lines.Count(ExpectsAnAnswer), $"round {round}");
            Assert.All(replies, reply =>
            {
                Assert.Equal("2.0", reply.GetProperty("jsonrpc").GetString());
                Assert.True(reply.TryGetProperty("id", out _));
                Assert.True(reply.TryGetProperty("result", out _) ^ reply.TryGetProperty("error", out _));
            });
        }

        Assert.False(File.Exists(Path.Combine(DataDirectory, "mcp-commands.log")));
    }

    // Everything gets an answer except a JSON object without an id.
    private static bool ExpectsAnAnswer(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.TryGetProperty("id", out _);
        }
        catch (JsonException)
        {
            return true;
        }
    }
}

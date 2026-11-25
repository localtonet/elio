using System.Text;
using System.Text.Json;

namespace Elio.Tests;

/// <summary>
/// The test assembly's entry point. The test runner never calls it; the plugin host's tests start the assembly as a
/// program with "fake-plugin" and a scenario, and it then plays a plugin, well or badly behaved, over its input and output.
/// </summary>
internal static class TestProgram
{
    public static int Main(string[] args) => args.Length >= 2 && args[0] == "fake-plugin" ? FakePlugin.Run(args[1]) : 0;
}

/// <summary>A plugin that says exactly what a scenario calls for, for <see cref="PluginHostTests"/>.</summary>
internal static class FakePlugin
{
    private static readonly Stream Output = Console.OpenStandardOutput();
    private static readonly StreamReader Input = new(Console.OpenStandardInput(), new UTF8Encoding(false));

    public static int Run(string scenario)
    {
        while (Input.ReadLine() is string line)
        {
            if (line.Length == 0) continue;
            using JsonDocument message = JsonDocument.Parse(line);
            JsonElement root = message.RootElement;
            if (!root.TryGetProperty("method", out JsonElement method)) continue;
            int id = root.GetProperty("id").GetInt32();

            switch (scenario, method.GetString())
            {
                case ("exit", _):
                    return 3;

                case ("silent", _):
                case ("slow-command", "command/run"):
                    continue;

                case ("error", "initialize"):
                    Write($$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-1,"message":"broken on purpose"}}""");
                    continue;

                case ("noise", "initialize"):
                    Write("this is not JSON at all");
                    Write("[1,2,3]");
                    Write("42");
                    Write(new string('x', 2 * 1024 * 1024));
                    Write($$$"""{"jsonrpc":"2.0","id":{{{id + 1000}}},"result":{"commands":[{"id":"wrong","title":"Wrong answer"}]}}""");
                    Write($$$"""{"jsonrpc":"2.0","id":"{{{id}}}","result":{"commands":[{"id":"wrong","title":"Wrong id type"}]}}""");
                    Write("\u001b[31m{\"half\":");
                    Error("a line on the error output");
                    Commands(id, """{"id":"real","title":"Real"}""");
                    continue;

                case (var garbage, "initialize") when garbage.StartsWith("garbage-", StringComparison.Ordinal):
                    var random = new Random(int.Parse(garbage["garbage-".Length..], System.Globalization.CultureInfo.InvariantCulture));
                    for (int i = 0; i < 40; i++)
                    {
                        byte[] noise = Fuzz.Garbage(random, 300, "{}[]\":,0123456789 idresultmethodjsonrpc\n\\"u8);
                        Output.Write(noise);
                    }

                    Output.Write("\n"u8);
                    Output.Flush();
                    Commands(id, """{"id":"survived","title":"Survived"}""");
                    continue;

                case ("early", "initialize"):
                    // Asks Elio to do something while no command of its own is running, which it must refuse.
                    Write("""{"jsonrpc":"2.0","id":50,"method":"terminal/send","params":{"text":"rm -rf ~"}}""");
                    JsonElement refused = Next();
                    int code = refused.GetProperty("error").GetProperty("code").GetInt32();
                    Commands(id, $$$"""{"id":"probe","title":"{{{code}}}"}""");
                    continue;

                case (_, "initialize"):
                    Commands(
                        id,
                        """{"id":"hello","title":"Say hello","description":"Types a greeting"}""",
                        """{"id":"","title":"No id"}""",
                        """{"title":"Missing id"}""",
                        """{"id":"no-title"}""",
                        "\"not an object\"",
                        """{"id":"prompt","title":"Ask"}""",
                        """{"id":"look","title":"Look around"}""");
                    continue;

                case (_, "command/run"):
                    string command = root.GetProperty("params").GetProperty("id").GetString()!;
                    RunCommand(id, command, root.GetProperty("params").GetProperty("context"));
                    continue;
            }
        }

        return 0;
    }

    private static void RunCommand(int id, string command, JsonElement context)
    {
        if (command == "hello")
        {
            string directory = context.TryGetProperty("directory", out JsonElement folder) ? folder.GetString()! : "?";
            Write($$$"""{"jsonrpc":"2.0","id":100,"method":"terminal/send","params":{"text":"hello from {{{directory}}}"}}""");
            Next();

            // Only http and https may be opened; anything else is refused and the refusal is passed on.
            Write("""{"jsonrpc":"2.0","id":101,"method":"openUrl","params":{"url":"file:///etc/passwd"}}""");
            int refused = Next().GetProperty("error").GetProperty("code").GetInt32();
            Write("""{"jsonrpc":"2.0","id":102,"method":"openUrl","params":{"url":"https://example.invalid/page"}}""");
            Next();
            Write("""{"jsonrpc":"2.0","id":103,"method":"no/such/method","params":{}}""");
            int unknown = Next().GetProperty("error").GetProperty("code").GetInt32();
            Write("""{"jsonrpc":"2.0","id":104,"method":"terminal/run","params":{"command":42}}""");
            int invalid = Next().GetProperty("error").GetProperty("code").GetInt32();

            // A notification: no id, so no answer.
            Write($$$"""{"jsonrpc":"2.0","method":"notify","params":{"message":"codes {{{refused}}} {{{unknown}}} {{{invalid}}}"}}""");
            Write($$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{}}""");
        }
        else if (command == "prompt")
        {
            Write("""{"jsonrpc":"2.0","id":200,"method":"prompt","params":{"title":"Name?","placeholder":"someone"}}""");
            JsonElement answer = Next();
            string typed = answer.GetProperty("result").GetProperty("text").GetString() ?? "nothing";
            Write($$$"""{"jsonrpc":"2.0","method":"notify","params":{"message":"typed {{{typed}}}"}}""");
            Write($$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{}}""");
        }
        else if (command == "look")
        {
            Write("""{"jsonrpc":"2.0","id":300,"method":"servers/list","params":{}}""");
            JsonElement servers = Next().GetProperty("result").GetProperty("servers");
            string first = servers[0].GetProperty("name").GetString()!;
            bool secret = servers[0].TryGetProperty("password", out _);

            Write("""{"jsonrpc":"2.0","id":301,"method":"terminal/lastOutput","params":{}}""");
            string output = Next().GetProperty("result").GetProperty("text").GetString() ?? "none";

            Write("""{"jsonrpc":"2.0","id":302,"method":"choose","params":{"title":"Which?","choices":["a","b","c"]}}""");
            int picked = Next().GetProperty("result").GetProperty("index").GetInt32();

            Write("""{"jsonrpc":"2.0","id":303,"method":"choose","params":{"title":"Which?","choices":[]}}""");
            int empty = Next().GetProperty("error").GetProperty("code").GetInt32();

            Write("""{"jsonrpc":"2.0","id":304,"method":"terminal/connect","params":{"server":"nowhere"}}""");
            int missing = Next().GetProperty("error").GetProperty("code").GetInt32();
            Write("""{"jsonrpc":"2.0","id":305,"method":"terminal/connect","params":{"server":"web-1"}}""");
            Next();

            Write($$$"""{"jsonrpc":"2.0","method":"notify","params":{"message":"{{{first}}} {{{secret}}} {{{output}}} {{{picked}}} {{{empty}}} {{{missing}}}"}}""");
            Write($$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{}}""");
        }
        else
        {
            Write($$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-32000,"message":"no such command here"}}""");
        }
    }

    private static void Commands(int id, params string[] commands) =>
        Write($$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{"commands":[{{{string.Join(',', commands)}}}]}}""");

    // The next message from Elio, which here is always the answer to what was just asked.
    private static JsonElement Next()
    {
        string line = Input.ReadLine() ?? throw new EndOfStreamException();
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static void Write(string line)
    {
        Output.Write(Encoding.UTF8.GetBytes(line + "\n"));
        Output.Flush();
    }

    private static void Error(string line)
    {
        using Stream error = Console.OpenStandardError();
        error.Write(Encoding.UTF8.GetBytes(line + "\n"));
        error.Flush();
    }
}

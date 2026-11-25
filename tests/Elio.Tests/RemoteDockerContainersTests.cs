using Elio.Connections;
using Elio.Core;

namespace Elio.Tests;

public sealed class RemoteDockerContainersTests
{
    private static readonly RemoteContainer Nginx = new("nginx", "nginx:1.27", "Up 3 hours");

    [Fact]
    public void ListCommand_IsDockerPsWithTheFieldsApartByTabs_AsTheServerShellSeesIt() =>
        Assert.Equal(@"docker ps --format '{{.Names}}\t{{.Image}}\t{{.Status}}'", RemoteDockerContainers.ListCommand);

    [Fact]
    public void Parse_GivesEachRunningContainer()
    {
        List<RemoteContainer> containers = RemoteDockerContainers.Parse("web-1\tnginx:1.27\tUp 3 hours\r\nredis\tredis:7-alpine\tUp 2 days (healthy)\n");

        Assert.Equal([new RemoteContainer("web-1", "nginx:1.27", "Up 3 hours"), new RemoteContainer("redis", "redis:7-alpine", "Up 2 days (healthy)")], containers);
    }

    [Theory]
    [InlineData("Welcome to Ubuntu 24.04 LTS")]
    [InlineData("web; rm -rf ~\tnginx\tUp")]
    [InlineData("-it\tnginx\tUp")]
    [InlineData(".hidden\tnginx\tUp")]
    [InlineData("$(id)\tnginx\tUp")]
    [InlineData("we b\tnginx\tUp")]
    [InlineData("web`id`\tnginx\tUp")]
    [InlineData("web,other/alias\tnginx\tUp")]
    [InlineData("web\tnginx")]
    [InlineData("\t\t")]
    public void Parse_LeavesOutWhatIsNotAContainerDockerWouldAllow(string line) =>
        Assert.Empty(RemoteDockerContainers.Parse(line));

    [Fact]
    public void Parse_TakesControlCharactersOutOfWhatIsShown()
    {
        RemoteContainer container = Assert.Single(RemoteDockerContainers.Parse("web\tevil\u001b[2J:latest\tUp\u0007 1 hour"));

        Assert.Equal(("evil[2J:latest", "Up 1 hour"), (container.Image, container.Status));
    }

    [Fact]
    public void Problem_IsNone_WhenTheCommandEndedWell() =>
        Assert.Null(RemoteDockerContainers.Problem(new CommandResult(0, "web\tnginx\tUp\n", string.Empty, TimeSpan.Zero, null)));

    [Fact]
    public void Problem_IsTheConnectionsOwn_WhenItNeverRan() =>
        Assert.Equal("The host key is not known. Connect once first.", RemoteDockerContainers.Problem(new CommandResult(null, string.Empty, string.Empty, TimeSpan.Zero, "The host key is not known. Connect once first.")));

    [Fact]
    public void Problem_WithoutTheRightToUseDocker_IsWhatTheServerSaid_WithWhatToDo()
    {
        const string Said = "permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock: Get \"http://%2Fvar%2Frun%2Fdocker.sock/v1.24/containers/json\": dial unix /var/run/docker.sock: connect: permission denied";

        string? problem = RemoteDockerContainers.Problem(new CommandResult(1, string.Empty, Said + "\n", TimeSpan.Zero, null));

        Assert.NotNull(problem);
        Assert.StartsWith(Said, problem);
        Assert.Contains("docker group", problem);
    }

    [Fact]
    public void Problem_WithoutDocker_IsWhatTheShellSaid()
    {
        Assert.Equal("bash: line 1: docker: command not found", RemoteDockerContainers.Problem(new CommandResult(127, string.Empty, "bash: line 1: docker: command not found\n", TimeSpan.Zero, null)));
        Assert.Contains("2", RemoteDockerContainers.Problem(new CommandResult(2, string.Empty, string.Empty, TimeSpan.Zero, null)));
    }

    private static HostEntry Server() => new()
    {
        Name = "web-1",
        Kind = HostKind.Ssh,
        Address = "203.0.113.10",
        Port = 2222,
        User = "deploy",
        KeyPath = "/home/me/.ssh/id_ed25519",
        IdentityId = Guid.NewGuid(),
        GroupId = Guid.NewGuid(),
        Tags = ["prod"],
        JumpHosts = [Guid.NewGuid()],
        UseAgent = true,
        ForwardAgent = true,
        UseMosh = true,
        LoginScript = "cd /srv/app",
        RemoteCommand = "tmux attach",
        Forwards = [new PortForward { Kind = ForwardKind.Local, BindPort = 8080, TargetHost = "localhost", TargetPort = 80 }],
        Environment = new Dictionary<string, string> { ["LANG"] = "C.UTF-8" },
        Proxy = new ProxySettings { Kind = ProxyKind.Socks5, Host = "proxy.example.com", Port = 1080, User = "me" },
        ModifiedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
    };

    [Fact]
    public void ShellIn_IsACopyOfTheServer_ThatRunsDockerExecInsteadOfItsShell()
    {
        HostEntry server = Server();

        HostEntry copy = RemoteDockerContainers.ShellIn(server, Nginx);

        Assert.Equal("web-1 / nginx", copy.Name);
        Assert.Equal("docker exec -it nginx sh -c \"if command -v bash >/dev/null 2>&1; then exec bash; else exec sh; fi\"", copy.RemoteCommand);

        // It connects exactly as the server does.
        Assert.Equal((server.Kind, server.Address, server.Port, server.User, server.KeyPath, server.IdentityId, server.GroupId), (copy.Kind, copy.Address, copy.Port, copy.User, copy.KeyPath, copy.IdentityId, copy.GroupId));
        Assert.Equal(server.JumpHosts, copy.JumpHosts);
        Assert.Equal((true, true, true), (copy.UseAgent, copy.ForwardAgent, copy.UseMosh));
        Assert.Equal(server.Environment, copy.Environment);
        Assert.Equal((ProxyKind.Socks5, "proxy.example.com", 1080, "me"), (copy.Proxy!.Kind, copy.Proxy.Host, copy.Proxy.Port, copy.Proxy.User));

        // What belongs to the server's own shell stays with it.
        Assert.Null(copy.LoginScript);
        Assert.Empty(copy.Forwards);
        Assert.False(copy.AutoReconnect);
        Assert.Equal(["nginx:1.27"], copy.Tags);
    }

    [Fact]
    public void ShellIn_HasAnIdOfItsOwn_TheSameEachTimeForTheSameContainer()
    {
        HostEntry server = Server();

        HostEntry copy = RemoteDockerContainers.ShellIn(server, Nginx);

        Assert.NotEqual(server.Id, copy.Id);
        Assert.Equal(copy.Id, RemoteDockerContainers.ShellIn(server, Nginx).Id);
        Assert.NotEqual(copy.Id, RemoteDockerContainers.ShellIn(server, Nginx with { Name = "redis" }).Id);
        Assert.NotEqual(copy.Id, RemoteDockerContainers.ShellIn(Server(), Nginx).Id);
    }

    [Fact]
    public void ShellIn_LeavesTheSavedServerAsItWas()
    {
        HostEntry server = Server();

        HostEntry copy = RemoteDockerContainers.ShellIn(server, Nginx);
        copy.JumpHosts.Add(Guid.NewGuid());
        copy.Environment["X"] = "1";
        copy.Proxy!.Host = "elsewhere.example.com";

        Assert.Equal("web-1", server.Name);
        Assert.Equal("tmux attach", server.RemoteCommand);
        Assert.Equal("cd /srv/app", server.LoginScript);
        Assert.Single(server.Forwards);
        Assert.Single(server.JumpHosts);
        Assert.False(server.Environment.ContainsKey("X"));
        Assert.Equal("proxy.example.com", server.Proxy!.Host);
        Assert.True(server.AutoReconnect);
    }

    [Theory]
    [InlineData("nginx; rm -rf ~")]
    [InlineData("-v")]
    [InlineData("")]
    [InlineData("$(id)")]
    public void ShellIn_RefusesANameDockerWouldNotAllow(string name) =>
        Assert.Throws<ArgumentException>(() => RemoteDockerContainers.ShellIn(Server(), Nginx with { Name = name }));

    [Fact]
    public void HostCopy_CopiesEverySetting()
    {
        HostEntry server = Server();
        server.Serial = new SerialSettings { BaudRate = 9600 };
        server.AllowMcpCommands = true;
        server.ForwardX11 = true;
        server.Directory = "/srv";
        server.Localtonet = true;
        server.LocaltonetAccountId = "account";

        HostEntry copy = HostCopy.Of(server);

        // Every property, compared through what is saved, so that one added later is covered as well.
        foreach (var property in typeof(HostEntry).GetProperties())
        {
            object? before = property.GetValue(server), after = property.GetValue(copy);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before), System.Text.Json.JsonSerializer.Serialize(after));
        }

        Assert.NotSame(server.Serial, copy.Serial);
    }
}

using Elio.Core;

namespace Elio.Tests;

public sealed class LibraryFileTests
{
    private static HostEntry Ssh() => new() { Name = "web", Address = "web.example.com", User = "root" };

    private static PortForward Forward(ForwardKind kind, string bind) =>
        new() { Kind = kind, BindAddress = bind, BindPort = 8080, TargetHost = "localhost", TargetPort = 80 };

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    [InlineData("{\"Hosts\": null}")]
    [InlineData("{\"Hosts\": [], \"Groups\": null, \"Snippets\": []}")]
    [InlineData("{\"Hosts\": \"none\"}")]
    public void Read_TextThatIsNotALibrary_ReturnsNull(string text) =>
        Assert.Null(LibraryFile.Read(text));

    [Fact]
    public void Read_WhatWriteProduced_RoundTrips()
    {
        var library = new HostLibrary { Hosts = [Ssh()], Groups = [new HostGroup { Name = "prod" }], Snippets = [new Snippet { Name = "up", Command = "uptime" }] };

        HostLibrary? read = LibraryFile.Read(LibraryFile.Write(library));

        Assert.NotNull(read);
        Assert.Equal("web.example.com", Assert.Single(read.Hosts).Address);
        Assert.Equal("prod", Assert.Single(read.Groups).Name);
        Assert.Equal("uptime", Assert.Single(read.Snippets).Command);
    }

    [Fact]
    public void Read_DropsNullEntriesFromLists()
    {
        const string text = """
            {
              "Hosts": [null, {"Name": "web", "Address": "web.example.com"}],
              "Groups": [null, {"Name": null}, {"Name": "prod"}],
              "Snippets": [null, {"Name": null, "Command": "ls"}, {"Name": "x", "Command": null}, {"Name": "up", "Command": "uptime"}]
            }
            """;

        HostLibrary? read = LibraryFile.Read(text);

        Assert.NotNull(read);
        Assert.Equal("web", Assert.Single(read.Hosts).Name);
        Assert.Equal("prod", Assert.Single(read.Groups).Name);
        Assert.Equal("up", Assert.Single(read.Snippets).Name);
    }

    [Fact]
    public void Read_ReplacesNullsInsideAHostWithEmptyValues()
    {
        const string text = """
            {
              "Hosts": [{"Name": null, "Address": null, "User": null, "Tags": null, "JumpHosts": null, "Environment": null, "Forwards": null}],
              "Groups": [],
              "Snippets": []
            }
            """;

        HostEntry host = Assert.Single(LibraryFile.Read(text)!.Hosts);

        Assert.Equal((string.Empty, string.Empty, string.Empty), (host.Name, host.Address, host.User));
        Assert.Empty(host.Tags);
        Assert.Empty(host.JumpHosts);
        Assert.Empty(host.Environment);
        Assert.Empty(host.Forwards);
    }

    [Fact]
    public void Read_DropsForwardsWithNullsInThem()
    {
        const string text = """
            {
              "Hosts": [{"Name": "web", "Forwards": [null, {"BindAddress": null, "TargetHost": "a"}, {"BindAddress": "127.0.0.1", "TargetHost": null}, {"BindAddress": "127.0.0.1", "BindPort": 8080, "TargetHost": "localhost", "TargetPort": 80}]}],
              "Groups": [],
              "Snippets": []
            }
            """;

        HostEntry host = Assert.Single(LibraryFile.Read(text)!.Hosts);

        Assert.Equal(8080, Assert.Single(host.Forwards).BindPort);
    }

    [Fact]
    public void ActsOnItsOwn_PlainSshEntry_IsFalse() =>
        Assert.False(LibraryFile.ActsOnItsOwn(Ssh()));

    [Fact]
    public void ActsOnItsOwn_LocalEntry_IsTrue()
    {
        HostEntry host = Ssh();
        host.Kind = HostKind.Local;
        Assert.True(LibraryFile.ActsOnItsOwn(host));
    }

    [Fact]
    public void ActsOnItsOwn_LoginScript_IsTrue()
    {
        HostEntry host = Ssh();
        host.LoginScript = "sudo -i";
        Assert.True(LibraryFile.ActsOnItsOwn(host));
    }

    [Fact]
    public void ActsOnItsOwn_RemoteCommand_IsTrue()
    {
        HostEntry host = Ssh();
        host.RemoteCommand = "tmux attach";
        Assert.True(LibraryFile.ActsOnItsOwn(host));
    }

    [Fact]
    public void ActsOnItsOwn_ForwardAgent_IsTrue()
    {
        HostEntry host = Ssh();
        host.ForwardAgent = true;
        Assert.True(LibraryFile.ActsOnItsOwn(host));
    }

    [Fact]
    public void ActsOnItsOwn_AllowMcpCommands_IsTrue()
    {
        HostEntry host = Ssh();
        host.AllowMcpCommands = true;
        Assert.True(LibraryFile.ActsOnItsOwn(host));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    public void ActsOnItsOwn_RemoteForward_IsTrueWhereverItListens(string bind)
    {
        HostEntry host = Ssh();
        host.Forwards.Add(Forward(ForwardKind.Remote, bind));
        Assert.True(LibraryFile.ActsOnItsOwn(host));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.168.1.10")]
    [InlineData("")]
    public void ActsOnItsOwn_LocalForwardOpenToOtherComputers_IsTrue(string bind)
    {
        HostEntry host = Ssh();
        host.Forwards.Add(Forward(ForwardKind.Local, bind));
        Assert.True(LibraryFile.ActsOnItsOwn(host));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void ActsOnItsOwn_LocalForwardOnThisComputerOnly_IsFalse(string bind)
    {
        HostEntry host = Ssh();
        host.Forwards.Add(Forward(ForwardKind.Local, bind));
        host.Forwards.Add(Forward(ForwardKind.Dynamic, bind));
        Assert.False(LibraryFile.ActsOnItsOwn(host));
    }

    private static HostLibrary Loaded()
    {
        HostEntry loaded = Ssh();
        loaded.LoginScript = "sudo -i";
        loaded.RemoteCommand = "tmux attach";
        loaded.ForwardAgent = true;
        loaded.AllowMcpCommands = true;
        loaded.ForwardX11 = true;
        loaded.UseAgent = true;
        loaded.KeyPath = "~/.ssh/id_ed25519";
        loaded.Forwards =
        [
            Forward(ForwardKind.Local, "127.0.0.1"),
            Forward(ForwardKind.Local, "0.0.0.0"),
            Forward(ForwardKind.Remote, "127.0.0.1"),
            Forward(ForwardKind.Dynamic, "localhost"),
        ];
        return new HostLibrary { Hosts = [new HostEntry { Name = "shell", Kind = HostKind.Local, Address = "pwsh" }, loaded] };
    }

    [Fact]
    public void MakePlain_RemovesLocalEntries()
    {
        HostLibrary library = Loaded();

        LibraryFile.MakePlain(library);

        Assert.Equal("web", Assert.Single(library.Hosts).Name);
    }

    [Fact]
    public void MakePlain_ClearsEverythingActsOnItsOwnLooksFor()
    {
        HostLibrary library = Loaded();

        LibraryFile.MakePlain(library);

        HostEntry host = Assert.Single(library.Hosts);
        Assert.Null(host.LoginScript);
        Assert.Null(host.RemoteCommand);
        Assert.False(host.ForwardAgent);
        Assert.False(host.AllowMcpCommands);
        Assert.False(LibraryFile.ActsOnItsOwn(host));
    }

    [Fact]
    public void MakePlain_KeepsOnlyForwardsThatListenOnThisComputer()
    {
        HostLibrary library = Loaded();

        LibraryFile.MakePlain(library);

        Assert.Equal(
            [(ForwardKind.Local, "127.0.0.1"), (ForwardKind.Dynamic, "localhost")],
            Assert.Single(library.Hosts).Forwards.Select(forward => (forward.Kind, forward.BindAddress)));
    }

    [Fact]
    public void MakePlain_LeavesTheOtherSettingsAlone()
    {
        HostLibrary library = Loaded();

        LibraryFile.MakePlain(library);

        HostEntry host = Assert.Single(library.Hosts);
        Assert.Equal(("web.example.com", "root", "~/.ssh/id_ed25519"), (host.Address, host.User, host.KeyPath));
        Assert.True(host.ForwardX11);
        Assert.True(host.UseAgent);
    }
}

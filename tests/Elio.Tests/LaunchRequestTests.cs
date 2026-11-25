using Elio.Core;

namespace Elio.Tests;

public sealed class LaunchRequestTests
{
    private static LaunchRequest Parse(params string[] args) => LaunchRequest.Parse(args);

    [Fact]
    public void NoArguments_IsANewTab() =>
        Assert.Equal(LaunchKind.NewTab, Parse().Kind);

    [Fact]
    public void TheArgumentAnUpdatePasses_IsNotARequest() =>
        Assert.Equal(LaunchKind.NewTab, Parse("--wait-for-exit=1234").Kind);

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    public void Help(string word) =>
        Assert.Equal(LaunchKind.Help, Parse(word).Kind);

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void Version(string word) =>
        Assert.Equal(LaunchKind.Version, Parse(word).Kind);

    [Fact]
    public void UnknownCommand_IsInvalid_AndSaysWhichWord()
    {
        LaunchRequest request = Parse("frobnicate");
        Assert.Equal(LaunchKind.Invalid, request.Kind);
        Assert.Contains("frobnicate", request.Error);
    }

    [Theory]
    [InlineData("web-1", "web-1", 22, "")]
    [InlineData("deploy@web-1", "web-1", 22, "deploy")]
    [InlineData("deploy@web-1:2222", "web-1", 2222, "deploy")]
    [InlineData("10.0.0.5:2200", "10.0.0.5", 2200, "")]
    [InlineData("root@[::1]:2222", "::1", 2222, "root")]
    [InlineData("root@::1", "::1", 22, "root")]
    [InlineData("me@corp.example@bastion", "bastion", 22, "me@corp.example")]
    public void Ssh_ReadsTheDestination(string destination, string host, int port, string user)
    {
        LaunchRequest request = Parse("ssh", destination);

        Assert.Equal(LaunchKind.Ssh, request.Kind);
        Assert.False(request.FromLink);
        Assert.Equal(new SshTarget(host, port, user), request.Target);
    }

    [Fact]
    public void Ssh_OptionsWinOverTheDestination()
    {
        LaunchRequest request = Parse("ssh", "-p", "2022", "-l", "admin", "-i", "~/.ssh/id_ed25519", "deploy@web-1:2222");

        Assert.Equal(new SshTarget("web-1", 2022, "admin", "~/.ssh/id_ed25519"), request.Target);
    }

    [Theory]
    [InlineData("ssh")]
    [InlineData("ssh", "-p")]
    [InlineData("ssh", "-p", "0", "web-1")]
    [InlineData("ssh", "-p", "65536", "web-1")]
    [InlineData("ssh", "-p", "22x", "web-1")]
    [InlineData("ssh", "-o", "ProxyCommand=evil", "web-1")]
    [InlineData("ssh", "web-1", "rm", "-rf", "/")]
    [InlineData("ssh", "-oProxyCommand=x")]
    [InlineData("ssh", "web 1")]
    [InlineData("ssh", "-web-1")]
    [InlineData("ssh", "@web-1")]
    [InlineData("ssh", "web-1:")]
    [InlineData("ssh", "[::1")]
    [InlineData("ssh", "-l", "-oops", "web-1")]
    public void Ssh_RefusesWhatItCannotUse(params string[] args) =>
        Assert.Equal(LaunchKind.Invalid, Parse(args).Kind);

    [Theory]
    [InlineData("ssh://web-1", "web-1", 22, "")]
    [InlineData("ssh://deploy@web-1:2222", "web-1", 2222, "deploy")]
    [InlineData("ssh://deploy@web-1:2222/", "web-1", 2222, "deploy")]
    [InlineData("SSH://Deploy@Web-1", "web-1", 22, "Deploy")]
    [InlineData("ssh://root@[2001:db8::1]:2200", "2001:db8::1", 2200, "root")]
    [InlineData("ssh://deploy;fingerprint=SHA256-abc@web-1", "web-1", 22, "deploy")]
    [InlineData("ssh://deploy:hunter2@web-1", "web-1", 22, "deploy")]
    [InlineData("ssh://first%20last@web-1", "web-1", 22, "")]
    public void SshLink_IsReadAsALink(string link, string host, int port, string user)
    {
        LaunchRequest request = Parse(link);

        if (user.Length == 0 && link.Contains('%'))
        {
            // A user name with a space in it is not one Elio takes.
            Assert.Equal(LaunchKind.Invalid, request.Kind);
            return;
        }

        Assert.Equal(LaunchKind.Ssh, request.Kind);
        Assert.True(request.FromLink);
        Assert.Equal(host, request.Target!.Host, ignoreCase: true);
        Assert.Equal(port, request.Target.Port);
        Assert.Equal(user, request.Target.User);
    }

    [Fact]
    public void SshLink_NeverKeepsAPassword()
    {
        LaunchRequest request = Parse("ssh://deploy:hunter2@web-1");

        Assert.DoesNotContain("hunter2", request.ToString());
        Assert.DoesNotContain("hunter2", string.Join(' ', request.ToArguments()));
    }

    [Theory]
    [InlineData("ssh://")]
    [InlineData("ssh://-oProxyCommand=x")]
    [InlineData("ftp://web-1")]
    [InlineData("javascript://alert(1)")]
    public void Links_ThatAreNotForElio_AreInvalid(string link) =>
        Assert.Equal(LaunchKind.Invalid, Parse(link).Kind);

    [Fact]
    public void Connect_TakesTheWholeName()
    {
        LaunchRequest request = Parse("connect", "web", "server", "1");

        Assert.Equal(LaunchKind.Connect, request.Kind);
        Assert.Equal("web server 1", request.Name);
        Assert.False(request.FromLink);
    }

    [Fact]
    public void Connect_WithoutAName_IsInvalid() =>
        Assert.Equal(LaunchKind.Invalid, Parse("connect").Kind);

    [Fact]
    public void Workspace_TakesTheName()
    {
        LaunchRequest request = Parse("workspace", "Morning checks");

        Assert.Equal(LaunchKind.Workspace, request.Kind);
        Assert.Equal("Morning checks", request.Name);
    }

    [Theory]
    [InlineData("elio://connect/web%20server%201", LaunchKind.Connect, "web server 1")]
    [InlineData("elio://workspace/Morning%20checks", LaunchKind.Workspace, "Morning checks")]
    public void ElioLinks_NameAServerOrAWorkspace(string link, LaunchKind kind, string name)
    {
        LaunchRequest request = Parse(link);

        Assert.Equal(kind, request.Kind);
        Assert.Equal(name, request.Name);
        Assert.True(request.FromLink);
    }

    [Fact]
    public void OtherElioLinks_GoOnAsTheyCame()
    {
        LaunchRequest request = LaunchRequest.FromLinkText("elio://sync/ABCDE-FGHIJ?s=https%3A%2F%2Fsync.example.com");

        Assert.Equal(LaunchKind.Link, request.Kind);
        Assert.Equal("elio://sync/ABCDE-FGHIJ?s=https%3A%2F%2Fsync.example.com", request.Link);
    }

    [Fact]
    public void ElioLink_WithAControlCharacterInTheName_IsInvalid() =>
        Assert.Equal(LaunchKind.Invalid, Parse("elio://connect/web%0Aserver").Kind);

    [Theory]
    [InlineData("ssh", "deploy@web-1:2222")]
    [InlineData("ssh", "-i", "C:\\keys\\id", "root@[::1]:2200")]
    [InlineData("ssh://deploy@web-1:2222")]
    [InlineData("connect", "web server 1")]
    [InlineData("workspace", "Morning checks")]
    [InlineData("elio://connect/web%20server")]
    [InlineData("elio://workspace/Morning")]
    [InlineData("elio://sync/ABCDE-FGHIJ")]
    public void ToArguments_IsReadBackAsTheSameRequest(params string[] args)
    {
        LaunchRequest request = Parse(args);

        Assert.Equal(request, LaunchRequest.Parse(request.ToArguments()));
    }

    private static HostEntry Server(string name, string address, int port = 22, string user = "") =>
        new() { Name = name, Kind = HostKind.Ssh, Address = address, Port = port, User = user };

    private static (string, int) AsSaved(HostEntry host) => (host.User, host.Port);

    [Fact]
    public void FindSaved_MatchesAddressAndPort_AndPrefersTheSameUser()
    {
        HostEntry open = Server("web (any user)", "web-1");
        HostEntry deploy = Server("web as deploy", "WEB-1", user: "deploy");
        HostEntry otherPort = Server("web on 2222", "web-1", 2222, "deploy");
        HostEntry[] hosts = [open, deploy, otherPort];

        Assert.Same(deploy, LaunchRequest.FindSaved(hosts, new SshTarget("web-1", 22, "deploy"), AsSaved));
        Assert.Same(otherPort, LaunchRequest.FindSaved(hosts, new SshTarget("web-1", 2222, "deploy"), AsSaved));
        Assert.Null(LaunchRequest.FindSaved(hosts, new SshTarget("web-1", 22, "root"), AsSaved));
        Assert.NotNull(LaunchRequest.FindSaved(hosts, new SshTarget("web-1", 22, ""), AsSaved));
        Assert.Null(LaunchRequest.FindSaved(hosts, new SshTarget("web-2", 22, ""), AsSaved));
    }

    [Fact]
    public void FindSaved_LeavesOutWhatIsNotSsh()
    {
        HostEntry telnet = new() { Name = "switch", Kind = HostKind.Telnet, Address = "10.0.0.1", Port = 22 };

        Assert.Null(LaunchRequest.FindSaved([telnet], new SshTarget("10.0.0.1", 22, ""), AsSaved));
    }

    [Fact]
    public void FindSaved_UsesTheUserTheServerConnectsWith()
    {
        // The server leaves the user to its group; what counts is the user it ends up connecting as.
        HostEntry server = Server("web", "web-1");

        Assert.Same(server, LaunchRequest.FindSaved([server], new SshTarget("web-1", 22, "deploy"), _ => ("deploy", 22)));
    }

    [Theory]
    [InlineData("web-1", 22, "", "ssh://web-1")]
    [InlineData("web-1", 2222, "deploy", "ssh://deploy@web-1:2222")]
    [InlineData("::1", 22, "root", "ssh://root@[::1]")]
    public void ToSshLink(string host, int port, string user, string link) =>
        Assert.Equal(link, LaunchRequest.ToSshLink(new SshTarget(host, port, user)));
}

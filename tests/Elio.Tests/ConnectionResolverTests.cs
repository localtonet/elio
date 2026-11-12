using Elio.Connections;
using Elio.Core;

namespace Elio.Tests;

/// <summary>
/// What a server connects with once its own settings, its groups' and its identity are combined. Nothing here
/// opens a vault: the secrets come from a plain dictionary.
/// </summary>
public sealed class ConnectionResolverTests
{
    private readonly HostLibrary _library = new();
    private readonly Dictionary<Guid, string> _vault = [];

    private string? SecretOf(Guid id) => _vault.GetValueOrDefault(id);

    private HostEntry Server(Guid? group = null, string user = "", int port = 0, string name = "web")
    {
        var host = new HostEntry { Name = name, Address = name + ".example.com", User = user, Port = port, GroupId = group };
        _library.Hosts.Add(host);
        return host;
    }

    private HostGroup Group(string name, HostGroup? parent = null)
    {
        var group = new HostGroup { Name = name, ParentId = parent?.Id };
        _library.Groups.Add(group);
        return group;
    }

    private Identity PasswordIdentity(string name, string user, string? password = "identity-password")
    {
        var identity = new Identity { Name = name, User = user, Kind = IdentityKind.Password };
        _library.Identities.Add(identity);
        if (password is not null) _vault[identity.Id] = new IdentitySecret { Password = password }.Write();
        return identity;
    }

    private Identity KeyIdentity(string name, string user, string? passphrase = null)
    {
        var identity = new Identity { Name = name, User = user, Kind = IdentityKind.Key };
        _library.Identities.Add(identity);
        _vault[identity.Id] = new IdentitySecret { PrivateKey = "KEY TEXT", Passphrase = passphrase }.Write();
        return identity;
    }

    private ResolvedHost Resolve(HostEntry host) => ConnectionResolver.Resolve(host, _library);

    private ResolvedLogin Login(HostEntry host) => ConnectionResolver.Login(Resolve(host), SecretOf);

    // As before: a server saved by a version without any of this.

    [Fact]
    public void Resolve_ServerWithNothingNew_ConnectsWithItsOwnFields()
    {
        HostEntry host = Server(user: "root", port: 2222);
        host.UseAgent = true;
        host.JumpHosts = [Guid.NewGuid()];

        ResolvedHost resolved = Resolve(host);

        Assert.Equal(("root", 2222, true, false), (resolved.User, resolved.Port, resolved.UseAgent, resolved.UseMosh));
        Assert.Equal(host.JumpHosts, resolved.JumpHosts);
        Assert.Null(resolved.Identity);
        Assert.Null(resolved.Proxy);
    }

    [Fact]
    public void Login_ServerWithASavedPassword_UsesIt()
    {
        HostEntry host = Server(user: "root", port: 22);
        _vault[host.Id] = "own-password";

        ResolvedLogin login = Login(host);

        Assert.Equal(("root", "own-password", null, null, null), (login.User, login.Password, login.KeyPath, login.KeyText, login.KeyPassphrase));
        Assert.False(login.NeedsSecret);
        Assert.True(login.TakesTypedPassword);
    }

    [Fact]
    public void Login_ServerWithAKeyFile_TreatsTheSavedSecretAsItsPassphrase()
    {
        HostEntry host = Server(user: "root", port: 22);
        host.KeyPath = "/home/me/.ssh/id_ed25519";
        _vault[host.Id] = "passphrase";

        ResolvedLogin login = Login(host);

        Assert.Equal((null, "/home/me/.ssh/id_ed25519", "passphrase"), (login.Password, login.KeyPath, login.KeyPassphrase));
        Assert.False(login.TakesTypedPassword);
    }

    [Fact]
    public void Login_ServerWithNothingSaved_NeedsASecret() =>
        Assert.True(Login(Server(user: "root", port: 22)).NeedsSecret);

    [Fact]
    public void Login_ServerThatUsesTheAgent_NeedsNoSecret()
    {
        HostEntry host = Server(user: "root", port: 22);
        host.UseAgent = true;

        Assert.False(Login(host).NeedsSecret);
    }

    [Fact]
    public void Resolve_PortLeftEmpty_IsTheUsualOne() =>
        Assert.Equal(22, Resolve(Server(user: "root")).Port);

    [Fact]
    public void Resolve_ServerThatIsNotSsh_TakesNothingFromItsGroup()
    {
        HostGroup group = Group("lab");
        (group.User, group.Port, group.IdentityId, group.UseMosh) = ("deploy", 2222, PasswordIdentity("ops", "ops").Id, true);
        group.Proxy = new ProxySettings { Kind = ProxyKind.Socks5, Host = "proxy", Port = 1080 };
        HostEntry telnet = Server(group.Id, port: 23);
        telnet.Kind = HostKind.Telnet;

        ResolvedHost resolved = Resolve(telnet);

        Assert.Equal((string.Empty, 23, false), (resolved.User, resolved.Port, resolved.UseMosh));
        Assert.Null(resolved.Identity);
        Assert.Null(resolved.Proxy);
    }

    // An identity on the server itself.

    [Fact]
    public void Login_ServerWithAPasswordIdentity_TakesUserAndPasswordFromIt()
    {
        HostEntry host = Server(user: "root", port: 22);
        host.KeyPath = "/own/key";
        _vault[host.Id] = "own-password";
        host.IdentityId = PasswordIdentity("ops", "deploy").Id;

        ResolvedLogin login = Login(host);

        Assert.Equal(("deploy", "identity-password", null, null), (login.User, login.Password, login.KeyPath, login.KeyText));
        Assert.Equal("ops", login.Identity?.Name);
        Assert.False(login.TakesTypedPassword);
    }

    [Fact]
    public void Login_ServerWithAKeyIdentity_TakesTheKeyTextAndPassphraseFromIt()
    {
        HostEntry host = Server(user: "root", port: 22);
        host.IdentityId = KeyIdentity("laptop", "deploy", "secret words").Id;

        ResolvedLogin login = Login(host);

        Assert.Equal(("deploy", null, "KEY TEXT", "secret words"), (login.User, login.Password, login.KeyText, login.KeyPassphrase));
        Assert.True(login.HasKey);
    }

    [Fact]
    public void Resolve_IdentityWithoutAUserName_LeavesTheUserToTheServer()
    {
        HostEntry host = Server(user: "root", port: 22);
        host.IdentityId = KeyIdentity("laptop", string.Empty).Id;

        Assert.Equal("root", Resolve(host).User);
        Assert.Equal("KEY TEXT", Login(host).KeyText);
    }

    [Fact]
    public void Resolve_IdentityThatNoLongerExists_FallsBackToTheServersOwnFields()
    {
        HostEntry host = Server(user: "root", port: 22);
        host.IdentityId = Guid.NewGuid();
        _vault[host.Id] = "own-password";

        Assert.Null(Resolve(host).Identity);
        Assert.Equal(("root", "own-password"), (Login(host).User, Login(host).Password));
    }

    [Fact]
    public void Login_IdentityWhoseSecretIsNotOnThisDevice_KeepsItsUserAndUsesTheServersOwnSecret()
    {
        // What a device sees that syncs without passwords, or after importing a file.
        HostEntry host = Server(user: "root", port: 22);
        host.KeyPath = "/own/key";
        _vault[host.Id] = "own-passphrase";
        host.IdentityId = PasswordIdentity("ops", "deploy", password: null).Id;

        ResolvedLogin login = Login(host);

        Assert.Equal(("deploy", "/own/key", "own-passphrase"), (login.User, login.KeyPath, login.KeyPassphrase));
        Assert.Null(login.Identity);
    }

    [Fact]
    public void Login_IdentityWithNoSecretAndAServerWithNone_AsksForAPasswordThatWillBeUsed()
    {
        HostEntry host = Server(port: 22);
        host.IdentityId = PasswordIdentity("ops", "deploy", password: null).Id;

        ResolvedLogin login = Login(host);

        Assert.True(login.NeedsSecret);
        Assert.True(login.TakesTypedPassword);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"Password\": null, \"PrivateKey\": \"\"}")]
    [InlineData("[]")]
    public void Login_IdentityWhoseVaultEntryIsUnreadableOrEmpty_IsTreatedAsHavingNoSecret(string stored)
    {
        HostEntry host = Server(user: "root", port: 22);
        Identity identity = PasswordIdentity("ops", "deploy", password: null);
        _vault[identity.Id] = stored;
        _vault[host.Id] = "own-password";
        host.IdentityId = identity.Id;

        Assert.Equal("own-password", Login(host).Password);
    }

    // Group defaults.

    [Fact]
    public void Resolve_EmptyFieldsOnTheServer_ComeFromItsGroup()
    {
        HostEntry bastion = Server(user: "jump", port: 22, name: "bastion");
        HostGroup group = Group("prod");
        (group.User, group.Port, group.UseAgent, group.UseMosh) = ("deploy", 2222, true, true);
        group.JumpHosts = [bastion.Id];
        HostEntry host = Server(group.Id);

        ResolvedHost resolved = Resolve(host);

        Assert.Equal(("deploy", 2222, true, true), (resolved.User, resolved.Port, resolved.UseAgent, resolved.UseMosh));
        Assert.Equal([bastion.Id], resolved.JumpHosts);
        Assert.Same(group, resolved.UserFrom);
        Assert.Same(group, resolved.PortFrom);
        Assert.Same(group, resolved.JumpHostsFrom);
        Assert.Same(group, resolved.AgentFrom);
        Assert.Same(group, resolved.MoshFrom);
    }

    [Fact]
    public void Resolve_FieldsTheServerSetsItself_WinOverItsGroup()
    {
        HostEntry bastion = Server(user: "jump", port: 22, name: "bastion");
        HostEntry other = Server(user: "jump", port: 22, name: "other");
        HostGroup group = Group("prod");
        (group.User, group.Port) = ("deploy", 2222);
        group.JumpHosts = [bastion.Id];
        HostEntry host = Server(group.Id, user: "root", port: 22);
        host.JumpHosts = [other.Id];

        ResolvedHost resolved = Resolve(host);

        Assert.Equal(("root", 22), (resolved.User, resolved.Port));
        Assert.Equal([other.Id], resolved.JumpHosts);
        Assert.Null(resolved.UserFrom);
        Assert.Null(resolved.PortFrom);
        Assert.Null(resolved.JumpHostsFrom);
    }

    [Fact]
    public void Resolve_NestedGroups_TheNearestThatSaysAnythingWins()
    {
        HostGroup company = Group("company");
        (company.User, company.Port) = ("admin", 2200);
        HostGroup prod = Group("prod", company);
        prod.User = "deploy";
        HostGroup web = Group("web", prod);
        HostEntry host = Server(web.Id);

        ResolvedHost resolved = Resolve(host);

        Assert.Equal(("deploy", 2200), (resolved.User, resolved.Port));
        Assert.Same(prod, resolved.UserFrom);
        Assert.Same(company, resolved.PortFrom);
    }

    [Fact]
    public void Resolve_GroupThatSaysNoToTheAgent_OverrulesTheGroupAboveIt_ButNotTheServer()
    {
        HostGroup company = Group("company");
        company.UseAgent = true;
        HostGroup prod = Group("prod", company);
        prod.UseAgent = false;
        HostEntry plain = Server(prod.Id, user: "root");
        HostEntry own = Server(prod.Id, user: "root", name: "own");
        own.UseAgent = true;

        Assert.False(Resolve(plain).UseAgent);
        Assert.Null(Resolve(plain).AgentFrom);
        Assert.True(Resolve(own).UseAgent);
        Assert.True(Resolve(Server(company.Id, user: "root", name: "top")).UseAgent);
    }

    [Fact]
    public void Resolve_GroupIdentity_SuppliesTheLoginToServersWithoutOneOfTheirOwn()
    {
        HostGroup group = Group("prod");
        group.IdentityId = KeyIdentity("laptop", "deploy").Id;
        HostEntry host = Server(group.Id);
        _vault[host.Id] = "a password saved before the group had an identity";

        ResolvedLogin login = Login(host);

        Assert.Equal(("deploy", "KEY TEXT", null), (login.User, login.KeyText, login.Password));
        Assert.Same(group, Resolve(host).IdentityFrom);
        Assert.Same(group, Resolve(host).UserFrom);
    }

    [Fact]
    public void Resolve_ServerWithItsOwnIdentity_IgnoresTheGroupsIdentity()
    {
        HostGroup group = Group("prod");
        group.IdentityId = KeyIdentity("laptop", "deploy").Id;
        HostEntry host = Server(group.Id);
        host.IdentityId = PasswordIdentity("ops", "ops").Id;

        Assert.Equal("ops", Resolve(host).Identity?.Name);
        Assert.Null(Resolve(host).IdentityFrom);
        Assert.Equal(("ops", "identity-password"), (Login(host).User, Login(host).Password));
    }

    [Fact]
    public void Resolve_ServerWithItsOwnKeyFile_IgnoresTheGroupsIdentity_ButStillTakesTheGroupsUser()
    {
        HostGroup group = Group("prod");
        group.IdentityId = KeyIdentity("laptop", "deploy").Id;
        group.User = "fallback";
        HostEntry host = Server(group.Id);
        host.KeyPath = "/own/key";

        ResolvedLogin login = Login(host);

        Assert.Null(Resolve(host).Identity);
        Assert.Equal(("fallback", "/own/key", null), (login.User, login.KeyPath, login.KeyText));
    }

    [Fact]
    public void Resolve_ServersOwnUserName_WinsOverTheGroupIdentitysUser()
    {
        HostGroup group = Group("prod");
        group.IdentityId = KeyIdentity("laptop", "deploy").Id;
        HostEntry host = Server(group.Id, user: "root");

        Assert.Equal("root", Resolve(host).User);
        Assert.Equal("KEY TEXT", Login(host).KeyText);
    }

    [Fact]
    public void Resolve_NearerGroupsUser_WinsOverAnIdentityFromFurtherUp()
    {
        HostGroup company = Group("company");
        company.IdentityId = KeyIdentity("laptop", "deploy").Id;
        HostGroup prod = Group("prod", company);
        prod.User = "admin";
        HostEntry host = Server(prod.Id);

        ResolvedHost resolved = Resolve(host);

        Assert.Equal("admin", resolved.User);
        Assert.Equal("laptop", resolved.Identity?.Name);
        Assert.Same(company, resolved.IdentityFrom);
        Assert.Same(prod, resolved.UserFrom);
    }

    [Fact]
    public void Resolve_WithinOneGroup_ItsIdentitysUserComesBeforeItsPlainUser()
    {
        HostGroup group = Group("prod");
        group.IdentityId = KeyIdentity("laptop", "deploy").Id;
        group.User = "plain";

        Assert.Equal("deploy", Resolve(Server(group.Id)).User);
    }

    [Fact]
    public void Resolve_NearerGroupsIdentity_WinsOverOneFurtherUp()
    {
        HostGroup company = Group("company");
        company.IdentityId = KeyIdentity("company-key", "everyone").Id;
        HostGroup prod = Group("prod", company);
        prod.IdentityId = KeyIdentity("prod-key", "deploy").Id;

        ResolvedHost resolved = Resolve(Server(prod.Id));

        Assert.Equal(("prod-key", "deploy"), (resolved.Identity?.Name, resolved.User));
    }

    [Fact]
    public void Resolve_GroupIdentityThatNoLongerExists_IsPassedOverForTheNextGroupUp()
    {
        HostGroup company = Group("company");
        company.IdentityId = KeyIdentity("company-key", "everyone").Id;
        HostGroup prod = Group("prod", company);
        prod.IdentityId = Guid.NewGuid();

        Assert.Equal("company-key", Resolve(Server(prod.Id)).Identity?.Name);
    }

    [Fact]
    public void Resolve_ServerInAGroupThatDoesNotExist_ConnectsWithItsOwnFields()
    {
        HostEntry host = Server(Guid.NewGuid(), user: "root", port: 22);

        Assert.Equal(("root", 22), (Resolve(host).User, Resolve(host).Port));
    }

    [Fact]
    public void Resolve_GroupWhoseParentDoesNotExist_StillGivesItsOwnDefaults()
    {
        HostGroup group = Group("prod");
        group.ParentId = Guid.NewGuid();
        group.User = "deploy";

        Assert.Equal("deploy", Resolve(Server(group.Id)).User);
    }

    [Fact]
    public void Resolve_GroupsThatContainEachOther_DoNotHang()
    {
        HostGroup a = Group("a");
        HostGroup b = Group("b", a);
        HostGroup c = Group("c", b);
        a.ParentId = c.Id;
        b.User = "deploy";
        a.Port = 2222;

        ResolvedHost resolved = Resolve(Server(c.Id));

        Assert.Equal(("deploy", 2222), (resolved.User, resolved.Port));
        Assert.Equal([c, b, a], ConnectionResolver.GroupChain(c.Id, _library));
    }

    [Fact]
    public void Resolve_GroupThatIsItsOwnParent_DoesNotHang()
    {
        HostGroup group = Group("loop");
        group.ParentId = group.Id;
        group.User = "deploy";

        Assert.Equal("deploy", Resolve(Server(group.Id)).User);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(70000)]
    public void Resolve_GroupPortThatIsNotAPort_IsIgnored(int port)
    {
        HostGroup group = Group("prod");
        group.Port = port;

        Assert.Equal(22, Resolve(Server(group.Id)).Port);
    }

    [Fact]
    public void Resolve_BlankUserNames_CountAsNotSet()
    {
        HostGroup group = Group("prod");
        group.User = "   ";
        HostEntry host = Server(group.Id, user: "  ");

        Assert.Equal(string.Empty, Resolve(host).User);
    }

    // Jump hosts from a group.

    [Fact]
    public void Resolve_ServerThatIsTheGroupsJumpHost_IsReachedDirectly()
    {
        HostGroup group = Group("prod");
        HostEntry bastion = Server(group.Id, user: "jump", name: "bastion");
        group.JumpHosts = [bastion.Id];

        Assert.Empty(Resolve(bastion).JumpHosts);
        Assert.Equal([bastion.Id], Resolve(Server(group.Id, user: "root")).JumpHosts);
    }

    [Fact]
    public void Resolve_ServerInTheMiddleOfTheGroupsJumpHosts_IsReachedThroughTheOnesBeforeIt()
    {
        HostGroup group = Group("prod");
        HostEntry outer = Server(group.Id, user: "jump", name: "outer");
        HostEntry inner = Server(group.Id, user: "jump", name: "inner");
        group.JumpHosts = [outer.Id, inner.Id];

        Assert.Empty(Resolve(outer).JumpHosts);
        Assert.Equal([outer.Id], Resolve(inner).JumpHosts);
    }

    // Proxies.

    private static ProxySettings Socks(string host = "proxy.example.com", string user = "") =>
        new() { Kind = ProxyKind.Socks5, Host = host, Port = 1080, User = user };

    [Fact]
    public void Resolve_ServerProxy_IsItsOwnAndFiledUnderIt()
    {
        HostEntry host = Server(user: "root");
        host.Proxy = Socks();

        ResolvedHost resolved = Resolve(host);

        Assert.Same(host.Proxy, resolved.Proxy);
        Assert.Equal(host.Id, resolved.ProxyOwner);
        Assert.Null(resolved.ProxyFrom);
    }

    [Fact]
    public void Resolve_GroupProxy_AppliesToItsServers_AndIsFiledUnderTheGroup()
    {
        HostGroup company = Group("company");
        company.Proxy = Socks();
        HostGroup prod = Group("prod", company);

        ResolvedHost resolved = Resolve(Server(prod.Id, user: "root"));

        Assert.Same(company.Proxy, resolved.Proxy);
        Assert.Equal(company.Id, resolved.ProxyOwner);
        Assert.Same(company, resolved.ProxyFrom);
    }

    [Fact]
    public void Resolve_ServerThatSaysDirect_OverrulesItsGroupsProxy()
    {
        HostGroup group = Group("prod");
        group.Proxy = Socks();
        HostEntry host = Server(group.Id, user: "root");
        host.Proxy = new ProxySettings { Kind = ProxyKind.None };

        Assert.Null(Resolve(host).Proxy);
        Assert.Null(Resolve(host).ProxyOwner);
    }

    [Fact]
    public void Resolve_ProxyWithoutAnAddress_IsNoProxy()
    {
        HostEntry host = Server(user: "root");
        host.Proxy = Socks(host: " ");

        Assert.Null(Resolve(host).Proxy);
    }

    [Fact]
    public void ProxySecretId_IsTheSameEveryTime_AndNeverTheOwnersOwnId()
    {
        var owner = Guid.NewGuid();

        Assert.Equal(ProxySettings.SecretId(owner), ProxySettings.SecretId(owner));
        Assert.NotEqual(owner, ProxySettings.SecretId(owner));
        Assert.NotEqual(ProxySettings.SecretId(owner), ProxySettings.SecretId(Guid.NewGuid()));
    }

    // Switching a server to an identity after its key was installed.

    [Fact]
    public void UseIdentity_IdentityWithoutAUser_KeepsTheAccountTheServerLoggedInTo()
    {
        HostEntry host = Server(port: 22);
        host.IdentityId = PasswordIdentity("ops", "deploy").Id;
        Identity key = KeyIdentity("laptop", string.Empty);

        Assert.True(ConnectionResolver.UseIdentity(host, key, _library));

        Assert.Equal(key.Id, host.IdentityId);
        Assert.Equal("deploy", Resolve(host).User);
        Assert.Equal("KEY TEXT", Login(host).KeyText);
    }

    [Fact]
    public void UseIdentity_IdentityForTheSameUser_LeavesTheServersOwnUserAlone()
    {
        HostEntry host = Server(user: "root", port: 22);
        Identity key = KeyIdentity("laptop", "root");

        Assert.True(ConnectionResolver.UseIdentity(host, key, _library));
        Assert.Equal(("root", key.Id), (host.User, host.IdentityId));
    }

    [Fact]
    public void UseIdentity_IdentityForAnotherUser_ChangesNothing()
    {
        HostEntry host = Server(user: "root", port: 22);
        Identity key = KeyIdentity("laptop", "deploy");

        Assert.False(ConnectionResolver.CanUseIdentity(host, key, _library));
        Assert.False(ConnectionResolver.UseIdentity(host, key, _library));
        Assert.Null(host.IdentityId);
    }

    // The endpoints a connection is made with.

    [Fact]
    public void ForPath_GivesEachServerItsOwnLogin_InOrder()
    {
        HostGroup group = Group("prod");
        (group.User, group.Port) = ("deploy", 2222);
        HostEntry bastion = Server(user: "jump", port: 22, name: "bastion");
        _vault[bastion.Id] = "bastion-password";
        HostEntry host = Server(group.Id);
        host.IdentityId = KeyIdentity("laptop", string.Empty, "words").Id;

        List<SshEndpoint> path = SshEndpoints.ForPath([Resolve(bastion), Resolve(host)], SecretOf);

        Assert.Equal(new SshEndpoint("bastion.example.com", 22, "jump", "bastion-password"), path[0]);
        Assert.Equal(new SshEndpoint("web.example.com", 2222, "deploy", null, null, "words", false, "KEY TEXT"), path[1]);
    }

    [Fact]
    public void ForPath_ProxyOfTheServerBeingOpened_GoesOnTheFirstHopOnly_WithItsPassword()
    {
        HostEntry bastion = Server(user: "jump", port: 22, name: "bastion");
        HostEntry host = Server(user: "root", port: 22);
        host.Proxy = Socks(user: "me");
        _vault[ProxySettings.SecretId(host.Id)] = "proxy-password";

        List<SshEndpoint> path = SshEndpoints.ForPath([Resolve(bastion), Resolve(host)], SecretOf);

        Assert.Equal(new SshProxy(ProxyKind.Socks5, "proxy.example.com", 1080, "me", "proxy-password"), path[0].Proxy);
        Assert.Null(path[1].Proxy);
    }

    [Fact]
    public void ForPath_NoProxyOnTheServer_UsesTheFirstJumpHostsOwn()
    {
        HostGroup group = Group("edge");
        group.Proxy = new ProxySettings { Kind = ProxyKind.Http, Host = "corp-proxy", Port = 3128 };
        HostEntry bastion = Server(group.Id, user: "jump", port: 22, name: "bastion");
        HostEntry host = Server(user: "root", port: 22);
        _vault[ProxySettings.SecretId(group.Id)] = "never asked for: the proxy has no user";

        List<SshEndpoint> path = SshEndpoints.ForPath([Resolve(bastion), Resolve(host)], SecretOf);

        Assert.Equal(new SshProxy(ProxyKind.Http, "corp-proxy", 3128), path[0].Proxy);
    }

    [Fact]
    public void ForPath_NoProxyAnywhere_ConnectsDirectly() =>
        Assert.Null(Assert.Single(SshEndpoints.ForPath([Resolve(Server(user: "root", port: 22))], SecretOf)).Proxy);

    [Fact]
    public void Endpoint_Printed_ShowsNoPasswordKeyOrProxyPassword()
    {
        var endpoint = new SshEndpoint("web", 22, "root", "PASSWORD", null, "PASSPHRASE", false, "KEYTEXT", new SshProxy(ProxyKind.Socks5, "proxy", 1080, "me", "PROXYPASS"));

        string printed = endpoint.ToString() + endpoint.Proxy;

        Assert.DoesNotContain("PASSWORD", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("PASSPHRASE", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("KEYTEXT", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("PROXYPASS", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentitySecret_WhatWriteProduced_ReadsBack()
    {
        IdentitySecret? read = IdentitySecret.Read(new IdentitySecret { PrivateKey = "-----BEGIN\nline\n-----END\n", Passphrase = "p\"q" }.Write());

        Assert.Equal(("-----BEGIN\nline\n-----END\n", "p\"q", null), (read?.PrivateKey, read?.Passphrase, read?.Password));
        Assert.Null(IdentitySecret.Read(null));
        Assert.Null(IdentitySecret.Read(string.Empty));
    }
}

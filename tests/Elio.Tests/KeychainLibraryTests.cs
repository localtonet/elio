using Elio.Core;
using Elio.Sync;

namespace Elio.Tests;

/// <summary>Identities and group defaults on their way through a file, an import and a sync.</summary>
public sealed class KeychainLibraryTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);

    private static bool NoSecrets(Identity identity) => false;

    private static ProxySettings Socks() => new() { Kind = ProxyKind.Socks5, Host = "proxy.example.com", Port = 1080, User = "me" };

    // The file.

    [Fact]
    public void File_Written_CarriesIdentitiesAndGroupDefaults_ButNotWhatWasDeleted()
    {
        var identity = new Identity { Name = "laptop", User = "deploy", Kind = IdentityKind.Key, PublicKey = "ssh-ed25519 AAAA laptop" };
        var host = new HostEntry { Name = "web", Address = "web.example.com", IdentityId = identity.Id, Proxy = Socks() };
        var group = new HostGroup { Name = "prod", IdentityId = identity.Id, User = "deploy", Port = 2222, JumpHosts = [host.Id], UseAgent = true, UseMosh = false, Proxy = Socks() };
        var library = new HostLibrary { Hosts = [host], Groups = [group], Identities = [identity], Deleted = { [Guid.NewGuid()] = Now } };

        HostLibrary read = LibraryFile.Read(LibraryFile.Write(library))!;

        Identity readIdentity = Assert.Single(read.Identities);
        Assert.Equal((identity.Id, "laptop", "deploy", IdentityKind.Key, "ssh-ed25519 AAAA laptop"), (readIdentity.Id, readIdentity.Name, readIdentity.User, readIdentity.Kind, readIdentity.PublicKey));

        HostEntry readHost = Assert.Single(read.Hosts);
        Assert.Equal(identity.Id, readHost.IdentityId);
        Assert.Equal((ProxyKind.Socks5, "proxy.example.com", 1080, "me"), (readHost.Proxy!.Kind, readHost.Proxy.Host, readHost.Proxy.Port, readHost.Proxy.User));

        HostGroup readGroup = Assert.Single(read.Groups);
        Assert.Equal((identity.Id, "deploy", 2222, true, false), (readGroup.IdentityId, readGroup.User, readGroup.Port, readGroup.UseAgent, readGroup.UseMosh));
        Assert.Equal([host.Id], readGroup.JumpHosts);
        Assert.Equal("proxy.example.com", readGroup.Proxy?.Host);
        Assert.Empty(read.Deleted);
    }

    [Fact]
    public void File_Written_HasNoPlaceASecretCouldBeIn()
    {
        // The identity type has no field for a password or a private key, and the proxy type none for its
        // password, so there is nothing a file could carry. This keeps it that way.
        string[] secretive = ["Password", "PrivateKey", "Passphrase", "Secret"];

        Assert.DoesNotContain(typeof(Identity).GetProperties(), property => secretive.Any(word => property.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(typeof(ProxySettings).GetProperties(), property => secretive.Any(word => property.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void File_FromBeforeIdentities_StillReads_WithNothingInherited()
    {
        const string text = """
            {
              "SchemaVersion": 1,
              "Hosts": [{"Name": "web", "Address": "web.example.com", "Port": 22, "User": "root", "KeyPath": "~/.ssh/id", "UseAgent": true}],
              "Groups": [{"Name": "prod", "Collapsed": true}],
              "Snippets": []
            }
            """;

        HostLibrary read = LibraryFile.Read(text)!;

        Assert.Empty(read.Identities);
        HostEntry host = Assert.Single(read.Hosts);
        Assert.Null(host.IdentityId);
        Assert.Null(host.Proxy);
        HostGroup group = Assert.Single(read.Groups);
        Assert.Equal((null, null, null, null, null, null), (group.IdentityId, group.User, group.Port, group.UseAgent, group.UseMosh, group.Proxy));
        Assert.Empty(group.JumpHosts);

        // And such a server connects exactly as it says.
        ResolvedHost resolved = ConnectionResolver.Resolve(host, read);
        Assert.Equal(("root", 22, true), (resolved.User, resolved.Port, resolved.UseAgent));
    }

    [Fact]
    public void File_WithNullsWhereTheNewListsAndTextsBelong_IsPutRight()
    {
        const string text = """
            {
              "Hosts": [{"Name": "web", "Proxy": {"Kind": 1, "Host": null, "User": null}}],
              "Groups": [{"Name": "prod", "JumpHosts": null, "Proxy": {"Kind": 2, "Host": null, "User": null}}],
              "Snippets": [],
              "Identities": [null, {"Name": null}, {"Name": "ops", "User": null}]
            }
            """;

        HostLibrary read = LibraryFile.Read(text)!;

        Identity identity = Assert.Single(read.Identities);
        Assert.Equal(("ops", string.Empty), (identity.Name, identity.User));
        Assert.Empty(read.Groups[0].JumpHosts);
        Assert.Equal((string.Empty, string.Empty), (read.Groups[0].Proxy!.Host, read.Groups[0].Proxy!.User));
        Assert.Equal((string.Empty, string.Empty), (read.Hosts[0].Proxy!.Host, read.Hosts[0].Proxy!.User));
    }

    [Fact]
    public void File_WithIdentitiesSetToNull_StillReads() =>
        Assert.Empty(LibraryFile.Read("{\"Hosts\": [], \"Groups\": [], \"Snippets\": [], \"Identities\": null}")!.Identities);

    [Fact]
    public void ActsOnItsOwn_GroupThatOnlyNamesAUserPortOrIdentity_IsFalse() =>
        Assert.False(LibraryFile.ActsOnItsOwn(new HostGroup { Name = "prod", User = "deploy", Port = 2222, IdentityId = Guid.NewGuid(), UseAgent = true, UseMosh = true }));

    [Fact]
    public void ActsOnItsOwn_GroupWithJumpHostsOrAProxy_IsTrue()
    {
        Assert.True(LibraryFile.ActsOnItsOwn(new HostGroup { Name = "prod", JumpHosts = [Guid.NewGuid()] }));
        Assert.True(LibraryFile.ActsOnItsOwn(new HostGroup { Name = "prod", Proxy = Socks() }));
        Assert.False(LibraryFile.ActsOnItsOwn(new HostGroup { Name = "prod", Proxy = new ProxySettings { Kind = ProxyKind.None } }));
    }

    [Fact]
    public void CountActing_CountsServersAndGroups_AndMakePlainLeavesNone()
    {
        var library = new HostLibrary
        {
            Hosts = [new HostEntry { Name = "a", ForwardAgent = true }, new HostEntry { Name = "b" }],
            Groups = [new HostGroup { Name = "prod", JumpHosts = [Guid.NewGuid()], User = "deploy" }, new HostGroup { Name = "edge", Proxy = Socks(), Port = 2222 }, new HostGroup { Name = "plain" }],
        };

        Assert.Equal(3, LibraryFile.CountActing(library));

        LibraryFile.MakePlain(library);

        Assert.Equal(0, LibraryFile.CountActing(library));
        Assert.All(library.Groups, group => Assert.Empty(group.JumpHosts));
        Assert.All(library.Groups, group => Assert.Null(group.Proxy));

        // What does not route anything stays.
        Assert.Equal(("deploy", 2222), (library.Groups[0].User, library.Groups[1].Port));
    }

    // Importing a file.

    private static HostLibrary FileWith(out HostEntry host, out HostGroup group, out Identity identity)
    {
        identity = new Identity { Name = "ops", User = "deploy", Kind = IdentityKind.Password };
        group = new HostGroup { Name = "prod", User = "deploy", Port = 2222, UseAgent = true, Proxy = Socks() };
        var bastion = new HostEntry { Name = "bastion", Address = "bastion.example.com", Port = 22, User = "jump", GroupId = group.Id };
        host = new HostEntry { Name = "web", Address = "web.example.com", Port = 22, User = "root", GroupId = group.Id, IdentityId = identity.Id };
        group.JumpHosts = [bastion.Id, Guid.NewGuid()];
        group.IdentityId = identity.Id;
        return new HostLibrary { Hosts = [bastion, host], Groups = [group], Identities = [identity] };
    }

    [Fact]
    public void Merge_FileIdentity_ArrivesUnderANewId_AndTheServerAndGroupPointToThatOne()
    {
        var library = new HostLibrary();
        HostLibrary file = FileWith(out HostEntry host, out HostGroup group, out Identity identity);
        Guid idInFile = identity.Id;

        Assert.Equal(2, LibraryMerge.Merge(library, file, NoSecrets));

        Identity here = Assert.Single(library.Identities);
        Assert.NotEqual(idInFile, here.Id);
        Assert.Equal(("ops", "deploy", IdentityKind.Password), (here.Name, here.User, here.Kind));
        Assert.Equal(here.Id, library.Hosts.Single(h => h.Name == "web").IdentityId);
        Assert.Equal(here.Id, Assert.Single(library.Groups).IdentityId);
        Assert.NotEqual(group.Id, library.Groups[0].Id);
        Assert.NotEqual(Guid.Empty, host.Id);
    }

    [Fact]
    public void Merge_FileThatNamesAnIdentityAlreadyHereById_DoesNotGetIt()
    {
        // The file guesses, or knows, the id of an identity whose password is saved on this device.
        var mine = new Identity { Name = "ops", User = "deploy", Kind = IdentityKind.Password };
        var library = new HostLibrary { Identities = [mine] };
        var file = new HostLibrary
        {
            Hosts = [new HostEntry { Name = "evil", Address = "evil.example.com", Port = 22, User = "root", IdentityId = mine.Id }],
            Groups = [new HostGroup { Name = "trap", IdentityId = mine.Id }],
        };

        LibraryMerge.Merge(library, file, _ => true);

        Assert.Null(Assert.Single(library.Hosts).IdentityId);
        Assert.Null(Assert.Single(library.Groups).IdentityId);
        Assert.Same(mine, Assert.Single(library.Identities));
    }

    [Fact]
    public void Merge_FileIdentityThatLooksLikeOneAlreadyHere_IsNeverMatchedToIt()
    {
        var mine = new Identity { Name = "ops", User = "deploy", Kind = IdentityKind.Password };
        var library = new HostLibrary { Identities = [mine] };
        var twin = new Identity { Id = mine.Id, Name = "ops", User = "deploy", Kind = IdentityKind.Password };
        var file = new HostLibrary
        {
            Hosts = [new HostEntry { Name = "evil", Address = "evil.example.com", Port = 22, User = "root", IdentityId = twin.Id }],
            Identities = [twin],
        };

        LibraryMerge.Merge(library, file, _ => true);

        Guid? used = Assert.Single(library.Hosts).IdentityId;
        Assert.NotNull(used);
        Assert.NotEqual(mine.Id, used);
        Assert.Equal(2, library.Identities.Count);
    }

    [Fact]
    public void Merge_PlainListOfServers_NeverKeepsAnIdentityId()
    {
        var mine = new Identity { Name = "ops", User = "deploy" };
        var library = new HostLibrary { Identities = [mine] };

        LibraryMerge.Merge(library, [new HostEntry { Name = "evil", Address = "evil.example.com", Port = 22, User = "root", IdentityId = mine.Id }]);

        Assert.Null(Assert.Single(library.Hosts).IdentityId);
    }

    [Fact]
    public void Merge_NewGroup_BringsItsDefaults_WithJumpHostsRemappedAndStrangersDropped()
    {
        var library = new HostLibrary();
        HostLibrary file = FileWith(out _, out _, out _);

        LibraryMerge.Merge(library, file, NoSecrets);

        HostGroup group = Assert.Single(library.Groups);
        Assert.Equal(("deploy", 2222, true, null), (group.User, group.Port, group.UseAgent, group.UseMosh));
        Assert.Equal("proxy.example.com", group.Proxy?.Host);
        Assert.Equal([library.Hosts.Single(h => h.Name == "bastion").Id], group.JumpHosts);
    }

    [Fact]
    public void Merge_GroupAlreadyHere_KeepsItsOwnDefaults()
    {
        var existing = new HostGroup { Name = "prod", User = "mine", Port = 22 };
        var library = new HostLibrary { Groups = [existing] };
        HostLibrary file = FileWith(out _, out _, out _);

        LibraryMerge.Merge(library, file, NoSecrets);

        Assert.Same(existing, Assert.Single(library.Groups));
        Assert.Equal(("mine", 22, null, null), (existing.User, existing.Port, existing.UseAgent, existing.Proxy));
        Assert.Empty(existing.JumpHosts);
        Assert.Null(existing.IdentityId);
        Assert.All(library.Hosts, host => Assert.Equal(existing.Id, host.GroupId));
    }

    [Fact]
    public void Merge_GroupHereThatLendsASavedSecret_IsNotTheOneAFilesServersLandIn()
    {
        // Otherwise opening the file's server would send the group identity's password to the file's address.
        var mine = new Identity { Name = "ops", User = "deploy", Kind = IdentityKind.Password };
        var company = new HostGroup { Name = "company", IdentityId = mine.Id };
        var prod = new HostGroup { Name = "prod", ParentId = company.Id };
        var library = new HostLibrary { Groups = [company, prod], Identities = [mine] };

        var fileCompany = new HostGroup { Name = "company" };
        var fileProd = new HostGroup { Name = "prod", ParentId = fileCompany.Id };
        var file = new HostLibrary
        {
            Groups = [fileCompany, fileProd],
            Hosts = [new HostEntry { Name = "evil", Address = "evil.example.com", Port = 22, User = "root", GroupId = fileProd.Id }],
        };

        LibraryMerge.Merge(library, file, identity => identity == mine);

        HostEntry evil = Assert.Single(library.Hosts);
        Assert.Null(ConnectionResolver.Resolve(evil, library).Identity);
        Assert.NotEqual(prod.Id, evil.GroupId);
        Assert.Equal(4, library.Groups.Count);
    }

    [Fact]
    public void Merge_GroupHereWhoseIdentityHasNoSecretOnThisDevice_IsMatchedAsBefore()
    {
        var shell = new Identity { Name = "ops", User = "deploy" };
        var prod = new HostGroup { Name = "prod", IdentityId = shell.Id };
        var library = new HostLibrary { Groups = [prod], Identities = [shell] };
        var fileProd = new HostGroup { Name = "prod" };
        var file = new HostLibrary { Groups = [fileProd], Hosts = [new HostEntry { Name = "web", Address = "web.example.com", Port = 22, User = "root", GroupId = fileProd.Id }] };

        LibraryMerge.Merge(library, file, NoSecrets);

        Assert.Same(prod, Assert.Single(library.Groups));
        Assert.Equal(prod.Id, Assert.Single(library.Hosts).GroupId);
    }

    [Fact]
    public void Merge_SameFileTwice_AddsNothingTheSecondTime()
    {
        var library = new HostLibrary();

        LibraryMerge.Merge(library, FileWith(out _, out _, out _), NoSecrets);
        int added = LibraryMerge.Merge(library, FileWith(out _, out _, out _), NoSecrets);

        Assert.Equal(0, added);
        Assert.Equal((2, 1, 1), (library.Hosts.Count, library.Groups.Count, library.Identities.Count));
    }

    [Fact]
    public void Merge_ServerJumpHosts_AreStillRemappedAndStrangersDropped()
    {
        var library = new HostLibrary();
        var bastion = new HostEntry { Name = "bastion", Address = "bastion.example.com", Port = 22, User = "jump" };
        var web = new HostEntry { Name = "web", Address = "web.example.com", Port = 22, User = "root", JumpHosts = [bastion.Id, Guid.NewGuid()] };

        var placed = new Dictionary<Guid, Guid>();
        Guid bastionInFile = bastion.Id;
        Assert.Equal(2, LibraryMerge.Merge(library, [bastion, web], placed));

        Assert.Equal([bastion.Id], web.JumpHosts);
        Assert.NotEqual(bastionInFile, bastion.Id);
        Assert.Equal(bastion.Id, placed[bastionInFile]);
    }

    // Syncing.

    private static Identity Synced(Guid id, string name, DateTime modified) => new() { Id = id, Name = name, User = "deploy", ModifiedUtc = modified };

    [Fact]
    public void Sync_Identities_MergePerItem_NewerWins()
    {
        var shared = Guid.NewGuid();
        var local = new SyncDocument { Identities = [Synced(shared, "old name", Now), Synced(Guid.NewGuid(), "only here", Now)] };
        var remote = new SyncDocument { Identities = [Synced(shared, "new name", Now.AddMinutes(1)), Synced(Guid.NewGuid(), "only there", Now)] };

        SyncDocument merged = SyncMerge.Merge(local, remote, Now.AddHours(1));

        Assert.Equal(["new name", "only here", "only there"], merged.Identities.Select(i => i.Name).Order());
    }

    [Fact]
    public void Sync_IdentityDeletedOnOneDevice_StaysDeleted_UnlessChangedAfterwards()
    {
        var gone = Guid.NewGuid();
        var revived = Guid.NewGuid();
        var local = new SyncDocument { Identities = [Synced(gone, "gone", Now), Synced(revived, "revived", Now.AddMinutes(10))] };
        var remote = new SyncDocument { Deleted = { [gone] = Now.AddMinutes(5), [revived] = Now.AddMinutes(5) } };

        SyncDocument merged = SyncMerge.Merge(local, remote, Now.AddHours(1));

        Assert.Equal("revived", Assert.Single(merged.Identities).Name);
        Assert.True(merged.Deleted.ContainsKey(gone));
        Assert.False(merged.Deleted.ContainsKey(revived));
    }

    [Fact]
    public void Sync_DocumentFromBeforeIdentities_LosesNoneOfThisDevices()
    {
        var local = new SyncDocument { Identities = [Synced(Guid.NewGuid(), "mine", Now)] };
        var remote = new SyncDocument { Identities = null! };

        Assert.Equal("mine", Assert.Single(SyncMerge.Merge(local, remote, Now).Identities).Name);
    }

    [Fact]
    public void Sync_SealedAndOpened_KeepsIdentitiesGroupDefaultsAndProxies()
    {
        var identity = new Identity { Name = "laptop", User = "deploy", Kind = IdentityKind.Key, PublicKey = "ssh-ed25519 AAAA laptop" };
        var document = new SyncDocument
        {
            Identities = [identity],
            Hosts = [new HostEntry { Name = "web", IdentityId = identity.Id, Proxy = Socks() }],
            Groups = [new HostGroup { Name = "prod", IdentityId = identity.Id, User = "deploy", Port = 2222, JumpHosts = [Guid.NewGuid()], UseAgent = true, Proxy = Socks() }],
            Secrets = { [identity.Id] = new VaultEntry { Secret = new IdentitySecret { PrivateKey = "KEY" }.Write(), ModifiedUtc = Now } },
        };

        byte[] key = SyncCrypto.NewDataKey();
        SyncDocument opened = SyncCrypto.Open(key, SyncCrypto.Seal(key, document));

        Identity read = Assert.Single(opened.Identities);
        Assert.Equal((identity.Id, "laptop", IdentityKind.Key, "ssh-ed25519 AAAA laptop"), (read.Id, read.Name, read.Kind, read.PublicKey));
        Assert.Equal((identity.Id, ProxyKind.Socks5), (opened.Hosts[0].IdentityId, opened.Hosts[0].Proxy!.Kind));
        HostGroup group = Assert.Single(opened.Groups);
        Assert.Equal((identity.Id, "deploy", 2222, true), (group.IdentityId, group.User, group.Port, group.UseAgent));
        Assert.Single(group.JumpHosts);
        Assert.Equal("KEY", IdentitySecret.Read(opened.Secrets[identity.Id].Secret)?.PrivateKey);
    }
}

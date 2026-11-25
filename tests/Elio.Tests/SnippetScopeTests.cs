using Elio.Core;

namespace Elio.Tests;

public sealed class SnippetScopeTests
{
    private static (HostLibrary Library, HostEntry Web, HostEntry Db, HostGroup Production, HostGroup Europe) Example()
    {
        var europe = new HostGroup { Name = "Europe" };
        var production = new HostGroup { Name = "Production", ParentId = europe.Id };
        var web = new HostEntry { Name = "web-1", Kind = HostKind.Ssh, Address = "10.0.0.5", GroupId = production.Id };
        var db = new HostEntry { Name = "db-1", Kind = HostKind.Ssh, Address = "10.0.0.6" };
        return (new HostLibrary { Hosts = [web, db], Groups = [europe, production] }, web, db, production, europe);
    }

    [Fact]
    public void ASnippetForNothingInParticular_IsForEveryTerminal()
    {
        var (library, web, _, _, _) = Example();
        var snippet = new Snippet { Name = "uptime", Command = "uptime" };

        Assert.True(SnippetScope.AppliesTo(snippet, web, library));
        Assert.True(SnippetScope.AppliesTo(snippet, null, library));
    }

    [Fact]
    public void ASnippetForOneServer_IsOnlyForThatOne()
    {
        var (library, web, db, _, _) = Example();
        var snippet = new Snippet { Name = "restart nginx", Command = "sudo systemctl restart nginx", For = [web.Id] };

        Assert.True(SnippetScope.AppliesTo(snippet, web, library));
        Assert.False(SnippetScope.AppliesTo(snippet, db, library));
        Assert.False(SnippetScope.AppliesTo(snippet, null, library));
    }

    [Fact]
    public void ASnippetForAGroup_IsForTheServersInIt_HoweverDeep()
    {
        var (library, web, db, _, europe) = Example();
        var snippet = new Snippet { Name = "deploy", Command = "deploy", For = [europe.Id] };

        Assert.True(SnippetScope.AppliesTo(snippet, web, library));
        Assert.False(SnippetScope.AppliesTo(snippet, db, library));
    }

    [Fact]
    public void ALoopInTheGroups_DoesNotHang()
    {
        var a = new HostGroup { Name = "a" };
        var b = new HostGroup { Name = "b", ParentId = a.Id };
        a.ParentId = b.Id;
        var host = new HostEntry { Name = "x", GroupId = a.Id };
        var library = new HostLibrary { Hosts = [host], Groups = [a, b] };

        Assert.False(SnippetScope.AppliesTo(new Snippet { For = [Guid.NewGuid()] }, host, library));
    }

    [Fact]
    public void Parse_FindsServersFirst_ThenGroups_AndSaysWhatItCouldNot()
    {
        var (library, web, _, production, _) = Example();

        (List<Guid> ids, List<string> unknown) = SnippetScope.Parse("WEB-1, production, nowhere, web-1", library);

        Assert.Equal([web.Id, production.Id], ids);
        Assert.Equal(["nowhere"], unknown);
    }

    [Fact]
    public void Names_LeavesOutWhatNoLongerExists()
    {
        var (library, web, _, production, _) = Example();
        var snippet = new Snippet { For = [web.Id, Guid.NewGuid(), production.Id] };

        Assert.Equal(["web-1", "Production"], SnippetScope.Names(snippet, library));
    }
}

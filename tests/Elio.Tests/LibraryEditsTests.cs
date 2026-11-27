using Elio.Core;
using Elio.Sync;

namespace Elio.Tests;

/// <summary>
/// A server edited in a dialog, or added while a sync is under way, has to end up in hosts.json, which is where the
/// AI tools connected through elio --mcp look for it. A sync replaces every item in the library with a new copy, and
/// that used to lose an edit made across it without a word.
/// </summary>
public sealed class LibraryEditsTests : TempDataDirectoryTest
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Earlier = Now.AddHours(-2);
    private static readonly DateTime Later = Now.AddHours(-1);

    private static HostEntry Host(string name, DateTime modified) =>
        new() { Kind = HostKind.Ssh, Name = name, Address = name + ".example.invalid", User = "root", ModifiedUtc = modified };

    // What a sync leaves in the library: the same items, as new objects.
    private static void ReplaceWithCopies(HostLibrary library)
    {
        SyncDocument copy = new SyncDocument { Hosts = library.Hosts, Snippets = library.Snippets }.Clone();
        library.Hosts.Clear();
        library.Hosts.AddRange(copy.Hosts);
        library.Snippets.Clear();
        library.Snippets.AddRange(copy.Snippets);
    }

    [Fact]
    public void EditedServer_IsSaved_EvenIfASyncReplacedTheListWhileTheDialogWasOpen()
    {
        var library = new HostLibrary { Hosts = [Host("web", Earlier), Host("db", Earlier)] };
        HostEntry opened = library.Hosts[0];
        ReplaceWithCopies(library);
        Assert.DoesNotContain(opened, library.Hosts);

        HostEntry edited = HostCopy.Of(opened);
        edited.AllowMcpCommands = true;
        edited.ModifiedUtc = Now;
        LibraryEdits.Replace(library, edited);
        Stores.Hosts().Save(library);

        HostLibrary saved = Stores.Hosts().Load();
        Assert.Equal(["db", "web"], saved.Hosts.Select(h => h.Name).Order());
        Assert.True(saved.Hosts.Single(h => h.Id == opened.Id).AllowMcpCommands);
    }

    [Fact]
    public void EditedServer_ThatASyncRemovedMeanwhile_ComesBackWithTheEdit()
    {
        HostEntry web = Host("web", Earlier);
        var library = new HostLibrary { Hosts = [Host("db", Earlier)], Deleted = { [web.Id] = Later } };

        HostEntry edited = HostCopy.Of(web);
        edited.AllowMcpCommands = true;
        edited.ModifiedUtc = Now;
        LibraryEdits.Replace(library, edited);

        Assert.True(library.Hosts.Single(h => h.Id == web.Id).AllowMcpCommands);
        Assert.False(library.Deleted.ContainsKey(web.Id));
    }

    [Fact]
    public void EditedSnippet_LandsInPlace_EvenIfASyncReplacedTheList()
    {
        var library = new HostLibrary { Snippets = [new Snippet { Name = "disk", Command = "df -h", ModifiedUtc = Earlier }, new Snippet { Name = "load", Command = "uptime", ModifiedUtc = Earlier }] };
        Snippet opened = library.Snippets[0];
        ReplaceWithCopies(library);

        LibraryEdits.Replace(library, new Snippet { Id = opened.Id, Name = "disk", Command = "df -hT", ModifiedUtc = Now });

        Assert.Equal(2, library.Snippets.Count);
        Assert.Equal("df -hT", library.Snippets[0].Command);
    }

    [Fact]
    public void ServerAddedOrEditedWhileASyncWasUnderWay_SurvivesBeingMergedAgain()
    {
        // The sync took its copy of the library and asked the server, which had changes of its own.
        HostEntry a = Host("a", Earlier), b = Host("b", Earlier), d = Host("d", Earlier);
        var captured = new SyncDocument { Hosts = [a, b, d] }.Clone();

        HostEntry renamed = HostCopy.Of(a);
        renamed.Name = "a-renamed-elsewhere";
        renamed.ModifiedUtc = Later;
        var remote = new SyncDocument { Hosts = [renamed, d, Host("c", Later)], Deleted = { [b.Id] = Later } }.Clone();
        SyncDocument merged = SyncMerge.Merge(captured, remote, Now);

        // Meanwhile, here: a server was added and another one given to the AI tools.
        SyncDocument live = captured.Clone();
        live.Hosts.Add(Host("new", Now));
        HostEntry allowed = live.Hosts.Single(h => h.Id == d.Id);
        allowed.AllowMcpCommands = true;
        allowed.ModifiedUtc = Now;

        SyncDocument applied = SyncMerge.Merge(live, merged, Now);

        Assert.Equal(["a-renamed-elsewhere", "c", "d", "new"], applied.Hosts.Select(h => h.Name).Order());
        Assert.True(applied.Hosts.Single(h => h.Id == d.Id).AllowMcpCommands);
        Assert.True(applied.Deleted.ContainsKey(b.Id));
    }

    [Fact]
    public async Task ReadingTheFile_FromAnotherProcess_DoesNotStopTheWindowSavingIt()
    {
        var library = new HostLibrary { Hosts = [Host("web", Earlier)] };
        Stores.Hosts().Save(library);

        // What elio --mcp does on every call, over and over, while the window keeps saving.
        using var stop = new CancellationTokenSource();
        Task reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Stores.Hosts().Load();
                }
                catch (IOException)
                {
                    // Caught in the moment between the old file going and the new one arriving. Only saving is tested here.
                }
            }
        });

        try
        {
            for (int i = 0; i < 300; i++)
            {
                library.Hosts[0].Name = "web-" + i;
                Stores.Hosts().Save(library);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await reader;
        }

        Assert.Equal("web-299", Assert.Single(Stores.Hosts().Load().Hosts).Name);
    }

    [Fact]
    public async Task ReadingTheVault_FromAnotherProcess_DoesNotStopTheWindowSavingAPassword()
    {
        Guid id = Guid.NewGuid();
        new Vault().Set(id, "first");

        // elio --mcp opens the vault for each command it runs, while the window may be saving a password.
        using var stop = new CancellationTokenSource();
        Task reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    new Vault().Get(id);
                }
                catch (IOException)
                {
                }
            }
        });

        var vault = new Vault();
        try
        {
            for (int i = 0; i < 100; i++) vault.Set(id, "password-" + i);
        }
        finally
        {
            await stop.CancelAsync();
            await reader;
        }

        Assert.Equal("password-99", new Vault().Get(id));
    }
}

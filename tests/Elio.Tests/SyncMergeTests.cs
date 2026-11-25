using System.Text.Json;
using Elio.Core;
using Elio.Sync;

namespace Elio.Tests;

/// <summary>
/// Combining two devices' copies item by item: the newer change wins, a removal is a change too, and every device
/// has to end up with the same result whichever of the two copies it calls its own.
/// </summary>
public sealed class SyncMergeTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Earlier = Now.AddHours(-2);
    private static readonly DateTime Later = Now.AddHours(-1);

    private static HostEntry Host(Guid id, string name, DateTime modified) => new() { Id = id, Name = name, Address = name + ".example.invalid", ModifiedUtc = modified };

    private static string Json(SyncDocument document) => JsonSerializer.Serialize(document);

    // The same content regardless of the order items are listed in.
    private static string Canonical(SyncDocument document)
    {
        document.Hosts = [.. document.Hosts.OrderBy(h => h.Id)];
        document.Groups = [.. document.Groups.OrderBy(g => g.Id)];
        document.Snippets = [.. document.Snippets.OrderBy(s => s.Id)];
        document.Identities = [.. document.Identities.OrderBy(i => i.Id)];
        document.Deleted = document.Deleted.OrderBy(p => p.Key).ToDictionary();
        document.Secrets = document.Secrets.OrderBy(p => p.Key).ToDictionary();
        document.SecretsDeleted = document.SecretsDeleted.OrderBy(p => p.Key).ToDictionary();
        document.KnownHosts = document.KnownHosts.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary();
        return Json(document);
    }

    [Fact]
    public void ConcurrentEdits_OfOneItem_TheLaterEditWins()
    {
        Guid id = Guid.NewGuid();
        var laptop = new SyncDocument { Hosts = [Host(id, "renamed-on-laptop", Earlier)] };
        var phone = new SyncDocument { Hosts = [Host(id, "renamed-on-phone", Later)] };

        Assert.Equal("renamed-on-phone", Assert.Single(SyncMerge.Merge(laptop, phone, Now).Hosts).Name);
        Assert.Equal("renamed-on-phone", Assert.Single(SyncMerge.Merge(phone, laptop, Now).Hosts).Name);
    }

    [Fact]
    public void EditsOfDifferentItems_AreBothKept()
    {
        Guid web = Guid.NewGuid(), db = Guid.NewGuid();
        var laptop = new SyncDocument { Hosts = [Host(web, "web-edited", Later), Host(db, "db", Earlier)], Snippets = [new Snippet { Name = "only here", ModifiedUtc = Earlier }] };
        var phone = new SyncDocument { Hosts = [Host(web, "web", Earlier), Host(db, "db-edited", Later)] };

        SyncDocument merged = SyncMerge.Merge(laptop, phone, Now);

        Assert.Equal(["db-edited", "web-edited"], merged.Hosts.Select(h => h.Name).Order());
        Assert.Equal("only here", Assert.Single(merged.Snippets).Name);
    }

    [Fact]
    public void Removal_AfterTheLastEdit_RemovesTheItemOnBothSides()
    {
        Guid id = Guid.NewGuid();
        var laptop = new SyncDocument { Hosts = [Host(id, "web", Earlier)] };
        var phone = new SyncDocument { Deleted = { [id] = Later } };

        SyncDocument merged = SyncMerge.Merge(laptop, phone, Now);

        Assert.Empty(merged.Hosts);
        Assert.Equal(Later, merged.Deleted[id]);
        Assert.Empty(SyncMerge.Merge(phone, laptop, Now).Hosts);
    }

    [Fact]
    public void Edit_AfterTheRemoval_BringsTheItemBackAndForgetsTheRemoval()
    {
        Guid id = Guid.NewGuid();
        var laptop = new SyncDocument { Hosts = [Host(id, "web", Later)] };
        var phone = new SyncDocument { Deleted = { [id] = Earlier } };

        SyncDocument merged = SyncMerge.Merge(phone, laptop, Now);

        Assert.Equal("web", Assert.Single(merged.Hosts).Name);
        Assert.False(merged.Deleted.ContainsKey(id));
    }

    [Fact]
    public void Removal_AtTheVerySameMomentAsTheEdit_Wins()
    {
        Guid id = Guid.NewGuid();
        var laptop = new SyncDocument { Hosts = [Host(id, "web", Later)] };
        var phone = new SyncDocument { Deleted = { [id] = Later } };

        Assert.Empty(SyncMerge.Merge(laptop, phone, Now).Hosts);
    }

    [Fact]
    public void Removal_OlderThanHalfAYear_IsForgotten()
    {
        Guid gone = Guid.NewGuid(), recent = Guid.NewGuid();
        var laptop = new SyncDocument { Deleted = { [gone] = Now - SyncMerge.RemovalMemory - TimeSpan.FromDays(1), [recent] = Now.AddDays(-1) } };

        SyncDocument merged = SyncMerge.Merge(laptop, new SyncDocument(), Now);

        Assert.Equal([recent], merged.Deleted.Keys);
    }

    [Fact]
    public void Removals_OnBothSides_KeepTheLaterTime()
    {
        Guid id = Guid.NewGuid();

        SyncDocument merged = SyncMerge.Merge(new SyncDocument { Deleted = { [id] = Earlier } }, new SyncDocument { Deleted = { [id] = Later } }, Now);

        Assert.Equal(Later, merged.Deleted[id]);
    }

    [Fact]
    public void EditsAtTheSameMoment_AreSettledTheSameWayOnEveryDevice()
    {
        Guid id = Guid.NewGuid();
        var laptop = new SyncDocument { Hosts = [Host(id, "alpha", Now)] };
        var phone = new SyncDocument { Hosts = [Host(id, "omega", Now)] };

        Assert.Equal(Assert.Single(SyncMerge.Merge(laptop, phone, Now).Hosts).Name, Assert.Single(SyncMerge.Merge(phone, laptop, Now).Hosts).Name);
    }

    [Fact]
    public void Secrets_FollowTheSameRules()
    {
        Guid kept = Guid.NewGuid(), removed = Guid.NewGuid();
        var laptop = new SyncDocument
        {
            Secrets = { [kept] = new VaultEntry { Secret = "old", ModifiedUtc = Earlier }, [removed] = new VaultEntry { Secret = "x", ModifiedUtc = Earlier } },
        };
        var phone = new SyncDocument
        {
            Secrets = { [kept] = new VaultEntry { Secret = "new", ModifiedUtc = Later } },
            SecretsDeleted = { [removed] = Later },
        };

        SyncDocument merged = SyncMerge.Merge(laptop, phone, Now);

        Assert.Equal("new", Assert.Single(merged.Secrets).Value.Secret);
        Assert.Equal(Later, merged.SecretsDeleted[removed]);
    }

    [Fact]
    public void SecretsChangedAtTheSameMoment_AreSettledTheSameWayOnEveryDevice()
    {
        Guid id = Guid.NewGuid();
        var laptop = new SyncDocument { Secrets = { [id] = new VaultEntry { Secret = "alpha", ModifiedUtc = Now } } };
        var phone = new SyncDocument { Secrets = { [id] = new VaultEntry { Secret = "omega", ModifiedUtc = Now } } };

        Assert.Equal(SyncMerge.Merge(laptop, phone, Now).Secrets[id].Secret, SyncMerge.Merge(phone, laptop, Now).Secrets[id].Secret);
    }

    [Fact]
    public void KnownHosts_TheKeyTrustedLaterWins()
    {
        var older = new KnownHostKey { Algorithm = "ssh-rsa", Fingerprint = "SHA256:old", AddedAt = Earlier };
        var newer = new KnownHostKey { Algorithm = "ssh-ed25519", Fingerprint = "SHA256:new", AddedAt = Later };

        Assert.Equal("SHA256:new", SyncMerge.Merge(new SyncDocument { KnownHosts = { ["web:22"] = older } }, new SyncDocument { KnownHosts = { ["web:22"] = newer } }, Now).KnownHosts["web:22"].Fingerprint);
        Assert.Equal("SHA256:new", SyncMerge.Merge(new SyncDocument { KnownHosts = { ["web:22"] = newer } }, new SyncDocument { KnownHosts = { ["web:22"] = older } }, Now).KnownHosts["web:22"].Fingerprint);
    }

    [Fact]
    public void CopyFromBeforeIdentitiesExisted_MergesWithoutLosingAny()
    {
        var identity = new Identity { Name = "ops", ModifiedUtc = Earlier };
        var old = new SyncDocument { Identities = null! };

        SyncDocument merged = SyncMerge.Merge(old, new SyncDocument { Identities = [identity] }, Now);

        Assert.Equal("ops", Assert.Single(merged.Identities).Name);
    }

    [Fact]
    public void DuplicateIdsInOneCopy_DoNotDuplicateTheItem()
    {
        Guid id = Guid.NewGuid();
        var broken = new SyncDocument { Hosts = [Host(id, "first", Earlier), Host(id, "second", Later)] };

        Assert.Single(SyncMerge.Merge(broken, new SyncDocument(), Now).Hosts);
    }

    [Fact]
    public void Merge_OfRandomHistories_IsTheSameEitherWayRoundAndSettlesAfterOneRound()
    {
        var random = new Random(2026);
        Guid[] ids = [.. Enumerable.Range(0, 12).Select(_ => Guid.NewGuid())];
        // Times to the tick, so two edits never fall on the same moment (that case is tested on its own above).
        DateTime Time() => Now.AddTicks(-random.NextInt64(1, TimeSpan.TicksPerDay));

        SyncDocument Device()
        {
            var document = new SyncDocument();
            foreach (Guid id in ids)
            {
                switch (random.Next(4))
                {
                    case 0:
                        document.Hosts.Add(Host(id, "host-" + random.Next(100), Time()));
                        break;
                    case 1:
                        document.Deleted[id] = Time();
                        break;
                    case 2:
                        document.Secrets[id] = new VaultEntry { Secret = "s" + random.Next(100), ModifiedUtc = Time() };
                        break;
                }
            }

            return document;
        }

        for (int round = 0; round < 200; round++)
        {
            SyncDocument a = Device(), b = Device();

            SyncDocument ab = SyncMerge.Merge(a, b, Now), ba = SyncMerge.Merge(b, a, Now);
            Assert.True(Canonical(ab) == Canonical(ba), $"round {round}: the result depends on which copy is local");

            // Merging the result with either side again changes nothing: one sync is enough to agree.
            Assert.True(Canonical(SyncMerge.Merge(ab, a, Now)) == Canonical(ab), $"round {round}: merging again changed the result");
            Assert.True(Canonical(SyncMerge.Merge(b, ab, Now)) == Canonical(ab), $"round {round}: merging again changed the result");

            // Nothing that was removed after its last edit survives.
            foreach (HostEntry host in ab.Hosts)
                Assert.False(ab.Deleted.TryGetValue(host.Id, out DateTime removedAt) && removedAt >= host.ModifiedUtc, $"round {round}");
        }
    }

    [Fact]
    public void Clone_SharesNothingWithTheOriginal()
    {
        var original = new SyncDocument { Hosts = [Host(Guid.NewGuid(), "web", Now)] };

        SyncDocument copy = original.Clone();
        copy.Hosts[0].Name = "changed";

        Assert.Equal("web", original.Hosts[0].Name);
    }
}

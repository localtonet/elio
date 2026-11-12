using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Elio.Core;

namespace Elio.Sync;

/// <summary>What is synced: the user's servers and what goes with them. Settings stay per device.</summary>
public sealed class SyncDocument
{
    public int Format { get; set; } = 1;
    public List<HostEntry> Hosts { get; set; } = [];
    public List<HostGroup> Groups { get; set; } = [];
    public List<Snippet> Snippets { get; set; } = [];

    /// <summary>The keychain's identities. Their passwords and keys travel in <see cref="Secrets"/>, under the same ids.</summary>
    public List<Identity> Identities { get; set; } = [];

    /// <summary>When each host, group, snippet or identity was removed.</summary>
    public Dictionary<Guid, DateTime> Deleted { get; set; } = [];

    public Dictionary<string, KnownHostKey> KnownHosts { get; set; } = [];

    public Dictionary<Guid, VaultEntry> Secrets { get; set; } = [];
    public Dictionary<Guid, DateTime> SecretsDeleted { get; set; } = [];

    /// <summary>A copy that shares nothing with the original, so the live library can't change under a sync.</summary>
    public SyncDocument Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(this, SyncJson.Default.SyncDocument), SyncJson.Default.SyncDocument)!;
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SyncDocument))]
[JsonSerializable(typeof(Keyring))]
[JsonSerializable(typeof(HostEntry))]
[JsonSerializable(typeof(HostGroup))]
[JsonSerializable(typeof(Snippet))]
[JsonSerializable(typeof(Identity))]
[JsonSerializable(typeof(VaultEntry))]
internal sealed partial class SyncJson : JsonSerializerContext;

/// <summary>
/// Combines two copies. For each item the newer change wins, and a removal counts as a change, so an
/// item edited on one device and removed on another ends up however it was touched last.
/// </summary>
public static class SyncMerge
{
    // Long enough for a device that was off for a while to hear about removals, short enough to stay small.
    public static readonly TimeSpan RemovalMemory = TimeSpan.FromDays(180);

    public static SyncDocument Merge(SyncDocument local, SyncDocument remote, DateTime now)
    {
        var deleted = MergeRemovals(local.Deleted, remote.Deleted, now);
        var secretsDeleted = MergeRemovals(local.SecretsDeleted, remote.SecretsDeleted, now);

        return new SyncDocument
        {
            Hosts = MergeItems(local.Hosts, remote.Hosts, h => h.Id, h => h.ModifiedUtc, deleted, SyncJson.Default.HostEntry),
            Groups = MergeItems(local.Groups, remote.Groups, g => g.Id, g => g.ModifiedUtc, deleted, SyncJson.Default.HostGroup),
            Snippets = MergeItems(local.Snippets, remote.Snippets, s => s.Id, s => s.ModifiedUtc, deleted, SyncJson.Default.Snippet),

            // A copy written before there were identities has no list of them at all.
            Identities = MergeItems(local.Identities ?? [], remote.Identities ?? [], i => i.Id, i => i.ModifiedUtc, deleted, SyncJson.Default.Identity),
            Deleted = deleted,
            KnownHosts = MergeKnownHosts(local.KnownHosts, remote.KnownHosts),
            Secrets = MergeItems(
                local.Secrets.Select(p => (p.Key, p.Value)).ToList(),
                remote.Secrets.Select(p => (p.Key, p.Value)).ToList(),
                p => p.Key,
                p => p.Value.ModifiedUtc,
                secretsDeleted,
                null).ToDictionary(p => p.Key, p => p.Value),
            SecretsDeleted = secretsDeleted,
        };
    }

    private static Dictionary<Guid, DateTime> MergeRemovals(Dictionary<Guid, DateTime> a, Dictionary<Guid, DateTime> b, DateTime now)
    {
        var merged = new Dictionary<Guid, DateTime>();
        foreach (var (id, when) in a.Concat(b))
        {
            if (now - when > RemovalMemory) continue;
            if (!merged.TryGetValue(id, out DateTime known) || when > known) merged[id] = when;
        }

        return merged;
    }

    private static List<T> MergeItems<T>(List<T> local, List<T> remote, Func<T, Guid> idOf, Func<T, DateTime> timeOf, Dictionary<Guid, DateTime> deleted, JsonTypeInfo<T>? type)
    {
        var mine = local.GroupBy(idOf).ToDictionary(g => g.Key, g => g.First());
        var theirs = remote.GroupBy(idOf).ToDictionary(g => g.Key, g => g.First());
        var result = new List<T>();

        foreach (Guid id in mine.Keys.Union(theirs.Keys))
        {
            bool haveMine = mine.TryGetValue(id, out T? a);
            bool haveTheirs = theirs.TryGetValue(id, out T? b);
            T winner = !haveTheirs ? a! : !haveMine ? b! : Newer(a!, b!, timeOf, type);

            // An item changed after it was removed has been brought back on purpose.
            if (deleted.TryGetValue(id, out DateTime removedAt))
            {
                if (removedAt >= timeOf(winner)) continue;
                deleted.Remove(id);
            }

            result.Add(winner);
        }

        return result;
    }

    // Ties are broken by content, so every device picks the same one.
    private static T Newer<T>(T a, T b, Func<T, DateTime> timeOf, JsonTypeInfo<T>? type)
    {
        int byTime = timeOf(a).CompareTo(timeOf(b));
        if (byTime != 0) return byTime > 0 ? a : b;
        if (type is null) return a;
        return string.CompareOrdinal(JsonSerializer.Serialize(a, type), JsonSerializer.Serialize(b, type)) >= 0 ? a : b;
    }

    private static Dictionary<string, KnownHostKey> MergeKnownHosts(Dictionary<string, KnownHostKey> a, Dictionary<string, KnownHostKey> b)
    {
        var merged = new Dictionary<string, KnownHostKey>(a);
        foreach (var (key, value) in b)
            if (!merged.TryGetValue(key, out KnownHostKey? known) || value.AddedAt > known.AddedAt) merged[key] = value;
        return merged;
    }
}

using System.Text.Json;

namespace Elio.Core;

/// <summary>Lists the cloud compute instances of a Vultr account as servers.</summary>
public static class VultrImport
{
    /// <summary>The group the instances are put in.</summary>
    public const string GroupName = "Vultr";

    private const string ApiHost = "api.vultr.com";

    // The most Vultr gives on one page.
    private const int PageSize = 500;

    /// <summary>
    /// Asks Vultr for every instance of the account, page by page. The key goes out in the request's header and
    /// nowhere else: it is not kept, written down or put in an address.
    /// </summary>
    /// <param name="handler">Answers in place of the network, in tests.</param>
    /// <exception cref="CloudImportException">Vultr refused or could not be reached. The message is its own.</exception>
    public static async Task<HostLibrary> ListAsync(string apiKey, HttpMessageHandler? handler = null, CancellationToken cancellation = default)
    {
        using HttpClient client = CloudHttp.Client(handler);
        var group = new HostGroup { Name = GroupName };
        var library = new HostLibrary { Groups = [group] };
        var seen = new HashSet<string>(StringComparer.Ordinal);

        string? cursor = null;
        for (int pages = 0; pages < CloudHttp.MaxPages; pages++)
        {
            // The cursor is put in as a value and nothing more, so whatever it holds the request stays with Vultr.
            string address = $"https://{ApiHost}/v2/instances?per_page={PageSize}" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            string body = await CloudJson.GetAsync(client, new Uri(address), apiKey, GroupName, ErrorMessage, cancellation).ConfigureAwait(false);
            foreach (HostEntry host in ParsePage(body, out cursor))
            {
                host.GroupId = group.Id;
                library.Hosts.Add(host);
            }

            // A cursor that comes round again would have the same page fetched for ever.
            if (cursor is null || !seen.Add(cursor)) break;
        }

        return library;
    }

    /// <summary>The instances on one page of the answer, and the cursor that asks for the page after it, if there is one.</summary>
    public static List<HostEntry> ParsePage(string json, out string? nextCursor)
    {
        var hosts = new List<HostEntry>();
        using JsonDocument document = CloudJson.ParseList(json, "instances", GroupName, out JsonElement instances);

        foreach (JsonElement instance in instances.EnumerateArray())
        {
            if (instance.ValueKind != JsonValueKind.Object || Address(instance) is not string address) continue;

            var tags = new List<string>();
            if (CloudJson.Text(instance, "region") is { } region) tags.Add(region);

            // "tag" is the single tag of old, which Vultr still sends beside the list that replaced it.
            List<string> labels = CloudJson.Texts(instance, "tags");
            if (labels.Count == 0 && CloudJson.Text(instance, "tag") is { } tag) labels.Add(tag);
            tags.AddRange(labels);

            hosts.Add(new HostEntry
            {
                Name = CloudJson.Text(instance, "label") ?? CloudJson.Text(instance, "hostname") ?? address,
                Address = address,

                // Vultr's own images let root in with the keys or password chosen when the instance was made.
                User = "root",
                UseAgent = true,
                Tags = tags.Distinct().ToList(),
            });
        }

        // The last page has an empty cursor for the next.
        nextCursor = CloudJson.Child(document.RootElement, "meta", "links") is { } links ? CloudJson.Text(links, "next") : null;
        return hosts;
    }

    /// <summary>What Vultr said was wrong, or null if the answer does not say.</summary>
    public static string? ErrorMessage(string body)
    {
        using JsonDocument? document = CloudJson.ParseError(body);
        return document is null ? null : CloudJson.Text(document.RootElement, "error");
    }

    private static string? Address(JsonElement instance)
    {
        // An instance still being set up has 0.0.0.0 for its main address, which is no address at all.
        if (CloudJson.Address(CloudJson.Text(instance, "main_ip")) is { } main) return main.ToString();
        if (CloudJson.Address(CloudJson.Text(instance, "v6_main_ip")) is { } v6) return v6.ToString();

        // An instance with no public address is still reachable from its neighbours, or through a jump host.
        return CloudJson.Address(CloudJson.Text(instance, "internal_ip"))?.ToString();
    }
}

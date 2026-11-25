using System.Net;
using System.Text.Json;

namespace Elio.Core;

/// <summary>Lists the Linodes (compute instances) of an Akamai Cloud account as servers.</summary>
public static class LinodeImport
{
    /// <summary>The group the instances are put in.</summary>
    public const string GroupName = "Linode";

    private const string ApiHost = "api.linode.com";

    // The most Linode gives on one page.
    private const int PageSize = 500;

    /// <summary>
    /// Asks Linode for every instance the token can see, page by page. The token goes out in the request's header
    /// and nowhere else: it is not kept, written down or put in an address.
    /// </summary>
    /// <param name="handler">Answers in place of the network, in tests.</param>
    /// <exception cref="CloudImportException">Linode refused or could not be reached. The message is its own.</exception>
    public static async Task<HostLibrary> ListAsync(string token, HttpMessageHandler? handler = null, CancellationToken cancellation = default)
    {
        using HttpClient client = CloudHttp.Client(handler);
        var group = new HostGroup { Name = GroupName };
        var library = new HostLibrary { Groups = [group] };

        int? page = 1;
        for (int pages = 0; page is int asked && pages < CloudHttp.MaxPages; pages++)
        {
            // Only the page's number is taken from the answer: the address it goes into is always Linode's own.
            string body = await CloudJson.GetAsync(client, new Uri($"https://{ApiHost}/v4/linode/instances?page={asked}&page_size={PageSize}"), token, GroupName, ErrorMessage, cancellation).ConfigureAwait(false);
            foreach (HostEntry host in ParsePage(body, out page))
            {
                host.GroupId = group.Id;
                library.Hosts.Add(host);
            }

            // Pages only go forward. One that pointed back, or at itself, would have the same instances asked for for ever.
            if (page <= asked) break;
        }

        return library;
    }

    /// <summary>The instances on one page of the answer, and the number of the page after it, if there is one.</summary>
    public static List<HostEntry> ParsePage(string json, out int? nextPage)
    {
        var hosts = new List<HostEntry>();
        using JsonDocument document = CloudJson.ParseList(json, "data", GroupName, out JsonElement linodes);

        foreach (JsonElement linode in linodes.EnumerateArray())
        {
            if (linode.ValueKind != JsonValueKind.Object || Address(linode) is not string address) continue;

            var tags = new List<string>();
            if (CloudJson.Text(linode, "region") is { } region) tags.Add(region);
            tags.AddRange(CloudJson.Texts(linode, "tags"));

            hosts.Add(new HostEntry
            {
                Name = CloudJson.Text(linode, "label") ?? address,
                Address = address,

                // Linode's own images let root in with the keys or password chosen when the instance was made.
                User = "root",
                UseAgent = true,
                Tags = tags.Distinct().ToList(),
            });
        }

        // The answer says which page it is and how many there are; the next is the one after, while there is one.
        JsonElement root = document.RootElement;
        nextPage = CloudJson.Number(root, "page") is int page && CloudJson.Number(root, "pages") is int count && page >= 1 && page < count ? page + 1 : null;
        return hosts;
    }

    /// <summary>What Linode said was wrong, or null if the answer does not say.</summary>
    public static string? ErrorMessage(string body)
    {
        using JsonDocument? document = CloudJson.ParseError(body);
        if (document is null) return null;

        // Every reason it gave, with the field it is about where it names one.
        List<string> reasons = CloudJson.Objects(document.RootElement, "errors")
            .Select(error => (Reason: CloudJson.Text(error, "reason"), Field: CloudJson.Text(error, "field")))
            .Where(error => error.Reason is not null)
            .Select(error => error.Field is null ? error.Reason! : $"{error.Field}: {error.Reason}")
            .Distinct()
            .ToList();

        return reasons.Count > 0 ? string.Join("; ", reasons) : null;
    }

    private static string? Address(JsonElement linode)
    {
        // One list holds the public addresses and the private one, in no promised order.
        List<IPAddress> ipv4 = CloudJson.Texts(linode, "ipv4").Select(CloudJson.Address).OfType<IPAddress>().ToList();
        if (ipv4.FirstOrDefault(CloudJson.IsPublic) is { } open) return open.ToString();

        // The IPv6 address comes as "2600:3c03::f03c:91ff:fe24:3a2f/128": a network of the one address. A wider
        // one would not say which of its addresses the instance has.
        if (CloudJson.Text(linode, "ipv6") is { } ipv6 && ipv6.EndsWith("/128", StringComparison.Ordinal) && CloudJson.FirstAddress(ipv6) is { } v6) return v6;

        // An instance with no public address is still reachable from its neighbours, or through a jump host.
        return ipv4.FirstOrDefault()?.ToString();
    }
}

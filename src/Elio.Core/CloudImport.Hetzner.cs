using System.Text.Json;

namespace Elio.Core;

/// <summary>Lists the servers of a Hetzner Cloud project as servers.</summary>
public static class HetznerImport
{
    /// <summary>The group the servers are put in.</summary>
    public const string GroupName = "Hetzner Cloud";

    private const string ApiHost = "api.hetzner.cloud";

    // The most Hetzner gives on one page.
    private const int PageSize = 50;

    /// <summary>
    /// Asks Hetzner for every server of the project the token belongs to, page by page. The token goes out in the
    /// request's header and nowhere else: it is not kept, written down or put in an address.
    /// </summary>
    /// <param name="handler">Answers in place of the network, in tests.</param>
    /// <exception cref="CloudImportException">Hetzner refused or could not be reached. The message is its own.</exception>
    public static async Task<HostLibrary> ListAsync(string token, HttpMessageHandler? handler = null, CancellationToken cancellation = default)
    {
        using HttpClient client = CloudHttp.Client(handler);
        var group = new HostGroup { Name = GroupName };
        var library = new HostLibrary { Groups = [group] };

        int? page = 1;
        for (int pages = 0; page is int asked && pages < CloudHttp.MaxPages; pages++)
        {
            // Only the page's number is taken from the answer: the address it goes into is always Hetzner's own.
            string body = await CloudJson.GetAsync(client, new Uri($"https://{ApiHost}/v1/servers?page={asked}&per_page={PageSize}"), token, GroupName, ErrorMessage, cancellation).ConfigureAwait(false);
            foreach (HostEntry host in ParsePage(body, out page))
            {
                host.GroupId = group.Id;
                library.Hosts.Add(host);
            }

            // Pages only go forward. One that pointed back, or at itself, would have the same servers asked for for ever.
            if (page <= asked) break;
        }

        return library;
    }

    /// <summary>The servers on one page of the answer, and the number of the page after it, if there is one.</summary>
    public static List<HostEntry> ParsePage(string json, out int? nextPage)
    {
        var hosts = new List<HostEntry>();
        using JsonDocument document = CloudJson.ParseList(json, "servers", GroupName, out JsonElement servers);

        foreach (JsonElement server in servers.EnumerateArray())
        {
            if (server.ValueKind != JsonValueKind.Object || Address(server) is not string address) continue;

            var tags = new List<string>();
            if (CloudJson.Child(server, "location") is { } location && CloudJson.Text(location, "name") is { } place) tags.Add(place);
            if (CloudJson.Child(server, "labels") is { } labels) tags.AddRange(CloudJson.Labels(labels));

            hosts.Add(new HostEntry
            {
                Name = CloudJson.Text(server, "name") ?? address,
                Address = address,

                // Hetzner's own images let root in with the key chosen when the server was made.
                User = "root",
                UseAgent = true,
                Tags = tags.Distinct().ToList(),
            });
        }

        nextPage = CloudJson.Child(document.RootElement, "meta", "pagination") is { } pagination && CloudJson.Number(pagination, "next_page") is int next && next > 0 ? next : null;
        return hosts;
    }

    /// <summary>What Hetzner said was wrong, or null if the answer does not say.</summary>
    public static string? ErrorMessage(string body)
    {
        using JsonDocument? document = CloudJson.ParseError(body);
        if (document is null || CloudJson.Child(document.RootElement, "error") is not { } error || CloudJson.Text(error, "message") is not string message) return null;

        return CloudJson.Text(error, "code") is string code ? $"{message} ({code})" : message;
    }

    private static string? Address(JsonElement server)
    {
        if (CloudJson.Child(server, "public_net", "ipv4") is { } ipv4 && CloudJson.Address(CloudJson.Text(ipv4, "ip")) is { } v4) return v4.ToString();

        // A server can do without IPv4, which Hetzner charges for. Its IPv6 comes as a network, "2a01:4f8::/64",
        // whose first address is the server's own.
        if (CloudJson.Child(server, "public_net", "ipv6") is { } ipv6 && CloudJson.FirstAddress(CloudJson.Text(ipv6, "ip")) is { } v6) return v6;

        // A server with no public address at all is still reachable from its neighbours, or through a jump host.
        return CloudJson.Objects(server, "private_net").Select(network => CloudJson.Address(CloudJson.Text(network, "ip"))?.ToString()).FirstOrDefault(ip => ip is not null);
    }
}

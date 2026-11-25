using System.Net;
using System.Text.Json;

namespace Elio.Core;

/// <summary>Lists the devices of a Tailscale tailnet as servers.</summary>
public static class TailscaleImport
{
    /// <summary>The group the devices are put in.</summary>
    public const string GroupName = "Tailscale";

    // "-" is the tailnet the token belongs to, so nobody has to look up its name.
    private static readonly Uri Devices = new("https://api.tailscale.com/api/v2/tailnet/-/devices");

    // Every address Tailscale gives out is in these.
    private static readonly IPNetwork TailscaleIPv4 = IPNetwork.Parse("100.64.0.0/10");
    private static readonly IPNetwork TailscaleIPv6 = IPNetwork.Parse("fd7a:115c:a1e0::/48");

    /// <summary>
    /// Asks Tailscale for every device of the tailnet the token belongs to. The token goes out in the request's
    /// header and nowhere else: it is not kept, written down or put in an address.
    /// </summary>
    /// <param name="handler">Answers in place of the network, in tests.</param>
    /// <exception cref="CloudImportException">Tailscale refused or could not be reached. The message is its own.</exception>
    public static async Task<HostLibrary> ListAsync(string token, HttpMessageHandler? handler = null, CancellationToken cancellation = default)
    {
        using HttpClient client = CloudHttp.Client(handler);
        var group = new HostGroup { Name = GroupName };
        var library = new HostLibrary { Groups = [group] };

        // Tailscale gives every device in one answer: there are no pages.
        string body = await CloudJson.GetAsync(client, Devices, token, GroupName, ErrorMessage, cancellation).ConfigureAwait(false);
        foreach (HostEntry host in ParsePage(body))
        {
            host.GroupId = group.Id;
            library.Hosts.Add(host);
        }

        return library;
    }

    /// <summary>The devices in the answer, which has all of them.</summary>
    public static List<HostEntry> ParsePage(string json)
    {
        var hosts = new List<HostEntry>();
        using JsonDocument document = CloudJson.ParseList(json, "devices", GroupName, out JsonElement devices);

        foreach (JsonElement device in devices.EnumerateArray())
        {
            if (device.ValueKind != JsonValueKind.Object) continue;

            // A device waiting for an admin's approval cannot be reached over the tailnet yet.
            if (device.TryGetProperty("authorized", out JsonElement authorized) && authorized.ValueKind == JsonValueKind.False) continue;

            if (Address(device) is not string address) continue;

            var tags = new List<string>();
            if (CloudJson.Text(device, "os") is { } os) tags.Add(os);

            // Every tag is "tag:" and a name, and the name is what is worth searching for.
            tags.AddRange(CloudJson.Texts(device, "tags").Select(tag => tag.StartsWith("tag:", StringComparison.Ordinal) ? tag[4..] : tag).Where(tag => tag.Length > 0));

            hosts.Add(new HostEntry
            {
                // The machine name, which the MagicDNS name starts with.
                Name = CloudJson.Text(device, "hostname") ?? (Uri.CheckHostName(address) == UriHostNameType.Dns ? address.Split('.')[0] : address),
                Address = address,

                // A tailnet holds every kind of machine, each with its own user, so none is guessed.
                User = string.Empty,
                UseAgent = true,
                Tags = tags.Distinct().ToList(),
            });
        }

        return hosts;
    }

    /// <summary>What Tailscale said was wrong, or null if the answer does not say.</summary>
    public static string? ErrorMessage(string body)
    {
        using JsonDocument? document = CloudJson.ParseError(body);
        return document is null ? null : CloudJson.Text(document.RootElement, "message");
    }

    private static string? Address(JsonElement device)
    {
        // The MagicDNS name, such as "pangolin.tailfe8c.ts.net", and the device's Tailscale address where there is none.
        if (CloudJson.Text(device, "name")?.TrimEnd('.') is { Length: > 0 } name && name[0] != '-' && name.Contains('.') && Uri.CheckHostName(name) == UriHostNameType.Dns) return name;

        List<IPAddress> addresses = CloudJson.Texts(device, "addresses").Select(CloudJson.Address).OfType<IPAddress>().ToList();
        return (addresses.FirstOrDefault(TailscaleIPv4.Contains) ?? addresses.FirstOrDefault(TailscaleIPv6.Contains))?.ToString();
    }
}

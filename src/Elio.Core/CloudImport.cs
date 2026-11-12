using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Elio.Core;

/// <summary>A cloud provider refused, or what it sent could not be used. The message is the provider's own where it gave one.</summary>
public sealed class CloudImportException(string message) : Exception(message);

/// <summary>What AWS wants to see before it answers. The session token is only there for temporary credentials.</summary>
public sealed record AwsCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken = null);

/// <summary>What the cloud imports share: one careful way of asking a provider something over HTTPS.</summary>
internal static class CloudHttp
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // A page of a few hundred servers is well under a megabyte. Nothing honest comes near this.
    private const int LargestAnswer = 16 * 1024 * 1024;

    /// <summary>An account with more pages than this is not being listed, it is being looped.</summary>
    public const int MaxPages = 100;

    /// <param name="handler">Stands in for the network, in tests. It is the caller's to dispose of.</param>
    public static HttpClient Client(HttpMessageHandler? handler) =>

        // A redirect is never followed: it would carry the credentials on to wherever it points.
        new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: handler is null)
        {
            Timeout = Timeout,
            MaxResponseContentBufferSize = LargestAnswer,
        };

    /// <summary>Sends a request and gives back what came, whatever the status. Only ever over HTTPS.</summary>
    public static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient client, HttpRequestMessage request, string provider, CancellationToken cancellation)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } address || address.Scheme != Uri.UriSchemeHttps)
            throw new CloudImportException(L.T("{0} can only be asked over HTTPS.", provider));

        // Cancelling between two pages stops before the next one is asked for.
        cancellation.ThrowIfCancellationRequested();

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // Nobody cancelled, so it was the clock.
            throw new CloudImportException(L.T("{0} did not answer in time.", provider));
        }
        catch (HttpRequestException ex)
        {
            throw new CloudImportException(ex.Message);
        }
    }

    public static CloudImportException Unreadable(string provider) =>
        new(L.T("The answer from {0} could not be read.", provider));

    public static CloudImportException Refused(string provider, HttpStatusCode status) =>
        new(L.T("{0} answered with an error ({1}).", provider, (int)status));
}

/// <summary>Lists the droplets of a DigitalOcean account as servers.</summary>
public static class DigitalOceanImport
{
    /// <summary>The group the droplets are put in.</summary>
    public const string GroupName = "DigitalOcean";

    private const string ApiHost = "api.digitalocean.com";

    /// <summary>
    /// Asks DigitalOcean for every droplet the token can see, page by page. The token goes out in the request's
    /// header and nowhere else: it is not kept, written down or put in an address.
    /// </summary>
    /// <param name="handler">Answers in place of the network, in tests.</param>
    /// <exception cref="CloudImportException">DigitalOcean refused or could not be reached. The message is its own.</exception>
    public static async Task<HostLibrary> ListAsync(string token, HttpMessageHandler? handler = null, CancellationToken cancellation = default)
    {
        using HttpClient client = CloudHttp.Client(handler);
        var group = new HostGroup { Name = GroupName };
        var library = new HostLibrary { Groups = [group] };

        Uri? page = new($"https://{ApiHost}/v2/droplets?per_page=200");
        for (int pages = 0; page is not null && pages < CloudHttp.MaxPages; pages++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, page);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token.Trim());
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            var (status, body) = await CloudHttp.SendAsync(client, request, GroupName, cancellation).ConfigureAwait(false);
            if ((int)status is < 200 or > 299) throw new CloudImportException(ErrorMessage(body) ?? CloudHttp.Refused(GroupName, status).Message);

            foreach (HostEntry host in ParsePage(body, out page))
            {
                host.GroupId = group.Id;
                library.Hosts.Add(host);
            }
        }

        return library;
    }

    /// <summary>The droplets on one page of the answer, and the address of the page after it, if there is one.</summary>
    public static List<HostEntry> ParsePage(string json, out Uri? next)
    {
        next = null;
        var hosts = new List<HostEntry>();

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("droplets", out JsonElement droplets) || droplets.ValueKind != JsonValueKind.Array)
                throw CloudHttp.Unreadable(GroupName);

            foreach (JsonElement droplet in droplets.EnumerateArray())
            {
                if (droplet.ValueKind != JsonValueKind.Object) continue;

                // A droplet without a public address is still reachable from its neighbours, or through a jump host.
                string? address = Address(droplet, "public") ?? Address(droplet, "private");
                if (address is null) continue;

                var tags = new List<string>();
                if (droplet.TryGetProperty("region", out JsonElement region) && Text(region, "slug") is { Length: > 0 } slug) tags.Add(slug);
                if (droplet.TryGetProperty("tags", out JsonElement labels) && labels.ValueKind == JsonValueKind.Array)
                    tags.AddRange(labels.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).Where(t => t.Length > 0));

                hosts.Add(new HostEntry
                {
                    Name = Text(droplet, "name") is { Length: > 0 } name ? name : address,
                    Address = address,

                    // DigitalOcean's own images are set up for root. A droplet made otherwise is one edit away.
                    User = "root",
                    UseAgent = true,
                    Tags = tags.Distinct().ToList(),
                });
            }

            // The next page is only fetched from DigitalOcean itself, since the token goes with the request.
            if (root.TryGetProperty("links", out JsonElement links) && links.ValueKind == JsonValueKind.Object
                && links.TryGetProperty("pages", out JsonElement pages) && Text(pages, "next") is { Length: > 0 } link
                && Uri.TryCreate(link, UriKind.Absolute, out Uri? following) && following.Scheme == Uri.UriSchemeHttps && following.Host == ApiHost)
                next = following;
        }
        catch (JsonException)
        {
            throw CloudHttp.Unreadable(GroupName);
        }

        return hosts;
    }

    /// <summary>What DigitalOcean said was wrong, or null if the answer does not say.</summary>
    public static string? ErrorMessage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return Text(document.RootElement, "message") is { Length: > 0 } message ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Address(JsonElement droplet, string type)
    {
        if (!droplet.TryGetProperty("networks", out JsonElement networks) || networks.ValueKind != JsonValueKind.Object) return null;
        if (!networks.TryGetProperty("v4", out JsonElement v4) || v4.ValueKind != JsonValueKind.Array) return null;

        return v4.EnumerateArray()
            .Where(network => Text(network, "type") == type)
            .Select(network => Text(network, "ip_address"))
            .FirstOrDefault(ip => !string.IsNullOrEmpty(ip));
    }
}

/// <summary>
/// Signs a request the way AWS asks for it: Signature Version 4. Written out here because the alternative is
/// AWS's whole SDK, for the sake of one request.
/// </summary>
public static class AwsSignature
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>The time in the form AWS wants it in the x-amz-date header.</summary>
    public static string Timestamp(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The key a day's requests to one service in one region are signed with, worked out from the secret key.</summary>
    public static byte[] SigningKey(string secretAccessKey, DateTime utc, string region, string service)
    {
        byte[] date = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        return Hmac(Hmac(Hmac(date, region), service), "aws4_request");
    }

    /// <summary>The parameters of a request as AWS signs them: sorted by name, each name and value percent-encoded.</summary>
    public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join('&', parameters
            .Select(p => (Name: Uri.EscapeDataString(p.Key), Value: Uri.EscapeDataString(p.Value)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={p.Value}"));

    /// <summary>The text that is hashed and signed: the request, reduced to one exact spelling.</summary>
    /// <param name="headers">Every header that is to be covered by the signature. "host" and "x-amz-date" have to be among them.</param>
    public static string CanonicalRequest(string method, string path, string canonicalQuery, IEnumerable<KeyValuePair<string, string>> headers, byte[] payload, out string signedHeaders)
    {
        // Names in lower case and in order, values without the spaces around them and with runs of spaces made one.
        List<(string Name, string Value)> canonical = headers
            .Select(h => (Name: h.Key.ToLowerInvariant(), Value: Regex.Replace(h.Value.Trim(), " +", " ")))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .ToList();

        signedHeaders = string.Join(';', canonical.Select(h => h.Name));
        return string.Join('\n',
            method,
            string.Join('/', path.Split('/').Select(Uri.EscapeDataString)),
            canonicalQuery,
            string.Concat(canonical.Select(h => $"{h.Name}:{h.Value}\n")),
            signedHeaders,
            Hex(SHA256.HashData(payload)));
    }

    /// <summary>The value of the Authorization header for a request.</summary>
    public static string Authorization(string method, string path, string canonicalQuery, IEnumerable<KeyValuePair<string, string>> headers, byte[] payload, AwsCredentials credentials, string region, string service, DateTime utc)
    {
        string request = CanonicalRequest(method, path, canonicalQuery, headers, payload, out string signedHeaders);
        string scope = $"{utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}/{region}/{service}/aws4_request";
        string toSign = string.Join('\n', Algorithm, Timestamp(utc), scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(request))));
        string signature = Hex(Hmac(SigningKey(credentials.SecretAccessKey, utc, region, service), toSign));

        return $"{Algorithm} Credential={credentials.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    private static byte[] Hmac(byte[] key, string text) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text));

    internal static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}

/// <summary>Lists the running EC2 instances of an AWS account in one region as servers.</summary>
public static partial class AwsEc2Import
{
    private const string Provider = "AWS";
    private const string Service = "ec2";
    private const string ApiVersion = "2016-11-15";

    // The region becomes part of the address the credentials are sent to, so it has to look like one and nothing else.
    [GeneratedRegex(@"^[a-z]{2,4}(-[a-z]+)+-\d{1,2}$")]
    private static partial Regex RegionPattern();

    /// <summary>Whether the text has the shape of a region's name, such as "eu-west-1".</summary>
    public static bool IsRegion(string region) => RegionPattern().IsMatch(region);

    /// <summary>The group a region's instances are put in.</summary>
    public static string GroupName(string region) => $"AWS / {region}";

    /// <summary>
    /// Asks EC2 for the running instances of a region, page by page. The secret key never leaves this computer:
    /// it only signs the requests. Nothing of the credentials is kept or written down here.
    /// </summary>
    /// <param name="handler">Answers in place of the network, in tests.</param>
    /// <param name="clock">The time the requests are signed with, in tests.</param>
    /// <exception cref="CloudImportException">AWS refused or could not be reached. The message is its own.</exception>
    public static async Task<HostLibrary> ListAsync(AwsCredentials credentials, string region, HttpMessageHandler? handler = null, CancellationToken cancellation = default, Func<DateTime>? clock = null)
    {
        region = region.Trim().ToLowerInvariant();
        if (!IsRegion(region)) throw new CloudImportException(L.T("\"{0}\" is not the name of an AWS region. One looks like eu-west-1.", region));

        using HttpClient client = CloudHttp.Client(handler);
        var group = new HostGroup { Name = GroupName(region) };
        var library = new HostLibrary { Groups = [group] };
        var seen = new HashSet<string>(StringComparer.Ordinal);

        string? token = null;
        for (int pages = 0; pages < CloudHttp.MaxPages; pages++)
        {
            using HttpRequestMessage request = Request(credentials, region, token, clock?.Invoke() ?? DateTime.UtcNow);
            var (status, body) = await CloudHttp.SendAsync(client, request, Provider, cancellation).ConfigureAwait(false);
            if ((int)status is < 200 or > 299) throw new CloudImportException(ErrorMessage(body) ?? CloudHttp.Refused(Provider, status).Message);

            foreach (HostEntry host in ParsePage(body, out token))
            {
                host.GroupId = group.Id;
                library.Hosts.Add(host);
            }

            // A token that comes round again would have the same page fetched for ever.
            if (token is null || !seen.Add(token)) break;
        }

        return library;
    }

    /// <summary>The signed request for one page of running instances.</summary>
    public static HttpRequestMessage Request(AwsCredentials credentials, string region, string? nextToken, DateTime utc)
    {
        // The regions in China are a separate AWS with its own domain.
        string host = $"{Service}.{region}.amazonaws.com" + (region.StartsWith("cn-", StringComparison.Ordinal) ? ".cn" : string.Empty);

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("Action", "DescribeInstances"),
            new("Version", ApiVersion),

            // Stopped and terminated instances have no address to connect to.
            new("Filter.1.Name", "instance-state-name"),
            new("Filter.1.Value.1", "running"),
            new("MaxResults", "1000"),
        };
        if (nextToken is not null) parameters.Add(new("NextToken", nextToken));

        var headers = new List<KeyValuePair<string, string>>
        {
            new("host", host),
            new("x-amz-date", AwsSignature.Timestamp(utc)),
        };
        if (!string.IsNullOrWhiteSpace(credentials.SessionToken)) headers.Add(new("x-amz-security-token", credentials.SessionToken.Trim()));

        // The address is built from the very text that is signed, so the two cannot come to differ.
        string query = AwsSignature.CanonicalQuery(parameters);
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{host}/?{query}"));
        foreach (var (name, value) in headers.Skip(1)) request.Headers.TryAddWithoutValidation(name, value);
        request.Headers.TryAddWithoutValidation("Authorization", AwsSignature.Authorization("GET", "/", query, headers, [], credentials, region, Service, utc));
        return request;
    }

    /// <summary>The running instances on one page of the answer, and the token that asks for the page after it, if there is one.</summary>
    public static List<HostEntry> ParsePage(string xml, out string? nextToken)
    {
        XElement root = Parse(xml) ?? throw CloudHttp.Unreadable(Provider);
        if (root.Name.LocalName != "DescribeInstancesResponse") throw CloudHttp.Unreadable(Provider);

        nextToken = Child(root, "nextToken")?.Value.Trim() is { Length: > 0 } token ? token : null;

        var hosts = new List<HostEntry>();
        IEnumerable<XElement> instances = Items(Child(root, "reservationSet")).SelectMany(reservation => Items(Child(reservation, "instancesSet")));
        foreach (XElement instance in instances)
        {
            // Asked for running ones only, and checked all the same: the list is about to become things to connect to.
            if (Child(instance, "instanceState") is { } state && Value(state, "name") is { } stateName && stateName != "running") continue;

            // The name that can be reached from outside first, and the address inside the network when there is no other.
            string? address = Value(instance, "dnsName") ?? Value(instance, "ipAddress") ?? Value(instance, "privateIpAddress");
            if (address is null) continue;

            string? name = null;
            var tags = new List<string>();
            foreach (XElement tag in Items(Child(instance, "tagSet")))
            {
                string? key = Value(tag, "key"), value = Value(tag, "value");
                if (key is null) continue;
                if (key == "Name") name = value;

                // Tags AWS puts on by itself say how the instance was made, not what it is.
                else if (!key.StartsWith("aws:", StringComparison.Ordinal)) tags.Add(value is null ? key : $"{key}={value}");
            }

            hosts.Add(new HostEntry
            {
                Name = name ?? Value(instance, "instanceId") ?? address,
                Address = address,

                // The user depends on the image, and AWS does not say. This is Amazon Linux's; Ubuntu's is "ubuntu".
                User = "ec2-user",
                UseAgent = true,
                Tags = tags,
            });
        }

        return hosts;
    }

    /// <summary>What AWS said was wrong, or null if the answer does not say.</summary>
    public static string? ErrorMessage(string xml)
    {
        XElement? error = Parse(xml)?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Error");
        if (error is null || Value(error, "Message") is not string message) return null;

        return Value(error, "Code") is string code ? $"{message} ({code})" : message;
    }

    private static XElement? Parse(string xml)
    {
        try
        {
            // No document types and nothing fetched from elsewhere: the answer is data, and is read as nothing more.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using XmlReader reader = XmlReader.Create(new StringReader(xml), settings);
            return XDocument.Load(reader).Root;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    // The answer's elements all carry the API version as their namespace, so they are found by name alone.
    private static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Items(XElement? set) => set?.Elements().Where(e => e.Name.LocalName == "item") ?? [];

    private static string? Value(XElement parent, string name) => Child(parent, name)?.Value.Trim() is { Length: > 0 } value ? value : null;
}
